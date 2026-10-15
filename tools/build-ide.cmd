@echo off
REM Build + package Cocoa.IDE with VS/dotnet-style layout -> tools\cocoa-ide
REM   tools\cocoa-ide\Cocoa.IDE.exe        (host)
REM   tools\cocoa-ide\Compiler\            (compiler assemblies Cocoa.*.dll, ALC-loaded by the host)
REM   tools\cocoa-ide\SDK\                 (system library System*.coa, discovered by SystemLibrary)
REM Usage: tools\build-ide.cmd
setlocal
set "ROOT=%~dp0.."
set "IDE_PROJ=%ROOT%\src\Cocoa.IDE\Cocoa.IDE.csproj"
set "OUT=%ROOT%\tools\cocoa-ide"
set "SDK_SRC=%ROOT%\tools\cocoa-sdk"

echo === Publish Cocoa.IDE (net9.0, framework-dependent) ===
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish "%IDE_PROJ%" --nologo -c Debug -o "%OUT%" || exit /b 1

REM 1) Compiler: move Cocoa.*.dll (except the IDE host) into .\Compiler (dotnet sdk/ layout).
REM    Host stays at root so Cocoa.IDE.exe finds its Cocoa.IDE.dll beside it.
set "COMPILER=%OUT%\Compiler"
if not exist "%COMPILER%" mkdir "%COMPILER%"
for %%F in ("%OUT%\Cocoa.*.dll") do (
    if /I not "%%~nxF"=="Cocoa.IDE.dll" move /y "%%F" "%COMPILER%\" >nul
)

REM 2) SDK: copy System*.coa into .\SDK (dotnet shared-framework layout)
set "SDK=%OUT%\SDK"
if not exist "%SDK%" mkdir "%SDK%"
if exist "%SDK_SRC%" copy /y "%SDK_SRC%\*.coa" "%SDK%\" >nul

echo.
echo === IDE 发布完成: %OUT% ===
echo   %OUT%\Cocoa.IDE.exe   (host)
echo   %OUT%\Compiler\       (compiler assemblies Cocoa.*.dll)
echo   %OUT%\SDK\            (system library System*.coa)
echo   run: %OUT%\Cocoa.IDE.exe
echo   SystemLibrary auto-discovers .\SDK; host ALC-loads compiler from .\Compiler
endlocal