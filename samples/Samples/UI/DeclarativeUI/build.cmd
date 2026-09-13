@echo off
setlocal
set ROOT=%~dp0..\..\..\..
echo === 1. Build System.UI (.coa library) ===
cocoa build -p "%ROOT%\src\Cocoa.UI\System.UI.coproj"
if errorlevel 1 exit /b 1

echo.
echo === 2. Build DeclarativeUI (IL backend, net48) ===
cd /d "%~dp0"
cocoa build -p DeclarativeUI.coproj
if errorlevel 1 exit /b 1

echo.
echo === 3. Run (ESC to quit) ===
out\DeclarativeUI.exe
endlocal
