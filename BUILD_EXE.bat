@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo ==================================================
echo ELB-DockTool v1.2.0 - Integrated Interpretation Build
echo ==================================================
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK is required.
  echo Install the .NET 8 SDK, then run this file again.
  pause
  exit /b 1
)

echo Restoring NuGet packages...
dotnet restore ELB_DockTool.csproj
if errorlevel 1 goto fail

echo Publishing folder-based Windows x64 build...
if exist publish rmdir /s /q publish
dotnet publish ELB_DockTool.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=false /p:PublishTrimmed=false /p:PublishReadyToRun=false -o publish
if errorlevel 1 goto fail

copy /y dist\vina.exe publish\vina.exe >nul
copy /y dist\vina_split.exe publish\vina_split.exe >nul
if errorlevel 1 goto fail

if not exist publish\InterpretationViewer\index.html goto fail

echo.
echo BUILD COMPLETE
echo Runnable application folder: publish\
echo Main program: publish\ELB_DockTool.exe
echo.
echo Keep the complete publish folder together.
pause
exit /b 0

:fail
echo.
echo BUILD FAILED
echo Check the messages above.
pause
exit /b 1
