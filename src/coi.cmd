@echo off

REM Vars
set "SLNDIR=%~dp0"

REM Restore + Build
dotnet build "%SLNDIR%Cocoa.Interactive" --nologo || exit /b

REM Run
dotnet run --project "%SLNDIR%Cocoa.Interactive" --no-build
