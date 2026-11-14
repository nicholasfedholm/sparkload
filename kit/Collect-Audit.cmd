@echo off
rem Double-click wrapper: packs the newest audit run into one zip. Extra arguments are passed through.
setlocal EnableExtensions
title Collect VS audit
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Audit.ps1" -OpenFolder %*
echo.
pause
endlocal
