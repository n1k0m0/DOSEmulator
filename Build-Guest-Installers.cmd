@echo off
REM SPDX-License-Identifier: Apache-2.0
REM Copyright 2026 Nils Kopal
setlocal
cd /d "%~dp0"
python "%~dp0scripts\build-guest-installers.py" %*
exit /b %errorlevel%
