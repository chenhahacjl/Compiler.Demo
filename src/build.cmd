@echo off

REM Restore + Build
dotnet build ".\Compiler.Demo\Cocoa.sln" --nologo || exit /b

REM Test
dotnet test ".\Compiler.Demo\Cocoa.Tests" --nologo --no-build