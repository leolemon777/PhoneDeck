@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-UpdateSupport.ps1" %*
pause
