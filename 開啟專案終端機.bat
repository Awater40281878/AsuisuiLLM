@echo off
set "PROJECT_DIR=%~dp0"
start "" powershell.exe -NoExit -NoProfile -Command "Set-Location -LiteralPath '%PROJECT_DIR%'"
