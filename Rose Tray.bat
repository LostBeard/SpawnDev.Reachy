@echo off
REM Rose as a tray icon - no terminal, just a coloured dot in the notification area.
REM Right-click it to start/stop her, pick a character, or turn on "Start with Windows".
REM This launcher opens her already talking (--start); the app starts Ollama itself and
REM pins its own tray icon so it is not buried in the Windows 11 overflow.
cd /d "%~dp0"
set "EXE=SpawnDev.Reachy.Rose\bin\Release\net10.0-windows\SpawnDev.Reachy.Rose.exe"
if exist "%EXE%" (
  start "" "%EXE%" --tray --start
) else (
  REM Not built yet - build and run once; next time the exe launches directly.
  start "" dotnet run -c Release --project SpawnDev.Reachy.Rose -- --tray --start
)
exit
