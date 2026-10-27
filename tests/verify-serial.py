# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent crystal/frame, physical IRQ, BIOS/DOS and resident-memory oracle for our UART probe."""
import argparse
import bisect
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def require(value, reason):
    """Reject incomplete or altered evidence without loading emulator register or timing code."""
    if not value:
        raise AssertionError(reason)


def verify(args):
    """Check the actually executed guest and its wire timings, independent readbacks and physical interrupt acknowledgements."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    memory, program = args.memory.read_bytes(), args.program.read_bytes()
    events = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    match = re.search(r'SERIAL-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    require(match is not None, 'executed guest proof pointer')
    cs, offset = (int(x, 16) for x in match.groups())
    signature = b'DOSE-SERIAL-P1!\0'
    require(offset == program.index(signature) + 256, 'actual probe layout')
    base, proof = cs * 16, cs * 16 + offset
    layout = struct.unpack_from('<6H', program, program.index(signature) + 16)
    code_end, rows_at, rx_at, lsr_at, overrun_at, end = layout

    def physical(address):
        """Walk actual final classic page tables, including EMM386 V86 memory relocation."""
        if not report['cpu']['pagingEnabled']:
            return address
        pde = struct.unpack_from('<I', memory, (report['cpu']['cr3'] & ~4095) + (address >> 22) * 4)[0]
        require(pde & 1 and not pde & 128, 'resident classic directory')
        pte = struct.unpack_from('<I', memory, (pde & ~4095) + (address >> 12 & 1023) * 4)[0]
        require(pte & 1, 'resident proof page')
        return (pte & ~4095) + (address & 4095)

    def read(ram, address, count):
        """Read only translated guest bytes, never assume a V86 linear address is physical."""
        return bytes(ram[physical(a)] for a in range(address, address + count))

    def check_ports(trace):
        """Derive frames from real DLAB/THR writes and certify their full crystal-cycle durations with integer remainders."""
        lcr = divisor = mcr = 0
        jobs, frames, reads, iirs, irqs, eois = [], [], [], [], [], []
        histories, times = [(0, 0, 0)], [0]
        for e in trace:
            if e['type'] == 'tx':
                frames.append(e['frame'])
            elif e['type'] == 'irq':
                require(e['irq'] == 4 and not e['spurious'] and 0 <= e['vector'] <= 255, 'real nonspurious physical IRQ4')
                irqs.append(e)
            else:
                port, value, width, write, now = (e[k] for k in ('port', 'value', 'width', 'write', 'time'))
                if port == 0x20 and write and value == 0x20:
                    eois.append(now)
                if not 0x3f8 <= port <= 0x3ff or width != 1:
                    continue
                if write:
                    if port == 0x3fb:
                        lcr = value
                    elif port == 0x3fc:
                        mcr = value
                    elif port in (0x3f8, 0x3f9) and lcr & 128:
                        divisor = (divisor & 0xff00) | value if port == 0x3f8 else (divisor & 255) | (value << 8)
                    elif port == 0x3f8:
                        jobs.append((now, value, bool(mcr & 16)))
                    histories.append((lcr, divisor, mcr)); times.append(now)
                elif port == 0x3f8 and not lcr & 128:
                    reads.append(e)
                elif port == 0x3fa:
                    iirs.append(value)
        require(len(jobs) == 126 and len(frames) == 125 and sum(j[2] for j in jobs) == 1, 'actual framed TX/loopback counts')
        require(len(reads) == 126 and len(irqs) > 0, 'actual RBR reads and physical interrupt path')
        remainder = 0
        for job, frame in zip((j for j in jobs if not j[2]), frames):
            start, finish, data, errors, fmt, div = (frame[k] for k in ('Start', 'End', 'Data', 'Errors', 'LineControl', 'Divisor'))
            state = histories[bisect.bisect_right(times, start) - 1]
            require(state[:2] == (fmt, div) and div > 0 and not state[2] & 16, 'sampled actual divisor/format/connection')
            bits = 2 * (1 + 5 + (fmt & 3) + bool(fmt & 8)) + (2 if not fmt & 4 else 3 if not fmt & 3 else 4)
            duration, remainder = divmod(bits * 8 * div * 1_000_000_000 + remainder, 1_843_200)
            require(start >= job[0] and finish - start == duration and errors == 0, 'complete nominal frame boundary and carried clock')
            require(data == (job[1] & ((1 << (5 + (fmt & 3))) - 1)), 'actual THR byte/word length')
        expected = [65 + i for i in range(8)] + [(i ^ 0xe5) & ((1 << (5 + (i & 3))) - 1) for i in range(32)]
        expected += [32 * i + n for i in range(4) for n in range(16)] + [214, 214, 214, 0, 2] + list(range(16)) + [150]
        require([e['value'] for e in reads] == expected, 'all actual RBR bytes including overrun retention and BI zero')
        # Each first 104 frames is echoed across a second complete frame, not injected at TX completion.
        rx_remainder, previous_end = 0, 0
        for frame, sample in zip(frames[:104], reads[:104]):
            fmt, div = frame['LineControl'], frame['Divisor']
            bits = 2 * (6 + (fmt & 3) + bool(fmt & 8)) + (2 if not fmt & 4 else 3 if not fmt & 3 else 4)
            duration, rx_remainder = divmod(bits * 8 * div * 1_000_000_000 + rx_remainder, 1_843_200)
            previous_end = max(frame['End'], previous_end) + duration
            require(sample['time'] >= previous_end, 'RBR cannot precede the full echoed input frame')
        for irq in irqs:
            require(bisect.bisect_left(eois, irq['time']) < len(eois), 'real master PIC EOI after physical IRQ4')
        require(all(code in iirs for code in (0xc2, 0xc4, 0xc6, 0xcc)), 'actual THRE/RDA/RLS/FIFO-timeout service')
        return reads, frames, irqs

    actual_reads, actual_frames, actual_irqs = check_ports(events)

    def irq_at(time):
        """Count physical acknowledgements already performed when the handler records the corresponding RBR byte."""
        return sum(e['time'] <= time for e in actual_irqs)

    def check_memory(ram):
        """Validate immutable execution bytes, every row, ISR data/error logs, BIOS returns, equipment and stack guards."""
        require(read(ram, base + 256, offset - 256 + 28) == program[:offset - 256 + 28], 'immutable actual program and layout')
        h = struct.unpack('<12H', read(ram, proof + 28, 24))
        actual_cs, bda, equipment, bios_equipment, count, interrupts, received, thre, modem, errors, timeout, complete = h
        require(actual_cs == cs and bda == 0x3f8 and equipment == bios_equipment and equipment & 0xe00 == 0x200, 'actual COM1 discovery')
        require(count == 52 and interrupts == len(actual_irqs) and received == 68 and thre == 8 and modem == errors == 0 and timeout == 0xe05a and complete == 0x600d, 'actual handler counts and firmware timeout')
        expected_rows = []
        for i, div in enumerate((1047, 768, 384, 192, 96, 48, 24, 12)):
            expected_rows.append((1, 3, div, 65 + i, 65 + i, 0x61, 0, 0, 0, 0x60b0, 0xa75c))
        for fmt in range(32):
            sent = fmt ^ 0xe5
            expected_rows.append((2, fmt, 12, sent, sent & ((1 << (5 + (fmt & 3))) - 1), 0x61, 1, 0, 0, 0, 0xa75c))
        for i in range(4):
            expected_rows.append((3, 3, 12, i * 32, 0, 0, 0, 16, irq_at(actual_reads[40 + 16 * i + 15]['time']), i * 16, 0xa75c))
        for i, (data, lsr, msr) in enumerate(((214, 0x61, 0xf0), (214, 0xe5, 0xb4), (214, 0xe9, 0xf0), (0, 0xf9, 0xb4))):
            expected_rows.append((4, 3, 12, 0, data, lsr, msr, 1, irq_at(actual_reads[104 + i]['time']), 64 + i, 0xa75c))
        expected_rows += [(5, 3, 1, 0, 0, 0x63, 0, 3, 0, 0, 0xa75c), (5, 3, 1, 0, 0, 0x63, 0, 18, 0, 0, 0xa75c),
                          (6, 3, 12, 150, 150, 0x61, 0xb0, 0, 0, 0, 0xa75c), (7, 3, 12, 0, 0, 0, 0, 0x8096, 0x8096, 0xffff, 0xa75c)]
        for i, expected in enumerate(expected_rows):
            require(struct.unpack('<BBH4B4H', read(ram, base + rows_at + i * 16, 16)) == expected, 'actual firmware/FIFO/error row ' + str(i))
        data = bytes([32 * i + n for i in range(4) for n in range(16)] + [214, 214, 214, 0])
        require(read(ram, base + rx_at, 68) == data and read(ram, base + rx_at + 68, 188) == bytes(188), 'every ISR RBR byte and buffer bounds')
        flags = read(ram, base + lsr_at, 68)
        require(all(v & 0x9f == 1 for v in flags[:65]) and flags[65:] == bytes([0xe5, 0xe9, 0xf9]), 'per-character PE/FE/BI and FIFO error summary')
        require(read(ram, base + lsr_at + 68, 188) == bytes(188), 'error buffer bounds')
        require(read(ram, base + overrun_at, 17) == bytes(list(range(16)) + [2]), 'FIFO drops newest, holding register keeps newest')
        require(read(ram, base + overrun_at + 17, 32) == bytes([0xa7]) * 32, 'stack guard')
        require(struct.unpack('<H', read(ram, 0x400, 2))[0] == 0x3f8, 'resident COM1 BDA pointer')

    def check_report(r):
        """Reject typed-only return markers and distinguish the virtual framed peer from any host COM resource."""
        require(r['status']['fault'] is None and 'SERIAL-FAILED' not in r['status']['debugOutput'], 'no guest exception/failure')
        require('SERIAL-OK' in r['status']['debugOutput'] and re.search(r'(?:^|\r?\n)SERIAL-DOS-RETURN\r?\n(?:\r?\n)?C:\\>', r['text']), 'executed DOS return')
        require(r['scenario']['complete'] and r['scenario']['serialInputsSent'] == 4 and r['scenario']['inputBytesSent'] == r['scenario']['inputBytesTotal'], 'complete explicit input scenario')
        s = r['serial']
        require(s['model'] == 'pc16550d-com1-development' and s['virtualEcho'] and not s['hostSerialPort'] and not s['fullChip'], 'bounded virtual board identity')
        require((s['transmittedCharacters'], s['receivedCharacters'], s['overruns'], s['rejectedTransmitWrites']) == (126, 130, 4, 0), 'actual transfer and overrun counters')
        require(s['externalCharactersQueued'] == 129 and s['externalCharactersIgnored'] == 0 and s['receivedCount'] == s['transmitCount'] == 0 and not s['transmitting'] and not s['interruptAsserted'], 'drained virtual queues and physical IRQ')
        require(s['lineStatus'] == 0x60 and s['interruptIdentification'] == 1 and s['interruptEnable'] == s['fifoControl'] == 0 and s['modemControl'] == 3 and s['divisor'] == 12 and s['lineControl'] == 3, 'actual final guest UART cleanup')
        require(r['ataSectorsWritten'] == r['floppySectorsWritten'] == 0, 'no media writes')
        if args.manager:
            require(r['cpu']['pagingEnabled'] and r['cpu']['virtual8086Instructions'] > 0, 'actual original EMM386 V86/paging')

    check_memory(memory); check_report(report)
    rejected = 0
    if args.negative_controls:
        targets = [proof + i for i in (0, 16, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50)]
        targets += [base + rows_at + i * 16 + j for i in (0, 7, 8, 39, 40, 43, 44, 47, 48, 49, 50, 51) for j in (0, 1, 2, 4, 5, 6, 7, 8, 10, 12, 14)]
        targets += [base + 256, base + rx_at, base + lsr_at + 65, base + overrun_at, base + overrun_at + 48]
        for address in targets:
            changed = bytearray(memory); changed[physical(address)] ^= 1
            try:
                check_memory(changed)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted damaged guest memory')
        for key in ('Data', 'End', 'Divisor'):
            changed = copy.deepcopy(events); next(e for e in changed if e['type'] == 'tx')['frame'][key] ^= 1
            try:
                check_ports(changed)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted damaged output frame')
        for key, value in (('serial', {**report['serial'], 'hostSerialPort': True}), ('scenario', {**report['scenario'], 'serialInputsSent': 3}), ('text', 'C:\\>echo SERIAL-DOS-RETURN\r\nC:\\>')):
            changed = copy.deepcopy(report); changed[key] = value
            try:
                check_report(changed)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted damaged guest report')
    return dict(passed=True, cases=52, outputFrames=126, externalOutputFrames=len(actual_frames), inputFrames=130,
                isrInputBytes=68, physicalIrq4Acknowledgements=len(actual_irqs), frameDurationsCertified=125,
                negativeControlsRejected=rejected, programBytesVerified=offset - 256 + 28,
                proofSha256=hashlib.sha256(read(memory, proof, overrun_at + 49 - offset)).hexdigest().upper(),
                hostSerialPort=False, scope='Nominal framed PC16550D/COM1 with actual BIOS/port/IRQ/DOS paths; no raw electrical/AC/modem/terminal compatibility claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('report', 'memory', 'io', 'program'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
