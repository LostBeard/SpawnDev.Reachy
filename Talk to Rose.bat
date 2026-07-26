@echo off
title Rose - talking to Aubs
cd /d "%~dp0"

echo ============================================
echo   Rose - Aubs's Murder Drones companion
echo ============================================
echo.

REM --- Her brain (the language model) must be running first. This was the
REM     thing that silently stopped her talking: the robot connected fine but
REM     Ollama was not serving. Start it if it is not already up.
curl -s http://localhost:11434/api/tags >nul 2>&1
if errorlevel 1 (
  echo Starting Rose's brain ^(Ollama^)...
  start "" /min "%LOCALAPPDATA%\Programs\Ollama\ollama.exe" serve
:waitbrain
  timeout /t 1 /nobreak >nul
  curl -s http://localhost:11434/api/tags >nul 2>&1
  if errorlevel 1 goto waitbrain
)
echo Brain is up.
echo.

echo Waking Rose and connecting to Aubs...
echo ^(She will say hi in N's voice, then listen. Just talk to her.^)
echo Close this window or press Ctrl+C to put her to sleep.
echo.

dotnet run -c Release --project SpawnDev.Reachy.Rose -- --talk --clone

echo.
echo Rose is asleep. Double-click this file again any time.
pause
