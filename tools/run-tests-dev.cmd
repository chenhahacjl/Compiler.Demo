@echo off
REM Cocoa.Cs 开发快速回路：只跑 C# 特性测试（不含自举/差分，后者在 Cocoa.Tests.SelfHosting）
REM 用法: tools\run-tests-dev.cmd
setlocal
set "ROOT=%~dp0.."
dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.Tests" --nologo || exit /b 1
dotnet test "%ROOT%\src\Cocoa.Cs\Cocoa.Tests" --no-restore --no-build || exit /b 1
endlocal
