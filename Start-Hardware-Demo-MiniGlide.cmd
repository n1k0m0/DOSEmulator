@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "FRONTEND=%~dp0artifacts\glide-bridge\frontend\DOSEmulator.exe"
set "MACHINE=%~dp0artifacts\miniglide-paths\legacy.dosebranch\branch.dospc.json"
if not exist "%FRONTEND%" (
  echo Das lokal gebaute Emulator-Frontend fehlt.
  exit /b 1
)
if not exist "%MACHINE%" (
  echo Die private Hardware-Demo-Kopie fehlt.
  echo In einer eigenen Windows-Maschine den MiniGlide-Starthelfer installieren.
  echo Anleitung: docs\GUEST-INSTALLERS.md
  exit /b 1
)
set "DOSE_GLIDE_HOST_DLL="
if /i "%~1"=="--inspect" (
  echo Frontend: "%FRONTEND%"
  echo Machine: "%MACHINE%"
  exit /b 0
)
start "" "%FRONTEND%" --engine whpx --machine "%MACHINE%" --data-dir "%~dp0artifacts\miniglide-paths\legacy.dosebranch\frontend-data"
exit /b 0
