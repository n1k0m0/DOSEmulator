"""Independent oracle for our ADC probe's guest port timeline, private RAM and PCM.

No emulator assemblies, DSP snapshots or pre-recorded sound are used to calculate
expected samples. The input is explicitly either unconnected midpoint or the
headless diagnostic ramp defined by absolute virtual nanoseconds.
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

parser = argparse.ArgumentParser()
for name in ("report", "memory", "audio", "io", "adc"):
    parser.add_argument("--" + name, required=True, type=Path)
parser.add_argument("--signal", required=True, choices=("ramp", "midpoint"))
parser.add_argument("--base", type=lambda value: int(value, 0), default=0x220)
parser.add_argument("--irq-line", type=int, default=5)
parser.add_argument("--irq-trace", type=Path)
args = parser.parse_args()
report = json.loads(args.report.read_text(encoding="utf-8-sig"))
io = [json.loads(line) for line in args.io.read_text(encoding="utf-8-sig").splitlines()]
adc = [json.loads(line) for line in args.adc.read_text(encoding="utf-8-sig").splitlines()]

# Decode actual guest OUTs, including the documented command/parameter framing.
# Command completion in this reference DSP occurs 1 us after the last OUT.
lengths = {0xE1: 0, 0x20: 0, 0x40: 1, 0x48: 2, 0x24: 2, 0x2C: 0,
           0x99: 0, 0x98: 0, 0xDA: 0, 0xD1: 0, 0xD3: 0, 0x14: 2}
commands, command, parameters = [], None, []
for entry in io:
    if entry["port"] != args.base + 0x0C or not entry["write"]:
        continue
    assert entry["width"] == 1
    if command is None:
        command, parameters = entry["value"], []
        assert command in lengths, f"Unexpected DSP command {command:02X}"
    else:
        parameters.append(entry["value"])
    if len(parameters) == lengths[command]:
        commands.append((entry["time"] + 1000, command, parameters))
        command = None
assert command is None, "Truncated DSP command"

source = (lambda time: time // 125000 & 255) if args.signal == "ramp" else (lambda time: 128)
direct = [time for time, cmd, _ in commands if cmd == 0x20]
assert len(direct) == 1
expected = [(direct[0], source(direct[0]), None)]
phases, period, block = [], 256000, 65536
for time, cmd, params in commands:
    if cmd == 0x40:
        period = (256 - params[0]) * 1000
    elif cmd == 0x48:
        block = (params[0] | params[1] << 8) + 1
    elif cmd in (0x24, 0x2C, 0x99, 0x98):
        count = (params[0] | params[1] << 8) + 1 if cmd == 0x24 else block
        assert count == (64 if cmd in (0x2C, 0x98) else 128)
        assert period == (68000 if cmd in (0x99, 0x98) else 125000)
        count *= 4 if cmd in (0x2C, 0x98) else 1
        samples = [(time + index * period, source(time + index * period), "Transferred")
                   for index in range(1, count + 1)]
        expected.extend(samples)
        phases.append((cmd, time, period, samples))
assert [cmd for cmd, _, _, _ in phases] == [0x24, 0x2C, 0x99, 0x98]
assert len(adc) == len(expected) == 769
for index, (actual, wanted) in enumerate(zip(adc, expected)):
    assert (actual["time"], actual["value"], actual["dmaResult"]) == wanted, (index, actual, wanted)
normal_auto, high_auto = phases[1], phases[3]
exit_auto = [time for time, cmd, _ in commands if cmd == 0xDA]
assert len(exit_auto) == 1
assert normal_auto[3][191][0] <= exit_auto[0] < normal_auto[3][192][0]
resets = [entry["time"] for entry in io if entry["port"] == args.base + 6 and entry["write"] and entry["value"] == 1]
assert len(resets) == 3
assert high_auto[3][-1][0] <= resets[1] < high_auto[3][-1][0] + high_auto[2]

# The guest saved all single-shot data and the final 128-byte half of each
# auto-init capture. Verify all saved bytes and the actual final DMA buffer.
saved = bytes(value for _, _, _, samples in phases for _, value, _ in samples[-128:])
memory, marker = args.memory.read_bytes(), b"SB2-ADC-RESULTS-V1"
matches, offset = [], 0
while (offset := memory.find(marker, offset)) >= 0:
    position = offset + len(marker)
    physical, direct_byte, midpoint = struct.unpack_from("<IBB", memory, position)
    if (physical >= 0x1000 and physical + 128 < len(memory) and physical % 128 == 0
            and direct_byte == source(direct[0]) and midpoint == (args.signal == "midpoint")
            and memory[position + 6:position + 6 + 512] == saved):
        matches.append((offset, physical))
    offset += 1
assert len(matches) == 1, f"Expected one live result structure, found {matches}"
physical = matches[0][1]
assert memory[physical:physical + 128] == saved[-128:]
assert (memory[physical - 1], memory[physical + 128]) == (0x5A, 0xA5)

# Reconstruct the actual channel-1 programming, independently of the DSP report.
address, count, page, flip, programmed = 0, 0, 0, 0, []
for entry in io:
    if entry["time"] < direct[0] or not entry["write"]:
        continue
    port, value = entry["port"], entry["value"]
    if port == 0x0C:
        flip = 0
    elif port in (2, 3):
        old = address if port == 2 else count
        updated = (old & 0xFF00 | value) if flip == 0 else (old & 255 | value << 8)
        if port == 2:
            address = updated
        else:
            count = updated
        flip ^= 1
    elif port == 0x83:
        page = value
    elif port == 0x0B and value & 3 == 1:
        programmed.append((value, page << 16 | address, count + 1))
assert programmed == [(mode, physical, 128) for mode in (0x45, 0x55, 0x45, 0x55, 0x49)], programmed

# The only audible signal is output DMA playing the actual final ADC buffer.
# Recording itself must not leak into the DAC or the 48-kHz host mix.
playback = [(time, params) for time, cmd, params in commands if cmd == 0x14]
assert len(playback) == 1 and playback[0][1] == [127, 0]
start = playback[0][0]
speaker = [(time, cmd) for time, cmd, _ in commands if cmd in (0xD1, 0xD3)]
assert len(speaker) == 2 and [cmd for _, cmd in speaker] == [0xD1, 0xD3]
assert speaker[0][0] < start and start + 128 * 125000 <= speaker[1][0]
transitions = [(0, 0.0)]
for index, value in enumerate(saved[-128:], 1):
    transitions.append((start + index * 125000, (value - 128) / 128))
transitions.append((speaker[1][0], 0.0))
with wave.open(str(args.audio), "rb") as wav:
    assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (2, 2, 48000)
    frames = wav.getnframes()
    data = wav.readframes(frames)
samples = array.array("h", data)
if sys.byteorder != "little":
    samples.byteswap()
assert samples[::2] == samples[1::2], "Mono ADC roundtrip must be centered"
mono = samples[::2]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1_000_000_000
assert frames == report["audio"]["generatedFrames"] == report["audio"]["recordedFrames"]
pcm_hash = hashlib.sha256(data).hexdigest().upper()
assert pcm_hash == report["audio"]["dataSha256"]
decay = math.exp(-2 * math.pi * 20 / 48000)
previous, filtered, pos, segment, largest_error = 0.0, 0.0, 0, 0, 0
for frame, actual in enumerate(mono):
    end = ((frame + 1) * 1_000_000_000 + 47999) // 48000
    beginning, area = pos, 0.0
    while pos < end:
        while segment + 1 < len(transitions) and transitions[segment + 1][0] <= pos:
            segment += 1
        boundary = min(end, transitions[segment + 1][0]) if segment + 1 < len(transitions) else end
        area += (boundary - pos) * transitions[segment][1]
        pos = boundary
    value = area / (end - beginning)
    filtered = decay * (filtered + value - previous)
    previous = value
    mixed = struct.unpack("<f", struct.pack("<f", filtered * 0.5))[0]
    wanted = round(mixed * 32767)
    largest_error = max(largest_error, abs(actual - wanted))
assert largest_error <= 1, f"PCM oracle differs by {largest_error} LSB"
assert any(mono) == (args.signal == "ramp")

# Counters are checked against the independently reconstructed transfers above;
# they did not supply any expected bytes or timing to this oracle.
sb = report["soundBlaster"]
for key, value in dict(transferredSamples=128, completedBlocks=11, interruptRequests=11,
                       recordedSamples=768, convertedSamples=769, highSpeedRecordedSamples=384,
                       highSpeedSamples=0, highSpeedTransfers=2, highSpeedResets=1,
                       ignoredHighSpeedWrites=0, resetCount=3, rejectedWrites=0,
                       unsupportedCommands=0, remainingSamples=0, adcValue=128, dacValue=128).items():
    assert sb[key] == value, (key, sb[key], value)
for key in ("recording", "active", "paused", "interruptAsserted", "speakerEnabled", "highSpeed", "hostMicrophone"):
    assert sb[key] is False, key
assert report["status"]["fault"] is None
if args.irq_trace:
    interrupts = [json.loads(line) for line in args.irq_trace.read_text(encoding="utf-8-sig").splitlines()]
    vector = 8 + args.irq_line if args.irq_line < 8 else 0x70 + args.irq_line - 8
    completions = [samples[index][0] for cmd, _, _, samples in phases
                   for index in (range(63, len(samples), 64) if cmd in (0x2C, 0x98) else [127])]
    completions.append(start + 128 * 125000)
    assert len(interrupts) == len(completions) == 11
    for actual, completion in zip(interrupts, completions):
        assert actual["vector"] == vector and completion <= actual["time"] < completion + 68000, (actual, completion)
    assert sb["basePort"] == args.base and sb["irq"] == args.irq_line and sb["dmaChannel"] == 1
    if args.irq_line >= 8:
        slave_eois = [entry for entry in io if entry["port"] == 0xA0 and entry["write"] and entry["value"] == 0x20]
        assert len(slave_eois) == 11
assert report["scenario"]["complete"]
assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
assert "ADC-FAILED" not in report["status"]["debugOutput"]
assert "ADC-DOS-RETURN-OK" in report["status"]["debugOutput"]
assert re.search(r"\r\nADC-RUN-END\r\n(?:\r\n)?C:\\>$", report["status"]["debugOutput"])
print(json.dumps(dict(passed=True, signal=args.signal, dmaInputBytes=768, directAdcBytes=1,
                      highSpeedInputBytes=384, savedGuestBytes=512, dmaOutputBytes=128,
                      dmaPhysical=physical, dmaPrograms=5, checkedAdcEvents=len(adc),
                      normalPeriodNs=125000, highSpeedPeriodNs=68000,
                      independentlyReconstructedFrames=frames, largestPcm16Difference=largest_error,
                      pcmSha256=pcm_hash, guestReturned=True, hostMicrophoneOpened=False)))
