# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent DOS/RAM/PIO/persistence oracle for four actual IDE devices."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def check(value, reason):
    """Fail closed on a missing guest, memory or actual-port invariant."""
    if not value:
        raise AssertionError(reason)


def verify(args):
    """Verify executable bytes, every transferred word and both cold-start phases without emulator imports."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    ram, program = args.memory.read_bytes(), args.program.read_bytes()
    io = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    capacities = (65536, 4096, 16384, 3145728)
    expected_heads = (15, 15, 15, 63)
    match = re.search(r'HD4-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    check(match is not None, 'missing guest pointer')
    segment, offset = (int(x, 16) for x in match.groups())
    check(offset == program.index(b'HD4TEST1') + 256, 'pointer outside own program')
    base, proof = segment * 16, segment * 16 + offset
    immutable = program.index(b'HD4MUTBL') + 8

    def physical(linear):
        """Walk ordinary 4-KiB guest tables independently for V86 proof/code addresses."""
        if not report['cpu']['pagingEnabled']:
            return linear
        pde = struct.unpack_from('<I', ram, (report['cpu']['cr3'] & ~4095) + (linear >> 22) * 4)[0]
        check(pde & 1 and not pde & 128, 'classic directory')
        pte = struct.unpack_from('<I', ram, (pde & ~4095) + (linear >> 12 & 1023) * 4)[0]
        check(pte & 1, 'proof page')
        return (pte & ~4095) + (linear & 4095)

    def read(memory, address, size):
        """Read only translated private RAM; never inspect user-play COWs."""
        return bytes(memory[physical(a)] for a in range(address, address + size))

    def check_report(r):
        """Require actual DOS return, completed KBC input and the exact four persistent bindings."""
        marker = 'HD4-PERSIST-OK' if args.persist else 'HD4-WRITE-OK'
        check(r['status']['fault'] is None and marker in r['status']['debugOutput'] and 'HD4-FAILED' not in r['status']['debugOutput'], 'guest failure')
        check('HD4-DOS-RETURN' in r['text'], 'actual DOS return')
        check(r['scenario']['complete'] and r['scenario']['inputBytesSent'] == r['scenario']['inputBytesTotal'], 'actual KBC input')
        disks = r['status']['hardDisks']
        check(len(disks) == 4 and [d['slot'] for d in disks] == list(range(4)), 'four physical slots')
        check(tuple(d['bytes'] // 512 for d in disks) == capacities and all(d['writable'] for d in disks), 'capacities/private overlays')
        check(r['ataSectorsWritten'] == (0 if args.persist else 5) and r['floppySectorsWritten'] == 0, 'write counts')
        check(r['ataSectorsRead'] >= 5 and r['floppySectorsRead'] > 0, 'real ATA/FDC loads')
        if args.manager:
            check(r['cpu']['protectedMode'] and r['cpu']['pagingEnabled'] and r['cpu']['virtual8086Instructions'] > 0, 'actual EMM386 path')

    def check_ram(memory):
        """Validate the guest's complete pattern loop, capacity/geometry proof and unchanged executed prefix."""
        check(read(memory, base + 256, immutable) == program[:immutable], 'actual program bytes')
        p = read(memory, proof, 41)
        check(p[:8] == b'HD4TEST1' and struct.unpack_from('<4I', p, 8) == capacities, 'guest capacities')
        check(tuple(p[24:28]) == expected_heads, 'translated BIOS heads')
        check(struct.unpack_from('<3H', p, 28) == (5, 0 if args.persist else 5, 0xbeef), 'verified reads/writes/complete')
        check(p[34:37] == bytes((int(args.persist), 3, 0)), 'phase/drive/failure stage')
        cylinder, remainder = divmod(1100000, 64 * 63)
        head, sector = divmod(remainder, 63)
        check(p[37] == head and p[38] == sector + 1, 'large CHS head/sector')
        cx = ((cylinder & 255) << 8) | ((cylinder >> 2) & 0xc0) | (sector + 1)
        check(struct.unpack_from('<H', p, 39)[0] == cx, 'large CHS cylinder')
        expected = bytes((i * 13 + 3 * 0x31 + 7) & 255 for i in range(512))
        check(read(memory, base + 256 + len(program) - 512, 512) == expected, 'all final readback bytes')

    def check_io(events):
        """Reconstruct each selected physical drive, LBA and 256-word data phase from real IN/OUT only."""
        text = ''; active = False; states = {0x1f0: [0] * 8, 0x170: [0] * 8}; transfers = []
        for event in events:
            port, value, width, write = (event[k] for k in ('port', 'value', 'width', 'write'))
            if port == 0xe9 and write:
                text = (text + chr(value & 255))[-100:]
                if 'HD4-BEGIN\r\n' in text:
                    active = True
                if 'HD4-PROOF=' in text:
                    break
            if not active:
                continue
            channel = 0x1f0 if 0x1f0 <= port <= 0x1f7 else 0x170 if 0x170 <= port <= 0x177 else None
            if channel is None:
                continue
            registers = states[channel]; at = port - channel
            if write and width == 1 and 1 <= at <= 6:
                registers[at] = value
            if at == 7 and write:
                check(value in (0x20, 0x30) and registers[2] == 1 and registers[6] & 0x40, 'real one-sector LBA28 command')
                disk = (0 if channel == 0x1f0 else 2) + (registers[6] >> 4 & 1)
                lba = registers[3] | registers[4] << 8 | registers[5] << 16 | (registers[6] & 15) << 24
                transfers.append(dict(disk=disk, lba=lba, write=value == 0x30, words=[]))
            elif at == 0:
                check(transfers and width == 2 and write == transfers[-1]['write'], 'actual data direction/word width')
                transfers[-1]['words'].append(value)
        expected = []
        for disk, lba in [(n, capacities[n] - 1) for n in range(4)] + [(3, 1100000)]:
            if not args.persist:
                expected.append((disk, lba, True))
            expected.append((disk, lba, False))
        check([(t['disk'], t['lba'], t['write']) for t in transfers] == expected, 'physical route/LBA/phase sequence')
        for transfer in transfers:
            data = bytes((i * 13 + transfer['disk'] * 0x31 + 7) & 255 for i in range(512))
            check(transfer['words'] == list(struct.unpack('<256H', data)), 'all actual PIO data words')

    check_report(report); check_ram(ram); check_io(io)
    rejected = 0
    if args.negative_controls:
        bad_reports = []
        for key, value in [('ataSectorsWritten', 99), ('floppySectorsWritten', 1)]:
            bad = copy.deepcopy(report); bad[key] = value; bad_reports.append(bad)
        bad = copy.deepcopy(report); bad['status']['hardDisks'][3]['bytes'] = 512; bad_reports.append(bad)
        bad = copy.deepcopy(report); bad['status']['hardDisks'].pop(); bad_reports.append(bad)
        bad = copy.deepcopy(report); bad['scenario']['complete'] = False; bad_reports.append(bad)
        for bad in bad_reports:
            try:
                check_report(bad)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted a corrupted report')
        for address in [base + 256, proof, proof + 8, proof + 20, proof + 24, proof + 28, proof + 30, proof + 32, proof + 34, proof + 36, proof + 37, proof + 39,
                        base + 256 + len(program) - 512, base + 256 + len(program) - 1]:
            bad = bytearray(ram); bad[physical(address)] ^= 1
            try:
                check_ram(bad)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted RAM')
        bad = copy.deepcopy(io)
        own = False
        text = ''
        for event in bad:
            if event['port'] == 0xe9 and event['write']:
                text = (text + chr(event['value'] & 255))[-100:]
            if 'HD4-BEGIN\r\n' in text:
                own = True
            if own and event['port'] == 0x170 and event['width'] == 2:
                event['value'] ^= 1
                break
        try:
            check_io(bad)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted corrupted PIO data')
    return dict(passed=True, phase='persist' if args.persist else 'write', disks=4, capacities=capacities,
                actualPioWords=1280 if args.persist else 2560, verifiedSectorBytes=5 * 512,
                chsLba=1100000, immutableProgramBytes=immutable, negativeControlsRejected=rejected,
                proofSha256=hashlib.sha256(read(ram, proof, 41)).hexdigest().upper())


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for option in ('report', 'memory', 'io', 'program'):
        parser.add_argument('--' + option, type=Path, required=True)
    parser.add_argument('--persist', action='store_true')
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
