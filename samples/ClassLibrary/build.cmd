@echo off
setlocal
set ROOT=%~dp0
cd /d "%ROOT%"

if not exist app\out mkdir app\out

coc build -p mylib\MyLib.coproj -b dotnet
if errorlevel 1 exit /b 1

copy /Y mylib\out\MyLib.dll app\out\ >nul

coc build -p app\App.coproj -b dotnet
if errorlevel 1 exit /b 1

echo.
echo Run: dotnet app\out\App.exe
endlocal
