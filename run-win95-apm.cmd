@rem SPDX-License-Identifier: Apache-2.0
@rem Copyright 2026 Nils Kopal
@echo off
@rem Uses the common current runtime with this PC and its existing private settings.
"%~dp0scripts\invoke-powershell.cmd" "%~dp0scripts\start-variant.ps1" -Variant win95-apm %*
exit /b %errorlevel%
