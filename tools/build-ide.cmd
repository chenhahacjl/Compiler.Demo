@echo off
REM 构建 Cocoa.IDE（Avalonia 桌面 IDE，最新可运行版）-> tools\cocoa-ide
REM 用法：tools\build-ide.cmd
setlocal
set "ROOT=%~dp0.."
set "BIN=%ROOT%\src\Cocoa.IDE\bin\Debug\net9.0"
set "OUT=%ROOT%\tools\cocoa-ide"

dotnet build "%ROOT%\src\Cocoa.IDE\Cocoa.IDE.slnx" --nologo || exit /b 1

if not exist "%OUT%" mkdir "%OUT%"
xcopy /y /e /i "%BIN%" "%OUT%\" >nul
endlocal
