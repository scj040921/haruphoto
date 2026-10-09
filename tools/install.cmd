@echo off
setlocal
set "HARUPHOTO_INSTALL_SCRIPT=%~dp0install.ps1"
rem Explicit UTF-8 decoding supports Windows PowerShell 5.1 on Chinese Windows.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create([IO.File]::ReadAllText($env:HARUPHOTO_INSTALL_SCRIPT,[Text.Encoding]::UTF8))) %*"
if errorlevel 1 (
  echo.
  echo Installation failed. Please send a screenshot of the error above.
  pause
  exit /b 1
)
echo.
echo Installation completed.
pause
