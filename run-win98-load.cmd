@rem SPDX-License-Identifier: Apache-2.0
@rem Copyright 2026 Nils Kopal
@echo off
@rem Opens the separately prepared C45 load test copy without rebuilding.
if not exist "%~dp0artifacts\Windows-Load-C45\Start-Windows98-Load.cmd" (
  echo The private Windows 98 load test copy is not prepared in this checkout.
  exit /b 1
)
call "%~dp0artifacts\Windows-Load-C45\Start-Windows98-Load.cmd"
