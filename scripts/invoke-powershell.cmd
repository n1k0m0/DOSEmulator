@echo off
setlocal
set "DOSE_PWSH="
for /f "delims=" %%P in ('where.exe pwsh.exe 2^>nul') do if not defined DOSE_PWSH set "DOSE_PWSH=%%P"
if defined DOSE_PWSH goto run
if exist "%ProgramW6432%\PowerShell\7\pwsh.exe" set "DOSE_PWSH=%ProgramW6432%\PowerShell\7\pwsh.exe"
if defined DOSE_PWSH goto run
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "DOSE_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if defined DOSE_PWSH goto run
if exist "%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe" set "DOSE_PWSH=%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe"
if defined DOSE_PWSH goto run
echo DOSEmulator requires PowerShell 7.4 or later ^(pwsh.exe^). Windows PowerShell 5.1 is not supported. 1>&2
echo Install PowerShell 7 and make pwsh.exe available on PATH, then run this launcher again. 1>&2
exit /b 1
:run
"%DOSE_PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File %*
exit /b %errorlevel%
