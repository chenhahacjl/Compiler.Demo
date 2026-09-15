@echo off
REM 构建 Cocoa.UI（System.UI.coproj）-> tools\cocoa-ui
REM 引导：C# cocoa CLI 编译 src\Cocoa.UI\System.UI.coproj，再复制 out
REM 用法：tools\build-ui.cmd
setlocal
set "ROOT=%~dp0.."
set "UI=%ROOT%\src\Cocoa.UI"
set "OUT=%ROOT%\tools\cocoa-ui"

dotnet build "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --no-build -- build -p "%UI%\System.UI.coproj" --no-incremental || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
copy /y "%UI%\out\System.UI.coa" "%OUT%\System.UI.coa" >nul
endlocal
