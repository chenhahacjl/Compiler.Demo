@echo off
REM Build Cocoa system stdlib: src\stdlib\System.cosln (System.Core\*.co) -> src\stdlib\System.Core\out\System.Core.cod
REM (Directory discovery loading: future big modules like System.Net.cod/System.Json.cod
REM  add their own .coproj to System.cosln and get built automatically)
REM Usage: tools\build-stdlib.cmd
setlocal
set "ROOT=%~dp0.."
set "STDLIB=%ROOT%\src\stdlib"
set "OUT=%STDLIB%\System.Core\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Compiler" --no-build -- build -p "%STDLIB%\System.cosln" --no-incremental || exit /b 1

echo System.Core.cod built: %OUT%\System.Core.cod
endlocal
