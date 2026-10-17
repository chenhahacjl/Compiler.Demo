@echo off
REM 构建 Cocoa SDK（标准库）（src\Cocoa.SDK\Cocoa.SDK.cosln）System.Core\*.co -> src\Cocoa.SDK\out\System.Core.coa
REM 子目录按需加载（未来增补模块：System.Net.coa/System.Json.coa）
REM  由 Cocoa.SDK.cosln 工程约定的 .coproj 工程文件自动包含
REM 用法：tools\build-sdk.cmd
setlocal
set "ROOT=%~dp0.."
set "SDK=%ROOT%\src\Cocoa.SDK"
set "OUT=%SDK%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Tools\Cli\Cocoa.Cli" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Tools\Cli\Cocoa.Cli" --no-build -- build -p "%SDK%\Cocoa.SDK.cosln" --no-incremental || exit /b 1

REM 收集输出到 src\Cocoa.Cs\libs（调试）；Directory.Build.targets 编译时自动分发到 bin。
REM SystemLibrary 依赖探测兜底覆盖构建前缺失。
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
REM 收集标准库模块（System.Core + System.Collections；collections 于 6b/M0-1c 起随后续迭代加入）
copy /y "%OUT%\System.Core.coa" "%LIBS%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%LIBS%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%LIBS%\System.IO.coa" >nul

REM 收集输出到 tools\cocoa-sdk（最新可运行版 SDK 汇聚）
set "SDKOUT=%ROOT%\tools\cocoa-sdk"
if not exist "%SDKOUT%" mkdir "%SDKOUT%"
copy /y "%OUT%\System.Core.coa" "%SDKOUT%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%SDKOUT%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%SDKOUT%\System.IO.coa" >nul

REM 有关 dll 的预处理：优先采用已发布的 coa 程序集生成；ProjectBuilder.EnsureManagedDlls。
endlocal
