@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "UNREAL_WORK=%~dp0artifacts\unreal-win98-20261102-1e0df488"
set "UNREAL_EMULATOR=%UNREAL_WORK%\frontend-patched\DOSEmulator.exe"
set "UNREAL_MACHINE=%UNREAL_WORK%\win98-unreal.dosebranch\branch.dospc.json"

if not exist "%UNREAL_EMULATOR%" (
  echo The patched DOSEmulator build is missing: "%UNREAL_EMULATOR%"
  exit /b 1
)
if not exist "%UNREAL_MACHINE%" (
  echo The Unreal Windows 98 machine is missing: "%UNREAL_MACHINE%"
  exit /b 1
)

start "" "%UNREAL_EMULATOR%" --engine whpx --machine "%UNREAL_MACHINE%" --data-dir "%UNREAL_WORK%\frontend-data"
