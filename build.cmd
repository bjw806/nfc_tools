@echo off
rem Double-click entry point: runs build.ps1 without changing the system execution policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set BUILD_EXIT=%ERRORLEVEL%
echo.
pause
exit /b %BUILD_EXIT%
