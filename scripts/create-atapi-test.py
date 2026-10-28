# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble an own PACKET client and build a fresh ISO9660/2048 CD plus private FAT12 and HDD fixtures."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]


def safe(path):
    """Resolve one named own destination before any access, excluding personal overlays."""
    path = Path(path).resolve()
    if 'doom2-play' in str(path).casefold():
        raise ValueError('Excluded personal overlay path.')
    return path


def both32(value):
    """Encode ISO9660's distinct little-endian and big-endian fields."""
    return struct.pack('<I', value) + struct.pack('>I', value)


def both16(value):
    """Encode ISO9660's duplicate 16-bit endian fields."""
    return struct.pack('<H', value) + struct.pack('>H', value)


def directory(extent, size, name, flags=0):
    """Construct one bounded ISO9660 directory record with the required identifier padding."""
    length = 33 + len(name) + int(len(name) % 2 == 0)
    record = bytearray(length)
    record[0] = length
    record[2:10] = both32(extent)
    record[10:18] = both32(size)
    record[18:25] = bytes([126, 10, 27, 0, 0, 0, 0])
    record[25] = flags
    record[28:32] = both16(1)
    record[32] = len(name)
    record[33:33 + len(name)] = name
    return record


def own_iso():
    """Build a single-session read-only Mode1 data image containing only reproducible own bytes."""
    image = bytearray(128 * 2048)
    pvd = bytearray(2048)
    pvd[:7] = b'\x01CD001\x01'
    pvd[8:40] = b'DOSE'.ljust(32, b' ')
    pvd[40:72] = b'DOSE_OWN_ATAPI'.ljust(32, b' ')
    pvd[80:88] = both32(128)
    pvd[120:124] = both16(1)
    pvd[124:128] = both16(1)
    pvd[128:132] = both16(2048)
    pvd[132:140] = both32(10)
    struct.pack_into('<I', pvd, 140, 18)
    struct.pack_into('>I', pvd, 148, 19)
    pvd[156:190] = directory(20, 2048, b'\x00', 2)
    for start, size, value in [(190, 128, b'DOSE OWN VALIDATION'), (318, 128, b'DOSE'), (446, 128, b'DOSE ATAPI TEST'),
                               (574, 128, b'OWN DATA ONLY')]:
        pvd[start:start + size] = value.ljust(size, b' ')
    for start in [813, 830]:
        pvd[start:start + 17] = b'2026102700000000\x00'
    pvd[847:864] = b'0000000000000000\x00'
    pvd[864:881] = b'0000000000000000\x00'
    pvd[881] = 1
    image[16 * 2048:17 * 2048] = pvd
    image[17 * 2048:17 * 2048 + 7] = b'\xffCD001\x01'
    image[18 * 2048:18 * 2048 + 10] = bytes([1, 0]) + struct.pack('<IH', 20, 1) + b'\x00\x00'
    image[19 * 2048:19 * 2048 + 10] = bytes([1, 0]) + struct.pack('>IH', 20, 1) + b'\x00\x00'
    text = (b'DOSE OWN ISO9660 CD. Immutable 2048-byte blocks, genuine PACKET PIO and original DOS.\r\n' * 3)[:197]
    payload = bytes((i * 29 + (i >> 8) * 7 + 0x53) & 255 for i in range(8205))
    records = [directory(20, 2048, b'\x00', 2), directory(20, 2048, b'\x01', 2),
               directory(21, len(text), b'IDENT.TXT;1'), directory(22, len(payload), b'PAYLOAD.BIN;1')]
    root = b''.join(records)
    image[20 * 2048:20 * 2048 + len(root)] = root
    image[21 * 2048:21 * 2048 + len(text)] = text
    image[22 * 2048:22 * 2048 + len(payload)] = payload
    return bytes(image), text, payload


def generate(output, nasm=None):
    """Create a wholly new own fixture directory; no existing disk or operating-system file is read."""
    output = safe(output)
    output.mkdir(parents=True, exist_ok=False)
    nasm = safe(nasm or REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    program, floppy, iso = output / 'ATAPI.COM', output / 'atapi-test.img', output / 'own-atapi.iso'
    process = subprocess.run([str(nasm), '-Werror', '-f', 'bin', str(REPO / 'tests/guests/atapi-test.asm'), '-o', str(program)],
                             cwd=REPO, text=True, capture_output=True, check=False)
    (output / 'assemble.log').write_text(process.stdout + process.stderr, encoding='utf-8', newline='\n')
    if process.returncode:
        raise RuntimeError(f'Own ATAPI assembly failed ({process.returncode}); see {output / "assemble.log"}')
    spec = importlib.util.spec_from_file_location('atapi_own_fat12', REPO / 'tests/prepare-font-media.py')
    fat = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fat)
    report = fat.prepare(program, floppy, 'ATAPI.COM')
    binary = program.read_bytes()
    if binary.count(b'ATMETA01') != 1 or b'ATAPIP01' not in binary:
        raise AssertionError('Own ATAPI executable metadata missing.')
    optical, text, payload = own_iso()
    with iso.open('xb') as stream:
        stream.write(optical)
    disks = []
    for number, seed in enumerate([0x31, 0xD1]):
        path = output / ('scratch.img' if number == 0 else 'auxiliary.img')
        data = bytes((sector * 13 + position * 17 + (position >> 8) * 7 + seed) & 255
                     for sector in range(4096) for position in range(512))
        with path.open('xb') as stream:
            stream.write(data)
        disks.append(dict(path=str(path), sha256=hashlib.sha256(data).hexdigest().upper()))
    second = output / 'second-floppy.img'
    with second.open('xb') as stream:
        stream.write(bytes([0xD6]) * 1474560)
    return dict(program=str(program), media=str(floppy), bytes=len(binary), onlyOwnProbe=True,
                programSha256=hashlib.sha256(binary).hexdigest().upper(), mediaSha256=report['floppySha256'],
                optical=dict(path=str(iso), sha256=hashlib.sha256(optical).hexdigest().upper(), blockSize=2048, blocks=128,
                             payloadSha256=hashlib.sha256(payload).hexdigest().upper(), textSha256=hashlib.sha256(text).hexdigest().upper()),
                scratch=disks[0], auxiliaries=disks[1:], secondFloppy=str(second),
                secondFloppySha256=hashlib.sha256(bytes([0xD6]) * 1474560).hexdigest().upper())


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--nasm', type=Path)
    options = parser.parse_args()
    print(json.dumps(generate(options.output, options.nasm), indent=2))
