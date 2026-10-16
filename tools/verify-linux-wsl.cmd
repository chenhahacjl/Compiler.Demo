@echo off
REM T3 gate 3 (WSL 本地版) launcher: delegates to verify-linux-wsl.ps1.
REM Usage: tools\verify-linux-wsl.cmd <elf-path> [stdin-text]
REM Requires WSL2 (wsl --status). Windows path auto-translated; stdout flows; exit 0 = clean.
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-linux-wsl.ps1" -ElfPath "%~f1" -InputText "%~2"
set "RC=%ERRORLEVEL%"
endlocal
exit /b %RC%