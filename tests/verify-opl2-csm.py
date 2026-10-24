"""Independent port/timer/PCM checks for our DOS CSM-keying probe; stdlib only.

Yamaha's documented timer/key behavior is checked. Neither this oracle nor our
one-native-sample CSM pulse is a measured silicon/total-level-latch reference.
"""
import array
import cmath
import hashlib
import json
import math
import pathlib
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
mono = [s / 32767 for s in samples[::2]]
assert hashlib.sha256(data).hexdigest().upper() == report["audio"]["dataSha256"]
assert frames == report["audio"]["recordedFrames"] == report["audio"]["generatedFrames"]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1_000_000_000
assert report["opl"]["generatedSamples"] == report["status"]["virtualNanoseconds"] * 3579545 // (72 * 1_000_000_000)

chars = [io for io in trace if io["port"] == 0xe9]
text = "".join(chr(io["value"]) for io in chars)


def marker(name):
    return chars[text.index(name) + len(name) - 1]["time"] / 1e9


def extract(start, end):
    return mono[round(start * 48000):round(end * 48000)]


def rms(part):
    return math.sqrt(sum(s * s for s in part) / len(part))


def amplitude(part, hz):
    step = cmath.exp(-2j * math.pi * hz / 48000)
    phase, total = 1 + 0j, 0j
    for sample in part:
        total += sample * phase
        phase *= step
    return 2 * abs(total) / len(part)


fm_io = [io for io in trace if io["port"] in (0x388, 0x389, 0x228, 0x229)]
register, address_time, data_time = None, None, None
writes = []
for i, io in enumerate(fm_io):
    assert io["width"] == 1
    if not io["write"]:
        continue
    count = 6 if io["port"] % 2 == 0 else 35
    reads = fm_io[i + 1:i + count + 1]
    assert len(reads) == count and all(not r["write"] and r["port"] % 2 == 0 for r in reads)
    assert reads[-1]["time"] - io["time"] >= count * 750
    if io["port"] % 2 == 0:
        assert data_time is None or io["time"] - data_time >= 23467
        register, address_time = io["value"], io["time"]
    else:
        assert register is not None and io["time"] - address_time >= 3353
        data_time = io["time"]
        writes.append((data_time, register, io["value"]))
assert len(writes) == report["opl"]["registerWrites"]
timing = report["opl"]["ioTiming"]
assert timing["accesses"] == timing["byteCycles"] == len(fm_io)
assert timing["elapsedNanoseconds"] == 750 * len(fm_io)

# Reconstruct both timer countdowns from actual port commits and the manufacturer
# clock formula. A preset write affects only the following reload; flag reset has
# no countdown effect. No emulator assembly, envelope/DAC trace or reported pulse
# counter is used to compute the expected pulses.
master = 3579545
presets, running, deadlines, overflows = [0, 0], [False, False], [None, None], [0, 0]
csm, pulses = False, []
for time, reg, value in writes + [(report["status"]["virtualNanoseconds"], -1, 0)]:
    for timer in range(2):
        unit = 288 if timer == 0 else 1152
        while running[timer] and (deadlines[timer] * 1_000_000_000 + master - 1) // master <= time:
            overflows[timer] += 1
            if timer == 0 and csm:
                pulses.append((deadlines[timer] * 1_000_000_000 + master - 1) // master)
            deadlines[timer] += (256 - presets[timer]) * unit
    if reg in (2, 3):
        presets[reg - 2] = value
    elif reg == 8:
        csm = bool(value & 0x80)
    elif reg == 4 and not value & 0x80:
        for timer in range(2):
            start = bool(value & (1 << timer))
            if start and not running[timer]:
                deadlines[timer] = (time * master + 999_999_999) // 1_000_000_000 + (256 - presets[timer]) * (288 if timer == 0 else 1152)
            running[timer] = start
assert overflows == [report["opl"]["timer1Overflows"], report["opl"]["timer2Overflows"]]
assert len(pulses) == report["opl"]["csmKeyPulses"] > 100
assert report["opl"]["keyOnCount"] == len(pulses) * 18 + 2
assert all(value & 0x20 == 0 for time, reg, value in writes if 0xb0 <= reg <= 0xb8 and time < marker("OPL2-CSM-STOPPED") * 1e9)
assert not report["opl"]["unsupportedProgramming"] and not report["opl"]["csmEnabled"] and not report["opl"]["csmPulseActive"]
assert report["opl"]["csmKeying"] and not report["opl"]["csm"] and not report["opl"]["siliconExact"]

t2 = marker("OPL2-CSM-TIMER2-SILENT")
assert max(map(abs, extract(t2 + .03, t2 + .17))) == 0, "Timer 2 incorrectly keyed CSM"
reset = marker("OPL2-CSM-RESET-FLAG")
fast_start = marker("OPL2-CSM-FAST")
assert any(reset * 1e9 < p < fast_start * 1e9 for p in pulses), "Flag reset stopped CSM"
slow_start = marker("OPL2-CSM-SLOW")
periods = {}
for name, start, clocks in [("slow", slow_start, 128 * 288), ("fast", fast_start, 64 * 288)]:
    part = extract(start + .04, start + .31)
    expected = master / clocks
    lag = round(48000 / expected)
    correlations = {}
    for offset in range(lag - 8, lag + 9):
        a, b = part[:-offset], part[offset:]
        correlations[offset] = sum(x*y for x, y in zip(a, b)) / math.sqrt(sum(x*x for x in a) * sum(y*y for y in b))
    best = max(correlations, key=correlations.get)
    assert abs(best - lag) <= 1 and correlations[best] > .94, f"Wrong {name} keying period: {best}, {correlations[best]}"
    assert rms(part) > .003 and amplitude(part, expected) > .001
    periods[name] = {"expectedHz": expected, "bestLagFrames": best, "correlation": correlations[best], "rms": rms(part)}
stopped = marker("OPL2-CSM-STOPPED")
assert max(map(abs, extract(stopped + .15, stopped + .19))) < 3 / 32767
melodic_start = marker("OPL2-CSM-MELODIC-RETURN")
melodic = extract(melodic_start + .03, melodic_start + .27)
expected_melodic = master / 72 * 580 * 16 / 2**20
assert .11 < amplitude(melodic, expected_melodic) < .14
assert max(map(abs, mono[-4800:])) < 3 / 32767
assert max(map(abs, mono)) < .4
print(json.dumps({"passed": True, "frames": frames, "nativeSamples": report["opl"]["generatedSamples"],
                  "timer1Overflows": overflows[0], "timer2Overflows": overflows[1], "csmPulses": len(pulses),
                  "operatorKeyOns": report["opl"]["keyOnCount"], "periods": periods, "fmPortAccesses": len(fm_io),
                  "melodicReturnHz": expected_melodic, "siliconComparison": False, "fullCsmAccuracy": False,
                  "pcmSha256": hashlib.sha256(data).hexdigest().upper()}))
