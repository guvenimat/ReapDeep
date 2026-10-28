@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-local.ps1"
echo.
echo Press any key to close this window...
pause >nul
