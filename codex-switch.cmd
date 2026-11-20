@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0switch-account.ps1" %*
exit /b %errorlevel%
