@echo off
rem task-49 / W2-Y: build the KKZ adapter (x86). Real logic lives in build.ps1.
rem Usage: build.cmd [build.ps1 args...]      e.g.  build.cmd -EnableTts -PollMs 150
rem Note: /EHa is fixed inside build.ps1 (task-46: without it a TJS exception kills the game).
setlocal
where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
    exit /b %ERRORLEVEL%
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
