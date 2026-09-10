@echo off
"%~dp0AgentStatusEmit.exe" --source %~1
<nul set /p "={}"
exit /b 0
