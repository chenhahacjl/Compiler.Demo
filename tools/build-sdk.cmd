@echo off
REM Build Cocoa SDK (stdlib): src\Cocoa.SDK\Cocoa.SDK.cosln (System.Core\*.co) -> src\Cocoa.SDK\out\System.Core.coa
REM (Directory discovery loading: future big modules like System.Net.coa/System.Json.coa
REM  add their own .coproj to Cocoa.SDK.cosln and get built automatically)
REM Usage: tools\build-sdk.cmd
setlocal
set "ROOT=%~dp0.."
set "SDK=%ROOT%\src\Cocoa.SDK"
set "OUT=%SDK%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --no-build -- build -p "%SDK%\Cocoa.SDK.cosln" --no-incremental || exit /b 1

REM Collect to central store src\Cocoa.Cs\libs (committed; Directory.Build.targets fans out
REM to project bins on build, SystemLibrary walk-up probe covers pre-build gap)
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
REM Collect all modules (System.Core + System.Collections; collections serializable since 6b/M0-1c)
copy /y "%OUT%\System.Core.coa" "%LIBS%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%LIBS%\System.Collections.coa" >nul
REM Managed dll not prebuilt: consumers regenerate lazily from coa (ProjectBuilder.EnsureManagedDlls)
endlocal
