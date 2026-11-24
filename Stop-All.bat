@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop-All.ps1"
exit /b %errorlevel%
