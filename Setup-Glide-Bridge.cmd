@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "ROOT=%~dp0"
set "MODE=%~1"
if not defined MODE (
  echo 95 - Windows 95 mit Unreal und schnellem MiniGlide-Profil
  echo 98 - Windows 98 mit Unreal und MiniGlide
  set /p "MODE=Windows-Version [95/98]: "
)
if "%MODE%"=="95" (
  set "SOURCE=%ROOT%artifacts\glide-bridge\win95-unreal-installed.dosebranch\branch.dospc.json"
  set "TARGET=%ROOT%artifacts\glide-bridge\win95-unreal-glide.dosebranch"
) else if "%MODE%"=="98" (
  set "SOURCE=%ROOT%artifacts\unreal-win98-20261102-1e0df488\win98-unreal.dosebranch\branch.dospc.json"
  set "TARGET=%ROOT%artifacts\unreal-win98-20261102-1e0df488\win98-unreal-gpubridge.dosebranch"
) else (
  echo Bitte 95 oder 98 angeben.
  exit /b 2
)

set "SHIM=%ROOT%artifacts\glide-bridge\guest\GLIDE2X.DLL"
set "MINI=%ROOT%artifacts\glide-bridge\guest\MGLIDE.EXE"
set "HOST=%ROOT%artifacts\glide-bridge\host\Glide2x-dgVoodoo-x64.dll"
set "FRONTEND=%ROOT%artifacts\glide-bridge\frontend\DOSEmulator.exe"
if not exist "%SOURCE%" (
  echo Quellmaschine fehlt: "%SOURCE%"
  exit /b 1
)
if not exist "%HOST%" (
  echo Host-Glide-Bibliothek fehlt: "%HOST%"
  exit /b 1
)
if "%MODE%"=="95" if not exist "%ROOT%Disks and CDs (DO NOT PUT INTO GITHUB)\unreal.iso" (
  echo Unreal-ISO fehlt im privaten CD-Verzeichnis.
  exit /b 1
)
call "%ROOT%scripts\glide-bridge\build-guest.cmd"
if errorlevel 1 exit /b %errorlevel%
dotnet publish "%ROOT%src\DOSEmulator\DOSEmulator.csproj" -c Release -o "%ROOT%artifacts\glide-bridge\frontend"
if errorlevel 1 exit /b %errorlevel%

if "%MODE%"=="95" (
  python "%ROOT%scripts\glide-bridge\setup.py" --source "%SOURCE%" --output "%TARGET%" --shim "%SHIM%" --guest-launcher "%MINI%" --host "%HOST%" --frontend "%FRONTEND%" --cd "%ROOT%Disks and CDs (DO NOT PUT INTO GITHUB)\unreal.iso" --reuse
) else (
  python "%ROOT%scripts\glide-bridge\setup.py" --source "%SOURCE%" --output "%TARGET%" --shim "%SHIM%" --guest-launcher "%MINI%" --host "%HOST%" --frontend "%FRONTEND%" --reuse
)
if errorlevel 1 exit /b %errorlevel%
if "%MODE%"=="95" (
  python "%ROOT%scripts\glide-bridge\tune_unreal95.py" "%TARGET%"
  if errorlevel 1 exit /b 1
)
echo Bereit: "%TARGET%\Start-Glide.cmd"
echo Im Gast Unreal mit C:\MGLIDE.EXE starten.
exit /b 0
