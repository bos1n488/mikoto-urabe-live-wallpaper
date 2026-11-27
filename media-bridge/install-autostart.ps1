$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$launcher = Join-Path $PSScriptRoot 'start-hidden.vbs'
$command = '"' + (Join-Path $env:WINDIR 'System32\wscript.exe') + '" "' + $launcher + '"'
New-ItemProperty -Path $key -Name 'MikotoMediaBridge' -Value $command -PropertyType String -Force | Out-Null
Write-Host "Autostart installed (HKCU Run): $command"
