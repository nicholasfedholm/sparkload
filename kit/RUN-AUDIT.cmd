@echo off
rem SparkLoad audit: starts Vintage Story under the load trace, watches memory, waits for you to close the game,
rem then packs one zip to send. Extra arguments are passed on to "sparkload kit" (used for testing).
setlocal EnableExtensions
title SparkLoad audit
set "KIT=%~dp0"
if "%KIT:~-1%"=="\" set "KIT=%KIT:~0,-1%"
set "SL=%KIT%\sparkload\sparkload.exe"
set "SX=%KIT%\sparkx\sparkx.exe"
set "PS=powershell -NoProfile -ExecutionPolicy Bypass"

echo.
echo  SparkLoad audit
echo  ---------------
if not exist "%SL%" (
  echo  The sparkload folder is missing next to this file. Please unzip the whole kit again.
  goto :end
)

for /f "usebackq delims=" %%i in (`%PS% -Command "Get-Date -Format yyyyMMdd-HHmmss"`) do set "STAMP=%%i"
set "RUNID=kit-%STAMP%"
set "RUNDIR="
set "RDF=%TEMP%\sparkload-rundir-%RUNID%.txt"
if exist "%RDF%" del "%RDF%"
start "" /wait /b "%SL%" kit --run-dir-file "%RDF%" --run-id %RUNID% %*
if exist "%RDF%" set /p RUNDIR=<"%RDF%"
if exist "%RDF%" del "%RDF%"
if not defined RUNDIR (
  echo  Could not work out where to save the results. Please check portable-kit.json in the sparkload folder.
  goto :end
)

rem Background helper: waits until the load trace is finished, then watches memory until the game closes.
rem It never blocks the game and ends by itself when the game exits.
if exist "%SX%" (
  start "" /b %PS% -File "%KIT%\sparkload\Watch-Sparkx.ps1" -RunDir "%RUNDIR%" -SparkxExe "%SX%" >nul 2>&1
)

echo  Starting Vintage Story. Pick your world in the menu as usual.
echo  When it has loaded, stand still for about 5 minutes (you may grind a quern a bit),
echo  then close the game normally. This window finishes by itself.
echo.
start "" /wait /b "%SL%" kit --run-id %RUNID% %*
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="3" goto :end
if "%RC%"=="2" (
  echo  The audit could not start. Please read the message above.
  goto :end
)
if "%RC%"=="4" (
  echo  The game could not be started. Please read the message above.
  goto :end
)
if not "%RC%"=="0" echo  The run ended with code %RC%. The results below may be incomplete, that is fine, send them anyway.

echo  Packing the results ...
%PS% -File "%KIT%\Collect-Audit.ps1" -RunDir "%RUNDIR%" -WaitForSparkx -OpenFolder

:end
echo.
pause
endlocal
