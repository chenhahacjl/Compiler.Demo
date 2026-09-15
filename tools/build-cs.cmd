@echo off
REM 构建 Cocoa.Cs（C# 宿主编译器，最新可运行版）-> tools\cocoa-cs
REM 用法：tools\build-cs.cmd
setlocal
set "ROOT=%~dp0.."
set "BIN=%ROOT%\src\Cocoa.Cs\Cli\Cocoa.Cli\bin\Debug\net9.0"
set "OUT=%ROOT%\tools\cocoa-cs"

dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.slnx" --nologo || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
xcopy /y /e /i "%BIN%" "%OUT%\" >nul
endlocal
