@echo off
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v MikotoMediaBridge /f 2>nul
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Mikoto Media Bridge.lnk" 2>nul
del "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Mikoto Media Bridge.vbs" 2>nul
echo Autostart removed.
pause
