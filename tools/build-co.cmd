@echo off
REM 构建 Cocoa.Co（自举编译器，最新可运行版）-> tools\cocoa-co
REM 引导：C# cocoa CLI 编译 src\Cocoa.Co\Cocoa.Co.cosln，再复制 Cli\out
REM 用法：tools\build-co.cmd
setlocal
set "ROOT=%~dp0.."
set "OUT=%ROOT%\tools\cocoa-co"

dotnet build "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli" --no-build -- build -p "%ROOT%\src\Cocoa.Co\Cocoa.Co.cosln" --no-incremental || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
xcopy /y /e /i "%ROOT%\src\Cocoa.Co\Cli\out" "%OUT%\" >nul
endlocal
