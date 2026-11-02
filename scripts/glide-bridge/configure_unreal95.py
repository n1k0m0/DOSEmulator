# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Set up the installed Win95 Unreal 205 files to use local game assets and Glide."""

from __future__ import annotations

import argparse
from pathlib import Path

from install_guest import read_guest_file, replace_file


def main() -> None:
    """Apply the selected Unreal configuration only to the named private Win95 test copy."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("branch", type=Path)
    args = parser.parse_args()
    branch = args.branch
    default = read_guest_file(branch, "UNREAL/SYSTEM/DEFAULT.INI")
    if default is None or read_guest_file(branch, "UNREAL/TEXTURES/PALETTES.UTX") is None:
        raise ValueError("Complete Unreal 205 game files are required")
    ini = default.replace(b"\r\r\n", b"\r\n")
    if ini.count(b"[FirstRun]\r\nFirstRun=True") != 1 or ini.count(b"[Engine.Engine]\r\n") != 1:
        raise ValueError("Unexpected Unreal 205 default configuration")
    ini = ini.replace(b"[FirstRun]\r\nFirstRun=True", b"[FirstRun]\r\nFirstRun=205")
    ini = ini.replace(b"[Engine.Engine]\r\n", b"[Engine.Engine]\r\nCdPath=C:\\UNREAL\\\r\n")
    ini = ini.replace(b"GameRenderDevice=SoftDrv.SoftwareRenderDevice",
                      b"GameRenderDevice=GlideDrv.GlideRenderDevice")
    previous = read_guest_file(branch, "UNREAL/SYSTEM/UNREAL.INI")
    if previous is not None and previous != ini:
        backup = branch / "before-glide-Unreal.ini"
        if not backup.exists():
            backup.write_bytes(previous)
    result = replace_file(branch, "UNREAL/SYSTEM/UNREAL.INI", ini)
    print(f"Unreal.ini: {result.installed_size} bytes; unchanged={result.unchanged}")


if __name__ == "__main__":
    main()
