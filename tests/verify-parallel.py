# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent IBM register/wire, synthetic handshake, physical IRQ and actual DOS PRN/COPY oracle."""
import argparse
import bisect
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def require(ok, why):
    """Reject missing, altered or only superficially successful evidence without importing emulator code."""
    if not ok:
        raise AssertionError(why)


def verify(args):
    """Certify actual IN/OUT, pin transitions, complete accepted bytes and resident DOS proof."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    memory, program = args.memory.read_bytes(), args.program.read_bytes()
    events = [json.loads(s) for s in args.io.read_text(encoding='utf-8-sig').splitlines()]
    sink = args.output is not None
    stream = args.output.read_bytes() if sink else b''
    require(not sink or args.copy_source is not None, 'explicit own COPY input')
    copy_source = args.copy_source.read_bytes() if sink else b''
    require(not sink or copy_source and 9 not in copy_source, 'tab-free cooked PRN COPY fixture')
    expected_stream = bytes(range(16)) + bytes(range(256)) + b'ZDOSE-LPT-COPY\r\n' + copy_source
    match = re.search(r'LPT-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    require(match is not None, 'actual guest proof pointer')
    cs, offset = (int(x, 16) for x in match.groups())
    signature = b'DOSE-LPT-P1' + bytes(5)
    require(offset == program.index(signature) + 256, 'actual own COM layout')
    _, readbacks, ports, irq_status, pin_rows, program_end = struct.unpack_from('<6H', program, offset - 256 + 16)
    base, proof = cs * 16, cs * 16 + offset

    def physical(linear):
        """Walk actual classic final page tables before reading V86 program or proof bytes."""
        if not report['cpu']['pagingEnabled']:
            return linear
        pde = struct.unpack_from('<I', memory, (report['cpu']['cr3'] & ~4095) + (linear >> 22) * 4)[0]
        require(pde & 1 and not pde & 128, 'classic proof directory')
        pte = struct.unpack_from('<I', memory, (pde & ~4095) + (linear >> 12 & 1023) * 4)[0]
        require(pte & 1, 'resident proof page')
        return (pte & ~4095) + (linear & 4095)

    def read(ram, linear, count):
        """Read only translated bytes of the actual resident program, tables and BIOS data area."""
        return bytes(ram[physical(a)] for a in range(linear, linear + count))

    def check_ports(trace):
        """Reconstruct latch/status truth tables and successful pulses from actual bus writes, independently of peer events."""
        data = control = 0
        pins = 0xd8 if sink else 0xf8
        reads = writes = 0
        pulses, interrupted, accepted, acks, changes, pin_changes, eois = [], 0, [], [], [], [], []
        last_change, pulse, text, phase = 0, None, '', None
        manual_reads = {}
        for event in trace:
            kind = event['type']
            if kind == 'pins':
                item = event['pins']; pins = item['Pins']; pin_changes.append((item['Time'], pins))
            elif kind == 'byte':
                accepted.append(event['item'])
            elif kind == 'irq':
                require(event['irq'] == 7 and not event['spurious'] and (control & 16) and (pins & 64), 'physical nonspurious IRQ7 follows direct high ACK gate')
                acks.append(event)
            elif kind == 'io':
                address, value, width, output, now = (event[k] for k in ('port', 'value', 'width', 'write', 'time'))
                if address == 0xe9 and output:
                    text = (text + chr(value & 255))[-100:]
                    marker = re.search(r'LPIN-READY-([0-9A-F]{2})\r\n$', text)
                    if marker:
                        phase = int(marker[1], 16)
                    if text.endswith('LPIN-TIMEOUT\r\n'):
                        phase = None
                if address == 0x20 and output and value == 0x20:
                    eois.append(now)
                if address & 0x3f8 != 0x378:
                    continue
                reg = address & 3
                if width != 1:
                    require(output or value == (65535 if width == 2 else 4294967295), 'wide undriven result')
                    continue
                if output:
                    if reg == 0:
                        writes += 1
                        if data != value:
                            last_change = now; changes.append(now)
                        data = value
                    elif reg == 2:
                        writes += 1; previous = control; control = value & 31
                        if sink:
                            if not previous & 1 and control & 1 and control & 12 == 12:
                                pulse = (now, data, control, last_change)
                            if pulse is not None and not control & 4:
                                interrupted += 1; pulse = None
                            elif pulse is not None and previous & 1 and not control & 1:
                                pulses.append((*pulse, now)); pulse = None
                else:
                    if reg == 3:
                        expected = 255
                    else:
                        reads += 1
                        expected = data if reg == 0 else 0xe0 | control if reg == 2 else (pins ^ 128) | 7 | (16 if not control & 8 else 0)
                    require(value == expected, f'actual register {address:04X} read {value:02X} != {expected:02X}')
                    if not sink and phase is not None and reg == 1:
                        manual_reads.setdefault(phase, []).append(event)
        require(len(acks) == (257 if sink else 4), 'actual physical ACK count independent from CPU vector numbers')
        require(all(bisect.bisect_left(eois, event['time']) < len(eois) for event in acks), 'actual PIC EOI after each IRQ7')
        if sink:
            expected = expected_stream
            require(len(accepted) == len(expected) == len(pulses) and interrupted == 1, 'every completed strobe and actual reset cancellation')
            require(bytes(v['Data'] for v in accepted) == expected, 'actual BIOS/DOS/direct/COPY raw byte stream')
            pin_map = dict(pin_changes)
            for pulse, item in zip(pulses, accepted):
                asserted, datum, ctr, changed, released = pulse
                require(asserted - changed >= 500 and released - asserted >= 500, 'actual setup and strobe minimums')
                require(item == dict(Data=datum, Control=ctr, Asserted=asserted, Released=released, AcceptedAt=released + 500), 'one actual strobe/hold/commit tuple')
                following = bisect.bisect_right(changes, released)
                require(following == len(changes) or changes[following] >= released + 500, 'actual held data after strobe')
                require(pin_map.get(released + 10500) == 0x98 and pin_map.get(released + 15500) == 0x58, 'independent ACK delay/width and BUSY completion')
        else:
            require(not accepted and not pulses and not interrupted, 'manual pins have no hidden accepting peer')
            require(set(manual_reads) == set(range(33)), 'all externally synchronized pin phases')
            for i, samples in manual_reads.items():
                value = i if i < 32 else 0
                expected = ((value << 3) ^ 128) | 7 | (16 if i == 32 else 0)
                require(samples and all(e['value'] == expected for e in samples), 'every external live pin sample ' + str(i))
        return dict(reads=reads, writes=writes, accepted=accepted, acks=acks, interrupted=interrupted)

    observed = check_ports(events)

    def check_memory(ram):
        """Check all actual guest readbacks, handler arrays, BIOS results, immutable bytes and stack boundaries."""
        require(read(ram, base + 256, offset - 256 + 28) == program[:offset - 256 + 28], 'actual immutable code and metadata')
        mcb = read(ram, base - 16, 5)
        require(mcb[0] in (ord('M'), ord('Z')) and struct.unpack_from('<2H', mcb, 1) == (cs, (program_end + 15) // 16), 'actual bounded DOS resident proof ownership/size')
        header = read(ram, proof, 65)
        require(struct.unpack_from('<H', header, 28)[0] == cs and struct.unpack_from('<H', header, 30)[0] == 0x378, 'actual segment/BDA discovery')
        eq, bios_eq = struct.unpack_from('<2H', header, 32)
        require(eq == bios_eq and eq & 0xc000 == 0x4000 and struct.unpack('<H', read(ram, 0x408, 2))[0] == 0x378, 'actual BDA/INT11 port count')
        require(header[36] == (0 if sink else 1) and header[37] == (16 if sink else 33), 'executed guest mode/phases')
        complete, irq, errors, init, wide, timeout, invalid_function, invalid_port, phase_word, expected_irqs = struct.unpack_from('<10H', header, 38)
        require(complete == 0x600d and irq == len(observed['acks']) and errors == 0 and invalid_function == invalid_port == 0x015a, 'completed handler/unsupported BIOS results')
        if sink:
            require((init, wide, timeout, phase_word, expected_irqs) == (0x905a, 0xffff, 0, 256, 257), 'actual sink BIOS/wide/loop proof')
            require(read(ram, base + readbacks, 256) == bytes(range(256)), 'every actual data feedback byte')
            expected_ports = [0x378 + ((i & 127) >> 1) * 1024 + (i & 1) * 4 for i in range(256)]
            require(struct.unpack('<256H', read(ram, base + ports, 512)) == tuple(expected_ports), 'all 128 original decoder aliases twice')
            require(read(ram, base + irq_status, 512) == bytes([0xdf]) * 257 + bytes(255), 'actual nonclearing ISR status samples/bounds')
            require(read(ram, base + pin_rows, 264) == bytes(264), 'unused pin-proof boundary')
        else:
            require((init, wide, timeout, phase_word, expected_irqs) == (0, 0, 0x1196, 0, 1) and header[64] == 0x8b, 'actual timeout/CMOS/NMI result')
            require(read(ram, base + readbacks, 256) == bytes(256) and read(ram, base + ports, 512) == bytes(512), 'unused readback boundaries')
            require(read(ram, base + irq_status, 512) == bytes([0x7f, 0xc7, 0x47, 0x5f]) + bytes(508), 'actual ACK rising-level handler samples')
            for i in range(33):
                pins = i if i < 32 else 0; status = ((pins << 3) ^ 128) | 7 | (16 if i == 32 else 0)
                expected = (i, i, 0xfc if i < 32 else 0xf4, status, (status & 0xf8) ^ 0x48, 0xa7, 1 + (i >= 8) + (i >= 24))
                require(struct.unpack('<6BH', read(ram, base + pin_rows + i * 8, 8)) == expected, 'actual pin/BIOS row ' + str(i))
        require(read(ram, base + pin_rows + 264, 32) == bytes([0xa7]) * 32, 'stack guard')

    def check_report(r, output):
        """Require actual DOS return, explicit peer selection and byte-exact COPY rather than an echoed command."""
        require(r['status']['fault'] is None and 'LPT-FAILED' not in r['status']['debugOutput'], 'no guest exception/failure')
        require('LPT-OK' in r['status']['debugOutput'] and re.search(r'(?:^|\r?\n)LPT-DOS-RETURN\r?\n(?:\r?\n)?C:\\>', r['text']), 'executed DOS return')
        require(r['scenario']['complete'] and r['scenario']['inputBytesSent'] == r['scenario']['inputBytesTotal'] and r['scenario']['parallelInputsSent'] == (0 if sink else 34), 'actual scenario completion')
        p = r['parallel']; require(p['model'] == 'ibm-printer-adapter-development' and p['virtualSink'] == sink, 'explicit concrete port/peer')
        require(not any(p[k] for k in ('hostPrinter', 'ecp', 'epp', 'bidirectional', 'fullPrinter', 'interruptAsserted')), 'bounded actual capabilities and final IRQ cleanup')
        require(p['readCount'] == observed['reads'] and p['writeCount'] == observed['writes'] and p['interruptEdges'] == len(observed['acks']) and p['control'] == 12 and p['controlPins'] == 7, 'actual bus counters and output cleanup')
        if sink:
            expected = expected_stream
            require(output == expected and p['acceptedBytes'] == p['outputFileBytes'] == len(expected) and p['outputSha256'] == hashlib.sha256(expected).hexdigest().upper(), 'whole actual PRN/COPY output and digest')
            require(p['rejectedStrobes'] == 1 and p['status'] == 0xdf and p['inputPins'] == 0x58 and p['data'] == expected[-1], 'actual reset cancellation/final ready')
        else:
            require(p['acceptedBytes'] is p['outputFileBytes'] is p['outputSha256'] is None and p['data'] == 32 and p['status'] == 0x5f and p['inputPins'] == 0xd8, 'no hidden peer/output file and real timeout pins')
        require(r['ataSectorsWritten'] == r['floppySectorsWritten'] == 0, 'no guest media writes')
        if args.manager:
            require(r['cpu']['pagingEnabled'] and r['cpu']['virtual8086Instructions'] > 0, 'actual original EMM386 paging/V86')

    check_memory(memory); check_report(report, stream)
    rejected = 0
    if args.negative_controls:
        targets = [base - 15, base - 13, base + 256, proof, proof + 16] + [proof + i for i in (28, 30, 32, 34, 36, 37, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56)]
        targets += [base + readbacks, base + readbacks + 255, base + ports, base + ports + 511, base + irq_status, base + irq_status + 511, base + pin_rows + 295]
        targets += [base + pin_rows + i * 8 + j for i in range(0, 33, 4) for j in range(8)]
        for address in targets:
            changed = bytearray(memory); changed[physical(address)] ^= 1
            try:
                check_memory(changed)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted resident proof')
        changed = copy.deepcopy(events); next(e for e in changed if e['type'] == 'io' and e['port'] & 0x3f8 == 0x378 and not e['write'])['value'] ^= 1
        try:
            check_ports(changed)
        except AssertionError:
            rejected += 1
        else:
            raise AssertionError('accepted corrupted physical register sample')
        if sink:
            changed = copy.deepcopy(events); next(e for e in changed if e['type'] == 'byte')['item']['AcceptedAt'] += 1
            try:
                check_ports(changed)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted hold deadline')
        for field, value in [('text', 'C:\\>echo LPT-DOS-RETURN\r\nC:\\>'), ('parallel', {**report['parallel'], 'hostPrinter': True})]:
            changed = copy.deepcopy(report); changed[field] = value
            try:
                check_report(changed, stream)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted return/capability report')
        if sink:
            damaged = bytes([stream[0] ^ 1]) + stream[1:]
            try:
                check_report(report, damaged)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError('accepted corrupted DOS printer bytes')
    return dict(passed=True, mode='sink' if sink else 'pins', outputBytes=len(stream),
                physicalIrq7Acknowledgements=len(observed['acks']), acceptedStrobes=len(observed['accepted']),
                cancelledStrobes=observed['interrupted'], externalPinStates=0 if sink else 34,
                decoderAliases=128 if sink else 1, negativeControlsRejected=rejected,
                programBytesVerified=offset - 256 + 28,
                proofSha256=hashlib.sha256(read(memory, proof, pin_rows + 296 - offset)).hexdigest().upper(),
                hostPrinter=False, scope='IBM 6361507 byte adapter and explicit synthetic peer; real BIOS/PRN/COPY/IRQ, no physical print-engine/ECP/EPP/host-spooler claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('report', 'memory', 'io', 'program'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--copy-source', type=Path)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
