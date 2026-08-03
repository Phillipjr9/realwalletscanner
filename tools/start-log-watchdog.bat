@echo off
setlocal
set SCRIPT=%~dp0log_monitor_watchdog.ps1
powershell -ExecutionPolicy Bypass -File "%SCRIPT%" -IntervalMinutes 5
