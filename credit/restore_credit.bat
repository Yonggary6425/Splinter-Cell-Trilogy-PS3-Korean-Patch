@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0apply_credit.ps1" -Restore %*
pause
