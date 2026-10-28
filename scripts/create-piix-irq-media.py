# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble only the own PIIX IRQ COM and create a new private FAT12 floppy; no operating-system bytes."""
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
    program, floppy = output / 'PIRQIRQ.COM', output / 'piix-irq.img'
    process = subprocess.run([str(nasm), '-Werror', '-f', 'bin', str(REPO / 'tests/guests/piix-irq.asm'), '-o', str(program)],
                             cwd=REPO, text=True, capture_output=True, check=False)
    (output / 'assemble.log').write_text(process.stdout + process.stderr, encoding='utf-8', newline='\n')
    if process.returncode:
        raise RuntimeError(f'Own PIIX assembly failed ({process.returncode}); see {output / "assemble.log"}')
    spec = importlib.util.spec_from_file_location('piix_own_fat12', REPO / 'tests/prepare-font-media.py')
    own = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(own)
    report = own.prepare(program, floppy, 'PIRQIRQ.COM')
    data = program.read_bytes()
    if data.count(b'PIXMETA1') != 1 or b'PIIXIRQ1' not in data:
        raise AssertionError('Own IRQ executable metadata missing.')
    return dict(program=str(program), media=str(floppy), bytes=len(data), onlyOwnProbe=True,
                programSha256=hashlib.sha256(data).hexdigest().upper(), mediaSha256=report['floppySha256'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--nasm', type=Path)
    arguments = parser.parse_args()
    print(json.dumps(generate(arguments.output, arguments.nasm), indent=2))