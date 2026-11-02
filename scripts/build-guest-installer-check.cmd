@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "VS=%%I"
if not defined VS exit /b 1
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1
cl /nologo /c /O2 /arch:IA32 /GS- /Zl /I"%~1" /DTARGET_TEXT=\"%~2\" /Fo"%~1\check.obj" "%~dp0..\tests\guests\dose-installer-check.c"
if errorlevel 1 exit /b 1
link /nologo /Brepro /NODEFAULTLIB /ENTRY:CheckEntry /MACHINE:X86 /DYNAMICBASE:NO /NXCOMPAT:NO /SUBSYSTEM:WINDOWS /OUT:"%~1\CHECK.EXE" "%~1\check.obj" kernel32.lib user32.lib advapi32.lib
if errorlevel 1 exit /b 1
python "%~dp0glide-bridge\patch_pe_version.py" "%~1\CHECK.EXE"
exit /b %errorlevel%
