@echo off
setlocal

echo Stopping any running task-stats instance...
call "%~dp0kill.bat"

set MSBUILD=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe
if not exist "%MSBUILD%" (
    echo ERROR: MSBuild not found.
    echo Expected: %MSBUILD%
    pause
    exit /b 1
)

echo Building task-stats.csproj ...
"%MSBUILD%" "%~dp0task-stats.csproj" /nologo /v:minimal /p:Configuration=Release

if %errorlevel% neq 0 (
    echo.
    echo Build FAILED. See errors above.
    pause
    exit /b 1
)

echo.
echo Build succeeded: %LOCALAPPDATA%\task-stats\task-stats.dll
echo You can now launch task-stats via task-stats.vbs
