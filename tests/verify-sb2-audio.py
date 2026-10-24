"""Independent oracle for our DOS probe: known DMA bytes, actual IN/OUT timeline, WAV and spectrum.

Uses no emulator assemblies, DSP snapshots, DAC trace or third-party Python packages.
"""
import array
import cmath
import hashlib
import json
import math
import pathlib
import struct
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
assert samples[::2] == samples[1::2], "Mono DSP must be centered in the stereo host mix"
mono = samples[::2]
assert frames == report["audio"]["recordedFrames"] == report["audio"]["generatedFrames"]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1_000_000_000
assert hashlib.sha256(data).hexdigest().upper() == report["audio"]["dataSha256"]
assert report["soundBlaster"]["rejectedWrites"] == report["soundBlaster"]["unsupportedCommands"] == 0
assert max(abs(v) for v in mono) < 25000, "Unexpected clipping in mixed audio"

# Decode the actual guest's command writes. The modeled input latch latency is 1 us.
lengths = {0x10: 1, 0x14: 2, 0x40: 1, 0x48: 2, 0x1C: 0, 0xD0: 0,
           0xD1: 0, 0xD3: 0, 0xD4: 0, 0xD8: 0, 0xDA: 0, 0xE1: 0}
commands = []
command, parameters = None, []
for io in trace:
    if io["port"] != 0x22C or not io.get("write", True):
        continue
    assert io["width"] == 1
    if command is None:
        command = io["value"]
        assert command in lengths, f"Unexpected DSP command {command:02X}"
        parameters = []
    else:
        parameters.append(io["value"])
    if len(parameters) == lengths[command]:
        commands.append((io["time"] + 1000, command, parameters))
        command = None
assert command is None

# Independently reconstruct the known square-wave buffer and device sample clock.
# DSP blocks and DMA buffers have different sizes in the auto-init phases.
transitions = [(0, 0.0)]
period, block, remaining, index = 256000, 65536, 0, 0
enabled, auto, exit_auto = False, False, False
dac, next_sample, paused_delay = 128, None, None
runs = []


def level(time):
    value = (dac - 128) / 128 if enabled else 0.0
    if value != transitions[-1][1]:
        transitions.append((time, value))


for time, cmd, params in commands:
    while next_sample is not None and next_sample <= time:
        tick = next_sample
        dac = 64 if index % 32 < 16 else 192
        index += 1
        level(tick)
        remaining -= 1
        if remaining == 0 and auto and not exit_auto:
            remaining = block
        next_sample = tick + period if remaining else None
    if cmd == 0x10:
        dac = params[0]
        level(time)
    elif cmd == 0x40:
        period = (256 - params[0]) * 1000
    elif cmd == 0x48:
        block = (params[0] | params[1] << 8) + 1
    elif cmd in (0x14, 0x1C):
        auto, exit_auto, index = cmd == 0x1C, False, 0
        remaining = block if auto else (params[0] | params[1] << 8) + 1
        next_sample = time + period
        runs.append((time, period))
    elif cmd == 0xDA:
        exit_auto = True
    elif cmd == 0xD0 and next_sample is not None:
        paused_delay, next_sample = next_sample - time, None
    elif cmd == 0xD4 and paused_delay is not None:
        next_sample, paused_delay = time + paused_delay, None
    elif cmd in (0xD1, 0xD3):
        enabled = cmd == 0xD1
        level(time)
assert len(runs) == 4 and [p for _, p in runs] == [125000, 125000, 91000, 125000]

# Compare every PCM-only output frame, including DMA pause/resume, block exit and silent gaps.
# Stop before the guest turns on PIT2, whose independent frequency is checked below.
speaker_on = next(io["time"] for io in trace if io["port"] == 0x61 and io["value"] & 3 == 3)
verified_frames = speaker_on * 48000 // 1_000_000_000
decay = math.exp(-2 * math.pi * 20 / 48000)
previous, filtered, pos, segment = 0.0, 0.0, 0, 0
largest_error = 0
for frame in range(verified_frames):
    end = ((frame + 1) * 1_000_000_000 + 47999) // 48000
    start, area = pos, 0.0
    while pos < end:
        while segment + 1 < len(transitions) and transitions[segment + 1][0] <= pos:
            segment += 1
        boundary = min(end, transitions[segment + 1][0]) if segment + 1 < len(transitions) else end
        area += (boundary - pos) * transitions[segment][1]
        pos = boundary
    value = area / (end - start)
    filtered = decay * (filtered + value - previous)
    previous = value
    float32 = struct.unpack("<f", struct.pack("<f", filtered * 0.5))[0]
    expected = round(float32 * 32767)
    largest_error = max(largest_error, abs(mono[frame] - expected))
assert largest_error <= 1, f"PCM differs from independent byte/timing oracle by {largest_error}"


def extract(start, end):
    return mono[round(start * 48000):round(end * 48000)]


def pitch(part):
    rising = [i for i in range(1, len(part)) if part[i - 1] < 0 <= part[i]]
    assert len(rising) >= 20
    return (len(rising) - 1) * 48000 / (rising[-1] - rising[0])


measured = []
for i, (start, ns) in enumerate(runs[:3]):
    a, b = (0.02, 0.12) if i == 0 else (0.1, 0.6)
    hz = pitch(extract(start / 1e9 + a, start / 1e9 + b))
    expected = 1e9 / ns / 32
    assert abs(hz - expected) < 0.2, (hz, expected)
    measured.append(hz)
mix_start = runs[3][0] / 1e9
mix = extract(mix_start + 0.2, mix_start + 0.7)


def amplitude(hz):
    step = cmath.exp(-2j * math.pi * hz / 48000)
    phase, total = 1 + 0j, 0j
    for sample in mix:
        total += sample / 32767 * phase
        phase *= step
    return 2 * abs(total) / len(mix)


components = {"pcm250": amplitude(250), "speaker440": amplitude(1193182 / 2712)}
assert components["pcm250"] > 0.28 and components["speaker440"] > 0.13, components
assert max(abs(v) for v in mono[-4800:]) < 3, "Audio did not settle after clean DOS return"
print(json.dumps({"passed": True, "frames": frames, "independentlyReconstructedFrames": verified_frames,
                  "largestPcm16Difference": largest_error, "measuredHz": measured, "mixedComponents": components,
                  "pcmSha256": hashlib.sha256(data).hexdigest().upper()}))
