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
REM --no-incremental：双产物（cod+托管 dll）发射在构建尾部，增量命中会跳过 dll 产出
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --no-build -- build -p "%STDLIB%\System.cosln" --no-incremental || exit /b 1

REM Collect to central store src\Cocoa.Cs\libs (committed; Directory.Build.targets fans out
REM to project bins on build, SystemLibrary walk-up probe covers pre-build gap)
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
copy /y "%OUT%\System.Core.cod" "%LIBS%\System.Core.cod" >nul
REM 托管程序集名映射 System.*→CocoaStd.*（避开框架门面与引擎程序集同名，见 CodAssemblyNaming）
if exist "%OUT%\CocoaStd.Core.dll" copy /y "%OUT%\CocoaStd.Core.dll" "%LIBS%\CocoaStd.Core.dll" >nul

echo System.Core.cod built: %OUT%\System.Core.cod (collected to src\Cocoa.Cs\libs)
endlocal
