# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent register/timing/spectral oracle for the original DOS OPL2 probe (stdlib only).

Checks observable melodic behavior; this is not a comparison against a recorded Yamaha chip.
"""
import array
import cmath
import hashlib
import json
import math
import pathlib
import sys
import wave

wav_path, report_path, trace_path = sys.argv[1:]
report = json.loads(pathlib.Path(report_path).read_text(encoding="utf-8-sig"))
trace = [json.loads(line) for line in pathlib.Path(trace_path).read_text(encoding="utf-8-sig").splitlines()]
with wave.open(wav_path, "rb") as wav:
    assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (2, 2, 48000)
    frames = wav.getnframes()
    data = wav.readframes(frames)
samples = array.array("h", data)
if sys.byteorder != "little":
    samples.byteswap()
assert samples[::2] == samples[1::2]
mono = [x / 32767 for x in samples[::2]]
assert frames == report["audio"]["recordedFrames"] == report["audio"]["generatedFrames"]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1_000_000_000
assert report["opl"]["generatedSamples"] == report["status"]["virtualNanoseconds"] * 3579545 // (72 * 1_000_000_000)
assert report["opl"]["rejectedWrites"] == 0 and not report["opl"]["unsupportedProgramming"]
assert hashlib.sha256(data).hexdigest().upper() == report["audio"]["dataSha256"]

chars = [io for io in trace if io["port"] == 0xE9]
text = "".join(chr(io["value"]) for io in chars)


def marker_time(marker):
    """Locates the virtual time of the requested actual guest debug-port marker."""
    index = text.index(marker) + len(marker) - 1
    return chars[index]["time"] / 1e9


def extract(start, end):
    """Selects the requested audio interval from the independent capture and marker times."""
    return mono[round(start * 48000):round(end * 48000)]


def rms(part):
    """Measures RMS over the explicit captured sample window, independently from the synthesizer."""
    return math.sqrt(sum(x*x for x in part) / len(part))


def amplitude(part, hz):
    """Measures the selected signal's sinusoidal component independently from the emulated oscillator state."""
    step = cmath.exp(-2j * math.pi * hz / 48000)
    phase, total = 1 + 0j, 0j
    for sample in part:
        total += sample * phase
        phase *= step
    return 2 * abs(total) / len(part)


def pitch(part):
    """Estimates signal frequency from the captured waveform independently from the chip's phase accumulator."""
    rising = [i for i in range(1, len(part)) if part[i - 1] < 0 <= part[i]]
    assert len(rising) > 30
    return (len(rising) - 1) * 48000 / (rising[-1] - rising[0])


# Verify the real guest respected both Yamaha write intervals through either mapped port pair.
last_data, last_address, register = None, None, None
writes = []
for io in trace:
    if io["port"] not in (0x388, 0x389, 0x228, 0x229) or not io.get("write", True):
        continue
    assert io["width"] == 1
    if io["port"] % 2 == 0:
        if last_data is not None:
            assert io["time"] - last_data >= 23467
        register, last_address = io["value"], io["time"]
    else:
        assert register is not None and io["time"] - last_address >= 3353
        writes.append((io["time"] / 1e9, register, io["value"]))
        last_data = io["time"]
assert len(writes) == report["opl"]["registerWrites"] == 137

# The guest's recovery routine now uses actual IN instructions, not CPU-counted delays.
# Six reads follow every address, 35 follow every data write. Extra reads are detection.
fm_io = [io for io in trace if io["port"] in (0x388, 0x389, 0x228, 0x229)]
for i, io in enumerate(fm_io):
    if not io["write"]:
        continue
    count = 6 if io["port"] % 2 == 0 else 35
    reads = fm_io[i + 1:i + 1 + count]
    assert len(reads) == count and all(not r["write"] and r["port"] % 2 == 0 for r in reads)
    assert reads[-1]["time"] - io["time"] >= count * 750
timing = report["opl"]["ioTiming"]
assert timing["frequency"] == 8_000_000 and timing["clocksPerByte"] == 6
assert timing["accesses"] == timing["byteCycles"] == len(fm_io)
assert timing["elapsedNanoseconds"] == len(fm_io) * 750

expected = 3579545 / 72 * 580 * 16 / 2**20
start = marker_time("OPL2-SINE-440")
sine = extract(start + 0.03, start + 0.28)
measured_sine = pitch(sine)
assert abs(measured_sine - expected) < 0.3
assert 0.11 < amplitude(sine, expected) < 0.14
assert amplitude(sine, expected * 3) < 0.003
start = marker_time("OPL2-RECTIFIED-880")
rectified = extract(start + 0.05, start + 0.27)
measured_rectified = pitch(rectified)
assert abs(measured_rectified - expected * 2) < 0.6
assert amplitude(rectified, expected * 2) > 0.045
assert amplitude(rectified, expected) < 0.003
start = marker_time("OPL2-ENVELOPE")
early = rms(extract(start + 0.003, start + 0.020))
sustained = rms(extract(start + 0.22, start + 0.38))
assert early < sustained * 0.5 and abs(sustained / rms(sine) - 10**(-12/20)) < 0.02
stops = [time for time, reg, value in writes if reg == 0xB0 and value == 0x12]
assert rms(extract(stops[2] + 0.15, stops[2] + 0.18)) < 0.00001, "Key-off did not release the envelope"
start = marker_time("OPL2-FM-FEEDBACK")
fm = extract(start + 0.03, start + 0.28)
fm_harmonics = sum(amplitude(fm, expected * harmonic)**2 for harmonic in range(2, 16))**0.5
# Strong self-feedback can spread energy outside harmonic bins. Test the residual after removing
# the carrier as well, rather than incorrectly requiring feedback noise to be a periodic tone.
fm_residual = math.sqrt(max(0, rms(fm)**2 - amplitude(fm, expected)**2 / 2))
assert fm_residual > 0.04 and rms(fm) > 0.04, "Missing actual two-operator modulation/feedback"
combo = report["soundBlaster"] is not None
start = marker_time("OPL2-MIX-250-440-660" if combo else "OPL2-MIX-440-660")
mix = extract(start + 0.03, start + 0.28)
components = {"pcm250": amplitude(mix, 250), "speaker440": amplitude(mix, 1193182 / 2712), "fm660": amplitude(mix, expected * 1.5)}
assert components["speaker440"] > 0.13 and components["fm660"] > 0.11
assert components["pcm250"] > 0.28 if combo else components["pcm250"] < 0.015
assert max(abs(v) for v in mono[-4800:]) < 3 / 32767
assert max(abs(v) for v in mono) < 0.8
print(json.dumps({"passed": True, "frames": frames, "nativeSamples": report["opl"]["generatedSamples"],
                  "sineHz": measured_sine, "rectifiedHz": measured_rectified, "envelopeEarlyRms": early,
                  "sustainRmsRatio": sustained / rms(sine), "fmHarmonics": fm_harmonics, "fmResidualRms": fm_residual, "mixedComponents": components,
                  "registerWrites": len(writes), "fmPortAccesses": len(fm_io), "classicRecoveryReads": True,
                  "siliconComparison": False, "pcmSha256": hashlib.sha256(data).hexdigest().upper()}))
