@echo off
REM T3 gate 3 launcher: delegates to verify-linux-vm.py (paramiko; deterministic, no ssh askpass).
REM Usage: tools\verify-linux-vm.cmd <elf-path>
REM Env overrides: COCOA_VM_HOST / COCOA_VM_USER / COCOA_VM_PASS (defaults 192.168.200.139 / root / 123456).
REM Requires python + paramiko (`pip install paramiko`).
setlocal
set "SCRIPT=%~dp0verify-linux-vm.py"
python "%SCRIPT%" "%~f1"
endlocal
exit /b %ERRORLEVEL%