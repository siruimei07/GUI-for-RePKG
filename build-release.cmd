@echo off
setlocal
if "%~1"=="" (
  echo Usage: build-release.cmd ^<OutputDirectory^> [-UpdateTrackedExecutable]
  exit /b 2
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" %*
set "buildExitCode=%ERRORLEVEL%"
endlocal & exit /b %buildExitCode%
