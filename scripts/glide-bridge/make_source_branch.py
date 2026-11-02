# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Wrap a separately prepared raw disk in a new empty DOSE COW branch."""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
import uuid
from pathlib import Path


def main() -> None:
    """Wrap an independently prepared disk in a new branch with an empty private overlay."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--template", required=True, type=Path)
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    image = args.image.resolve()
    output = args.output.resolve()
    if output.exists() or output.suffix != ".dosebranch":
        raise FileExistsError(output)
    if not image.is_file() or image.stat().st_size % 512:
        raise ValueError("Source image must contain whole sectors")
    definition = json.loads(args.template.read_text(encoding="utf-8-sig"))
    disk = next(item for item in definition["hardDisks"] if item["slot"] == 0)
    definition["id"] = uuid.uuid4().hex
    definition["name"] = "Windows 95 Unreal installed source"
    disk["imagePath"] = str(image)
    disk["overlayPath"] = "ide0.cow"
    digest = hashlib.sha256()
    with image.open("rb") as stream:
        while chunk := stream.read(8 * 1024 * 1024):
            digest.update(chunk)
    output.mkdir(parents=True)
    (output / "branch.dospc.json").write_text(json.dumps(definition, indent=2) + "\n", encoding="utf-8")
    (output / "ide0.cow").write_bytes(b"DOSCOW01" + struct.pack("<q", image.stat().st_size // 512) + digest.digest())
    print(output)


if __name__ == "__main__":
    main()
