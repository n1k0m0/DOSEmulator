# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble only the own S3 Trio64 PCI/bank/LFB COM and create a new private FAT12 floppy; no operating-system bytes."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

REPO = Path(__file__).resolve().parents[1]


def safe(path):
    """Resolve one explicitly named destination while excluding personal game overlays."""
    path = Path(path).resolve()
    if 'doom2-play' in str(path).lower():
        raise ValueError('Excluded personal overlay path.')
    return path


def generate(output, nasm=None):
    """Create a fresh directory and assemble/independently parse only our own executable bytes."""
    output = safe(output)
    output.mkdir(parents=True, exist_ok=False)
    nasm = safe(nasm or REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    program, floppy = output / 'S3TRIO.COM', output / 's3-trio64.img'
    process = subprocess.run([str(nasm), '-Werror', '-f', 'bin', str(REPO / 'tests/guests/s3-trio64.asm'), '-o', str(program)],
                             cwd=REPO, text=True, capture_output=True, check=False)
    (output / 'assemble.log').write_text(process.stdout + process.stderr, encoding='utf-8', newline='\n')
    if process.returncode:
        raise RuntimeError(f'Own PIIX assembly failed ({process.returncode}); see {output / "assemble.log"}')
    spec = importlib.util.spec_from_file_location('piix_own_fat12', REPO / 'tests/prepare-font-media.py')
    own = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(own)
    report = own.prepare(program, floppy, 'S3TRIO.COM')
    data = program.read_bytes()
    if data.count(b'S3META01') != 1 or b'S3PROOF1' not in data:
        raise AssertionError('Own Trio64 executable metadata missing.')
    disks = []
    for number, seed in enumerate([0x31, 0x91, 0xD1]):
        image = output / ('scratch.img' if number == 0 else f'auxiliary-{number}.img')
        payload = bytes((sector * 13 + position * 17 + (position >> 8) * 7 + seed) & 255
                        for sector in range(4096) for position in range(512))
        with image.open('xb') as stream:
            stream.write(payload)
        disks.append(dict(path=str(image), sha256=hashlib.sha256(payload).hexdigest().upper()))
    second_floppy = output / 'second-floppy.img'
    with second_floppy.open('xb') as stream:
        stream.write(bytes([0xD6]) * 1474560)
    return dict(program=str(program), media=str(floppy), bytes=len(data), onlyOwnProbe=True, scratch=disks[0], auxiliaries=disks[1:],
                secondFloppy=str(second_floppy), secondFloppySha256=hashlib.sha256(bytes([0xD6]) * 1474560).hexdigest().upper(),
                programSha256=hashlib.sha256(data).hexdigest().upper(), mediaSha256=report['floppySha256'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--nasm', type=Path)
    arguments = parser.parse_args()
    print(json.dumps(generate(arguments.output, arguments.nasm), indent=2))