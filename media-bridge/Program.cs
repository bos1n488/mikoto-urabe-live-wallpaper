using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MikotoMediaBridge;

public static class Program
{
    private const string ListenUrl = "http://127.0.0.1:47823";
    private const string ControlToken = "mk-47f2a9";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "MikotoMediaBridge.SingleInstance", out var first);
        if (!first)
        {
            Console.WriteLine("Mikoto media bridge is already running.");
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ListenUrl);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddSingleton<MediaService>();

        var app = builder.Build();
        var logPath = Path.Combine(AppContext.BaseDirectory, "bridge.log");
        void Log(string message)
        {
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }

        app.Use(async (ctx, next) =>
        {
            // Chrome's Private Network Access, needed when the wallpaper is
            // opened from file:// and talks to 127.0.0.1.
            ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";

            var requestOrigin = ctx.Request.Headers.Origin.ToString();
            var requestPath = ctx.Request.Path + ctx.Request.QueryString;
            var requestMethod = ctx.Request.Method;
            var userAgent = ctx.Request.Headers.UserAgent.ToString();

            // Only local wallpaper documents may read media state or send
            // commands. Remote web pages get a 403, so media titles and
            // artwork cannot be scraped by an arbitrary site.
            var origin = ctx.Request.Headers.Origin.ToString();
            var loopbackHost = false;
            if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                var host = originUri.Host;
                loopbackHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                               host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                               host == "127.0.0.1" ||
                               host == "::1";
            }

            var originOk = string.IsNullOrEmpty(origin) ||
                           origin == "null" ||
                           origin.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase) ||
                           origin.StartsWith("oc-preview://", StringComparison.OrdinalIgnoreCase) ||
                           origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                           loopbackHost ||
                           origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                           origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
            if (!originOk)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("forbidden origin");
                Log($"{requestMethod} {requestPath} origin=[{requestOrigin}] => 403 forbidden  ua=[{userAgent}]");
                return;
            }

            await next();
            if (ctx.Response.StatusCode != 200 || requestPath.StartsWith("/control", StringComparison.OrdinalIgnoreCase))
                Log($"{requestMethod} {requestPath} origin=[{requestOrigin}] => {ctx.Response.StatusCode}  ua=[{userAgent}]");
        });
        app.UseCors();

        app.MapGet("/state", async (MediaService media) =>
        {
            var state = await media.GetStateAsync();
            return Results.Json(state, JsonOptions);
        });

        app.MapGet("/thumb", (MediaService media) =>
        {
            var thumb = media.GetThumbnail();
            if (thumb is null)
                return Results.NotFound();
            return Results.Bytes(thumb.Value.Bytes, thumb.Value.Mime);
        });

        app.MapGet("/control/toggle", async (MediaService media, string? token) =>
            token == ControlToken
                ? Results.Json(new { ok = await media.ToggleAsync() }, JsonOptions)
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/control/next", async (MediaService media, string? token) =>
            token == ControlToken
                ? Results.Json(new { ok = await media.NextAsync() }, JsonOptions)
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/control/prev", async (MediaService media, string? token) =>
            token == ControlToken
                ? Results.Json(new { ok = await media.PreviousAsync() }, JsonOptions)
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/control/seek", async (MediaService media, double pos, string? token) =>
            token == ControlToken
                ? Results.Json(new { ok = await media.SeekAsync(pos) }, JsonOptions)
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet("/health", () => Results.Json(new { ok = true }, JsonOptions));

        Console.WriteLine($"Mikoto media bridge listening on {ListenUrl}");
        app.Run();
    }
}

public sealed record MediaState(
    bool HasSession,
    bool Playing,
    string Status,
    string Title,
    string Artist,
    string Album,
    string MediaType,
    string Source,
    string SourceId,
    double Position,
    double Duration,
    bool CanSeek,
    bool CanNext,
    bool CanPrev,
    string ThumbVersion,
    int ThumbWidth,
    int ThumbHeight,
    bool ThumbUsable
);

public sealed class MediaService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _thumbLock = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private byte[]? _thumb;
    private string _thumbMime = "image/png";
    private string _thumbVersion = "";
    private string _thumbKey = "";
    private int _thumbWidth;
    private int _thumbHeight;
    private bool _thumbUsable;
    private DateTime _thumbLastRead = DateTime.MinValue;

    private string _webArtId = "";
    private DateTime _webArtLast = DateTime.MinValue;

    private string _browserKey = "";
    private string? _browserUrl;
    private string? _browserType;
    private IntPtr _browserWindow;
    private DateTime _browserAt = DateTime.MinValue;

    private string _seekKey = "";
    private double _seekPosition = -1;
    private double _seekDuration;

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return client;
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> ManagerAsync()
    {
        try
        {
            return _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            _manager = null;
            return null;
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> SelectSessionAsync()
    {
        var manager = await ManagerAsync();
        if (manager is null) return null;

        GlobalSystemMediaTransportControlsSession? current = null;
        try { current = manager.GetCurrentSession(); } catch { }

        GlobalSystemMediaTransportControlsSession? session = null;
        if (current is not null && IsPlaying(current))
            session = current;

        if (session is null)
        {
            try { session = manager.GetSessions().FirstOrDefault(IsPlaying); } catch { }
        }

        return session ?? current;
    }

    public async Task<MediaState> GetStateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = await SelectSessionAsync();
            if (session is null)
            {
                ClearThumb();
                _seekKey = "";
                _seekPosition = -1;
                _seekDuration = 0;
                return EmptyState();
            }

            var info = session.GetPlaybackInfo();
            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();

            var title = props?.Title ?? "";
            var source = FriendlySource(session.SourceAppUserModelId);
            var browser = IsBrowser(session.SourceAppUserModelId);

            string? siteType = null;
            string? pageUrl = null;
            var keyChanged = false;

            if (browser)
            {
                var browserKey = (session.SourceAppUserModelId ?? "") + "\u001f" + title;
                keyChanged = browserKey != _browserKey;

                if (keyChanged || (DateTime.UtcNow - _browserAt).TotalSeconds > 10)
                {
                    var ctx = FindBrowserContext(title);
                    _browserKey = browserKey;
                    _browserUrl = ctx?.Url;
                    _browserType = ctx?.Type;
                    _browserWindow = ctx?.Window ?? IntPtr.Zero;
                    _browserAt = DateTime.UtcNow;
                }

                siteType = _browserType;
                pageUrl = _browserUrl;

                if (keyChanged && string.IsNullOrWhiteSpace(pageUrl))
                    ClearThumb();
            }

            var type = ResolveType(props?.PlaybackType, session.SourceAppUserModelId ?? "", siteType);

            if (browser)
            {
                await RefreshWebArtAsync(pageUrl);
            }
            else
            {
                var nativeKey = string.Join('\u001f',
                    session.SourceAppUserModelId,
                    title,
                    props?.Artist ?? "",
                    props?.AlbumTitle ?? "",
                    props?.PlaybackType?.ToString() ?? "");
                await RefreshThumbnailAsync(props?.Thumbnail, nativeKey);
            }

            var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            if (duration <= 0)
                duration = Math.Max(0, timeline.MaxSeekTime.TotalSeconds);
            var position = Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds);

            // Chromium keeps the Windows timeline at ~0 for YouTube playback.
            // Read the real player position from the page's seek slider instead.
            if (browser && _browserWindow != IntPtr.Zero)
            {
                var seek = ReadSeekTimeline(_browserWindow, duration);
                if (seek is not null)
                {
                    position = seek.Value.Position;
                    if (seek.Value.Duration > 0)
                        duration = seek.Value.Duration;
                    _seekKey = _browserKey;
                    _seekPosition = position;
                    _seekDuration = duration;
                }
                else if (_browserKey == _seekKey && _seekPosition >= 0)
                {
                    // A slow UIA read must not snap the wallpaper back to 0.
                    position = _seekPosition;
                    if (_seekDuration > 0)
                        duration = _seekDuration;
                }
            }

            if (duration > 0) position = Math.Min(position, duration);

            var status = info.PlaybackStatus.ToString();
            return new MediaState(
                HasSession: true,
                Playing: info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Status: status,
                Title: title,
                Artist: props?.Artist ?? "",
                Album: props?.AlbumTitle ?? "",
                MediaType: type,
                Source: source,
                SourceId: session.SourceAppUserModelId ?? "",
                Position: Math.Round(position, 3),
                Duration: Math.Round(duration, 3),
                CanSeek: info.Controls.IsPlaybackPositionEnabled,
                CanNext: info.Controls.IsNextEnabled,
                CanPrev: info.Controls.IsPreviousEnabled,
                ThumbVersion: _thumbUsable ? _thumbVersion : "",
                ThumbWidth: _thumbWidth,
                ThumbHeight: _thumbHeight,
                ThumbUsable: _thumbUsable
            );
        }
        catch
        {
            ClearThumb();
            return EmptyState();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ToggleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = await SelectSessionAsync();
            return session is not null && await session.TryTogglePlayPauseAsync();
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public async Task<bool> NextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = await SelectSessionAsync();
            return session is not null && await session.TrySkipNextAsync();
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public async Task<bool> PreviousAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var session = await SelectSessionAsync();
            return session is not null && await session.TrySkipPreviousAsync();
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public async Task<bool> SeekAsync(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return false;
        await _gate.WaitAsync();
        try
        {
            var session = await SelectSessionAsync();
            if (session is null) return false;
            var timeline = session.GetTimelineProperties();
            var start = timeline.StartTime.TotalSeconds;
            var end = timeline.EndTime.TotalSeconds;
            var target = Math.Clamp(seconds, start, end > 0 ? end : seconds);
            return await session.TryChangePlaybackPositionAsync(
                (long)(target * TimeSpan.TicksPerSecond));
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public (byte[] Bytes, string Mime)? GetThumbnail()
    {
        lock (_thumbLock)
        {
            return _thumb is null ? null : (_thumb, _thumbMime);
        }
    }

    /* ---------- browser preview (YouTube) ---------- */

    private async Task RefreshWebArtAsync(string? pageUrl)
    {
        var id = YouTubeId(pageUrl);
        if (string.IsNullOrEmpty(id))
        {
            if (_thumbUsable) ClearThumb();
            _webArtId = "";
            return;
        }

        if (id == _webArtId)
        {
            if (_thumbUsable && _thumb is not null) return;
            if ((DateTime.UtcNow - _webArtLast).TotalSeconds < 25) return;
        }

        byte[]? bytes = null;
        string mime = "image/jpeg";
        try
        {
            foreach (var name in new[] { "maxresdefault.jpg", "hqdefault.jpg", "mqdefault.jpg" })
            {
                using var resp = await Http.GetAsync($"https://i.ytimg.com/vi/{id}/{name}");
                if (!resp.IsSuccessStatusCode) continue;
                var candidate = await resp.Content.ReadAsByteArrayAsync();
                if (candidate.Length is > 0 and < 12 * 1024 * 1024)
                {
                    bytes = candidate;
                    mime = SniffMime(candidate, resp.Content.Headers.ContentType?.MediaType);
                    break;
                }
            }
        }
        catch
        {
            // No network / private video / age wall - use the type placeholder.
        }

        _webArtId = id;
        _webArtLast = DateTime.UtcNow;

        if (bytes is null)
        {
            ClearThumb();
            return;
        }

        var size = ImageSize(bytes);
        lock (_thumbLock)
        {
            _thumb = bytes;
            _thumbMime = mime;
            _thumbVersion = Convert.ToHexString(SHA1.HashData(bytes))[..12].ToLowerInvariant();
            _thumbWidth = size?.Width ?? 0;
            _thumbHeight = size?.Height ?? 0;
            _thumbUsable = true;
        }
    }

    private static string? YouTubeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url,
            @"(?:youtube\.com/(?:watch\?[^#]*v=|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /* ---------- native app artwork ---------- */

    private async Task RefreshThumbnailAsync(IRandomAccessStreamReference? reference, string key)
    {
        var sameTrack = key == _thumbKey;
        if (!sameTrack)
        {
            ClearThumb();
            _thumbKey = key;
        }

        if (reference is null) return;
        var stale = (DateTime.UtcNow - _thumbLastRead).TotalSeconds > 20;
        if (sameTrack && _thumbUsable && _thumb is not null && !stale) return;

        try
        {
            using var stream = await reference.OpenReadAsync();
            var length = stream.Size;
            if (length == 0 || length > 12 * 1024 * 1024) return;

            var bytes = new byte[(int)length];
            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            await reader.LoadAsync((uint)length);
            reader.ReadBytes(bytes);

            var mime = SniffMime(bytes, stream.ContentType);
            var size = ImageSize(bytes);
            lock (_thumbLock)
            {
                _thumb = bytes;
                _thumbMime = mime;
                _thumbVersion = Convert.ToHexString(SHA1.HashData(bytes))[..12].ToLowerInvariant();
                _thumbWidth = size?.Width ?? 0;
                _thumbHeight = size?.Height ?? 0;
                _thumbUsable = true;
            }
            _thumbLastRead = DateTime.UtcNow;
        }
        catch
        {
            // Keep the state usable; the player shows a type placeholder.
        }
    }

    private void ClearThumb()
    {
        lock (_thumbLock)
        {
            _thumb = null;
            _thumbMime = "image/png";
            _thumbVersion = "";
            _thumbWidth = 0;
            _thumbHeight = 0;
            _thumbUsable = false;
        }
        _thumbKey = "";
    }

    /* ---------- detection ---------- */

    private static bool IsBrowser(string? appId)
    {
        var id = (appId ?? "").ToLowerInvariant();
        return new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "chromium", "yandex" }
            .Any(id.Contains);
    }

    private static string ResolveType(MediaPlaybackType? type, string appId, string? siteType)
    {
        if (siteType is "video" or "music" or "image") return siteType;
        if (type == MediaPlaybackType.Video) return "video";
        if (type == MediaPlaybackType.Image) return "image";
        if (type == MediaPlaybackType.Music) return "music";

        var id = (appId ?? "").ToLowerInvariant();
        if (new[] { "vlc", "mpc-", "mpc.", "potplayer", "mpv", "movies", "video", "wmplayer" }
            .Any(id.Contains)) return "video";
        if (new[] { "spotify", "music", "itunes", "aimp", "foobar", "deezer", "tidal", "zune" }
            .Any(id.Contains)) return "music";
        return "unknown";
    }

    private static (double Position, double Duration)? ReadSeekTimeline(IntPtr hWnd, double smtcDuration)
    {
        (double, double)? result = null;
        try
        {
            var thread = new Thread(() =>
            {
                try { result = ReadSeekTimelineCore(hWnd, smtcDuration); }
                catch { }
            })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(900) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static (double Position, double Duration)? ReadSeekTimelineCore(IntPtr hWnd, double smtcDuration)
    {
        var window = AutomationElement.FromHandle(hWnd);
        if (window is null) return null;

        var sliders = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider));

        (double Value, double Maximum)? best = null;
        var bestScore = double.MinValue;

        foreach (AutomationElement slider in sliders)
        {
            try
            {
                if (!slider.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern))
                    continue;

                var range = (RangeValuePattern)pattern;
                var name = (slider.Current.Name ?? "").ToLowerInvariant();
                var maximum = range.Current.Maximum;
                var value = range.Current.Value;
                if (maximum <= 0 || value < 0) continue;

                if (name.Contains("volume") || name.Contains("громк") || name.Contains("vol") ||
                    name.Contains("звук") || name.Contains("mute") || name.Contains("muted"))
                    continue;

                // YouTube exposes an extra "чтобы перемотать видео точнее..."
                // hint as a slider; it is not the real seek control.
                if (name.Length > 64 ||
                    name.Contains("нажмите") || name.Contains("прокрутите") ||
                    name.Contains("точнее") || name.Contains("click") ||
                    name.Contains("drag") || name.Contains("scroll"))
                    continue;

                var seekNamed = name.Contains("seek") || name.Contains("перемот") ||
                                name.Contains("progress") || name.Contains("timeline") ||
                                name.Contains("position") || name.Contains("позици") ||
                                name.Contains("прогресс") || name.Contains("поиск") ||
                                name.Contains("search");

                var score = seekNamed ? 5000.0 : 0.0;
                if (!seekNamed && (name.Contains("смотреть") || name.Contains("watch")))
                    score -= 2000;
                if (smtcDuration > 0)
                {
                    var diff = Math.Abs(maximum - smtcDuration);
                    if (diff <= 2.5) score += 3000;
                    else if (maximum > 120) score += 1000;
                }
                // A likely volume slider (0..100 and at max) when its localized
                // name is not recognized.
                if (Math.Abs(maximum - 100) < 0.01 && Math.Abs(value - 100) < 0.01)
                    score -= 1500;
                score += Math.Min(maximum, 3600) * 0.01;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = (value, maximum);
                }
            }
            catch
            {
                // Stale element or an inaccessible provider.
            }
        }

        if (best is null) return null;
        var (sliderValue, sliderMaximum) = best.Value;

        // Some players expose milliseconds instead of seconds.
        if (smtcDuration > 0 && sliderMaximum > smtcDuration * 10 && sliderMaximum > 600)
        {
            sliderValue /= 1000;
            sliderMaximum /= 1000;
        }

        double duration;
        double position;
        if (smtcDuration > 0 && Math.Abs(sliderMaximum - smtcDuration) <= 2.5)
        {
            position = sliderValue;
            duration = sliderMaximum;
        }
        else if (sliderMaximum > 120)
        {
            position = sliderValue;
            duration = sliderMaximum;
        }
        else if (sliderMaximum <= 1.001)
        {
            duration = smtcDuration;
            position = smtcDuration > 0 ? sliderValue * smtcDuration : sliderValue;
        }
        else if (smtcDuration > 0 && Math.Abs(sliderMaximum - 100) < 0.01)
        {
            duration = smtcDuration;
            position = sliderValue / 100.0 * smtcDuration;
        }
        else
        {
            position = sliderValue;
            duration = sliderMaximum > 0 ? sliderMaximum : smtcDuration;
        }

        if (duration <= 0) duration = smtcDuration;
        if (position < 0) position = 0;
        if (duration > 0) position = Math.Min(position, duration);
        return (position, duration);
    }

    private sealed record BrowserContext(string Type, string? Url, IntPtr Window);

    private static BrowserContext? FindBrowserContext(string mediaTitle)
    {
        if (string.IsNullOrWhiteSpace(mediaTitle)) return null;
        var needle = mediaTitle.Trim();
        if (needle.Length > 40) needle = needle[..40];

        foreach (var window in GetWindows())
        {
            if (!window.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            var type = SiteTypeFromTitle(window.Title);
            if (type is null) continue;
            var url = BrowserUrlFromWindow(window.Handle);
            return new BrowserContext(type, url, window.Handle);
        }
        return null;
    }

    private static string? SiteTypeFromTitle(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("youtube music") || t.Contains("soundcloud") ||
            t.Contains("spotify") || t.Contains("deezer") || t.Contains("apple music") ||
            t.Contains("bandcamp") || t.Contains("yandex music") || t.Contains("music.yandex") ||
            t.Contains("tidal"))
            return "music";
        if (t.Contains("youtube") || t.Contains("netflix") || t.Contains("vimeo") ||
            t.Contains("twitch") || t.Contains("rutube") || t.Contains("кинопоиск") ||
            t.Contains("prime video") || t.Contains("disney+") || t.Contains("hulu") ||
            t.Contains("crunchyroll") || t.Contains("dailymotion") || t.Contains("ivi") ||
            t.Contains("okko") || t.Contains("more.tv") || t.Contains("wink"))
            return "video";
        return null;
    }

    private static string? BrowserUrlFromWindow(IntPtr hWnd)
    {
        try
        {
            string? result = null;
            var thread = new Thread(() => { result = ReadAddressBar(hWnd); })
            {
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread.Join(1800) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadAddressBar(IntPtr hWnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(hWnd);
            if (window is null) return null;

            var names = new[]
            {
                "Address and search bar",
                "Search or enter address",
                "Address bar",
                "Adress- und Suchleiste",
                "Адресная строка и строка поиска",
                "Адресная строка"
            };

            foreach (var name in names)
            {
                var condition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, name));
                var edit = window.FindFirst(TreeScope.Descendants, condition);
                if (edit is not null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                    return ((ValuePattern)pattern).Current.Value;
            }

            var edits = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var seen = 0;
            foreach (AutomationElement edit in edits)
            {
                if (++seen > 60) break;
                var name = edit.Current.Name ?? "";
                if (!name.Contains("address", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("адрес", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                    return ((ValuePattern)p).Current.Value;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct WindowInfo(IntPtr Handle, string Title);

    private static List<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                if (GetWindowText(hWnd, sb, sb.Capacity) > 0)
                    windows.Add(new WindowInfo(hWnd, sb.ToString()));
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Window titles are only a hint; never fail media state for them.
        }
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /* ---------- image helpers ---------- */

    private static string FriendlySource(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        var s = appId;
        var bang = s.LastIndexOf('!');
        if (bang >= 0 && bang + 1 < s.Length) s = s[(bang + 1)..];
        s = s.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        var dot = s.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < s.Length && !s.Contains(' ')) s = s[(dot + 1)..];
        return s;
    }

    private static string SniffMime(byte[] b, string? reported)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
        if (!string.IsNullOrWhiteSpace(reported) && reported.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return reported;
        return "image/png";
    }

    private static (int Width, int Height)? ImageSize(byte[] b)
    {
        try
        {
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            {
                var w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                var h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return (w, h);
            }

            if (b.Length >= 10 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
            {
                var w = b[6] | (b[7] << 8);
                var h = b[8] | (b[9] << 8);
                return (w, h);
            }

            if (b.Length >= 26 && b[0] == 0x42 && b[1] == 0x4D)
            {
                var w = BitConverter.ToInt32(b, 18);
                var h = Math.Abs(BitConverter.ToInt32(b, 22));
                return (w, h);
            }

            if (b.Length >= 30 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50 &&
                b[12] == 0x56 && b[13] == 0x50 && b[14] == 0x38 && b[15] == 0x58)
            {
                var w = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
                var h = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
                return (w, h);
            }

            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    while (i < b.Length && b[i] == 0xFF) i++;
                    if (i >= b.Length) break;
                    var marker = b[i++];
                    if (marker == 0xD8 || marker == 0xD9) continue;
                    if (marker == 0xDA) break;
                    if (i + 1 >= b.Length) break;
                    var len = (b[i] << 8) | b[i + 1];
                    if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        if (i + 6 >= b.Length) break;
                        var h = (b[i + 3] << 8) | b[i + 4];
                        var w = (b[i + 5] << 8) | b[i + 6];
                        return (w, h);
                    }
                    if (len < 2) break;
                    i += len;
                }
            }
        }
        catch
        {
            // Not fatal: the UI simply will not get a size hint.
        }
        return null;
    }

    private static MediaState EmptyState() => new(
        HasSession: false,
        Playing: false,
        Status: "Closed",
        Title: "",
        Artist: "",
        Album: "",
        MediaType: "unknown",
        Source: "",
        SourceId: "",
        Position: 0,
        Duration: 0,
        CanSeek: false,
        CanNext: false,
        CanPrev: false,
        ThumbVersion: "",
        ThumbWidth: 0,
        ThumbHeight: 0,
        ThumbUsable: false
    );
}
