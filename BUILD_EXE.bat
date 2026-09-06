@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ================================================
echo ELB DockTool v1.2.0 - Build
 echo ================================================
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK is required.
  echo Install the .NET 8 SDK and run this file again.
  pause
  exit /b 1
)

echo Restoring project...
dotnet restore ELB_DockTool.csproj
if errorlevel 1 goto fail

echo Publishing self-contained Windows x64 EXE...
dotnet publish ELB_DockTool.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false -o publish
if errorlevel 1 goto fail

echo.
echo BUILD COMPLETE
echo EXE: publish\ELB_DockTool.exe
pause
exit /b 0

:fail
echo.
echo BUILD FAILED
pause
exit /b 1
