@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "ROOT=%~dp0..\.."
set "SRC=%ROOT%\src\DOSEmulator.GlideGuest"
set "OUT=%ROOT%\artifacts\glide-bridge\guest"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
  echo MSVC Build Tools mit x86-Compiler fehlen.
  exit /b 1
)
for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath`) do set "VS=%%I"
if not defined VS (
  echo Keine Visual-Studio-Installation gefunden.
  exit /b 1
)
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1
cl /nologo /c /O2 /arch:IA32 /GS- /Zl /Fo"%OUT%\miniglide_splash.obj" "%SRC%\miniglide_splash.c"
if errorlevel 1 exit /b 1
cl /nologo /c /O2 /arch:IA32 /GS- /Zl /Fo"%OUT%\glide2x_bridge.obj" "%SRC%\glide2x_bridge.c"
if errorlevel 1 exit /b 1
link /nologo /Brepro /DLL /NODEFAULTLIB /ENTRY:DllMain@12 /MACHINE:X86 /DYNAMICBASE:NO /NXCOMPAT:NO /SUBSYSTEM:WINDOWS /DEF:"%SRC%\glide2x_bridge.def" /OUT:"%OUT%\GLIDE2X.DLL" "%OUT%\glide2x_bridge.obj" "%OUT%\miniglide_splash.obj" kernel32.lib user32.lib gdi32.lib
if errorlevel 1 exit /b 1
python "%~dp0patch_pe_version.py" "%OUT%\GLIDE2X.DLL"
if errorlevel 1 exit /b 1
cl /nologo /c /O2 /arch:IA32 /GS- /Zl /Fo"%OUT%\miniglide_launcher.obj" "%SRC%\miniglide_launcher.c"
if errorlevel 1 exit /b 1
link /nologo /Brepro /NODEFAULTLIB /ENTRY:MiniGlideEntry /MACHINE:X86 /DYNAMICBASE:NO /NXCOMPAT:NO /SUBSYSTEM:WINDOWS /OUT:"%OUT%\MGLIDE.EXE" "%OUT%\miniglide_launcher.obj" "%OUT%\miniglide_splash.obj" kernel32.lib user32.lib gdi32.lib
if errorlevel 1 exit /b 1
python "%~dp0patch_pe_version.py" "%OUT%\MGLIDE.EXE"
exit /b %errorlevel%
