@echo off

REM Vars
set "SLNDIR=%~dp0"

REM Restore + Build
dotnet build "%SLNDIR%Cocoa.CommandLine" --nologo || exit /b

REM Run
dotnet run --project "%SLNDIR%Cocoa.CommandLine" --no-build -- %*
