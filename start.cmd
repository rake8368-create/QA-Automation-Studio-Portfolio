@echo off
setlocal
cd /d "%~dp0"
if exist "%~dp0release\win-x64\QA.AutomationStudio.Desktop.exe" goto launch_native
echo Native desktop EXE is missing. Build and publish QA.AutomationStudio.Desktop first.
pause
exit /b 1
:launch_native
start "" "%~dp0release\win-x64\QA.AutomationStudio.Desktop.exe"
endlocal
