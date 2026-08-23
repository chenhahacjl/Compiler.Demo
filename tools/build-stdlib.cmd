@echo off
REM Build Cocoa system stdlib: src\Cocoa.Lib\System.cosln (System.Core\*.co) -> src\Cocoa.Lib\out\System.Core.cod
REM (Directory discovery loading: future big modules like System.Net.cod/System.Json.cod
REM  add their own .coproj to System.cosln and get built automatically)
REM Usage: tools\build-stdlib.cmd
setlocal
set "ROOT=%~dp0.."
set "STDLIB=%ROOT%\src\Cocoa.Lib"
set "OUT=%STDLIB%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --no-build -- build -p "%STDLIB%\System.cosln" --no-incremental || exit /b 1

REM Deploy to compiler & test output dirs (runtime discovers System*.cod beside the exe)
set "CSBIN=%ROOT%\src\Cocoa.Cs"
copy /y "%OUT%\System.Core.cod" "%CSBIN%\Cocoa.Compiler\bin\Debug\net9.0\System.Core.cod" >nul
copy /y "%OUT%\System.Core.cod" "%CSBIN%\Cocoa.Tests\bin\Debug\net9.0\System.Core.cod" >nul

echo System.Core.cod built: %OUT%\System.Core.cod (deployed to Compiler/Tests bin)
endlocal
