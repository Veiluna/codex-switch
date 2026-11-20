@echo off
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Window.ps1" %*
exit /b %errorlevel%
