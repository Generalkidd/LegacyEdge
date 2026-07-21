@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "legacyEdgeExitCode=%ERRORLEVEL%"
if not "%legacyEdgeExitCode%"=="0" pause
exit /b %legacyEdgeExitCode%
