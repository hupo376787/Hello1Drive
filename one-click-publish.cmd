@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-publish.ps1" %*
if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)
echo.
echo Publish completed.
pause
