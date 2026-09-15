@echo off
REM 构建 Cocoa SDK（标准库）：src\Cocoa.SDK\Cocoa.SDK.cosln（System.Core\*.co）-> src\Cocoa.SDK\out\System.Core.coa
REM （目录发现加载：未来的大模块如 System.Net.coa/System.Json.coa
REM  在 Cocoa.SDK.cosln 里加自己的 .coproj 即可自动构建）
REM 用法：tools\build-sdk.cmd
setlocal
set "ROOT=%~dp0.."
set "SDK=%ROOT%\src\Cocoa.SDK"
set "OUT=%SDK%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --no-build -- build -p "%SDK%\Cocoa.SDK.cosln" --no-incremental || exit /b 1

REM 收集到中央库仓 src\Cocoa.Cs\libs（入库；Directory.Build.targets 构建时自动分发到各 bin，
REM SystemLibrary 向上探测兜底覆盖构建前空缺）
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
REM 收集所有模块（System.Core + System.Collections；collections 自 6b/M0-1c 起可序列化）
copy /y "%OUT%\System.Core.coa" "%LIBS%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%LIBS%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%LIBS%\System.IO.coa" >nul

REM 收集到中央库仓 tools\cocoa-sdk（最新可运行版 SDK 快照）
set "SDKOUT=%ROOT%\tools\cocoa-sdk"
if not exist "%SDKOUT%" mkdir "%SDKOUT%"
copy /y "%OUT%\System.Core.coa" "%SDKOUT%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%SDKOUT%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%SDKOUT%\System.IO.coa" >nul

REM 托管 dll 不预构建：消费方按需从 coa 惰性生成（ProjectBuilder.EnsureManagedDlls）
endlocal
