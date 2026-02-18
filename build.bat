@echo off
setlocal

echo Stopping any running taskmon instance...
call "%~dp0kill.bat"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo ERROR: .NET Framework 4 compiler not found.
    echo Expected: %CSC%
    pause
    exit /b 1
)

set OUT=%LOCALAPPDATA%\taskmon
if not exist "%OUT%" mkdir "%OUT%"
set DLL=%OUT%\taskmon.dll

echo Building taskmon.cs ...
"%CSC%" ^
    /nologo ^
    /target:library ^
    /optimize+ ^
    /out:"%DLL%" ^
    /r:System.Windows.Forms.dll ^
    /r:System.Drawing.dll ^
    /r:System.Core.dll ^
    /r:System.dll ^
    "%~dp0taskmon.cs"

if %errorlevel% neq 0 (
    echo.
    echo Build FAILED. See errors above.
    pause
    exit /b 1
)

echo.
echo Build succeeded: %DLL%
echo You can now launch taskmon via taskmon.vbs
