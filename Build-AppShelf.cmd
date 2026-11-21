@echo off
setlocal
cd /d "%~dp0"
set "NO_START=0"
set "NO_PAUSE=0"
:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--no-start" set "NO_START=1"
if /i "%~1"=="--no-pause" set "NO_PAUSE=1"
shift
goto parse_args
:args_done
set "DOTNETDIR=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%DOTNETDIR%\csc.exe" set "DOTNETDIR=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
set "CSC=%DOTNETDIR%\csc.exe"
if not exist "%CSC%" (
  echo ERROR: Windows .NET Framework compiler was not found.
  echo Install .NET Framework 4.8 or use the .NET 8 SDK to publish the WPF project.
  if not "%NO_PAUSE%"=="1" pause
  exit /b 1
)
if not exist "%DOTNETDIR%\WPF\PresentationFramework.dll" (
  echo ERROR: WPF runtime assemblies not found: %DOTNETDIR%\WPF
  echo Check whether .NET Framework WPF is installed on Windows.
  if not "%NO_PAUSE%"=="1" pause
  exit /b 1
)
echo Building AppShelf.exe (C# / WPF / Windows .NET Framework)...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"AppShelf.exe" /win32icon:"Assets\AppShelf.ico" /resource:"Assets\AppShelf.png",AppShelf.Brand.png /reference:"%DOTNETDIR%\System.dll" /reference:"%DOTNETDIR%\System.Core.dll" /reference:"%DOTNETDIR%\System.Runtime.Serialization.dll" /reference:"%DOTNETDIR%\System.Windows.Forms.dll" /reference:"%DOTNETDIR%\System.Drawing.dll" /reference:"%DOTNETDIR%\System.IO.Compression.dll" /reference:"%DOTNETDIR%\System.Xaml.dll" /reference:"%DOTNETDIR%\WPF\WindowsBase.dll" /reference:"%DOTNETDIR%\WPF\PresentationCore.dll" /reference:"%DOTNETDIR%\WPF\PresentationFramework.dll" "AppShelf.cs" "AppShelfExtras.cs" "FluentUi.cs" "Reminders.cs" "WeatherProviders.cs" "BackupManager.cs" "VersionInfo.cs" "SiteLinks.cs" "WindowsIntegration.cs" "Collections.cs" "CollectionsWindow.cs"
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Please copy the compiler output to the developer.
  if not "%NO_PAUSE%"=="1" pause
  exit /b 1
)
echo.
echo Done: %~dp0AppShelf.exe
if not "%NO_START%"=="1" start "" "%~dp0AppShelf.exe"
exit /b 0
