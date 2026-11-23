@echo off
setlocal
python "%~dp0inspect-ipad-capture.py"
if errorlevel 1 pause
