# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Extract the original Unreal CD's Joliet game files for an isolated FAT copy."""

from __future__ import annotations

import argparse
from pathlib import Path

import pycdlib


GAME_DIRS = {"System", "Maps", "Textures", "Music", "Sounds", "Help"}


def main() -> None:
    """Extract the explicitly supplied private Unreal ISO into a separate staging directory."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("iso", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError(output)
    output.mkdir(parents=True)
    iso = pycdlib.PyCdlib()
    iso.open(str(args.iso.resolve()))
    count = 0
    total = 0
    try:
        for directory, subdirs, files in iso.walk(joliet_path="/"):
            parts = tuple(part for part in directory.split("/") if part)
            if not parts or parts[0] not in GAME_DIRS:
                continue
            target = output.joinpath(*parts)
            target.mkdir(parents=True, exist_ok=True)
            for filename in files:
                name = filename.rsplit(";", 1)[0]
                if name in (".", "..") or "/" in name or "\\" in name:
                    raise ValueError(f"Unsafe ISO path: {filename}")
                path = target / name
                with path.open("wb") as stream:
                    iso.get_file_from_iso_fp(stream, joliet_path=directory.rstrip("/") + "/" + filename)
                count += 1
                total += path.stat().st_size
        print(f"Extracted {count} files ({total} bytes) to {output}")
    finally:
        iso.close()


if __name__ == "__main__":
    main()
