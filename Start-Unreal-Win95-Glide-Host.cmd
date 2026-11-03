@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
set "LAUNCHER=%~dp0artifacts\glide-bridge\win95-unreal-glide.dosebranch\Start-Glide.cmd"
if not exist "%LAUNCHER%" (
  echo Die Windows-95-Glide-Maschine fehlt. Zuerst Setup-Glide-Bridge.cmd 95 ausfuehren.
  exit /b 1
)
call "%LAUNCHER%" %*
exit /b %errorlevel%
