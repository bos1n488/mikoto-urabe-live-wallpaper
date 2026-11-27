# MIKOTO URABE — live wallpaper без Wallpaper Engine

Браузерная реконструкция сцены MIKOTO URABE (Nazo No Kanojo X) плюс
локальный Windows media bridge. Wallpaper Engine не нужен.

## Что умеет

- фон 3840×2160, дождь, живое время/дата, свечение и виньетка;
- интеграция с медиа-сеансом Windows (Spotify, VLC, Media Player, Chrome, Edge);
- название, исполнитель, обложка/превью, тип «музыка/видео», прогресс и пауза;
- для YouTube/YouTube Music превью ролика подтягивается по ссылке;
- полоса и время показывают реальную позицию: для браузера читается ползунок
  плеера через UI Automation, для нативных приложений — системный таймлайн;
- клик по обложке — пауза/плей, клик по полосе — перемотка;
- работает как обычная страница в браузере и как обои в Lively Wallpaper.

## Быстрый запуск

1. `wallpaper\start.bat` — запускает media-bridge и открывает обои в браузере.
2. `F` — на весь экран, `Space` — пауза.

Нужен установленный **.NET 8** (ASP.NET Core + Windows Desktop Runtime).
Готовая сборка моста лежит в `media-bridge\app`.

## Lively Wallpaper

1. Установите бесплатный [Lively Wallpaper](https://github.com/rocksdanister/lively).
2. **Add Wallpaper** → выберите `wallpaper/mikoto-urabe.html`.
3. Мост должен быть запущен. Автозапуск:
   `powershell -ExecutionPolicy Bypass -File media-bridge\install-autostart.ps1`.
   Убрать: `media-bridge\remove-autostart.bat`.

Lively открывает локальную страницу под `https://<id>.localhost`; мост разрешает
локальные `.localhost` origins.

## Структура

| Путь | Назначение |
|---|---|
| `wallpaper/` | обои: `index.html`, `mikoto-urabe.html`, фон, шрифты, постер, инструкция |
| `media-bridge/` | локальный медиа-мост: исходник, сборка `app`, скрипты запуска |
| `scene.pkg`, `shaders/`, `project.json`, `preview.jpg` | оригинальные файлы обоев |
| `EN.pdf`, `RU.pdf` | инструкции Wallpaper Engine из архива |

Подробности по обоям — в `wallpaper/README.md`.

## Сборка моста

```powershell
dotnet publish media-bridge\MikotoMediaBridge.csproj -c Release -r win-x64 `
  --self-contained false -o media-bridge\app
```

Мост слушает только `127.0.0.1:47823`; команды защищены локальным токеном,
удалённые origin-ы получают 403.

## Оригинал и права

Оригинальные обои: **MIKOTO URABE** (Nazo No Kanojo X), Steam Workshop
[`3491127388`](https://steamcommunity.com/sharedfiles/filedetails/?id=3491127388),
автор **negro_nazi**. Все художественные материалы принадлежат автору
оригинала. Репозиторий задуман как личная реконструкция; перед публикацией
проверьте права на исходные ассеты.
