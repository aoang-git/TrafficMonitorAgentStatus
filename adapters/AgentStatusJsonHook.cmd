@echo off
"%~dp0AgentStatusEmit.exe" --source %~1
exit /b %errorlevel%
