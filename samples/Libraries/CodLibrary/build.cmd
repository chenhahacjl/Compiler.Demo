@echo off
setlocal
set ROOT=%~dp0
cd /d "%ROOT%"

echo === 1. Build library (.cod semantic assembly) ===
cocoa build -p mylib\MyLib.coproj
if errorlevel 1 exit /b 1

echo.
echo === 2. Build app - native backend ===
cocoa build -p app\App.coproj -b native
if errorlevel 1 exit /b 1
app\out\App.exe

echo.
echo === 3. Build app - dotnet backend (netfx, runs directly) ===
cocoa build -p app\App.coproj -b dotnet
if errorlevel 1 exit /b 1
app\out\App.exe

endlocal
