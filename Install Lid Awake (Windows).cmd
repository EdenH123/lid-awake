@echo off
if not exist "%~dp0windows\install.ps1" (echo Please unpack the whole ZIP first, then open this file again. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0windows\install.ps1" %*
set "rc=%errorlevel%"
if not "%rc%"=="0" pause
exit /b %rc%
