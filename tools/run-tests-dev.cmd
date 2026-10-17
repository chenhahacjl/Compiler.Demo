@echo off
REM Cocoa.Cs 开发快速回路：只跑 C# 特性测试（不含自举/差分，后者在 Cocoa.Tests.SelfHosting）
REM 用法: tools\run-tests-dev.cmd
setlocal
set "ROOT=%~dp0.."
set "TESTS=%ROOT%\src\Cocoa.Cs\Tests"

dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.slnx" --nologo || exit /b 1

dotnet test "%TESTS%\Cocoa.Tests.CodeAnalysis" --no-restore --no-build --nologo || exit /b 1
dotnet test "%TESTS%\Cocoa.Tests.CodeGen" --no-restore --no-build --nologo || exit /b 1
dotnet test "%TESTS%\Cocoa.Tests.Integration" --no-restore --no-build --nologo || exit /b 1
endlocal
