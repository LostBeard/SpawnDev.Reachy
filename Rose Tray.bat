@echo off
REM Rose as a tray icon - no terminal, just a coloured dot in the notification area.
REM Right-click it to start/stop her, pick a character, or turn on "Start with Windows".
REM This launcher opens her already talking (--start); the app starts Ollama itself.
cd /d "%~dp0"
start "" dotnet run -c Release --project SpawnDev.Reachy.Rose -- --tray --start
exit
