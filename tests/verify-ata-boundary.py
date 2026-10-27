# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent actual-DOS/RAM/PIO oracle for odd INT13 buffers at a 64-KiB boundary."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def require(value, reason):
    """Reject an incomplete or contradictory guest proof without emulator imports."""
    if not value:
        raise AssertionError(reason)


def pattern(case, size):
    """Independently specify every source/readback byte from the published case number."""
    return bytes((i * 13 + (i >> 8) + case * 31 + 7) & 255 for i in range(size))


def verify(args):
    """Certify the real program, guards, final bytes and every physical ATA data word."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    ram, program = args.memory.read_bytes(), args.program.read_bytes()
    io = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    match = re.search(r'ATA-BOUNDARY-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    require(match is not None, 'missing actual DOS proof pointer')
    segment, offset = (int(value, 16) for value in match.groups())
    immutable = program.index(b'ATAMUTBL') + 8
    require(offset == program.index(b'ATABND01') + 256, 'own program proof address')
    base, proof = segment * 16, segment * 16 + offset

    def physical(address):
        """Independently walk classic guest page tables when EMM386 is actually resident."""
        if not report['cpu']['pagingEnabled']:
            return address
        directory = (report['cpu']['cr3'] & ~4095) + (address >> 22) * 4
        pde = struct.unpack_from('<I', ram, directory)[0]
        require(pde & 1 and not pde & 128, 'classic directory entry')
        pte = struct.unpack_from('<I', ram, (pde & ~4095) + (address >> 12 & 1023) * 4)[0]
        require(pte & 1, 'resident proof page')
        return (pte & ~4095) + (address & 4095)

    def read(memory, address, size):
        """Read only the independently translated private RAM requested by this proof."""
        return bytes(memory[physical(a)] for a in range(address, address + size))

    def check_report(value):
        """Require a successful actual DOS return, real disk writes and completed KBC input."""
        require(value['status']['fault'] is None and 'ATA-BOUNDARY-FAILED' not in value['status']['debugOutput'], 'guest fault/failure')
        require('ATA-BOUNDARY-OK' in value['status']['debugOutput'] and
                re.search(r'(?:^|\r?\n)ATA-BOUNDARY-DOS-RETURN\r?\n(?:\r?\n)?C:\\>', value['text']), 'actual program/DOS return')
        require(value['scenario']['complete'] and value['scenario']['inputBytesSent'] == value['scenario']['inputBytesTotal'], 'completed guest input')
        disks = value['status']['hardDisks']
        require(len(disks) == 2 and disks[1]['slot'] == 1 and disks[1]['bytes'] == 2097152 and disks[1]['writable'], 'private scratch binding')
        require(value['ataSectorsWritten'] == 48 and value['floppySectorsWritten'] == 0, 'real write counts')
        if args.manager:
            require(value['cpu']['pagingEnabled'] and value['cpu']['protectedMode'] and value['cpu']['virtual8086Instructions'] > 0, 'actual monitor path')

    def check_memory(memory):
        """Verify the executed prefix, every guard and the complete final odd-addressed readback."""
        require(read(memory, base + 256, immutable) == program[:immutable], 'actual immutable program')
        p = read(memory, proof, 42 + 768 + 1536)
        require(p[:8] == b'ATABND01', 'proof signature')
        done, cases, allocation, checked, transfers = struct.unpack_from('<5H', p, 8)
        require((done, cases, checked, transfers) == (0xbeef, 24, 24576, 48) and allocation > segment, 'case/allocation/byte/transfer proof')
        require(p[18] == 0 and struct.unpack_from('<3H', p, 19) == (0xffff, 3, 1536) and p[25] == 1, 'final EDD/boundary request')
        require(struct.unpack_from('<BBHHHQ', p, 26) == (16, 0, 3, 0xffff, allocation, 8), 'final actual DAP')
        require(p[42:810] == bytes([0xa7]) * 768, 'all actual before/after guards')
        require(p[810:] == pattern(23, 1536), 'all final readback bytes, including both sides of FFFFh')

    def check_io(events):
        """Reconstruct each real multi-sector word transaction and compare its independent data oracle."""
        text, active = '', False
        registers = [0] * 8
        transfers = []
        for event in events:
            port, value, width, write = (event[k] for k in ('port', 'value', 'width', 'write'))
            if port == 0xe9 and write:
                text = (text + chr(value & 255))[-100:]
                if 'ATA-BOUNDARY-BEGIN' in text:
                    active = True
                if 'ATA-BOUNDARY-PROOF=' in text:
                    break
            if not active or not 0x1f0 <= port <= 0x1f7:
                continue
            at = port - 0x1f0
            if write and width == 1 and 1 <= at <= 6:
                registers[at] = value
            if at == 7 and write:
                require(width == 1 and value in (0x20, 0x30), 'actual ATA command')
                lba = registers[3] | registers[4] << 8 | registers[5] << 16 | (registers[6] & 15) << 24
                require(registers[6] & 0xf0 == 0xf0 and lba == 8, 'actual private slave/LBA')
                transfers.append(dict(count=registers[2] or 256, write=value == 0x30, words=[]))
            elif at == 0:
                require(transfers and width == 2 and write == transfers[-1]['write'], 'one actual word per ATA grant')
                transfers[-1]['words'].append(value)
        require(len(transfers) == 48, 'actual command count')
        for index, transfer in enumerate(transfers):
            case = index // 2
            count = 1 if (case // 6) % 2 == 0 else 3
            require(transfer['count'] == count and transfer['write'] == (index % 2 == 0), 'case count/direction')
            require(transfer['words'] == list(struct.unpack(f'<{count * 256}H', pattern(case, count * 512))), 'all actual ATA data words')

    check_report(report)
    check_memory(ram)
    check_io(io)
    rejected = 0
    if args.negative_controls:
        for key, replacement in [('ataSectorsWritten', 47), ('floppySectorsWritten', 1),
                                 ('text', 'C:\\>echo ATA-BOUNDARY-DOS-RETURN\r\nATA-B')]:
            value = copy.deepcopy(report)
            value[key] = replacement
            try:
                check_report(value)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted a corrupted report')
        for address in [base + 256, proof, proof + 8, proof + 10, proof + 14, proof + 16, proof + 18, proof + 19,
                        proof + 26, proof + 42, proof + 809, proof + 810, proof + 811, proof + 2345]:
            value = bytearray(ram)
            value[physical(address)] ^= 1
            try:
                check_memory(value)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted program/proof/guard/readback')
        value = copy.deepcopy(io)
        started = False
        text = ''
        for event in value:
            if event['port'] == 0xe9 and event['write']:
                text = (text + chr(event['value'] & 255))[-100:]
                started |= 'ATA-BOUNDARY-BEGIN' in text
            if started and event['port'] == 0x1f0 and event['width'] == 2:
                event['value'] ^= 256
                break
        try:
            check_io(value)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted a corrupted physical data word')
    return dict(passed=True, cases=24, actualCommands=48, actualWords=24576, checkedBytes=24576,
                guardBytes=768, chsAndEdd=True, manager=args.manager, negativeControlsRejected=rejected,
                programSha256=hashlib.sha256(program).hexdigest().upper())


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for option in ('report', 'memory', 'program', 'io'):
        parser.add_argument('--' + option, type=Path, required=True)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
