#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Package the already assembled own clock installer on a new read-only FAT12 data disk. Never run its PE."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


def require(value, message):
    """Reject unexpected own inputs or an attempted overwrite before publication."""
    if not value:
        raise ValueError(message)


def private(path):
    """Resolve only explicit build artifacts, excluding the personal play tree before access."""
    value = Path(path).absolute()
    require('doom2-play' not in [p.casefold() for p in value.parts], 'Personal overlays are excluded.')
    value = value.resolve()
    require(value.is_relative_to(ROOT / 'artifacts') and 'doom2-play' not in [p.casefold() for p in value.parts],
            'The own package must stay inside artifacts.')
    return value


def floppy(binary):
    """Build ordinary FAT12 with a content-derived serial, duplicate FATs and one read-only SETUP.EXE."""
    sectors = (len(binary) + 511) // 512
    require(1 <= sectors <= 2847, 'Installer exceeds the own 1.44 MiB data disk.')
    image = bytearray(1474560)
    image[:11] = b'\xeb\x3c\x90DOSETOOL'
    struct.pack_into('<HBHBHHBHHHII', image, 11, 512, 1, 1, 2, 224, 2880, 0xf0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    image[39:43] = hashlib.sha256(binary).digest()[:4]
    image[43:54] = b'DOSE TOOLS '
    image[54:62] = b'FAT12   '
    image[62:66] = b'\xfa\xf4\xeb\xfd'
    image[510:512] = b'\x55\xaa'
    fat = bytearray(4608)
    fat[:3] = b'\xf0\xff\xff'
    for cluster in range(2, 2 + sectors):
        following = cluster + 1 if cluster < sectors + 1 else 0xfff
        offset = cluster + cluster // 2
        word = struct.unpack_from('<H', fat, offset)[0]
        word = (word & 15) | (following << 4) if cluster & 1 else (word & 0xf000) | following
        struct.pack_into('<H', fat, offset, word)
    image[512:5120] = fat
    image[5120:9728] = fat
    image[19 * 512:19 * 512 + 11] = b'DOSE TOOLS '
    image[19 * 512 + 11] = 8
    entry = 19 * 512 + 32
    image[entry:entry + 11] = b'SETUP   EXE'
    image[entry + 11] = 0x21
    struct.pack_into('<HH', image, entry + 22, 0, ((2026 - 1980) << 9) | (10 << 5) | 28)
    struct.pack_into('<HI', image, entry + 26, 2, len(binary))
    image[33 * 512:33 * 512 + len(binary)] = binary
    # The existing independent reader validates geometry, allocation, both FATs and the entire file.
    spec = importlib.util.spec_from_file_location('dose_time_fat_oracle', ROOT / 'tests/verify-msdos-install.py')
    reader = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(reader)
    files, allocation = reader.Fat(bytes(image)).files()
    require(files == {'SETUP.EXE': binary}, 'Independent FAT reader found a changed installer or extra file.')
    return bytes(image), allocation


def main():
    """Bind the exact assembler result and publish only new own package artifacts."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', type=Path, required=True, help='The new build-dose-time output directory.')
    parser.add_argument('--output', type=Path, required=True, help='New own FAT12 .img path.')
    args = parser.parse_args()
    source, output = private(args.build), private(args.output)
    require(output.suffix.casefold() == '.img' and output.parent.is_dir(), 'Need an existing parent and a new .img name.')
    report_path = output.with_suffix('.json')
    require(not output.exists() and not report_path.exists(), 'Package output already exists.')
    metadata = json.loads((source / 'build.json').read_text(encoding='utf-8'))
    binary = (source / 'DOSETIME.EXE').read_bytes()
    digest = hashlib.sha256(binary).hexdigest().upper()
    require(metadata['passed'] and metadata['onlyNasmExecuted'] and not metadata['hostPeExecuted'] and
            metadata['programSha256'] == digest and metadata['programBytes'] == len(binary),
            'Assembler metadata does not bind the complete own executable.')
    image, allocation = floppy(binary)
    with output.open('xb') as stream:
        stream.write(image)
    require(output.read_bytes() == image, 'New package reread differs.')
    report = {'passed': True, 'kind': 'DOSE-TOOLS-TIME-FAT12-1', 'path': str(output), 'bytes': len(image),
              'sha256': hashlib.sha256(image).hexdigest().upper(), 'programBytes': len(binary), 'programSha256': digest,
              'file': 'SETUP.EXE', 'fileAttribute': '21h=read-only+archive', 'onlyOwnData': True,
              'nonBootDataDisk': True, 'allocation': allocation}
    with report_path.open('x', encoding='utf-8') as stream:
        json.dump(report, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'passed': True, 'image': str(output), 'bytes': len(image), 'programSha256': digest}))


if __name__ == '__main__':
    main()
