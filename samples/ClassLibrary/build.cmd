@echo off
setlocal
set ROOT=%~dp0
cd /d "%ROOT%"

coc build -p mylib\MyLib.coproj -b dotnet
if errorlevel 1 exit /b 1

coc build -p app\App.coproj -b dotnet
if errorlevel 1 exit /b 1

echo.
echo Run: app\out\App.exe
endlocal
