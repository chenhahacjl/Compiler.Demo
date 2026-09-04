@echo off

REM Vars
set "SLNDIR=%~dp0"

REM Restore + Build
dotnet build "%SLNDIR%Cli\Cocoa.Cli" --nologo || exit /b

REM Run
dotnet run --project "%SLNDIR%Cli\Cocoa.Cli" --no-build -- %*
