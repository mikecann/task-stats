@echo off
call "%~dp0build.bat"
if %errorlevel% neq 0 (
    echo Build failed - task-stats not launched.
    pause
    exit /b 1
)

echo.
echo Launching task-stats...
start "" wscript.exe "%~dp0task-stats.vbs"
