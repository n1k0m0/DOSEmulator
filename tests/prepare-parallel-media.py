# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create private FAT12 media with our LPT probe and a tab-free DOS COPY fixture.

FreeDOS cooked PRN output expands tabs using its global console column. The
separate printable fixture makes COPY byte-exact across both guest kernels;
the probe independently sends all 256 byte values through the real port.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys


def prepare(probe, destination, copy_source):
    """Write two new own files and validate both FAT chains without copying any OS bytes."""
    probe, destination, copy_source = map(Path, (probe, destination, copy_source))
    program = probe.read_bytes()
    assert 0 < len(program) <= 65535
    payload = b'DOSE LPT1 COPY /B\r\n' + b''.join(
        f'{row:02X}: '.encode('ascii') + bytes(range(32, 127)) + b'\r\n' for row in range(16))
    files = {'LPT.COM': program, 'LPTDATA.TXT': payload}
    image = bytearray(1474560)
    image[:3] = b'\xeb\x3c\x90'
    image[3:11] = b'DOSELPT '
    struct.pack_into('<HBHBHHBHHHII', image, 11, 512, 1, 1, 2, 224, 2880, 0xf0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[43:54] = b'DOSE LPT   '
    image[54:62] = b'FAT12   '
    image[510:512] = b'\x55\xaa'
    fat = bytearray(9 * 512)
    fat[:3] = b'\xf0\xff\xff'
    first = 2
    for index, (name, data) in enumerate(files.items()):
        count = (len(data) + 511) // 512
        for cluster in range(first, first + count):
            value = cluster + 1 if cluster + 1 < first + count else 0xfff
            offset = cluster * 3 // 2
            word = int.from_bytes(fat[offset:offset + 2], 'little')
            word = (word & 15 if cluster & 1 else word & 0xf000) | (value << 4 if cluster & 1 else value)
            fat[offset:offset + 2] = word.to_bytes(2, 'little')
        entry = 19 * 512 + index * 32
        stem, extension = name.split('.')
        image[entry:entry + 11] = (stem.ljust(8) + extension.ljust(3)).encode('ascii')
        image[entry + 11] = 0x20
        struct.pack_into('<HI', image, entry + 26, first, len(data))
        start = (33 + first - 2) * 512
        image[start:start + len(data)] = data
        first += count
    image[512:10 * 512] = fat
    image[10 * 512:19 * 512] = fat
    spec = importlib.util.spec_from_file_location('fat_oracle', Path(__file__).with_name('verify-msdos-install.py'))
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    assert oracle.Fat(image).files()[0] == files
    with copy_source.open('xb') as output:
        output.write(payload)
    with destination.open('xb') as output:
        output.write(image)
    return dict(passed=True, onlyOwnFiles=True, copyBytes=len(payload), programBytes=len(program),
                fileSha256={name: hashlib.sha256(data).hexdigest().upper() for name, data in files.items()},
                floppySha256=hashlib.sha256(image).hexdigest().upper())


if __name__ == '__main__':
    print(json.dumps(prepare(*sys.argv[1:]), indent=2))
