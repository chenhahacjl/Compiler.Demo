@echo off
REM Build Cocoa system stdlib: src\stdlib\System.co -> src\stdlib\out\System.cod
REM Usage: tools\build-stdlib.cmd
setlocal
set "ROOT=%~dp0.."
set "STDLIB=%ROOT%\src\stdlib"
set "OUT=%STDLIB%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Compiler" --no-build -- build -p "%STDLIB%\System.coproj" --no-incremental || exit /b 1

echo System.cod built: %OUT%\System.cod
endlocal
