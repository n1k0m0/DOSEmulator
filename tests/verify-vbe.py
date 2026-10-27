# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently certify VBE structures, real bank calls and the existing whole-pixel ET4000 oracle."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import re
import struct


def check(ok, reason):
    """Fail closed on missing firmware, guest-memory or physical-port evidence."""
    if not ok:
        raise AssertionError(reason)


def verify(args):
    """Verify the bounded VBE contract without importing the emulator or deriving expected values from it."""
    mode = int(args.mode, 0)
    legacy, width, height, pages = {0x101: (0x2e, 640, 480, 2), 0x103: (0x30, 800, 600, 1), 0x105: (0x38, 1024, 768, 0)}[mode]
    spec = importlib.util.spec_from_file_location('et_pixel_oracle', Path(__file__).with_name('verify-et4000.py'))
    et = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(et)
    legacy_args = copy.copy(args)
    legacy_args.mode = str(legacy)
    legacy_args.vbe = True
    pixel_result = et.verify(legacy_args)
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    memory, program, rom = args.memory.read_bytes(), args.program.read_bytes(), args.rom.read_bytes()
    io = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    pointer = re.search(r'ET4000-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    check(pointer is not None, 'actual program proof pointer')
    base = int(pointer[1], 16) * 16
    proof = base + 256 + program.index(b'VBE12001')
    check(len(rom) == 65536, 'own firmware image size')

    def physical(address):
        """Translate classic 4-KiB tables independently, retaining a clear separation from ROM bytes."""
        if not report['cpu']['pagingEnabled']:
            return address
        pde = struct.unpack_from('<I', memory, (report['cpu']['cr3'] & ~4095) + (address >> 22) * 4)[0]
        check(pde & 1 and not pde & 128, 'classic proof directory')
        pte = struct.unpack_from('<I', memory, (pde & ~4095) + (address >> 12 & 1023) * 4)[0]
        check(pte & 1, 'proof page')
        return (pte & ~4095) + (address & 4095)

    def read(ram, address, count):
        """Read only translated RAM; ROM is checked against the separate tested firmware artifact."""
        return bytes(ram[physical(a)] for a in range(address, address + count))

    def check_memory(ram):
        """Validate every returned structure byte, actual guards and both complete bank-walk proofs."""
        p = read(ram, proof, 1432)
        check(p[:8] == b'VBE12001' and struct.unpack_from('<8H', p, 8) == (0xbeef, 70, 14, 255, 4, 16, mode, 0xffff), 'VBE counts/status/boundary/mode')
        check(struct.unpack_from('<16H', p, 24) == tuple(range(16)), 'all INT bank readbacks')
        check(struct.unpack_from('<16H', p, 56) == tuple(range(16)), 'all far-call bank readbacks')
        check(struct.unpack_from('<16H', p, 88) == tuple(0xb000 + n for n in range(16)), 'full-MiB checked word values')
        check(p[120:136] == bytes([0xa7]) * 16 and p[1416:] == bytes([0xa7]) * 16, 'actual information guards')
        check(p[1160:1416] == bytes([0xa7]) * 256, 'rejected calls leave the whole buffer')
        controller = p[136:392]
        check(controller[:6] == b'VESA\x02\x01' and controller[10:14] == bytes(4) and struct.unpack_from('<H', controller, 18)[0] == 16, 'controller signature/version/capabilities/MiB')
        check(controller[20:] == bytes(236), 'only 1.x information, zero unused fields')
        oem, modes = struct.unpack_from('<I', controller, 6)[0], struct.unpack_from('<I', controller, 14)[0]
        check(oem >> 16 == modes >> 16 == 0xf000, 'real own-ROM far pointers')
        check(rom[modes & 65535:(modes & 65535) + 8] == struct.pack('<4H', 0x101, 0x103, 0x105, 0xffff), 'actual advertised immutable mode list')
        start = oem & 65535
        end = rom.index(0, start, min(start + 128, len(rom)))
        check(rom[start:end].startswith(b'DOSEBios ') and b'ET4000AX ISA 1 MiB banked subset' in rom[start:end], 'honest own OEM string')
        callbacks = []
        for index, (w, h, page_count) in enumerate(((640, 480, 2), (800, 600, 1), (1024, 768, 0))):
            record = p[392 + index * 256:648 + index * 256]
            callback = struct.unpack_from('<I', record, 12)[0]
            check(callback >> 16 == 0xf000 and rom[callback & 65535:(callback & 65535) + 2] == b'\x9c\x55', 'actual far-window entry')
            expected = bytearray(256)
            struct.pack_into('<HBBHHHHIHHH9B', expected, 0, 0x1b, 7, 0, 64, 64, 0xa000, 0, callback, w, w, h, 8, 16, 1, 8, 1, 4, 0, page_count, 1)
            check(record == expected, 'all 256 independent mode-info bytes')
            callbacks.append(callback)
        check(len(set(callbacks)) == 1, 'same supported window contract in each mode')

    def check_ports(rows):
        """Check only the VBE discovery/bank phase, distinguishing direct callbacks from INT calls."""
        active, text = False, ''
        banks, requests = [], []
        for item in rows:
            if item['kind'] == 'io' and item['port'] == 0xe9 and item['write']:
                text = (text + chr(item['value'] & 255))[-80:]
                if 'VBE-BEGIN' in text:
                    active = True
                if 'VBE-END' in text:
                    break
            if not active:
                continue
            if item['kind'] == 'int10':
                requests.append(item['ax'] & 65535)
            if item['kind'] == 'io' and item['port'] == 0x3cd and item['write']:
                check(item['width'] == 1, 'actual byte bank latch')
                banks.append(item['value'])
        check(len(banks) == 50 and banks[-32:] == [n * 17 for n in range(16)] + [n * 17 for n in range(15, -1, -1)], 'actual INT and reverse far-call bank walks')
        check(banks[:-32] == [0] + list(range(16)) + [0], 'actual initial full-memory clear')
        expected = {0x4f00: 2, 0x4f01: 6, 0x4f02: 2, 0x4f03: 1, 0x4f05: 51, 0x4f04: 1, 0x4f06: 1, 0x4f07: 1, 0x4f08: 1, 0x4f09: 1, 0x4f0a: 1}
        check({key: requests.count(key) for key in set(requests)} == expected, 'actual supported and rejected INT10 attempts')

    check_memory(memory)
    check_ports(io)
    rejected = 0
    if args.negative_controls:
        for at in (0, 8, 10, 12, 14, 16, 18, 20, 24, 54, 56, 86, 88, 118, 120, 135, 136, 140, 146, 150, 154, 160,
                   392, 394, 396, 398, 400, 404, 408, 410, 412, 416, 421, 422, 423, 648, 904, 1159, 1160, 1415, 1416, 1431):
            changed = bytearray(memory)
            changed[physical(proof + at)] ^= 1
            try:
                check_memory(changed)
            except (AssertionError, ValueError, IndexError, struct.error):
                rejected += 1
            else:
                raise AssertionError('accepted corrupted VBE information/bank/guard proof')
        changed = copy.deepcopy(io)
        started, text = False, ''
        for item in changed:
            if item['kind'] == 'io' and item['port'] == 0xe9 and item['write']:
                text = (text + chr(item['value'] & 255))[-80:]
                started |= 'VBE-BEGIN' in text
            if started and item['kind'] == 'io' and item['port'] == 0x3cd and item['write']:
                item['value'] ^= 16
                break
        try:
            check_ports(changed)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted a corrupted physical VBE bank')
    return dict(passed=True, vbeMode=mode, legacyMode=legacy, width=width, height=height,
                vbeSuccessfulCalls=70, rejectedCalls=14, coreModeSets=3, fullMiBComparedViaVbe=True,
                farWindowCalls=16, bankWindows=1, negativeControlsRejected=rejected + pixel_result['negativeControlsRejected'],
                vbeNegativeControlsRejected=rejected, legacyAndWholePixelOracle=pixel_result,
                scope='VBE 1.2-format banked subset 00/01/02/03/05; not complete VBE conformance')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('report', 'memory', 'program', 'frame', 'io', 'rom'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--mode', required=True)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
