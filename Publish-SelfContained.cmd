@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK is not installed. Run Build-AppShelf.cmd instead.
  echo Download .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
dotnet publish AppShelf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o publish
if errorlevel 1 (
  echo PUBLISH FAILED.
  pause
  exit /b 1
)
echo Created self-contained: %~dp0publish\AppShelf.exe
pause
