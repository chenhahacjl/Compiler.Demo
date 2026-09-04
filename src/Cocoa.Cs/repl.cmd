@echo off

REM Delegate to the main CLI in interactive (REPL) mode
"%~dp0cocoa.cmd" --interactive %*
