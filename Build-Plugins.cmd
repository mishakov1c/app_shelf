@echo off
setlocal
cd /d "%~dp0" || exit /b 1
set "NO_OPEN=0"
set "NO_PAUSE=0"
:parse_args
if "%~1"=="" goto build
if /i "%~1"=="--no-open" (
  set "NO_OPEN=1"
  shift
  goto parse_args
)
if /i "%~1"=="--no-pause" (
  set "NO_PAUSE=1"
  shift
  goto parse_args
)
echo ERROR: Unknown argument "%~1"
exit /b 1

:build
set "OUTPUT=%~dp0artifacts\plugins"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" goto failed
set "FOUND=0"
for /d %%D in ("%~dp0samples\*") do (
  if exist "%%~fD\plugin.json" if exist "%%~fD\Build.ps1" (
    set "FOUND=1"
    echo Building %%~nxD...
    "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%%~fD\Build.ps1" -OutputDirectory "%OUTPUT%"
    if errorlevel 1 goto failed
  )
)
if "%FOUND%"=="0" (
  echo ERROR: No plugin projects found.
  goto failed
)
echo.
echo Done: "%OUTPUT%"
if "%NO_OPEN%"=="0" start "" "%OUTPUT%"
if "%NO_PAUSE%"=="0" pause
exit /b 0

:failed
echo.
echo PLUGIN BUILD FAILED. Previously built packages may still be present.
if "%NO_PAUSE%"=="0" pause
exit /b 1
