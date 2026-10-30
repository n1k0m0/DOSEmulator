#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create a new own FAT12 APM INF disk; original Microsoft drivers are never included."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parents[1]


def create():
    """Build ordinary read-only files and verify every byte through an independent FAT reader."""
    names = {'APM95.INF': 'APM95.INF', 'APM98.INF': 'APM98.INF', 'README.TXT': 'APM-README.txt'}
    files = {name: (ROOT / 'guest-tools/windows95' / source).read_bytes().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
             for name, source in names.items()}
    assert all(0 < len(data) <= 131072 for data in files.values())
    image = bytearray(1474560)
    image[:11] = b'\xeb\x3c\x90DOSEAPM '
    struct.pack_into('<HBHBHHBHHHII', image, 11, 512, 1, 1, 2, 224, 2880, 0xf0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[39:43] = hashlib.sha256(b''.join(files.values())).digest()[:4]
    image[43:54] = b'DOSE APM   '
    image[54:62] = b'FAT12   '
    image[62:66] = b'\xfa\xf4\xeb\xfd'
    image[510:512] = b'\x55\xaa'
    fat = bytearray(4608)
    fat[:3] = b'\xf0\xff\xff'
    next_cluster = 2
    for index, (name, payload) in enumerate(files.items()):
        stem, ext = name.split('.')
        count = (len(payload) + 511) // 512
        start, end = next_cluster, next_cluster + count
        assert end < 2849
        for cluster in range(start, end):
            value = 0xfff if cluster == end - 1 else cluster + 1
            offset = cluster * 3 // 2
            old = int.from_bytes(fat[offset:offset + 2], 'little')
            value = (old & 15) | (value << 4) if cluster & 1 else (old & 0xf000) | value
            fat[offset:offset + 2] = value.to_bytes(2, 'little')
        entry = 19 * 512 + index * 32
        image[entry:entry + 11] = (stem.ljust(8) + ext.ljust(3)).encode('ascii')
        image[entry + 11] = 0x21
        struct.pack_into('<HI', image, entry + 26, start, len(payload))
        offset = (33 + start - 2) * 512
        image[offset:offset + len(payload)] = payload
        next_cluster = end
    image[512:5120] = fat
    image[5120:9728] = fat
    spec = importlib.util.spec_from_file_location('own_apm_fat_reader', ROOT / 'tests/verify-msdos-install.py')
    reader = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(reader)
    actual, allocation = reader.Fat(bytes(image)).files()
    assert actual == files
    return bytes(image), files, allocation


def main():
    """Publish one new private package and content metadata, refusing replacement or an external path."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    assert output.is_relative_to(ROOT / 'artifacts') and output.suffix.lower() == '.img'
    assert output.parent.is_dir() and not output.exists() and not output.with_suffix('.json').exists()
    image, files, allocation = create()
    with output.open('xb') as stream:
        stream.write(image)
    assert output.read_bytes() == image
    result = {'passed': True, 'onlyOwnData': True, 'microsoftBinariesIncluded': False,
              'imageSha256': hashlib.sha256(image).hexdigest().upper(), 'allocation': allocation,
              'files': {name: {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest().upper()} for name, data in files.items()}}
    with output.with_suffix('.json').open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)
        stream.write('\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
