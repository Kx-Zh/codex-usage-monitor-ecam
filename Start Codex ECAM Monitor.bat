@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0CodexEcamMonitor.exe" (
  start "" "%~dp0CodexEcamMonitor.exe"
  exit /b 0
)

if exist "%~dp0dist\CodexEcamMonitor.exe" (
  start "" "%~dp0dist\CodexEcamMonitor.exe"
  exit /b 0
)

echo CodexEcamMonitor.exe was not found.
echo Download a release package or run scripts\build.cmd first.
pause
exit /b 1
