# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Materialize a private DOSE COW branch to a separate raw disk image.

The input branch and its base image remain unchanged. This is used only to
prepare an isolated FAT image for tools that cannot address DOSE COW records.
"""

from __future__ import annotations

import argparse
import shutil
from pathlib import Path

from install_guest import Overlay


def main() -> None:
    """Materialize a private branch into a new raw image without changing its source."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("branch", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() or output == args.branch.resolve():
        raise FileExistsError(output)
    branch = Overlay(args.branch)
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        temporary = output.with_name(output.name + ".partial")
        if temporary.exists():
            raise FileExistsError(temporary)
        try:
            shutil.copyfile(branch.base, temporary)
            with temporary.open("r+b", buffering=8 * 1024 * 1024) as image:
                for lba, position in branch.index.items():
                    branch.file.seek(position)
                    sector = branch.file.read(512)
                    if len(sector) != 512:
                        raise ValueError(f"Truncated COW sector {lba}")
                    image.seek(lba * 512)
                    image.write(sector)
            temporary.rename(output)
        except BaseException:
            temporary.unlink(missing_ok=True)
            raise
        print(f"Materialized {len(branch.index)} changed sectors into {output}")
    finally:
        branch.close()


if __name__ == "__main__":
    main()
