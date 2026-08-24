@echo off
setlocal
cd /d "%~dp0"

set "VENV_DIR=.venv"
set "VENV_PYTHON=%VENV_DIR%\Scripts\python.exe"
set "VENV_ACTIVATE=%VENV_DIR%\Scripts\activate.bat"
set "NEW_VENV=0"

if exist "%VENV_PYTHON%" goto activate

echo.
echo [Hello1Drive Server] Creating Python virtual environment...

where py >nul 2>&1
if %errorlevel%==0 (
    py -3 -m venv "%VENV_DIR%"
) else (
    where python >nul 2>&1
    if errorlevel 1 (
        echo ERROR: Python was not found. Please install Python 3 and add it to PATH.
        pause
        exit /b 1
    )
    python -m venv "%VENV_DIR%"
)

if errorlevel 1 (
    echo ERROR: Failed to create virtual environment.
    pause
    exit /b 1
)

set "NEW_VENV=1"

:activate
if not exist "%VENV_ACTIVATE%" (
    echo ERROR: Virtual environment is incomplete: %VENV_DIR%
    pause
    exit /b 1
)

call "%VENV_ACTIVATE%"
if errorlevel 1 (
    echo ERROR: Failed to activate virtual environment.
    pause
    exit /b 1
)

if "%NEW_VENV%"=="1" (
    if exist "requirements.txt" (
        echo.
        echo [Hello1Drive Server] Installing dependencies...
        python -m pip install --upgrade pip
        if errorlevel 1 goto install_failed
        python -m pip install -r requirements.txt
        if errorlevel 1 goto install_failed
    ) else (
        echo.
        echo [Hello1Drive Server] requirements.txt was not found. Skipping dependency installation.
    )
)

echo.
echo [Hello1Drive Server] Starting server...
echo.

rem Prefer an explicit server launcher when present, then fall back to common Python entry names.
if exist "start_server.py" (
    python start_server.py %*
    goto server_finished
)
if exist "server.py" (
    python server.py %*
    goto server_finished
)
if exist "main.py" (
    python main.py %*
    goto server_finished
)
if exist "app.py" (
    python app.py %*
    goto server_finished
)

echo ERROR: No server entry point was found.
echo Expected one of: start_server.py, server.py, main.py, app.py
pause
exit /b 1

:install_failed
echo.
echo ERROR: Failed to install Python dependencies.
pause
exit /b 1

:server_finished
set "SERVER_EXIT=%errorlevel%"
if not "%SERVER_EXIT%"=="0" (
    echo.
    echo Server exited with code %SERVER_EXIT%.
    pause
)
exit /b %SERVER_EXIT%
