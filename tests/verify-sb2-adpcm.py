# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent Creative-format, guest DMA/IRQ and complete PCM acceptance.

Expected input is the probe's own affine byte sequence. No DSP sample trace,
emulator assembly, or saved expected sound is used. Timing checks implement the
explicit sample-level reference contract, not a measurement of original silicon.
"""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import sys
import wave

p = argparse.ArgumentParser()
for name in ('report', 'memory', 'audio', 'io', 'irq'):
    p.add_argument('--' + name, required=True, type=Path)
a = p.parse_args()
report = json.loads(a.report.read_text(encoding='utf-8-sig'))
io = [json.loads(line) for line in a.io.read_text(encoding='utf-8-sig').splitlines()]
irq = [json.loads(line) for line in a.irq.read_text(encoding='utf-8-sig').splitlines()]
lengths = {0xe1: 0, 0x40: 1, 0xd1: 0, 0xd3: 0, 0xd0: 0, 0xd4: 0,
           0xda: 0, 0x48: 2, 0x1f: 0, 0x7d: 0, 0x7f: 0,
           **{cmd: 2 for cmd in (0x16, 0x17, 0x74, 0x75, 0x76, 0x77)}}
commands, command, parameters = [], None, []
for event in io:
    if event['port'] != 0x22c or not event['write']:
        continue
    assert event['width'] == 1
    if command is None:
        command, parameters = event['value'], []
        assert command in lengths, hex(command)
    else:
        parameters.append(event['value'])
    if len(parameters) == lengths[command]:
        commands.append((event['time'] + 1000, command, parameters))
        command = None
assert command is None
data = bytes([128] + [(i * 73 + 19) & 255 for i in range(1, 128)])
memory, marker = a.memory.read_bytes(), b'SB2-ADPCM-RESULTS-V1'
matches = []
for match in re.finditer(re.escape(marker), memory):
    physical, = struct.unpack_from('<I', memory, match.end())
    if 0x1000 <= physical < len(memory) - 129 and physical % 256 == 0 and memory[physical:physical+128] == data:
        matches.append(physical)
assert len(matches) == 1, matches
physical = matches[0]
assert (memory[physical-1], memory[physical+128]) == (0x5a, 0xa5)

# Check actual DMA programming; no device snapshot supplies addresses or counts.
address = count = page = flip = 0
programs = []
for e in io:
    if not e['write']:
        continue
    port, value = e['port'], e['value']
    if port == 0x0c:
        flip = 0
    elif port in (2, 3):
        old = address if port == 2 else count
        value = (old & 0xff00 | value) if flip == 0 else (old & 255 | value << 8)
        if port == 2:
            address = value
        else:
            count = value
        flip ^= 1
    elif port == 0x83:
        page = value
    elif port == 0x0b and value & 3 == 1:
        programs.append((e['time'], value, page << 16 | address, count+1))
assert [(mode, addr, size) for _, mode, addr, size in programs] == [
    (mode, physical, size) for _ in range(3) for mode, size in ((0x49,64),(0x49,64),(0x59,128),(0x59,128))]

# Decode the three codebooks from their scalar equations. Values are based on
# DOSLIB's primary hardware observations, including its corrected 2-bit top step.
predictor, level, samples, transitions, completions, dma_bytes = 128, 0, 0, [(0, 0.0)], [], 0
period = 125000

def block(bits, payload, reference):
    """Decodes one explicitly selected ADPCM block using the independent predictor/scale contract."""
    global predictor, level, dma_bytes
    out = []
    if reference:
        predictor, level = payload[0], 0
        out.append(predictor)
        payload = payload[1:]
        dma_bytes += 1
    else:
        level = {2: 5, 3: 4, 4: 3}[bits]
    for byte in payload:
        dma_bytes += 1
        codes = ([byte >> 4, byte & 15] if bits == 4 else
                 [byte >> 5, byte >> 2 & 7, (byte & 3) * 2] if bits == 3 else
                 [byte >> shift & 3 for shift in (6, 4, 2, 0)])
        sign = 2 ** (bits-1)
        for code in codes:
            magnitude = code % sign
            scale = ([0, 1, 2, 4, 5] if bits == 3 else [0, 1, 2, 4, 8, 16])[level]
            distance = magnitude if level == 0 else scale * (2*magnitude+1)
            predictor = min(255, max(0, predictor + distance * (1 if code < sign else -1)))
            out.append(predictor)
            if magnitude >= {2: 1, 3: 3, 4: 5}[bits]:
                level = min(level+1, {2: 5, 3: 4, 4: 3}[bits])
            elif magnitude == 0:
                level = max(0, level-1)
    return out

starts = [c for c in commands if c[1] in (0x75,0x77,0x17,0x74,0x76,0x16,0x7d,0x7f,0x1f)]
assert len(starts) == 15
pauses = [time for time,cmd,_ in commands if cmd == 0xd0]
resumes = [time for time,cmd,_ in commands if cmd == 0xd4]
exits = [time for time,cmd,_ in commands if cmd == 0xda]
assert len(pauses) == len(resumes) == len(exits) == 3
for fmt, bits in enumerate((4,3,2)):
    ref, continuation, auto, queued_auto, queued = starts[fmt*5:fmt*5+5]
    single, autocmd = {4: (0x75,0x7d), 3: (0x77,0x7f), 2: (0x17,0x1f)}[bits]
    assert [c[1] for c in (ref,continuation,auto,queued_auto,queued)] == [single,single-1,autocmd,autocmd,single-1]
    assert ref[2] == continuation[2] == [63,0] and queued[2] == [15,0]
    for index, (start, blocks) in enumerate((
            (ref[0], [(data[:64],True)]),
            (continuation[0], [(data[:64],False)]),
            (auto[0], [(data[:64],True),(data[64:],False),(data[:64],False)]),
            (queued_auto[0], [(data[:32],True),(data[32:64],False),(data[64:80],False)]))):
        ordinal = 0
        for block_index, (payload, is_reference) in enumerate(blocks):
            decoded = block(bits,payload,is_reference)
            for value in decoded:
                ordinal += 1
                time = start + ordinal * period
                if index == 0 and time > pauses[fmt]:
                    time += resumes[fmt] - pauses[fmt]
                transitions.append((time, (value-128)/128))
                samples += 1
            completions.append(time)
            if index == 2 and block_index == 1:
                assert time <= exits[fmt] < time+period
            if index == 3 and block_index == 0:
                assert time <= queued[0] < start+(ordinal+32*(8//bits))*period
speaker = [(t,c) for t,c,_ in commands if c in (0xd1,0xd3)]
assert [c for _,c in speaker] == [0xd1,0xd3]
assert speaker[0][0] < transitions[1][0] and speaker[1][0] > transitions[-1][0]
transitions.append((speaker[1][0],0.0))
assert len(irq) == len(completions) == 24
for actual, time in zip(irq,completions):
    assert actual['vector'] == 0x0d and time <= actual['time'] < time+period, (actual,time)

with wave.open(str(a.audio),'rb') as wav:
    assert (wav.getnchannels(),wav.getsampwidth(),wav.getframerate()) == (2,2,48000)
    frames = wav.getnframes()
    raw = wav.readframes(frames)
pcm = array.array('h',raw)
if sys.byteorder != 'little':
    pcm.byteswap()
assert pcm[::2] == pcm[1::2]
assert frames == report['status']['virtualNanoseconds'] * 48000 // 1_000_000_000
decay, previous, filtered, pos, segment, error = math.exp(-2*math.pi*20/48000), 0., 0., 0, 0, 0
for frame, actual in enumerate(pcm[::2]):
    end = ((frame+1)*1_000_000_000+47999)//48000
    begin, area = pos, 0.
    while pos < end:
        while segment+1 < len(transitions) and transitions[segment+1][0] <= pos:
            segment += 1
        boundary = min(end,transitions[segment+1][0]) if segment+1 < len(transitions) else end
        area += (boundary-pos)*transitions[segment][1]
        pos = boundary
    value = area/(end-begin)
    filtered = decay*(filtered+value-previous)
    previous = value
    mixed, = struct.unpack('<f',struct.pack('<f',filtered*.5))
    error = max(error,abs(actual-round(mixed*32767)))
assert error <= 1, error
sb = report['soundBlaster']
assert dma_bytes == 1200 and sb['adpcmDmaBytes'] == sb['transferredSamples'] == dma_bytes
assert samples == sb['adpcmDecodedSamples']
assert sb['completedBlocks'] == sb['interruptRequests'] == 24
assert sb['rejectedWrites'] == sb['unsupportedCommands'] == sb['remainingSamples'] == sb['adpcmBits'] == 0
for key in ('active','paused','interruptAsserted','speakerEnabled','highSpeed'):
    assert sb[key] is False, key
assert sb['resetCount'] == 2 and sb['recordedSamples'] == 0
assert report['status']['fault'] is None and report['scenario']['complete']
assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
assert report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
assert re.search(r'\r\nADPCM-RUN-END\r\n(?:\r\n)?C:\\>$',report['status']['debugOutput'])
digest = hashlib.sha256(raw).hexdigest().upper()
assert digest == report['audio']['dataSha256']
print(json.dumps(dict(passed=True,dmaBytes=dma_bytes,decodedSamples=samples,irqCount=len(irq),
                      verifiedPcmFrames=frames,largestPcm16Difference=error,pcmSha256=digest,
                      actualDmaPrograms=len(programs),guestReturned=True,siliconTimingComparison=False)))
