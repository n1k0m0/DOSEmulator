# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent sustained DMA/frame/IRQ/RAM/audio oracle; imports no emulator implementation."""
import argparse
import array
import base64
import copy
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

REPO = Path(__file__).resolve().parents[1]
BLOCKS, WORDS, RATE, MASTER, DIVIDER = 512, 512, 8000, 14318180, 288


def check(value, reason):
    """Reject an incorrect independently observed port, raw payload, source, deadline or guest result."""
    if not value:
        raise AssertionError(reason)


def safe(path):
    """Resolve an explicitly supplied artifact without opening any personal play journal."""
    result = Path(path).resolve()
    check('doom2-play' not in str(result).lower(), 'personal play journal excluded')
    return result


def load(path):
    """Load exactly one named UTF-8 evidence file."""
    return json.loads(safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Stream one specifically named evidence file into an uppercase SHA-256 digest."""
    with safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def raw_array(raw, code):
    """Interpret portable little-endian captures without depending on the host byte order."""
    values = array.array(code)
    values.frombytes(raw)
    if sys.byteorder != 'little':
        values.byteswap()
    return values


def signed(value):
    """Interpret the actual two's-complement sixteen-bit DMA component."""
    return (value + 32768) % 65536 - 32768


def state_proof(work, result, cut, final, manifest):
    """Check sealed state identities, explicit field/event inventory and truly active simultaneous devices at capture."""
    root, saved = work / 'evidence', work / 'evidence/active.dossav'
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1, 'full snapshot format')
    check(manifest['ramBytes'] == 8388608 and manifest['ramSha256'] == sha(saved / 'ram.bin'), 'capture RAM identity')
    check(manifest['stateBytes'] == (saved / 'state.json').stat().st_size and manifest['stateSha256'] == sha(saved / 'state.json'), 'capture raw state')
    check(manifest['mediaSha256'] == sha(saved / 'media.dosemedia/media.json'), 'sealed media identity')
    check(result['rejectedImports'] == 1 and not (root / 'rejected.dosebranch').exists(), 'invalid import rejected before adoption')
    check(load(root / 'invalid.dossav/snapshot.json')['stateSha256'] == '0' * 64, 'actual malformed import identity')
    check(result['passed'] and result['comparisons'] >= 200 and result['packets'] == BLOCKS and result['steps'] > 1000000, 'actual sustained execution')
    check('COMB-DONE' in result['debug'] and 'COMB-FAILED' not in result['debug'] and result['debug'].rfind('A:\\>') > result['debug'].rfind('COMB-DONE') and 'A:\\>' in result['text'], 'executed DOS return')
    check(result['futureFrames'] > 500000 and len(result['futureAudioSha256']) == 64 and result['futureAudioSha256'] == result['restoredAudioSha256'], 'continued PCM equality')
    check(len(result['futureSignalsSha256']) == 64 and result['futureSignalsSha256'] == result['restoredSignalsSha256'], 'continued FM/DMA/MIDI equality')
    check(result['capturedStateSha256'] == manifest['stateSha256'] and result['manifest'] == manifest, 'capture manifest equality')
    check(result['finalStateSha256'] == sha(root / 'final-state.json') == sha(root / 'final-b-state.json'), 'both complete final states')
    check(result['finalRamSha256'] == sha(root / 'final-ram.bin') == sha(root / 'final-b-ram.bin'), 'both complete final RAMs')
    schema = load(REPO / 'docs/state-schema-v1.json')['nodes']
    for state in (cut, final):
        for component in state['components'].values():
            check(component['type'] in schema and component['version'] == 1, 'known component schema')
            expected = {f['name'] for f in schema[component['type']]['fields'] if f['mode'] not in ('children', 'excluded')}
            check(expected == set(component['fields']), 'complete reviewed state field set')
    now = cut['scheduler']['now']
    check(now == manifest['virtualNanoseconds'] == result['longStart'] + 170 * 32000000, 'actual owner cut clock')
    check(load(saved / 'media.dosemedia/media.json')['virtualNanoseconds'] == now, 'shared media clock')
    fields = {key: row['fields'] for key, row in cut['components'].items()}
    check(fields['sb16']['_auto'] and fields['sb16']['RemainingSamples'] == WORDS and fields['sb16']['_sampleDeadline'] > now, 'live auto-init PCM at cut')
    check(fields['midi']['UartMode'] and fields['midi']['_receiving'] and fields['midi']['_receiveStart'] == now, 'live framed MIDI at cut')
    check(fields['opl3']['KeyOnCount'] == 4 and any(fields[f'opl3/_operators/{i}']['Keyed'] for i in range(4)), 'live FM envelopes at cut')
    check(any(fields[f'audio/_inputs/{i}']['Filtered'] != 0 for i in range(1, 5)), 'retained nonzero audio filter state')
    legal = {(row['owner'], row['operation']) for row in load(REPO / 'docs/state-events-v1.json')['events']}
    ids = [row['id'] for row in cut['scheduler']['events']]
    check(len(ids) == len(set(ids)), 'unique scheduler tokens')
    for event in cut['scheduler']['events']:
        check(now <= event['time'] and 0 < event['id'] <= cut['scheduler']['sequence'], 'pending event bounds')
        check((cut['components'][event['owner']]['type'], event['operation']) in legal, 'known event continuation')
    check({'midi', 'sb16'} <= {e['owner'] for e in cut['scheduler']['events']}, 'live DMA and MIDI deadlines')
    check(load(root / 'final-a.dosemedia/media.json')['media'] == load(root / 'final-b.dosemedia/media.json')['media'], 'future media equality')
    sf, mf, of = (final['components'][k]['fields'] for k in ('sb16', 'midi', 'opl3'))
    check(sf['Dma8Grants'] == 16 and sf['Dma16Grants'] == BLOCKS * WORDS + 16 and sf['CompletedBlocks'] == BLOCKS + 2, 'actual controller grant/block totals')
    check(sf['SoftwareIrq8Requests'] == sf['SoftwareIrq16Requests'] == sf['UnsupportedCommands'] == sf['UnsupportedMixerAccesses'] == 0, 'no software IRQ or unsupported card traffic')
    check(sf['Irq8Requests'] == 1 and sf['Irq16Requests'] == BLOCKS + 1 and sf['_interruptSources'] == 0, 'separate physical completion sources')
    check(mf['TransmittedBytes'] == BLOCKS * 3 and mf['ReceivedBytes'] == 1 + BLOCKS * 3 and mf['Overruns'] == mf['RejectedWrites'] == mf['IgnoredCommands'] == 0, 'actual MIDI totals')
    check(of['UnsupportedWrites'] == of['RejectedWrites'] == of['IgnoredBankWrites'] == 0 and of['RegisterWrites'] == 26, 'bounded valid FM writes')
    check(final['components']['machine']['fields']['Fault'] is None and final['components']['machine']['fields']['HardwareResetCount'] == 0, 'no reset/fault')
    return dict(components=len(cut['components']), pendingEvents=len(ids), captureNanoseconds=now)


def ram_proof(ram, program, result, config, final, manager):
    """Check real DOS-owned buffers, unchanged code, VDS balance, restored vector/masks and every input byte."""
    check(len(ram) == 8388608, 'complete RAM size')
    offset = program.index(b'COMBPRO1')
    address = result['proofAddress']
    segment = (address - offset - 256) // 16
    check(address == segment * 16 + offset + 256 and ram[address:address + 8] == b'COMBPRO1', 'executed proof location')
    check(ram[segment * 16 + 256:address] == program[:offset], 'all immutable code/tables')
    allocation, window, owner, size, old_ip, old_cs = struct.unpack_from('<6H', ram, address + 8)
    check(owner == segment and size == 0x4000 and allocation <= window and window % 0x2000 == 0 and window + 0x2000 <= allocation + size, 'own aligned DOS DMA allocation')
    check(window * 16 + 0x20000 < 0xA0000, 'DMA conventional RAM bounds')
    check(tuple(ram[address + 22:address + 24]) == (1, 1), 'long phase and guest success')
    proof = struct.unpack_from('<16H', ram, address + 24)
    joint, wide, handlers, first, byte_count, word_count, blocks, sent, irq_count, errors, rx_count, tx_count, locks, unlocks, disables, enables = proof
    check((joint, wide, handlers, first, byte_count, word_count, blocks, sent, errors, rx_count, tx_count) ==
          (7, 65535, 1, 7, 1, BLOCKS + 1, BLOCKS, BLOCKS, 0, BLOCKS * 3 + 1, BLOCKS * 3), 'real three-source/width/length guest proof')
    check((locks, unlocks, disables, enables) == ((1, 1, 2, 2) if manager else (0, 0, 0, 0)), 'actual VDS lock/translation balance')
    check(struct.unpack_from('<IIHHI', ram, address + 56) == (0x20000, 0, window, 0, window * 16 if manager else 0), 'actual VDS identity DDS')
    received = bytes([0x69] + [v for i in range(BLOCKS) for v in (0x80, i & 127, (i * 29 + 7) & 127)])
    check(ram[address + 72:address + 72 + len(received)] == received, 'all ISR MIDI data bytes')
    check(struct.unpack_from('<H', ram, address + 72 + len(received))[0] == 0xFACE, 'proof end guard')
    start = window * 16
    check(ram[start + 0x1000:start + 0x1010] == bytes((17 + 13 * i) & 255 for i in range(16)), 'byte DMA buffer intact')
    check(ram[start + 0x4000:start + 0x4400] == b''.join(struct.pack('<H', (0x1234 + 4093 * i) & 65535) for i in range(WORDS)), 'whole auto-init ring intact')
    for location, expected in [(0xFFE, 0xA55A), (0x1010, 0x5AA5), (0x3FFE, 0xA55A), (0x4400, 0x5AA5)]:
        check(struct.unpack_from('<H', ram, start + location)[0] == expected, 'DMA RAM guard')
    mcb = (segment - 1) * 16
    check(ram[mcb] in (0x4D, 0x5A) and struct.unpack_from('<HH', ram, mcb + 1)[0] == segment, 'proof resident MCB ownership')
    check(struct.unpack_from('<H', ram, mcb + 3)[0] == (len(program) + 0x10F) // 16, 'limited TSR MCB size')
    vector = config['irq'] + (8 if config['irq'] < 8 else 0x68)
    check(struct.unpack_from('<HH', ram, vector * 4) == (old_ip, old_cs), 'restored DOS handler vector')
    cpu = final['components']['cpu']['fields']
    check(bool(cpu['Cr0'] & 1) == manager, 'actual protected mode')
    if manager:
        check(cpu['Cr3'] != 0 and cpu['Virtual8086Instructions'] > 1000 and cpu['ProtectedInstructions'] > 1000, 'actual EMM386 paging/V86 caller')
    return dict(irqHandlers=irq_count, oldMaster=ram[address + 20], oldSlave=ram[address + 21], proofAddress=address)


def streams_proof(events, io, config, result):
    """Reconstruct every DMA component and framed serial byte from fixed math, raw commands and actual times."""
    transfers = [e['value'] for e in events if e['kind'] == 'transfer']
    samples = [e['value'] for e in events if e['kind'] == 'sample']
    blocks = [e['value'] for e in events if e['kind'] == 'block']
    check(len(transfers) == 3 and len(samples) == 32 + BLOCKS * WORDS and len(blocks) == BLOCKS + 2, 'actual stream dimensions')
    packets, pending, expected = [], [], 0
    for row in io:
        if row['write'] and row['port'] == config['card'] + 12:
            check(row['width'] == 1 and row['count'] == 1, 'byte command port')
            pending.append(row['value'])
            if len(pending) == 1:
                expected = 4 if 0xB0 <= pending[0] <= 0xCF else 3 if pending[0] == 0x41 else 1
            if len(pending) == expected:
                packets.append((pending, row['first'] + 1000))
                pending = []
    check(not pending and [p[0] for p in packets] == [[0x41, 0x1F, 0x40], [0xC0, 0, 15, 0], [0xB0, 0x30, 15, 0], [0xB6, 0x30, 255, 1], [0xD9]], 'actual DSP commands, no F2/F3')
    cursor = 0
    for i, t in enumerate(transfers):
        word, count = i != 0, 16 if i < 2 else BLOCKS * WORDS
        check(t['time'] == packets[i + 1][1] and t['rate'] == RATE and t['word'] == word and t['signed'] == word and t['stereo'] == word, 'physical DSP start/format')
        check(not t['recording'] and t['autoInit'] == (i == 2) and t['samples'] == (WORDS if i == 2 else 16), 'actual playback/ring packet')
        check(t['dmaChannel'] == config['dma16' if word else 'dma8'], 'actual DMA resource')
        for index in range(count):
            sample = samples[cursor]
            raw = (0x1234 + 4093 * (index % WORDS)) & 65535 if word else (17 + 13 * index) & 255
            value = signed(raw) if word else (raw - 128) * 256
            check(sample == dict(time=t['time'] + (index // (2 if word else 1) + 1) * 125000, word=word, recording=False,
                                 right=bool(index & 1) if word else False, raw=raw, value=value, dmaResult='Transferred'), 'DMA data/direction/stereo/time')
            cursor += 1
    expected_blocks = [dict(time=transfers[0]['time'] + 2000000, word=False), dict(time=transfers[1]['time'] + 1000000, word=True)]
    expected_blocks += [dict(time=transfers[2]['time'] + n * 32000000, word=True) for n in range(1, BLOCKS + 1)]
    check(blocks == expected_blocks and transfers[2]['time'] == result['longStart'], 'every actual DMA block deadline')
    outgoing = [e['value'] for e in events if e['kind'] == 'midi-out']
    incoming = [e['value'] for e in events if e['kind'] == 'midi-in']
    outer = [e['value'] for e in events if e['kind'] == 'outer']
    check(len(outgoing) == BLOCKS * 3 and len(incoming) == BLOCKS * 3 + 1 and len(outer) == BLOCKS + 1, 'actual full MIDI counts')
    writes = [r for r in io if r['write'] and r['port'] == config['mpu']]
    check(len(writes) == len(outgoing) and all(r['width'] == r['count'] == 1 for r in writes), 'raw MPU data writes')
    end = 0
    for index, (frame, row) in enumerate(zip(outgoing, writes)):
        block, slot = index // 3 + 1, index % 3
        expected_byte = (0x90, block & 127, (block * 17 + 3) & 127)[slot]
        check(frame == dict(started=max(end, row['first']), completed=max(end, row['first']) + 320000, data=expected_byte), 'complete 31250-baud TX frame')
        check(frame['started'] >= result['longStart'] + block * 32000000, 'MIDI output before actual DMA block')
        end = frame['completed']
    expected_input, end = [], 0
    for index, row in enumerate(outer):
        data = [0x69] if index == 0 else [0x80, (index - 1) & 127, ((index - 1) * 29 + 7) & 127]
        check(base64.b64decode(row['data']) == bytes(data), 'virtual input packet content')
        if index:
            check(result['longStart'] + (index - 1) * 32000000 <= row['time'] < result['longStart'] + (index - 1) * 32000000 + 1000000, 'outer owner packet time')
        for value in data:
            start = max(end, row['time'])
            expected_input.append(dict(frame=dict(started=start, completed=start + 320000, data=value), accepted=True, overrun=False))
            end = start + 320000
    check(incoming == expected_input, 'all complete RX frames/deadlines/acceptance')
    return dict(transfers=len(transfers), dmaComponents=len(samples), dmaBlocks=len(blocks), txFrames=len(outgoing), rxFrames=len(incoming), durationNanoseconds=BLOCKS * 32000000)


def irq_proof(events, io, config, proof):
    """Rebuild source latches and MPU FIFO from callbacks and actual independent-width port acknowledgments."""
    start = next(e['value']['time'] for e in events if e['kind'] == 'transfer')
    timeline = []
    for order, event in enumerate(events):
        if event['kind'] in ('block', 'midi-in', 'irq'):
            v = event['value']
            time = v['frame']['completed'] if event['kind'] == 'midi-in' else v['time']
            timeline.append((time, 0, order, event['kind'], v))
    for order, row in enumerate(io):
        if row['first'] >= start:
            timeline.append((row['first'], 1, order, 'io', row))
    timeline.sort()
    source, fifo, selected, handlers, joint, wrong, ack8, ack16, reads, master_eoi, slave_eoi = 0, [], 0, 0, False, 0, 0, 0, 0, 0, 0
    bases, icw = {0: 8, 1: 0x70}, {0: False, 1: False}
    # Recover the actual ICW2 mapping, including EMM386's protected-mode PIC layout.
    for row in io:
        if row['first'] >= start:
            break
        if row['write'] and row['port'] in (0x20, 0xA0) and row['value'] & 0x10:
            icw[int(row['port'] == 0xA0)] = True
        elif row['write'] and row['port'] in (0x21, 0xA1) and icw[int(row['port'] == 0xA1)]:
            n = int(row['port'] == 0xA1)
            bases[n], icw[n] = row['value'] & 0xF8, False
    pending_handler, first_handler = False, False
    for _, _, _, kind, value in timeline:
        if kind == 'block':
            source |= 2 if value['word'] else 1
        elif kind == 'midi-in':
            fifo.append(value['frame']['data'])
            source |= 4
        elif kind == 'irq':
            check(source != 0 and not pending_handler, 'physical card IRQ source/in-service ordering')
            check(value['line'] == config['irq'] and not value['spurious'] and value['vector'] == bases[int(config['irq'] >= 8)] + (config['irq'] & 7), 'physical IRQ line/remapped vector')
            handlers += 1
            pending_handler, first_handler = True, handlers == 1
            if first_handler:
                check(source == 7, 'first physical handler has true DMA8 + DMA16 + MPU')
                joint = True
        else:
            row = value
            port, width, data = row['port'], row['width'], row['value']
            if row['write']:
                if port == config['card'] + 4:
                    selected = data
                if pending_handler and port == 0xA0 and data == 0x20:
                    slave_eoi += 1
                if pending_handler and port == 0x20 and data == 0x20:
                    check(source == 0, 'handler EOI before separate source drain')
                    pending_handler = False
                    master_eoi += 1
            elif port == config['card'] + 5 and selected == 0x82:
                check(width == 1 and data == source, 'independent mixer source status')
            elif port == config['card'] + 14 and width == 1:
                if source & 1:
                    ack8 += 1
                elif source & 2:
                    wrong += 1
                source &= ~1
            elif port == config['card'] + 15:
                if width == 2:
                    check(data == 65535 and source == 7, 'wide port must not acknowledge byte sources')
                else:
                    check(width == 1 and source & 2, 'word source acknowledgment')
                    source &= ~2
                    ack16 += 1
            elif port == config['mpu'] and width == 1:
                check(fifo and data == fifo.pop(0), 'actual MPU read returns oldest framed byte')
                reads += 1
                if not fifo:
                    source &= ~4
    port_text = ''.join(chr(row['value']) for row in io if row['port'] == 0xE9 and row['write'])
    check(port_text.count('COMB-DONE') == 1 and port_text.rfind('A:\\>') > port_text.rfind('COMB-DONE'), 'actual DOS prompt after TSR through real guest ports')
    check(joint and source == 0 and not fifo and not pending_handler, 'all sources drained separately')
    check((ack8, ack16, wrong, reads) == (1, BLOCKS + 1, BLOCKS, BLOCKS * 3 + 1), 'independent ACK counts/wrong-width preservation')
    check(handlers == proof['irqHandlers'] and master_eoi == handlers and slave_eoi == (handlers if config['irq'] >= 8 else 0), 'real IRQ handlers and cascade EOIs')
    for port, expected in ((0x21, proof['oldMaster']), (0xA1, proof['oldSlave'])):
        check([r['value'] for r in io if r['write'] and r['port'] == port][-1] == expected, 'restored PIC mask')
    return dict(physicalHandlers=handlers, wrongSourceAcks=wrong, tripleSourceHandlers=1)


def pcm_reference(events, native, frames):
    """Integrate actual native FM plus independently validated DAC holds, with four separate nominal gain/DC paths."""
    changes = []
    for event in events:
        if event['kind'] == 'sample':
            sample = event['value']
            changes.append((sample['time'], 1, sample))
        elif event['kind'] == 'transfer':
            changes.append((event['value']['time'], 0, event['value']))
    changes.sort(key=lambda r: (r[0], r[1]))
    # Native output N is published at ceil(N*288*1e9/14318180); it holds until N+1.
    time, native_index, change_index, next_native = 0, 0, 0, (DIVIDER * 1000000000 + MASTER - 1) // MASTER
    values, previous, filtered, result = [0, 0, 0, 0], [0.0] * 4, [0.0] * 4, array.array('f')
    stereo, gain, decay = False, 10 ** (-28 / 20), math.exp(-2 * math.pi * 20 / 48000)
    for frame in range(frames):
        end = ((frame + 1) * 1000000000 + 47999) // 48000
        begin, area = time, [0, 0, 0, 0]
        while time < end:
            change_time = changes[change_index][0] if change_index < len(changes) else end
            stop = min(end, next_native, change_time)
            check(stop >= time, 'ordered audio intervals')
            span = stop - time
            for side in range(4):
                area[side] += span * values[side]
            time = stop
            if time == next_native:
                check(native_index * 4 + 3 < len(native), 'native frame availability')
                values[2], values[3] = native[native_index * 4], native[native_index * 4 + 1]
                native_index += 1
                next_native = ((native_index + 1) * DIVIDER * 1000000000 + MASTER - 1) // MASTER
            while change_index < len(changes) and changes[change_index][0] == time:
                _, kind, value = changes[change_index]
                if kind == 0:
                    stereo = value['stereo']
                elif stereo:
                    values[int(value['right'])] = value['value']
                else:
                    values[0] = values[1] = value['value']
                change_index += 1
        for side in range(4):
            average = area[side] * gain / ((end - begin) * 32768)
            filtered[side] = decay * (filtered[side] + average - previous[side])
            previous[side] = average
        result.extend((max(-1, min(1, (filtered[0] + filtered[2]) * 0.5)), max(-1, min(1, (filtered[1] + filtered[3]) * 0.5))))
    return result


def audio_proof(events, io, native, actual, config, result):
    """Check all stereo PCM values, independent routing/pitch, and actual uninterrupted FM during the long DMA stream."""
    check(len(actual) == result['pcmFrames'] * 2 and len(native) == result['nativeFrames'] * 4, 'complete audio captures')
    pcm = [e['value'] for e in events if e['kind'] == 'pcm']
    count = 0
    for row in pcm:
        check(row['time'] == (count * 1000000000 + 47999) // 48000 and 0 < row['frames'] <= 480, 'continuous PCM block boundaries')
        count += row['frames']
    check(count == result['pcmFrames'], 'published PCM count')
    check(all(x == 0 for x in native[2::4]) and all(x == 0 for x in native[3::4]), 'unused C/D native buses silent')
    check(not any(r['write'] and r['port'] == 0x61 and r['value'] & 3 for r in io), 'PC speaker never enabled')
    address, registers, last = None, [], -1000
    for row in io:
        if row['write'] and config['card'] <= row['port'] < config['card'] + 4:
            check(row['width'] == 1 and row['first'] >= last + 750, 'actual timed byte FM writes')
            last = row['first']
            offset = row['port'] - config['card']
            if offset % 2 == 0:
                address = row['value'] | (256 if offset == 2 else 0)
            else:
                check(address is not None, 'FM address before data')
                registers.append((address, row['value']))
    expected_registers = [(0x105, 1), (0x20, 33), (0x23, 33), (0x40, 32), (0x43, 0), (0x60, 240), (0x63, 240), (0x80, 15), (0x83, 15), (0xC0, 16), (0xA0, 152), (0xB0, 49),
                          (0x21, 33), (0x24, 33), (0x41, 32), (0x44, 0), (0x61, 240), (0x64, 240), (0x81, 15), (0x84, 15), (0xC1, 32), (0xA1, 85), (0xB1, 50), (1, 0), (0xB0, 0), (0xB1, 0)]
    check(registers == expected_registers, 'independent FM program and stereo routing')
    pitches = []
    start = result['longStart']
    for side, fnum in [(0, 408), (1, 597)]:
        estimates = []
        # Probe beginning, middle and end; no silent hole is hidden by one global RMS value.
        for seconds in (1, 8, 15):
            begin = (start + seconds * 1000000000) * MASTER // (DIVIDER * 1000000000)
            values = native[begin * 4 + side:(begin + 24000) * 4:4]
            check(len(values) == 24000 and sum(x * x for x in values) / len(values) > 1000000, 'sustained nonzero FM route')
            edges = [i for i in range(1, len(values)) if values[i - 1] < 0 <= values[i]]
            check(len(edges) > 100, 'tonal FM periods')
            hz = (len(edges) - 1) * MASTER / DIVIDER / (edges[-1] - edges[0])
            expected_hz = MASTER / DIVIDER * fnum / 65536
            check(abs(hz / expected_hz - 1) < 0.005, 'independently expected channel pitch')
            estimates.append(hz)
        pitches.append(estimates)
    reference = pcm_reference(events, native, len(actual) // 2)
    error = max(abs(x - y) for x, y in zip(actual, reference))
    check(error <= 1 / 32767, 'combined gain/integration/filter/stereo PCM differs by more than one PCM16 LSB')
    check(max(abs(x) for x in actual) > 0.02 and all(math.isfinite(x) for x in actual), 'finite audible combined output')
    return dict(pcmFrames=len(actual) // 2, nativeFrames=len(native) // 4, maximumAbsoluteError=error, frequencyHz=pitches), reference


def reject(action):
    """Require a deliberately corrupted observation to fail its independent oracle."""
    try:
        action()
    except (AssertionError, KeyError, ValueError, IndexError):
        return 1
    raise AssertionError('corrupted evidence accepted')


def verify(work, program, manager=False):
    """Certify one original/restored DOS run and exercise distinct state, source, byte, time and signal corruptions."""
    work, program = safe(work), safe(program).read_bytes()
    root = work / 'evidence'
    result, config = load(root / 'result.json'), load(work / 'input.json')
    definition = load(work / 'source.dospc.json')['hardware']
    config['dma8'], config['dma16'] = definition['soundBlasterDmaChannel'], definition['soundBlasterDma16Channel']
    cut, final, manifest = load(root / 'active.dossav/state.json'), load(root / 'final-state.json'), load(root / 'active.dossav/snapshot.json')
    ram = (root / 'final-ram.bin').read_bytes()
    events = [json.loads(row) for row in (root / 'trace/events.jsonl').read_text().splitlines()]
    io = [json.loads(row) for row in (root / 'trace/io.jsonl').read_text().splitlines()]
    check(all(row['count'] > 0 and row['first'] <= row['last'] for row in io), 'valid compressed poll bounds')
    check(all(a['last'] <= b['first'] for a, b in zip(io, io[1:])), 'monotonic real port trace')
    state = state_proof(work, result, cut, final, manifest)
    proof = ram_proof(ram, program, result, config, final, manager)
    streams = streams_proof(events, io, config, result)
    irq = irq_proof(events, io, config, proof)
    native, actual = raw_array((root / 'trace/opl3.s16').read_bytes(), 'h'), raw_array((root / 'trace/audio.f32').read_bytes(), 'f')
    audio, reference = audio_proof(events, io, native, actual, config, result)
    negatives = 0
    for key in ['rejectedImports', 'passed', 'comparisons', 'packets', 'steps', 'futureFrames', 'futureAudioSha256', 'restoredSignalsSha256', 'finalStateSha256', 'finalRamSha256', 'debug', 'text']:
        changed = dict(result)
        changed[key] = False if isinstance(changed[key], bool) else 0 if isinstance(changed[key], (int, float)) else 'bad'
        negatives += reject(lambda: state_proof(work, changed, cut, final, manifest))
    for node, key in [('cpu', 'Cr3'), ('sb16', '_sampleDeadline'), ('midi', '_receiving'), ('audio/_inputs/1', 'Filtered'), ('opl3/_operators/0', 'Phase')]:
        changed = copy.deepcopy(cut)
        del changed['components'][node]['fields'][key]
        negatives += reject(lambda: state_proof(work, result, changed, final, manifest))
    for offset in [0, 24, 26, 28, 30, 32, 34, 36, 42, 44, 46, 48, 56, 72, 72 + BLOCKS * 3]:
        changed = bytearray(ram)
        changed[result['proofAddress'] + offset] ^= 1
        negatives += reject(lambda: ram_proof(changed, program, result, config, final, manager))
    for kind, key in [('sample', 'raw'), ('sample', 'time'), ('block', 'time'), ('midi-out', 'completed'), ('midi-in', 'accepted')]:
        changed = copy.deepcopy(events)
        row = next(e['value'] for e in changed if e['kind'] == kind)
        row[key] = not row[key] if isinstance(row[key], bool) else row[key] + 1
        negatives += reject(lambda: streams_proof(changed, io, config, result))
    changed = copy.deepcopy(io)
    row = next(row for row in changed if not row['write'] and row['port'] == config['card'] + 5 and row['value'] == 7)
    row['value'] = 3
    negatives += reject(lambda: irq_proof(events, changed, config, proof))
    changed = copy.deepcopy(events)
    next(row['value'] for row in changed if row['kind'] == 'irq')['spurious'] = True
    negatives += reject(lambda: irq_proof(changed, io, config, proof))
    # Reuse the independently calculated signal, not a second device-model calculation.
    for index in [int(result['longStart'] * 48000 // 1000000000) * 2 + 100, len(actual) // 2, len(actual) - 100]:
        changed = actual[index] + 0.01
        negatives += reject(lambda: check(abs(changed - reference[index]) <= 1 / 32767, 'PCM mutation'))
    return dict(passed=True, negativeOracles=negatives, **state, **proof, **streams, **irq, **audio)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('program', type=Path)
    parser.add_argument('--manager', action='store_true')
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.program, args.manager), indent=2))
