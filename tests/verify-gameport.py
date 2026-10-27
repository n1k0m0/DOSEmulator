# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent nominal RC/port/RAM oracle for our actual DOS gameport probe."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def require(value, reason):
    """Reject incomplete guest evidence without importing emulator arithmetic or device code."""
    if not value:
        raise AssertionError(reason)


def verify(args):
    """Certify all actual samples from their OUT timestamp and independently specified external resistances."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    memory, program = args.memory.read_bytes(), args.program.read_bytes()
    events = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    cases = [(None, None, None, None, 0), (0, 25000, 50000, 100000, 5), (100000, 50000, 25000, 0, 10),
             (1, 100, 49999, 99999, 15), (50000, 50000, 50000, 50000, 0), (12345, 67890, None, None, 3),
             (None, 0, None, 100000, 12), (100000, 100000, 100000, 100000, 0)]
    cases += [(0, 0, 0, 0, b) for b in range(16)]
    ports = [0x201, 0x601, 0xfe01, 0xa01] * 2 + [0x201] * 16
    match = re.search(r'GAMEPORT-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    require(match is not None, 'actual guest proof pointer')
    segment, offset = (int(v, 16) for v in match.groups())
    require(offset == program.index(b'GPORT001') + 256, 'actual program proof address')
    base, proof = segment * 16, segment * 16 + offset
    immutable = program.index(b'GPMUTABL') + 8

    def physical(address):
        """Walk classic final guest page tables independently; never treat V86 linear memory as physical by assumption."""
        if not report['cpu']['pagingEnabled']:
            return address
        pde = struct.unpack_from('<I', memory, (report['cpu']['cr3'] & ~4095) + (address >> 22) * 4)[0]
        require(pde & 1 and not pde & 128, 'classic proof directory')
        pte = struct.unpack_from('<I', memory, (pde & ~4095) + (address >> 12 & 1023) * 4)[0]
        require(pte & 1, 'resident proof page')
        return (pte & ~4095) + (address & 4095)

    def read(ram, address, length):
        """Read only the translated proof bytes and real immutable program prefix."""
        return bytes(ram[physical(a)] for a in range(address, address + length))

    def check_ports(rows):
        """Reconstruct the two pulse measurements in each phase, including every sampled byte and late open circuits."""
        text, phase = '', None
        groups = [[] for _ in cases]
        for event in rows:
            if event['port'] == 0xe9 and event['write']:
                text = (text + chr(event['value'] & 255))[-100:]
                m = re.search(r'GAMEPORT-READY-([0-9A-F]{2})\r\n$', text)
                if m:
                    phase = int(m[1], 16)
                    require(phase < 24, 'phase marker range')
                if text.endswith('GAMEPORT-PROOF='):
                    phase = None
            elif phase is not None and event['port'] & 0x3ff == 0x201:
                groups[phase].append(event)
        initial = [e for e in rows if e['port'] & 0x3ff == 0x201][0]
        require(not initial['write'] and initial['port'] == 0x201 and initial['width'] == 1 and initial['value'] == 0xf0, 'actual initial AH84 switch read')
        result, bios_result = [], []
        for index, (state, rows) in enumerate(zip(cases, groups)):
            writes = [i for i, e in enumerate(rows) if e['write']]
            require(len(writes) == 3 and writes[0] == 1, 'switch read, BIOS trigger and two raw triggers')
            bios, rows = rows[:writes[1]], rows[writes[1]:]
            require(all(e['width'] == 1 and e['port'] == 0x201 for e in bios), 'actual BIOS byte ports')
            require(not bios[0]['write'] and bios[0]['value'] & 0xf0 == (15 ^ state[4]) << 4, 'BIOS live switches')
            require(bios[1]['write'] and bios[1]['value'] == 0 and all(not e['write'] for e in bios[2:]), 'single BIOS trigger')
            require(1 <= len(bios[2:]) <= 4096 and bios[-1]['time'] - bios[1]['time'] < 12000000, 'actual bounded BIOS time and poll count')
            counts = [0] * 4
            for event in bios[2:]:
                elapsed = event['time'] - bios[1]['time']
                expected = (15 ^ state[4]) << 4
                for axis, resistance in enumerate(state[:4]):
                    if resistance is None or elapsed < 24200 + 11 * resistance:
                        expected |= 1 << axis
                        counts[axis] += 1
                require(event['value'] == expected, 'independent actual BIOS RC sample')
            for axis in range(4):
                if bios[-1]['value'] & (1 << axis):
                    counts[axis] = 65535
            bios_result.append(((15 ^ state[4]) << 4, counts))
            require(rows and all(e['width'] == 1 and e['port'] == ports[index] for e in rows), 'actual decoded byte port/alias')
            trigger, passes = None, []
            for event in rows:
                if event['write']:
                    require(event['value'] == index, 'actual written trigger byte')
                    trigger = event['time']
                    passes.append([])
                else:
                    require(trigger is not None and event['time'] >= trigger, 'read follows actual OUT')
                    elapsed = event['time'] - trigger
                    expected = (15 ^ state[4]) * 16
                    for axis, resistance in enumerate(state[:4]):
                        if resistance is None or elapsed < 24200 + 11 * resistance:
                            expected |= 1 << axis
                    require(event['value'] == expected, f'phase {index} sample at {elapsed} ns: {event["value"]:02x} != {expected:02x}')
                    passes[-1].append(event)
            require(len(passes) == 2 and all(passes), 'two actual simultaneous triggers')
            require(len(passes[1]) >= 2 and passes[1][-1]['time'] - trigger > 1124200, 'real late sample beyond full-scale expiry')
            late = passes[1][-1]['value']
            expected_late = (15 ^ state[4]) * 16 + sum(1 << i for i, v in enumerate(state[:4]) if v is None)
            require(late == expected_late, 'settled analog/open/button result')
            require(all(1 <= n <= 10000 for n in (len(passes[0]), len(passes[1]) - 1)), 'bounded guest polling')
            result.append((ports[index], passes[0][0]['value'], passes[0][-1]['value'], passes[1][0]['value'],
                           passes[1][-2]['value'], late, 0xa7, len(passes[0]), len(passes[1]) - 1, index, 0xbeef))
        return result, bios_result

    expected_rows, expected_bios = check_ports(events)

    def check_memory(ram):
        """Validate the executed program, equipment/unsupported-service proof, all guest readbacks and guards."""
        require(read(ram, base + 256, immutable) == program[:immutable], 'actual immutable program')
        p = read(ram, proof, 436)
        require(p[:8] == b'GPORT001', 'proof signature')
        done, phases, equipment, bios_equipment, ah84, flags = struct.unpack_from('<6H', p, 8)
        require(done == 0xbeef and phases == 24, 'completed phases')
        require(equipment == bios_equipment and equipment & 0x1000 and struct.unpack('<H', read(ram, 0x410, 2))[0] == equipment, 'real BDA/INT11 adapter discovery')
        require(ah84 == 0xf0 and not flags & 1, 'AH84 real initial switches')
        bios_base = base + 256 + program.index(b'GPBIOS01') + 8
        b = read(ram, bios_base, 24 * 16)
        for i, (buttons, counts) in enumerate(expected_bios):
            actual = struct.unpack_from('<8H', b, i * 16)
            require(actual[0] == buttons and not actual[1] & 1 and list(actual[2:6]) == counts and not actual[6] & 1 and actual[7] == 0xbeef, 'actual BIOS return registers, flags and guard')
        require(p[20:36] == p[420:436] == bytes([0xa7]) * 16, 'actual before/after guards')
        for index, row in enumerate(expected_rows):
            require(struct.unpack_from('<H6B4H', p, 36 + index * 16) == row, 'actual port/timing readback row ' + str(index))

    def check_report(r):
        """Reject echoed-but-unexecuted return markers and missing actual configured/input/monitor paths."""
        require(r['status']['fault'] is None and 'GAMEPORT-FAILED' not in r['status']['debugOutput'], 'guest fault/failure')
        require('GAMEPORT-OK' in r['status']['debugOutput'] and re.search(r'(?:^|\r?\n)GAMEPORT-DOS-RETURN\r?\n(?:\r?\n)?C:\\>', r['text']), 'actual DOS return')
        require(r['scenario']['complete'] and r['scenario']['inputBytesSent'] == r['scenario']['inputBytesTotal'] and
                r['scenario']['gameportInputsSent'] == 24 and r['scenario']['mouseInputsSent'] == 0, 'real external inputs and KBC completion')
        require(r['status']['configuration']['gameport'] == 'ibm-game-control-development', 'explicit standalone adapter')
        gp = r['gameport']
        require(gp['model'] == 'ibm-game-control-development' and not gp['hostGamepad'] and not gp['midi'] and gp['biosAh84'], 'bounded hardware capabilities')
        all_io = [e for e in events if e['port'] & 0x3ff == 0x201 and e['width'] == 1]
        require(gp['triggerCount'] == sum(e['write'] for e in all_io) == 72 and gp['readCount'] == sum(not e['write'] for e in all_io) and gp['inputUpdates'] == 24, 'actual input/read/trigger counts')
        require(r['ataSectorsWritten'] == r['floppySectorsWritten'] == 0, 'no guest media writes')
        if args.manager:
            require(r['cpu']['pagingEnabled'] and r['cpu']['virtual8086Instructions'] > 0, 'actual EMM386 V86/paging')

    check_memory(memory)
    check_report(report)
    rejected = 0
    if args.negative_controls:
        for at in (0, 8, 10, 12, 14, 17, 18, 20, 35, 420, 435) + tuple(36 + i * 16 + n for i in (0, 1, 2, 5, 6, 7, 8, 23) for n in (0, 6, 8, 14)):
            damaged = bytearray(memory)
            damaged[physical(proof + at)] ^= 1
            try:
                check_memory(damaged)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted guest proof')
        damaged = bytearray(memory)
        damaged[physical(base + 256)] ^= 1
        try:
            check_memory(damaged)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted corrupted executed program')
        bios_base = base + 256 + program.index(b'GPBIOS01') + 8
        for row in range(24):
            for at in (0, 2, 4, 6, 8, 10, 12, 14):
                damaged = bytearray(memory)
                damaged[physical(bios_base + row * 16 + at)] ^= 1
                try:
                    check_memory(damaged)
                except AssertionError:
                    rejected += 1
                else:
                    raise AssertionError('accepted corrupted BIOS return')
        changed = copy.deepcopy(events)
        next(e for e in changed if e['port'] & 0x3ff == 0x201 and not e['write'])['value'] ^= 1
        try:
            check_ports(changed)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted corrupted physical sample')
        changed = copy.deepcopy(report)
        changed['scenario']['gameportInputsSent'] = 23
        try:
            check_report(changed)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted missing external input')
        changed = copy.deepcopy(report)
        changed['text'] = 'C:\\>echo GAMEPORT-DOS-RETURN\r\nC:\\>'
        try:
            check_report(changed)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted typed but unexecuted return marker')
    return dict(passed=True,phases=24,triggers=72,biosCalls=49,
                actualByteReads=report['gameport']['readCount'],differentPorts=sorted(set(ports)),
                allSwitchCombinations=True,fullScaleOhms=100000,proofSha256=hashlib.sha256(read(memory,proof,436) + read(memory,base+256+program.index(b'GPBIOS01')+8,384)).hexdigest().upper(),
                programBytesVerified=immutable,negativeControlsRejected=rejected,hostInput=False,
                scope='Nominal IBM analog byte-I/O, trigger-sampled resistances; INT15/AH84 bounded counts, no host-gamepad/electrical precision')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('report', 'memory', 'io', 'program'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
