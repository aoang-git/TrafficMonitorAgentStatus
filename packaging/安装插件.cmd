@echo off
setlocal
echo Please enter the folder containing TrafficMonitor.exe:
set /p "TMAS_TARGET=> "
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -TrafficMonitorDirectory "%TMAS_TARGET%"
pause
