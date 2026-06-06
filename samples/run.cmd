@echo off
setlocal enabledelayedexpansion

REM Vars
set "SLNDIR=%~dp0..\src\"

REM Check whether the first parameter is a file or a folder
if exist "%~1\" (
    REM It's a folder, read all .co files under this folder
    set "CO_FILES="
    for %%f in ("%~1\*.co") do (
        set "CO_FILES=!CO_FILES! "%%f""
    )
    if not "!CO_FILES!"=="" (
        echo Executing: "%SLNDIR%coc.cmd" !CO_FILES!
        "%SLNDIR%coc.cmd" !CO_FILES!
    ) else (
        echo No .co files found in the folder
    )
) else (
    REM It's a file (or multiple files), use the original parameters directly
    echo Executing: "%SLNDIR%coc.cmd" %*
    "%SLNDIR%coc.cmd" %*
)

endlocal