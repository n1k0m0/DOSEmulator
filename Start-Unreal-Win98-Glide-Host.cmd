@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "LAUNCHER=%~dp0artifacts\unreal-win98-20261102-1e0df488\win98-unreal-gpubridge.dosebranch\Start-Glide.cmd"
if not exist "%LAUNCHER%" (
  echo Die Windows-98-Glide-Maschine fehlt. Zuerst Setup-Glide-Bridge.cmd 98 ausfuehren.
  exit /b 1
)
call "%LAUNCHER%" %*
exit /b %errorlevel%
