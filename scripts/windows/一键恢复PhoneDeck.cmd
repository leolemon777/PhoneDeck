@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0StartOrRepairPhoneDeck.ps1"
endlocal
