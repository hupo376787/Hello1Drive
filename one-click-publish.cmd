@echo off
setlocal
cd /d "%~dp0"

rem Windows entry point. The actual publish logic lives in scripts\one-click-publish.ps1.
rem If a target is passed on the command line, forward it directly.
if not "%~1"=="" goto run_args

echo.
echo Hello1Drive Publish
echo ==================
echo [1] Windows x64          ^(recommended for normal Windows PCs^)
echo [2] Windows x64 + ARM64
echo [3] All Desktop RIDs     ^(Windows / Linux / macOS^)
echo [4] Android
echo [5] Browser
echo [6] All targets
echo [0] Cancel
echo.
set /p "CHOICE=Select target: "

if "%CHOICE%"=="1" set "PUBLISH_ARGS=-Target win-x64"
if "%CHOICE%"=="2" set "PUBLISH_ARGS=-Target windows"
if "%CHOICE%"=="3" set "PUBLISH_ARGS=-Target desktop"
if "%CHOICE%"=="4" set "PUBLISH_ARGS=-Target android"
if "%CHOICE%"=="5" set "PUBLISH_ARGS=-Target browser"
if "%CHOICE%"=="6" set "PUBLISH_ARGS=-Target all"
if "%CHOICE%"=="0" exit /b 0
if not defined PUBLISH_ARGS (
  echo Invalid selection.
  pause
  exit /b 2
)
goto run_selected

:run_args
set "PUBLISH_ARGS=%*"

:run_selected
where pwsh >nul 2>&1
if %errorlevel%==0 (
  pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-publish.ps1" %PUBLISH_ARGS%
) else (
  powershell -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\one-click-publish.ps1" %PUBLISH_ARGS%
)

if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)

echo.
echo Publish completed.
echo Output: %~dp0artifacts
echo.
pause
