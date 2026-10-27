# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create a NEW private FAT12 test floppy, extracting CuteMouse only from local FreeDOS.
No OS, driver or ROM binary becomes a repository asset. Stdlib independent of emulator.
"""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import sys
import zipfile

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
fat_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fat_oracle)


def prepare(source, probe, destination):
    """Creates a fresh private mouse media image from validated inputs, leaving every source medium unchanged."""
    source, probe, destination = map(Path, (source, probe, destination))
    original = source.read_bytes()
    digest = hashlib.sha256(original).hexdigest().upper()
    assert digest == "F539D456B792594BC3CA59D4E0F4C23D4F1FEE73370C1390B2DA245400718D36"
    partition = struct.unpack_from("<I", original, 454)[0] * 512
    files = fat_oracle.Fat(original, partition).files()[0]
    with zipfile.ZipFile(io.BytesIO(files["PACKAGES/BASE/CTMOUSE.ZIP"])) as package:
        driver = package.read("BIN/CTMOUSE.EXE")
    added = {"CTMOUSE.EXE": driver, "MOUSECHK.COM": probe.read_bytes()}
    graphics = probe.with_name("MOUSEGFX.COM")
    if graphics.is_file(): added["MOUSEGFX.COM"] = graphics.read_bytes()
    image = bytearray(1474560)
    image[:3] = b"\xeb\x3c\x90"
    image[3:11] = b"DOSMOUSE"
    struct.pack_into("<HBHBHHBHHHII", image, 11, 512, 1, 1, 2, 224, 2880, 0xF0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[43:54] = b"MOUSE TEST "
    image[54:62] = b"FAT12   "
    image[510:512] = b"\x55\xaa"
    fat = bytearray(9 * 512)
    fat[:3] = b"\xf0\xff\xff"

    def entry(cluster, value):
        """Writes one FAT12 cluster entry while preserving the neighboring nibble."""
        offset = cluster * 3 // 2
        word = int.from_bytes(fat[offset:offset + 2], "little")
        word = (word & 0xF if cluster & 1 else word & 0xF000) | (value << 4 if cluster & 1 else value)
        fat[offset:offset + 2] = word.to_bytes(2, "little")

    cluster = 2
    for index, (name, data) in enumerate(added.items()):
        start = cluster
        for offset in range(0, len(data), 512):
            entry(cluster, cluster + 1 if offset + 512 < len(data) else 0xFFF)
            address = (33 + cluster - 2) * 512
            image[address:address + len(data[offset:offset + 512])] = data[offset:offset + 512]
            cluster += 1
        root = 19 * 512 + index * 32
        stem, suffix = name.split(".")
        image[root:root + 11] = (stem.ljust(8) + suffix).encode("ascii")
        image[root + 11] = 0x20
        struct.pack_into("<HI", image, root + 26, start, len(data))
    image[512:10 * 512] = fat
    image[10 * 512:19 * 512] = fat
    actual = fat_oracle.Fat(image).files()[0]
    assert actual == added
    with destination.open("xb") as output:
        output.write(image)
    return {"baseSha256": digest, "floppySha256": hashlib.sha256(image).hexdigest().upper(),
            "files": {name: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest().upper()} for name, data in added.items()}}


if __name__ == "__main__":
    print(json.dumps(prepare(*sys.argv[1:]), indent=2))
