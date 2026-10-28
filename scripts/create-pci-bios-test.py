# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble the own PCI client and independently configure its own firmware on fresh private media."""
import argparse
import importlib.util
from pathlib import Path
import struct
import subprocess
import sys
import json

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]


def module(name, path):
    """Load an explicitly named existing fixture helper without producing caches."""
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


cd = module('pci_fixture_cd', REPO / 'scripts/create-mscdex-test.py')


def generate(output, firmware, nasm=None):
    """Create a new own COM/floppy/ISO and freeze the selected raw assembler image."""
    output, firmware = cd.safe(output), cd.safe(firmware)
    output.mkdir(parents=True, exist_ok=False)
    assembler = cd.safe(nasm or REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    program, floppy, iso = output / 'PCIBTEST.COM', output / 'pci-bios-test.img', output / 'own-pci-bios.iso'
    completed = subprocess.run([str(assembler), '-Werror', '-f', 'bin', str(REPO / 'tests/guests/pci-bios-test.asm'), '-o', str(program)],
                               cwd=REPO, text=True, capture_output=True, check=False)
    (output / 'assemble.log').write_text(completed.stdout + completed.stderr, encoding='utf-8', newline='\n')
    cd.check(completed.returncode == 0, 'Own PCI client assembly failed; see assemble.log.')
    binary = program.read_bytes()
    cd.check(binary.count(b'PCIBMET1') == 1, 'Own PCI client metadata absent or duplicated.')
    fat = module('pci_fixture_fat12', REPO / 'tests/prepare-font-media.py')
    fat_report = fat.prepare(program, floppy, 'PCIBTEST.COM')
    optical, text, payload = module('pci_fixture_iso', REPO / 'scripts/create-atapi-test.py').own_iso()
    cd.write_new(iso, optical)
    raw = firmware.read_bytes()
    cd.check(len(raw) == 65536 and raw.count(b'PCIMETA1') == 1, 'Selected raw PCI ROM is missing its own manifest.')
    rom = output / 'own-raw-firmware.bin'
    cd.write_new(rom, raw)
    return dict(program=str(program), programBytes=len(binary), programSha256=cd.sha(binary), floppy=str(floppy),
                floppySha256=fat_report['floppySha256'], iso=str(iso), isoSha256=cd.sha(optical), isoBlocks=len(optical) // 2048,
                textSha256=cd.sha(text), payloadSha256=cd.sha(payload), firmware=str(rom), firmwareSha256=cd.sha(raw))


def expected_firmware(firmware, disk, destination):
    """Derive every expected ROM byte from own source artwork, fixed hardware and selected disk size before any VM runs."""
    rom = bytearray(cd.safe(firmware).read_bytes())
    cd.check(len(rom) == 65536, 'A complete own raw firmware image is required.')
    sectors = cd.safe(disk).stat().st_size // 512
    cd.check(cd.safe(disk).stat().st_size == sectors * 512 and 0 < sectors <= 0x10000000, 'Own HDD size is not sector aligned.')
    # The matrix has one writable HDD0, both physical 1.44-MiB drives, PS/2,
    # gameport, COM1, LPT1 and the CT1740 UART at330h/IRQ5. CD remains an IDE target.
    struct.pack_into('<HIB', rom, 0xe000, 31 * 1024, sectors, 1)
    rom[0xe007:0xe011] = bytes([4, 4, 0x80, 18, 18, 0, 0, 2, 1, 0])
    struct.pack_into('<I', rom, 0xe011, 32 * 1024)
    rom[0xe015:0xe018] = bytes([1, 1, 1])
    struct.pack_into('<HB', rom, 0xe018, 0x330, 5)
    struct.pack_into('<I', rom, 0xe040, sectors)
    rom[0xe050] = rom[0xe054] = 1
    rom[0xe058] = 0
    heads = next((n for n in (16, 32, 64, 128, 255) if sectors <= 1024 * n * 63), 255)
    rom[0xe05c] = heads
    rom[0xe060:0xe070] = bytes(16)
    struct.pack_into('<H', rom, 0xe060, max(1, min(1024, sectors // (heads * 63))))
    rom[0xe062], rom[0xe06e] = heads, 63
    for address in (0xe024, 0xe034):
        rom[address], rom[address + 3] = 18, 0x6c
    font = module('pci_fixture_artwork', REPO / 'tests/verify-cp437-frame.py')
    for address, height in ((0xa000, 8), (0xa800, 14), (0xc000, 16)):
        values = font.artwork(height)
        rom[address:address + len(values)] = values
    planar = bytearray(768)
    for index in range(64):
        planar[index * 3:index * 3 + 3] = bytes(42 * ((index >> bit) & 1) + 21 * ((index >> (bit + 3)) & 1) for bit in (2, 1, 0))
    indexed = bytearray(768)
    colors = (0x000000, 0x0000aa, 0x00aa00, 0x00aaaa, 0xaa0000, 0xaa00aa, 0xaa5500, 0xaaaaaa,
              0x555555, 0x5555ff, 0x55ff55, 0x55ffff, 0xff5555, 0xff55ff, 0xffff55, 0xffffff)
    for index, value in enumerate(colors):
        indexed[index * 3:index * 3 + 3] = bytes(((value >> shift) & 255) * 63 // 255 for shift in (16, 8, 0))
    for index in range(16, 40):
        indexed[index * 3:index * 3 + 3] = bytes([(index - 16) * 63 // 23] * 3)
    for index in range(40, 256):
        color = index - 40
        indexed[index * 3:index * 3 + 3] = bytes((color // 36 * 63 // 5, color // 6 % 6 * 63 // 5, color % 6 * 63 // 5))
    rom[0xe100:0xe400], rom[0xe400:0xe700] = planar, indexed
    for before, after in ((b'  |  486 / AT   |   Original firmware - 64 KiB ROM', b' | PCI BIOS CD | Original firmware - 64 KiB ROM'),
                          (b'Configured CPU: 486DX / AT development profile', b'Configured CPU: P54C B5 / PCI BIOS subset')):
        cd.check(rom.count(before + b'\0') == 1 and len(after) <= len(before), 'Own inventory string capacity differs.')
        start = rom.index(before)
        rom[start:start + len(before)] = after.ljust(len(before), b' ')
    cd.write_new(destination, rom)
    return dict(path=str(cd.safe(destination)), bytes=len(rom), sha256=cd.sha(rom), sectors=sectors,
                source='Raw assembled own ROM, independently specified fixed hardware, own artwork and palettes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--firmware', type=Path, required=True)
    parser.add_argument('--nasm', type=Path)
    args = parser.parse_args()
    print(json.dumps(generate(args.output, args.firmware, args.nasm), indent=2))
