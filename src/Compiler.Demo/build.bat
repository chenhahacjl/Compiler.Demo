@echo off

REM Restore + Build
dotnet build ".\Cocoa.sln" --nologo || exit /b

REM Test
dotnet test ".\Cocoa.Tests" --nologo --no-build