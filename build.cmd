@echo off
rem Double-click to run build.ps1 without changing the execution policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set BUILD_EXIT=%ERRORLEVEL%
echo.
pause
exit /b %BUILD_EXIT%
