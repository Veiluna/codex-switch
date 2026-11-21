@echo off
if exist "%~dp0codex-switch.exe" goto installed
if exist "%~dp0dist\codex-switch.exe" goto built
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0switch-account.ps1" %*
exit /b %errorlevel%
:installed
"%~dp0codex-switch.exe" %*
exit /b %errorlevel%
:built
"%~dp0dist\codex-switch.exe" %*
exit /b %errorlevel%
