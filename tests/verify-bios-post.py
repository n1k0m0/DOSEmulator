# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Check an own halted boot sector's complete POST against an explicit fixture.

The expected inventory comes from input files/profile, not observed VRAM. Glyphs
come from the project's original artwork. No operating system or game is used.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct


def expected_lines(fixture):
    """Describe the intended POST layout independently of firmware rendering."""
    lines = [
        '  +-------------+   DOSE - DosEmulator',
        '  |  > DOSE_    |   ' + fixture['biosName'] + ' ' + fixture['biosVersion'],
        '  |  486 / AT   |   Original firmware - 64 KiB ROM',
        '  +-------------+   Copyright 2026 Nils Kopal', '',
        'Configured CPU: 486DX / AT development profile',
        f"Memory: {fixture['ramMiB'] * 1024} KiB installed / 639 KiB conventional",
        'Video: ' + ('Tseng ET4000AX ISA / 1024 KiB' if fixture['et4000'] else 'DOSE VGA')]
    for number, disk in enumerate(fixture['disks']):
        channel = 'primary' if disk['slot'] < 2 else 'secondary'
        position = 'slave:  ' if disk['slot'] & 1 else 'master: '
        access = 'read/write' if disk['writable'] else 'read only'
        lines.append(f"IDE {0x80 + number:02X}h {channel} {position}{disk['bytes'] // (1024*1024)} MiB [{access}]")
    lines.append(f"Floppy: A={fixture['floppyA']} / B={fixture['floppyB']}")
    if fixture.get('gameport'):
        lines.append('Game control: IBM analog / 201h')
    if fixture.get('serial'):
        lines.append('COM1: National PC16550D / 3F8h IRQ4')
    if fixture.get('parallel'):
        lines.append('LPT1: IBM Printer Adapter / 378h IRQ7')
    if fixture.get('midiPort'):
        lines.append(f"MIDI: CT1740 MPU-UART / {fixture['midiPort']:03X}h IRQ{fixture['midiIrq']}")
    lines.extend(['Boot target: IDE drive 80h', '', 'POST: IVT/BDA/EBDA, PIC/PIT and A20 initialized'])
    if fixture['floppyA'] != 'none' or fixture['floppyB'] != 'none':
        lines.append('POST: floppy controller reset complete')
    lines.extend(['POST: IDE controllers reset', 'POST: keyboard initialized; loading boot sector'])
    assert len(lines) < 25 and all(len(line) <= 80 for line in lines)
    return lines


def verify(frame, report, memory, fixture, rom):
    """Compare every text cell/pixel, cursor, ROM identity and successful boot."""
    lines = expected_lines(fixture)
    status = report['status']
    assert status['fault'] is None and status['state'] == 'Halted'
    assert status['position'] == '0000:00007C02' and report['postCode'] == 0x20
    assert status['configuration']['ramMiB'] == fixture['ramMiB']
    if fixture.get('midiPort'):
        assert status['configuration']['midiInterface'] == 'ct1740-mpu401-uart-development'
        assert report['midi']['basePort'] == fixture['midiPort'] and report['sb16']['irq'] == fixture['midiIrq']
        assert not report['midi']['uartMode'] and not report['midi']['hostMidi'] and not report['midi']['synthesizer']
    assert report['ataSectorsRead'] == 1 and report['ataSectorsWritten'] == 0
    assert report['floppySectorsRead'] == report['floppySectorsWritten'] == 0
    assert report['inputBytesSent'] == report['inputBytesTotal'] == 0
    assert status['debugOutput'] == '\r\n'.join(lines) + '\r\n'
    assert 0 <= status['virtualNanoseconds'] < 10_000_000, 'Fixture must capture the first cursor-on frame'
    assert len(memory) == fixture['ramMiB'] * 1024 * 1024
    assert memory[0x449] == 3 and memory[0x484] == 24
    assert struct.unpack_from('<H', memory, 0x44a)[0] == 80
    assert struct.unpack_from('<H', memory, 0x450)[0] == len(lines) * 256
    assert struct.unpack_from('<H', memory, 0x460)[0] == 0x0e0f
    if all(name in fixture for name in ('gameport', 'serial', 'parallel')):
        equipment = (0x1000 if fixture['gameport'] else 0) | (0x200 if fixture['serial'] else 0) | (0x4000 if fixture['parallel'] else 0)
        assert struct.unpack_from('<H', memory, 0x410)[0] & 0xde00 == equipment
        assert struct.unpack_from('<H', memory, 0x400)[0] == (0x3f8 if fixture['serial'] else 0)
        assert struct.unpack_from('<H', memory, 0x408)[0] == (0x378 if fixture['parallel'] else 0)
        assert memory[0x478] == (1 if fixture['parallel'] else 0)
    # The private dump contains backing RAM, not MMIO/ROM. Check the separately
    # assembled own ROM rather than pretending its zero backing bytes are mapped.
    assert len(rom) == 65536 and rom[0xfff5:0xfffd] == fixture['biosDate'].encode('ascii')
    assert memory[0x7c00:0x7c04] == bytes([0xfa, 0xf4, 0xeb, 0xfd])
    assert memory[0x7dfe:0x7e00] == b'\x55\xaa'
    spec = importlib.util.spec_from_file_location('font_art', Path(__file__).with_name('verify-cp437-frame.py'))
    font_art = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(font_art)
    font = font_art.artwork(16)
    cells = [(32, 0x1f if row < 4 else 0x07) for row in range(25) for _ in range(80)]
    for row, line in enumerate(lines):
        for col, code in enumerate(line.encode('ascii')):
            cells[row*80 + col] = (code, 0x1f if row < 4 else 0x07)
    magic, size, maximum, pixels = frame.split(b'\n', 3)
    assert (magic, size, maximum) == (b'P6', b'640 400', b'255')
    palette = [(0,0,0), (0,0,170), (0,170,0), (0,170,170), (170,0,0), (170,0,170),
               (170,85,0), (170,170,170), (85,85,85), (85,85,255), (85,255,85), (85,255,255),
               (255,85,85), (255,85,255), (255,255,85), (255,255,255)]
    expected = bytearray()
    argb = bytearray()
    for y in range(400):
        for x in range(640):
            code, attr = cells[(y//16)*80 + x//8]
            glyph = bool(font[code*16 + y%16] & (0x80 >> (x%8)))
            cursor = y//16 == len(lines) and x < 8 and y%16 >= 14
            rgb = palette[attr & 15 if glyph or cursor else (attr >> 4) & 7]
            expected.extend(rgb)
            argb.extend((rgb[2], rgb[1], rgb[0], 255))
    assert pixels == expected, 'POST pixel mismatch'
    assert report['frameHash'] == hashlib.sha256(argb).hexdigest().upper()
    return dict(passed=True, pixelsVerified=256000, renderedCellsVerified=2000, cursorPixels=16,
                logoRows=4, biosName=fixture['biosName'], biosVersion=fixture['biosVersion'],
                actualGuestBoot=True, originalMediaUsed=False, nativeInputSent=False)


def negative_controls(frame, report, memory, fixture, rom):
    """Prove that independent corruption of image, state and identity is rejected."""
    cases = []
    changed = bytearray(frame)
    changed[-1] ^= 1
    cases.append((bytes(changed), report, memory, fixture, rom))
    for address in [0x449, 0x450, 0x460, 0x7c00]:
        changed = bytearray(memory)
        changed[address] ^= 1
        cases.append((frame, report, bytes(changed), fixture, rom))
    if all(name in fixture for name in ('gameport', 'serial', 'parallel')):
        for address in [0x400, 0x408, 0x411, 0x478]:
            changed = bytearray(memory)
            changed[address] ^= 2 if address == 0x411 else 1
            cases.append((frame, report, bytes(changed), fixture, rom))
    changed = bytearray(rom)
    changed[0xfff5] ^= 1
    cases.append((frame, report, memory, fixture, bytes(changed)))
    changed = copy.deepcopy(report)
    changed['status']['debugOutput'] = changed['status']['debugOutput'].replace('IDE controllers reset', 'IDE reset skipped')
    cases.append((frame, changed, memory, fixture, rom))
    changed = dict(fixture, biosVersion='0.0.0-wrong')
    cases.append((frame, report, memory, changed, rom))
    for case in cases:
        try:
            verify(*case)
        except AssertionError:
            continue
        raise AssertionError('Corrupted POST evidence accepted')
    return len(cases)


def main():
    """Read explicit fixture/evidence paths and emit a small auditable result."""
    parser = argparse.ArgumentParser()
    for name in ['frame', 'report', 'memory', 'fixture', 'rom']:
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    inputs = (args.frame.read_bytes(), json.loads(args.report.read_text(encoding='utf-8-sig')),
              args.memory.read_bytes(), json.loads(args.fixture.read_text(encoding='utf-8-sig')), args.rom.read_bytes())
    result = verify(*inputs)
    result['negativeControlsRejected'] = negative_controls(*inputs) if args.self_test else 0
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
