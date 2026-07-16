@echo off
setlocal

set "ROOT=%~dp0.."
set "DIST=%ROOT%\dist"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo C# compiler not found: %CSC%
  exit /b 1
)

if not exist "%DIST%" mkdir "%DIST%"
if not exist "%DIST%\assets" mkdir "%DIST%\assets"

"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /debug- /out:"%DIST%\CodexEcamMonitor.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%ROOT%\src\Program.cs"
if errorlevel 1 exit /b %ERRORLEVEL%

copy /y "%ROOT%\assets\ECAMFontRegular.ttf" "%DIST%\assets\ECAMFontRegular.ttf" >nul
echo Built %DIST%\CodexEcamMonitor.exe
exit /b 0
