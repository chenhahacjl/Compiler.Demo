@echo off

REM Vars
set "SLNDIR=%~dp0"

REM Restore + Build
dotnet build "%SLNDIR%Cocoa.sln" --nologo || exit /b

REM Test
dotnet test "%SLNDIR%Cocoa.Tests" --nologo --no-build