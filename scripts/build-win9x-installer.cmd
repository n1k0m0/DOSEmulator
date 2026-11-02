@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "VS=%%I"
if not defined VS exit /b 1
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1
rc /nologo /fo "%~2\installer.res" "%~1"
if errorlevel 1 exit /b 1
cl /nologo /c /O2 /arch:IA32 /GS- /Zl /DTARGET_MINOR=%~4 /DTARGET_TEXT=\"%~5\" /Fo"%~2\installer.obj" "%~dp0..\guest-tools\installer\installer.c"
if errorlevel 1 exit /b 1
link /nologo /Brepro /NODEFAULTLIB /ENTRY:SetupEntry /MACHINE:X86 /DYNAMICBASE:NO /NXCOMPAT:NO /SUBSYSTEM:WINDOWS /OUT:"%~3" "%~2\installer.obj" "%~2\installer.res" kernel32.lib user32.lib advapi32.lib ole32.lib uuid.lib
if errorlevel 1 exit /b 1
python "%~dp0glide-bridge\patch_pe_version.py" "%~3"
exit /b %errorlevel%
