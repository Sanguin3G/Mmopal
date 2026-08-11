@echo off
setlocal EnableDelayedExpansion

:: Define lock file path
set "LOCKFILE=%TEMP%\gw2_launch.lock"

:: Check if lock file exists
if exist "%LOCKFILE%" (
    echo Script is already running. Exiting...
    timeout /t 2 /nobreak >nul
    exit /b
)

:: Create lock file
echo %date% %time% > "%LOCKFILE%"

echo Stopping any running Guild Wars 2 processes...
taskkill /F /IM Gw2-64.exe >nul 2>&1
taskkill /F /IM Gw2.exe >nul 2>&1
taskkill /F /IM GameOverlayUI.exe >nul 2>&1

timeout /t 2 /nobreak >nul

echo Launching Guild Wars 2...
start "" "D:\Entertainment\Steam\steam.exe" -applaunch 1284210

:: Wait for GW2 to actually start
echo Waiting for game to launch...
set /a counter=0
:wait_loop
timeout /t 1 /nobreak >nul
tasklist /FI "IMAGENAME eq Gw2-64.exe" 2>NUL | find /I /N "Gw2-64.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo Game launched successfully!
    goto cleanup
)
set /a counter+=1
if %counter% LSS 30 goto wait_loop

echo Timeout waiting for game to start, but continuing...

:cleanup
:: Remove lock file
del "%LOCKFILE%" >nul 2>&1

timeout /t 2 /nobreak >nul
exit