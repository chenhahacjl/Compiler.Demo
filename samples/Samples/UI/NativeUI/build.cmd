@echo off
setlocal
set ROOT=%~dp0..\..\..\..
echo === 1. Build System.UI (.coa library) ===
cocoa build -p "%ROOT%\src\Cocoa.UI\System.UI.coproj"
if errorlevel 1 exit /b 1

echo.
echo === 2. Build NativeUI (native backend, x64) ===
cd /d "%~dp0"
cocoa build -p NativeUI.coproj -b native --platform x64
if errorlevel 1 exit /b 1

echo.
echo === 3. Run (ESC to quit) ===
out\NativeUI.exe
endlocal
