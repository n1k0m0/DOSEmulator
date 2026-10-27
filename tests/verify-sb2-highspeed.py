# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent oracle for our own DOS high-speed probe: actual I/O and known guest DMA bytes.

Checks the declared DSP-2.01 subset, including its modeled 1-us command latch and
100-us reset response. No emulator assemblies, DAC/sample trace, or DSP snapshots.
Not a comparison against an original Creative card's analog output/microcontroller.
"""
import array
import hashlib
import json
import math
import pathlib
import struct
import sys
import wave

wav_path, report_path, trace_path = map(pathlib.Path, sys.argv[1:])
report = json.loads(report_path.read_text(encoding="utf-8-sig"))
trace = [json.loads(line) for line in trace_path.read_text(encoding="utf-8-sig").splitlines()]
with wave.open(str(wav_path), "rb") as wav:
    assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (2, 2, 48000)
    frames = wav.getnframes()
    data = wav.readframes(frames)
samples = array.array("h", data)
if sys.byteorder != "little":
    samples.byteswap()
assert samples[::2] == samples[1::2]
mono = samples[::2]
assert hashlib.sha256(data).hexdigest().upper() == report["audio"]["dataSha256"]
assert frames == report["audio"]["recordedFrames"] == report["audio"]["generatedFrames"]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1_000_000_000
chars = [io for io in trace if io["port"] == 0xe9]
text = "".join(chr(io["value"]) for io in chars)


def marker(name):
    # Match the complete printed line rather than its longer success-marker prefix.
    """Finds the unique guest proof marker and rejects missing or ambiguous observations."""
    return chars[text.index(name + "\r\n") + len(name) - 1]["time"]


def phase(start, end):
    """Selects accepted DSP observations belonging to the requested guest test phase."""
    a, b = marker(start), marker(end)
    return [io for io in trace if a < io["time"] < b]


def dsp_writes(ios, values):
    """Selects actual writes to the DSP command port from the timestamped I/O trace."""
    writes = [io for io in ios if io["port"] == 0x22c and io["write"]]
    assert [io["value"] for io in writes] == values, [io["value"] for io in writes]
    return writes


dma_base = None


def check_dma(ios, mode, partial_abort=False):
    """Validates DMA address/count/mode progress against the actual accepted transfer components."""
    global dma_base
    writes = [io for io in ios if io["write"] and io["port"] in (2, 3, 0x83, 0x0a, 0x0b, 0x0c)]
    assert [io["port"] for io in writes] == [0x0a, 0x0c, 0x0b, 2, 2, 0x83, 3, 3, 0x0a] + ([0x0a] if partial_abort else [])
    assert [writes[i]["value"] for i in (0, 1, 2, 6, 7, 8)] == [5, 0, mode, 255, 31, 1]
    base = writes[3]["value"] | writes[4]["value"] << 8 | writes[5]["value"] << 16
    assert base & 8191 == 0 and (base & 65535) + 8192 <= 65536
    assert dma_base is None or dma_base == base
    dma_base = base
    if partial_abort:
        assert writes[-1]["value"] == 5
        return writes[-1]["time"]


events, streams, completions = [], [], []


def accepted(writes, index):
    """Selects only completed guest transfers from the observed device protocol."""
    return writes[index]["time"] + 1000


def stream(start, period, count, high_speed):
    # The guest fills its actual DMA buffer with 64/192, each repeated 16 times.
    """Selects the accepted timestamped DMA components for one actual DSP transfer."""
    for index in range(count):
        events.append((start + (index + 1) * period, "dac", 64 if index % 32 < 16 else 192))
    streams.append({"start": start, "period": period, "bytes": count, "highSpeed": high_speed})


def check_ack(deadline):
    """Checks that real DSP status reads acknowledge only the intended interrupt source."""
    candidates = [io for io in trace if io["port"] == 0x22e and not io["write"] and deadline <= io["time"] < deadline + 20000]
    assert candidates, f"No actual guest IRQ acknowledgement near {deadline}"
    ack = candidates[0]
    assert not ack["value"] & 0x80  # An IRQ acknowledgement, not a response-FIFO poll.
    completions.append({"deadline": deadline, "ackLatencyNs": ack["time"] - deadline})


# Single-cycle 91h: block length is set by 48h; eight raw busy-port writes must
# leave the 32-us sample period, output gate and empty reply FIFO untouched.
single = phase("SB2-HS-SINGLE-31250", "SB2-HS-SINGLE-IRQ-OK")
w = dsp_writes(single, [0x40, 224, 0x48, 255, 15, 0xd1, 0x91, 0xd0, 0xd3, 0xe1, 0x40, 131, 0x48, 0, 0, 0xd3])
check_dma(single, 0x49)
start = accepted(w, 6)
events += [(accepted(w, 5), "gate", True), (accepted(w, 15), "gate", False)]
stream(start, 32000, 4096, True)
check_ack(start + 4096 * 32000)
assert all(start < io["time"] < start + 4096 * 32000 for io in w[7:15])
busy_reads = [io for io in single if io["port"] == 0x22c and not io["write"] and start < io["time"] < start + 4096 * 32000]
assert busy_reads and all(io["value"] & 0x80 for io in busy_reads)

# Auto-init 90h: three 2,048-byte DSP blocks within one 8-KiB DMA buffer, then
# reset from the actual IRQ handler. The escape preserves DAC/gate/configuration.
auto = phase("SB2-HS-AUTO-43478", "SB2-HS-AUTO-THREE-IRQS-RESET-OK")
w = dsp_writes(auto, [0x40, 233, 0x48, 255, 7, 0xd1, 0x90, 0xd8])
check_dma(auto, 0x59)
start = accepted(w, 6)
reset = [io for io in auto if io["port"] == 0x226 and io["write"]]
assert [io["value"] for io in reset] == [1, 0] and reset[1]["time"] - reset[0]["time"] >= 3000
assert (reset[0]["time"] - start) // 23000 == 6144
events.append((accepted(w, 5), "gate", True))
stream(start, 23000, 6144, True)
for block in (1, 2, 3):
    check_ack(start + block * 2048 * 23000)

# The guest sends no new 40h here: 80h's 32-period interrupt exposes the retained
# time constant without asking the emulator to report its own value.
preserved = phase("SB2-HS-PRESERVED-TC", "SB2-HS-PRESERVED-TC-IRQ-OK")
w = dsp_writes(preserved, [0x80, 31, 0, 0xd3])
check_ack(accepted(w, 2) + 32 * 23000)
events.append((accepted(w, 3), "gate", False))

# Normal auto-init uses 8 kHz and deliberately omits 48h: the retained block must
# still contain 2,048 bytes. DAh ends that one block normally.
normal = phase("SB2-HS-NORMAL-8000", "SB2-HS-RESTORED-BLOCK-OK")
w = dsp_writes(normal, [0x40, 131, 0xd1, 0x1c, 0xda, 0xd3])
check_dma(normal, 0x59)
start = accepted(w, 3)
events += [(accepted(w, 2), "gate", True), (accepted(w, 5), "gate", False)]
stream(start, 125000, 2048, False)
check_ack(start + 2048 * 125000)

# Abort a partly transferred 65,536-byte block. Masking DMA1 ends memory reads;
# the subsequent reset must not invent a block-complete IRQ or disable the gate.
partial = phase("SB2-HS-PARTIAL-ABORT", "SB2-HS-ABORT-NO-IRQ-OK")
w = dsp_writes(partial, [0x40, 224, 0x48, 255, 255, 0xd1, 0x90, 0xda, 0xd3, 0xd3])
mask_time = check_dma(partial, 0x59, partial_abort=True)
start = accepted(w, 6)
partial_bytes = (mask_time - start) // 32000
assert 1000 < partial_bytes < 65536
events += [(accepted(w, 5), "gate", True), (accepted(w, 9), "gate", False)]
stream(start, 32000, partial_bytes, True)
reset = [io for io in partial if io["port"] == 0x226 and io["write"]]
assert [io["value"] for io in reset] == [1, 0] and mask_time < reset[0]["time"]
assert start < w[7]["time"] < w[8]["time"] < mask_time

# Observe the actual reply bytes, not just the DSP metadata. The modeled reset
# handshake is checked separately from the parameter retention asserted above.
replies = [io["value"] for io in trace if io["port"] == 0x22a and not io["write"]]
assert replies == [0xaa, 2, 1, 0xaa, 255, 0xaa, 0xaa], replies
sb = report["soundBlaster"]
assert sb["transferredSamples"] == sum(s["bytes"] for s in streams)
assert sb["highSpeedSamples"] == sum(s["bytes"] for s in streams if s["highSpeed"])
assert sb["completedBlocks"] == sb["interruptRequests"] == len(completions) == 6
assert sb["highSpeedTransfers"] == 3 and sb["highSpeedResets"] == 2 and sb["resetCount"] == 4
assert sb["ignoredHighSpeedWrites"] == 10 and sb["rejectedWrites"] == sb["unsupportedCommands"] == 0
assert not any(sb[k] for k in ("active", "paused", "highSpeed", "interruptAsserted", "speakerEnabled"))

# Reconstruct all host PCM frames, including the held last DAC byte over the
# high-speed escape and the return to normal commands. Match the declared 20-Hz
# source filter and float32/PCM16 conversion, without reading any sample trace.
transitions = [(0, 0.0)]
dac, gate = 128, False
for time, kind, value in sorted(events):
    if kind == "dac":
        dac = value
    else:
        gate = value
    level = (dac - 128) / 128 if gate else 0.0
    if level != transitions[-1][1]:
        transitions.append((time, level))
decay = math.exp(-2 * math.pi * 20 / 48000)
previous, filtered, pos, segment, largest_error = 0.0, 0.0, 0, 0, 0
for frame in range(frames):
    end = ((frame + 1) * 1_000_000_000 + 47999) // 48000
    start, area = pos, 0.0
    while pos < end:
        while segment + 1 < len(transitions) and transitions[segment + 1][0] <= pos:
            segment += 1
        boundary = min(end, transitions[segment + 1][0]) if segment + 1 < len(transitions) else end
        area += (boundary - pos) * transitions[segment][1]
        pos = boundary
    level = area / (end - start)
    filtered = decay * (filtered + level - previous)
    previous = level
    float32 = struct.unpack("<f", struct.pack("<f", filtered * .5))[0]
    expected = round(float32 * 32767)
    largest_error = max(largest_error, abs(mono[frame] - expected))
assert largest_error <= 1, f"PCM differs from independent timing/known-byte reconstruction: {largest_error}"

measured = []
for stream_index in (0, 1, 2):
    s = streams[stream_index]
    a, b = (s["start"] / 1e9 + .02, s["start"] / 1e9 + (.11 if stream_index < 2 else .21))
    part = mono[round(a * 48000):round(b * 48000)]
    rising = [i for i in range(1, len(part)) if part[i - 1] < 0 <= part[i]]
    assert len(rising) > 30
    pitch = (len(rising) - 1) * 48000 / (rising[-1] - rising[0])
    expected = 1e9 / s["period"] / 32
    assert abs(pitch - expected) < .4, (pitch, expected)
    measured.append(pitch)
assert max(abs(v) for v in mono[-4800:]) < 3
assert max(abs(v) for v in mono) < 20000
print(json.dumps({"passed": True, "frames": frames, "independentlyReconstructedFrames": frames,
                  "largestPcm16Difference": largest_error, "dmaPhysicalBase": dma_base,
                  "transferredSamples": sb["transferredSamples"], "highSpeedSamples": sb["highSpeedSamples"],
                  "partialAbortBytes": partial_bytes, "completedBlocks": len(completions), "irqAcks": completions,
                  "measuredHz": measured, "siliconComparison": False,
                  "pcmSha256": hashlib.sha256(data).hexdigest().upper()}))
