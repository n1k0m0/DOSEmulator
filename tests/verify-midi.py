# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent integer serial/protocol, shared-IRQ and resident DOS proof oracle.

The bounded queue/service rules are the published DOSE contract, not measured
CT1740 silicon parameters. No emulator module or host MIDI backend is imported.
"""
import argparse
from collections import deque
import copy
import hashlib
import heapq
import json
from pathlib import Path
import re
import struct


def require(ok, why):
    """Reject missing or falsified observations rather than accepting a guest success string alone."""
    if not ok:
        raise AssertionError(why)


def certify_ports(events, base, card, irq, end):
    """Reconstruct nominal wire deadlines, host buffers and three independent card sources from actual I/O."""
    pending, received, outgoing, incoming = [], deque(), [], []
    wire, holding, ready_at, uart, generation, order = None, None, 0, False, 0, 0
    sources, irq_pending, irq_edges, old_level, old_midi, midi_edges = 0, False, 0, False, False, 0
    resets = acknowledgements = rejected = ignored = aborted = overruns = ignored_rx = 0
    index, dsp_ready, dsp_busy, dsp_response = 0, False, 0, None
    master = slave = 255
    irq_events, eois, packets, mode_history = [], [], [], [(0, False, 0)]

    def schedule(at, kind, data=None):
        """Order independently computed protocol deadlines with integer virtual nanoseconds."""
        nonlocal order
        order += 1
        heapq.heappush(pending, (at, order, kind, data))

    def levels():
        """Derive the unread-MPU bit and combined board edge without acknowledging the PIC."""
        nonlocal sources, irq_pending, irq_edges, old_level, old_midi, midi_edges
        midi = any(v[1] for v in received)
        if midi and not old_midi:
            midi_edges += 1
        old_midi = midi
        sources = (sources & 3) | (4 if midi else 0)
        high = sources != 0
        if high and not old_level:
            irq_pending = True
            irq_edges += 1
        old_level = high

    def launch(at, data):
        """Start one complete fixed 8N1 MIDI frame; a later reset invalidates its completion token."""
        nonlocal wire
        wire = (at, data, generation)
        schedule(at + 320000, 'tx', wire)

    def advance(at):
        """Apply serial and service deadlines before observing a CPU I/O completion at that instant."""
        nonlocal wire, holding, uart, ready_at, acknowledgements, overruns, ignored_rx, sources, dsp_ready, dsp_response
        while pending and pending[0][0] <= at:
            now, _, kind, data = heapq.heappop(pending)
            if kind == 'tx':
                if wire != data:
                    continue
                started, value, _ = data
                outgoing.append(dict(Started=started, Completed=now, Data=value)); wire = None
                if holding is not None:
                    datum, holding = holding, None
                    launch(now, datum)
            elif kind in ('reset', 'mode'):
                if kind == 'mode':
                    uart = True
                    mode_history.append((now, True, generation))
                received.append((254, kind == 'mode')); acknowledgements += 1
            elif kind == 'rx':
                started, value = data
                before = next(state for state in reversed(mode_history) if state[0] <= started)
                eligible = before[1] and uart and before[2] == generation
                accepted = eligible and len(received) < 16
                if accepted:
                    received.append((value, True))
                elif eligible:
                    overruns += 1
                else:
                    ignored_rx += 1
                incoming.append(dict(Frame=dict(Started=started, Completed=now, Data=value), Accepted=accepted, Overrun=eligible and not accepted))
            elif kind == 'dsp-ready':
                dsp_ready, dsp_response = True, 0xaa
            elif kind == 'dsp8':
                sources |= 1
            elif kind == 'dsp16':
                sources |= 2
            levels()

    wire_end = 0
    for event in events:
        kind = event['type']
        if kind in ('tx', 'rx'):
            continue
        now = event['time']; advance(now)
        if kind == 'input':
            data = event['data']; packets.append(data)
            for datum in data:
                started = max(now, wire_end); wire_end = started + 320000
                schedule(wire_end, 'rx', (started, datum))
        elif kind == 'irq':
            require(event['irq'] == irq and not event['spurious'] and event['sources'] == sources and sources != 0 and irq_pending, 'actual physical shared IRQ/source/pending edge')
            require(not (master & (1 << irq) if irq < 8 else (master & 4) or (slave & (1 << (irq - 8)))), 'actual unmasked board IRQ')
            irq_pending = False; irq_events.append(event)
        elif kind == 'io':
            port, value, width, write = (event[k] for k in ('port', 'value', 'width', 'write'))
            if port in (32, 160) and write and value == 32:
                eois.append((now, port))
            if port == 33 and write:
                master = value
            if port == 161 and write:
                slave = value
            if port in (base, base + 1):
                if width != 1:
                    require(write or value == (65535 if width == 2 else 4294967295), 'wide MPU read remains undriven')
                    continue
                host_ready = now >= ready_at and holding is None
                if not write:
                    expected = (0x3f | (128 if not received else 0) | (0 if host_ready else 64)) if port == base + 1 else received[0][0] if received else 255
                    require(value == expected, f'actual MPU register {port:X}: {value:X} != {expected:X}')
                    if port == base and received:
                        received.popleft()
                elif not host_ready:
                    rejected += 1
                elif port == base:
                    if not uart:
                        rejected += 1
                    elif wire is None:
                        launch(now, value)
                    else:
                        holding = value
                elif value == 255:
                    resets += 1; generation += 1; uart = False; mode_history.append((now, False, generation)); received.clear()
                    aborted += int(wire is not None) + int(holding is not None); wire = holding = None
                    ready_at = now + 500000; schedule(ready_at, 'reset')
                elif value == 63 and not uart:
                    ready_at = now + 10000; schedule(ready_at, 'mode')
                else:
                    ignored += 1; ready_at = now + 2000
            elif card <= port <= card + 15:
                offset = port - card
                if width != 1:
                    require(write or value == (65535 if width == 2 else 4294967295), 'wide DSP read has no acknowledgement')
                    continue
                if write:
                    if offset == 4:
                        index = value
                    elif offset == 6:
                        if value & 1:
                            dsp_ready = False; dsp_response = None; sources &= 4
                        else:
                            schedule(now + 100000, 'dsp-ready')
                    elif offset == 12:
                        require(dsp_ready and now >= dsp_busy and value in (0xf2, 0xf3), 'actual ready diagnostic DSP F2/F3 only')
                        dsp_busy = now + 1000; schedule(dsp_busy, 'dsp16' if value == 0xf3 else 'dsp8')
                elif offset == 5 and index == 0x82:
                    require(value == sources, 'actual read-only mixer82 source bits')
                elif offset == 14:
                    require(value == (128 if dsp_response is not None else 0), 'actual independent DSP read readiness')
                    sources &= ~1
                elif offset == 15:
                    require(value == 255, 'actual DSP16 acknowledgement byte')
                    sources &= ~2
                elif offset == 10:
                    require(value == dsp_response == 0xaa, 'actual original DSP reset response')
                    dsp_response = None
                elif offset == 12:
                    require(value == (0 if dsp_ready and now >= dsp_busy else 128), 'actual DSP command readiness')
            levels()
    advance(end)
    observed_out = [e['frame'] for e in events if e['type'] == 'tx']
    observed_in = [e['reception'] for e in events if e['type'] == 'rx']
    require(observed_out == outgoing and observed_in == incoming, 'every whole MIDI frame, byte, deadline and delivery decision')
    require(packets == [list(range(32)), [65], [(i * 37 + 240) & 255 for i in range(128)]], 'explicit actual outer source packets')
    for i, event in enumerate(irq_events):
        limit = irq_events[i + 1]['time'] if i + 1 < len(irq_events) else end
        after = [port for t, port in eois if event['time'] <= t < limit]
        require(32 in after and (irq < 8 or 160 in after and after.index(160) < after.index(32)), 'actual PIC EOIs after shared IRQ')
    return dict(outgoing=outgoing, incoming=incoming, irq=irq_events, resets=resets, acknowledgements=acknowledgements,
                rejected=rejected, ignored=ignored, aborted=aborted, overruns=overruns, ignored_rx=ignored_rx,
                received=sum(v['Accepted'] for v in incoming), sourceEdges=irq_edges, midiEdges=midi_edges)


def verify(args):
    """Check complete real guest evidence, including classic paging and genuine DOS-owned retained memory."""
    report = json.loads(args.report.read_text(encoding='utf-8-sig'))
    program, memory, output = args.program.read_bytes(), args.memory.read_bytes(), args.output.read_bytes()
    events = [json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()]
    match = re.search(r'MPU-PROOF=([0-9A-F]{4}):([0-9A-F]{4})', report['status']['debugOutput'])
    require(match is not None, 'actual guest proof pointer')
    cs, offset = (int(x, 16) for x in match.groups()); base = cs * 16; proof = base + offset
    require(offset == program.index(b'DOSE-MPU-P1' + bytes(5)) + 256, 'compiled own COM layout')
    _, tx, rx, rows, guard, end = struct.unpack_from('<6H', program, offset - 256 + 16)
    card, port, irq = struct.unpack_from('<3H', program, offset - 256 + 30)
    observed = certify_ports(events, port, card, irq, report['status']['virtualNanoseconds'])
    expected_tx = bytes(range(256)) + bytes(range(255, 127, -1))
    expected_rx = bytes([254]) + bytes((i * 37 + 240) & 255 for i in range(128))

    def physical(linear):
        """Translate actual classic final guest pages without assuming that V86 memory remains identity mapped."""
        if not report['cpu']['pagingEnabled']:
            return linear
        pde = struct.unpack_from('<I', memory, (report['cpu']['cr3'] & ~4095) + (linear >> 22) * 4)[0]
        require(pde & 1 and not pde & 128, 'classic directory entry')
        pte = struct.unpack_from('<I', memory, (pde & ~4095) + (linear >> 12 & 1023) * 4)[0]
        require(pte & 1, 'actual resident proof page')
        return (pte & ~4095) + (linear & 4095)

    def read(ram, linear, count):
        """Read only translated actual bytes of the program and proof, never host-guessed contents."""
        return bytes(ram[physical(a)] for a in range(linear, linear + count))

    def check_memory(ram):
        """Validate actual executed code, every source/result array, IRQ handler rows and DOS memory ownership."""
        require(read(ram, base + 256, offset - 256 + 28) == program[:offset - 256 + 28], 'immutable program and metadata')
        mcb = read(ram, base - 16, 5)
        require(mcb[0] in (77, 90) and struct.unpack_from('<2H', mcb, 1) == (cs, (end + 15) // 16), 'real bounded DOS resident block')
        h = read(ram, proof, 79)
        require(struct.unpack_from('<9H', h, 28) == (cs, card, port, irq, 0x600d, len(observed['irq']), 0, 6, 0xffff), 'actual completion/resources/IRQs/polled acknowledgements/wide result')
        require(h[46:50] == bytes([7, 6, 2, 0]) and struct.unpack_from('<3H', h, 50) == (1, 384, 129), 'independent three-source clearing and handler data counts')
        require(h[62] == 15 and h[63:79] == bytes(range(16)), 'actual overrun-retained oldest sixteen bytes')
        require(read(ram, base + tx, 384) == expected_tx and read(ram, base + rx, 129) == expected_rx, 'whole actual source/receive arrays')
        handler = read(ram, base + rows, 516); count = len(observed['irq'])
        require(1 <= count <= 129 and sum(handler[i * 4 + 3] for i in range(count)) == 129, 'all received bytes handled exactly once')
        for i, event in enumerate(observed['irq']):
            a, status, final, amount = handler[i * 4:i * 4 + 4]
            require(a == event['sources'] == (7 if i == 0 else 4) and status in (191, 255) and final == 0 and 1 <= amount <= 16, 'actual shared handler source/status/drain/clear row')
        require(handler[count * 4:] == bytes(516 - count * 4), 'unused handler array boundary')
        require(read(ram, base + guard, 32) == bytes([167]) * 32, 'resident stack guard')

    def check_report(r, raw):
        """Require executed DOS return and exact raw serial bytes, excluding any synth/host or general-card claim."""
        require(r['status']['fault'] is None and 'MPU-FAILED' not in r['status']['debugOutput'] and 'MPU-OK' in r['status']['debugOutput'], 'actual successful guest execution')
        require(re.search(r'(?:^|\r?\n)MIDI-DOS-RETURN\r?\n(?:\r?\n)?C:\\>', r['text']), 'executed DOS return rather than echoed command')
        s = r['scenario']; require(s['complete'] and s['midiInputsSent'] == 3 and s['inputBytesSent'] == s['inputBytesTotal'], 'actual KBC/outer packet synchronization')
        m = r['midi']; require(m['model'] == 'ct1740-mpu401-uart-development' and m['basePort'] == port and not any(m[k] for k in ('uartMode', 'transmitting', 'receiving', 'interruptAsserted', 'hostMidi', 'synthesizer', 'intelligentMode', 'siliconExact')), 'bounded final actual transport state')
        require(m['interruptEdges'] == observed['midiEdges'], 'actual MPU source edges distinct from shared board/PIC acknowledgements')
        require((m['status'], m['receiveCount'], m['externalFramesPending'], m['baud'], m['dataBits'], m['stopBits'], m['parity']) == (191, 0, 0, 31250, 8, 1, False), 'final clear queues and explicit complete 8N1 frames')
        for key, value in dict(transmittedBytes=len(observed['outgoing']), receivedFrames=len(observed['incoming']), receivedBytes=observed['received'],
                               ignoredInputFrames=observed['ignored_rx'], overruns=observed['overruns'], resets=observed['resets'], acknowledgements=observed['acknowledgements'],
                               rejectedWrites=observed['rejected'], ignoredCommands=observed['ignored'], abortedOutputBytes=observed['aborted']).items():
            require(m[key] == value, 'independently derived report counter ' + key)
        require(len(raw) == 384 and raw == expected_tx == bytes(v['Data'] for v in observed['outgoing']), 'complete byte-exact raw MIDI output')
        require(m['outputFileBytes'] == 384 and m['outputSha256'] == hashlib.sha256(raw).hexdigest().upper(), 'actual output file/digest')
        require((m['resets'], m['acknowledgements'], m['overruns'], m['abortedOutputBytes']) == (4, 7, 16, 1), 'intended independent control cases actually executed')
        sb = r['sb16']; require(sb['mpu401'] and sb['interruptSources'] == 0 and sb['softwareIrq8Requests'] == sb['softwareIrq16Requests'] == 2 and sb['completedBlocks'] == sb['dma8Grants'] == sb['dma16Grants'] == sb['unsupportedCommands'] == sb['unsupportedMixerAccesses'] == 0, 'actual distinct software IRQs without invented PCM/DMA or unsupported commands')
        require(r['ataSectorsWritten'] == r['floppySectorsWritten'] == 0, 'no guest media writes')
        if args.manager:
            require(r['cpu']['pagingEnabled'] and r['cpu']['virtual8086Instructions'] > 0, 'actual original manager paging/V86')

    check_memory(memory); check_report(report, output)
    negatives = 0
    if args.negative_controls:
        changed = bytearray(memory)
        targets = [base - 15, base - 13, base + 256, proof, proof + 16] + [proof + i for i in range(28, 56)]
        targets += [proof + 62 + i for i in range(17)] + [base + tx + i for i in range(0, 384, 13)]
        targets += [base + rx + i for i in range(129)] + [base + guard + i for i in range(32)]
        targets += [base + rows + i * 4 + j for i in range(0, 129, 8) for j in range(4)]
        for address in targets:
            position = physical(address); changed[position] ^= 1
            try:
                check_memory(changed)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('accepted altered code/proof/array/MCB')
            changed[position] ^= 1
        for kind in ('tx', 'rx', 'io', 'irq'):
            damaged = copy.deepcopy(events)
            event = next(e for e in damaged if e['type'] == kind and (kind != 'io' or e['port'] == port + 1 and not e['write']))
            if kind == 'tx':
                event['frame']['Completed'] += 1
            elif kind == 'rx':
                event['reception']['Frame']['Data'] ^= 1
            elif kind == 'io':
                event['value'] ^= 1
            else:
                event['sources'] ^= 1
            try:
                certify_ports(damaged, port, card, irq, report['status']['virtualNanoseconds'])
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('accepted altered physical event')
        for field in ('text', 'midi'):
            damaged = copy.deepcopy(report)
            damaged[field] = 'C:\\>echo MIDI-DOS-RETURN\r\nC:\\>' if field == 'text' else {**damaged['midi'], 'hostMidi': True}
            try:
                check_report(damaged, output)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('accepted altered return/capability report')
        try:
            check_report(report, bytes([output[0] ^ 1]) + output[1:])
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('accepted altered serial output')
    return dict(passed=True, basePort=port, irq=irq, cardBase=card, outputFrames=len(observed['outgoing']), inputFrames=len(observed['incoming']),
                receivedBytes=observed['received'], ignoredInputFrames=observed['ignored_rx'], overruns=observed['overruns'],
                physicalIrqAcknowledgements=len(observed['irq']), handlerBytes=129, coalescedThreeSourceHandlers=1,
                frameNanoseconds=320000, frameBoundsCertified=len(observed['outgoing']) + len(observed['incoming']),
                negativeControlsRejected=negatives, proofSha256=hashlib.sha256(read(memory, proof, guard + 32 - offset)).hexdigest().upper(),
                hostMidi=False, synthesizer=False, intelligentMode=False,
                scope='CT1740 MPU UART protocol and nominal integer frame/queue/service model; actual DOS shared IRQs, no physical silicon/synth/host-MIDI claim')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for name in ('report', 'memory', 'program', 'output', 'io'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--manager', action='store_true')
    parser.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(parser.parse_args()), indent=2))
