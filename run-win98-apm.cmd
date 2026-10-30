@rem SPDX-License-Identifier: Apache-2.0
@rem Copyright 2026 Nils Kopal
@echo off
@rem Opens the separately prepared APM test copy without rebuilding.
if not exist "%~dp0artifacts\Windows-Realtime-C44b\Start-Windows98-APM.cmd" (
  echo The private APM test copy is not prepared in this checkout.
  exit /b 1
)
call "%~dp0artifacts\Windows-Realtime-C44b\Start-Windows98-APM.cmd"
