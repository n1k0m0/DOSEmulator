"""Build a NEW private FAT12 floppy containing only our assembled font probe.
No operating system, driver or font bytes are copied to this disk.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
fat_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fat_oracle)


def prepare(probe, destination, filename="FONTCP.COM"):
    probe, destination = map(Path, (probe, destination))
    stem, extension = filename.split(".")
    assert 1 <= len(stem) <= 8 and 1 <= len(extension) <= 3
    assert all(char in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789" for char in stem + extension)
    data = probe.read_bytes()
    assert 0 < len(data) <= 65535
    image = bytearray(1474560)
    image[:3] = b"\xeb\x3c\x90"
    image[3:11] = b"FONTTEST"
    struct.pack_into("<HBHBHHBHHHII", image, 11, 512, 1, 1, 2, 224, 2880, 0xF0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[43:54] = b"FONT TEST  "
    image[54:62] = b"FAT12   "
    image[510:512] = b"\x55\xaa"
    fat = bytearray(9 * 512)
    fat[:3] = b"\xf0\xff\xff"
    count = (len(data) + 511) // 512
    for cluster in range(2, 2 + count):
        value = cluster + 1 if cluster < count + 1 else 0xFFF
        offset = cluster * 3 // 2
        word = int.from_bytes(fat[offset:offset + 2], "little")
        word = (word & 0xF if cluster & 1 else word & 0xF000) | (value << 4 if cluster & 1 else value)
        fat[offset:offset + 2] = word.to_bytes(2, "little")
    image[512:10 * 512] = fat
    image[10 * 512:19 * 512] = fat
    image[19 * 512:19 * 512 + 11] = (stem.ljust(8) + extension.ljust(3)).encode("ascii")
    image[19 * 512 + 11] = 0x20
    struct.pack_into("<HI", image, 19 * 512 + 26, 2, len(data))
    image[33 * 512:33 * 512 + len(data)] = data
    assert fat_oracle.Fat(image).files()[0] == {filename: data}
    with destination.open("xb") as output:
        output.write(image)
    return {"bytes": len(data), "probeSha256": hashlib.sha256(data).hexdigest().upper(),
            "floppySha256": hashlib.sha256(image).hexdigest().upper(), "onlyOwnProbe": True}


if __name__ == "__main__":
    print(json.dumps(prepare(*sys.argv[1:]), indent=2))
