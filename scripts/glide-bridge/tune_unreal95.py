# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Apply a reversible low-detail Unreal Glide preset to an isolated Win95 branch."""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from install_guest import read_guest_file, replace_file


def set_option(data: bytes, section: str, option: str, value: str) -> bytes:
    """Replace exactly one setting in the selected Unreal INI section."""
    marker = f"[{section}]".encode("ascii")
    if data.count(marker) != 1:
        raise ValueError(f"Expected one {section} section")
    prefix, following = data.split(marker, 1)
    boundary = following.find(b"\n[")
    if boundary < 0:
        body, suffix = following, b""
    else:
        body, suffix = following[:boundary + 1], following[boundary + 1:]
    pattern = re.compile(rb"(?m)^" + re.escape(option.encode("ascii")) + rb"=[^\r\n]*")
    body, changed = pattern.subn(f"{option}={value}".encode("ascii"), body)
    if changed != 1:
        raise ValueError(f"Expected one {section}/{option} setting, found {changed}")
    return prefix + marker + body + suffix


def main() -> None:
    """Apply and back up the reversible low-detail profile in a private Win95 branch."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("branch", type=Path)
    args = parser.parse_args()
    branch = args.branch.resolve()
    before = read_guest_file(branch, "UNREAL/SYSTEM/UNREAL.INI")
    if before is None:
        raise FileNotFoundError("C:\\UNREAL\\SYSTEM\\UNREAL.INI")
    after = before
    for section, option, value in (
        ("WinDrv.WindowsClient", "LowDetailTextures", "True"),
        ("GlideDrv.GlideRenderDevice", "DetailTextures", "False"),
        ("GlideDrv.GlideRenderDevice", "VolumetricLighting", "False"),
        ("GlideDrv.GlideRenderDevice", "ShinySurfaces", "False"),
        ("GlideDrv.GlideRenderDevice", "Coronas", "False"),
        ("GlideDrv.GlideRenderDevice", "HighDetailActors", "False"),
        ("GlideDrv.GlideRenderDevice", "ScreenSmoothing", "False"),
    ):
        after = set_option(after, section, option, value)
    if after != before:
        backup = branch / "before-performance-Unreal.ini"
        if not backup.exists():
            backup.write_bytes(before)
    result = replace_file(branch, "UNREAL/SYSTEM/UNREAL.INI", after)
    print(f"Unreal Glide preset: {result.installed_size} bytes; unchanged={result.unchanged}")


if __name__ == "__main__":
    main()
