@echo off
REM Build Cocoa system stdlib: src\stdlib\System.co -> src\stdlib\out\System.Core.cod
REM (目录发现加载：未来大模块 System.Net.cod/System.Json.cod 各配 .coproj 追加构建)
REM Usage: tools\build-stdlib.cmd
setlocal
set "ROOT=%~dp0.."
set "STDLIB=%ROOT%\src\stdlib"
set "OUT=%STDLIB%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Compiler" --no-build -- build -p "%STDLIB%\System.Core.coproj" --no-incremental || exit /b 1

echo System.cod built: %OUT%\System.Core.cod
endlocal
