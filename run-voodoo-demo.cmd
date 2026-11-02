@rem SPDX-License-Identifier: Apache-2.0
@rem Copyright 2026 Nils Kopal
@echo off
"%~dp0scripts\invoke-powershell.cmd" "%~dp0scripts\start-mmx-voodoo-demo.ps1" %*
exit /b %errorlevel%
