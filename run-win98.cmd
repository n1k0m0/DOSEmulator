@rem SPDX-License-Identifier: Apache-2.0
@rem Copyright 2026 Nils Kopal
@echo off
@rem Opens the separately installed private Windows 98 SE PC without rebuilding.
if not exist "%~dp0artifacts\Windows98\Start-Windows98.cmd" (
  echo The private Windows 98 SE installation is not ready in this checkout.
  exit /b 1
)
call "%~dp0artifacts\Windows98\Start-Windows98.cmd"
