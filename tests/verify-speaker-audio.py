# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent PCM16 pitch/silence oracle for the original DOS speaker probe. Standard library only."""
import array
import hashlib
import json
import pathlib
import sys
import wave

wav_path, report_path = sys.argv[1:]
with wave.open(wav_path, "rb") as wav:
    assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (2, 2, 48000)
    frames = wav.getnframes()
    data = wav.readframes(frames)
samples = array.array("h", data)
if sys.byteorder != "little":
    samples.byteswap()
assert samples[::2] == samples[1::2], "Speaker must be centered stereo"
mono = samples[::2]
report = json.loads(pathlib.Path(report_path).read_text(encoding="utf-8-sig"))
assert report["audio"]["recordedFrames"] == frames == report["audio"]["generatedFrames"]
assert frames == report["status"]["virtualNanoseconds"] * 48000 // 1000000000
assert hashlib.sha256(data).hexdigest().upper() == report["audio"]["dataSha256"]
assert not report["audio"]["unsupportedPitProgramming"]
assert max(abs(v) for v in mono) <= 8300, "Unexpected clipping/gain"
window = 4800
pitches = []
silent_windows = 0
for start in range(0, len(mono) - window, 480):
    part = mono[start:start + window]
    if max(abs(v) for v in part) < 3:
        silent_windows += 1
        continue
    if sum(v * v for v in part) / window < 1_000_000:
        continue
    crossings = [i for i in range(1, window) if part[i - 1] < 0 <= part[i]]
    if len(crossings) > 20:
        pitch = (len(crossings) - 1) * 48000 / (crossings[-1] - crossings[0])
        if all(abs((crossings[i] - crossings[i-1]) - 48000 / pitch) < 2 for i in range(1, len(crossings))):
            pitches.append(pitch)
measured = {}
for divisor in (2712, 1356):
    expected = 1193182 / divisor
    matching = [p for p in pitches if abs(p - expected) < 1]
    assert len(matching) >= 8, f"Missing stable {expected:.3f}-Hz PIT tone"
    measured[str(divisor)] = sum(matching) / len(matching)
assert silent_windows >= 10, "Missing disabled-speaker intervals"
assert max(abs(v) for v in mono[-4800:]) < 3, "Speaker did not settle to silence on DOS return"
print(json.dumps({"passed": True, "frames": frames, "measuredHz": measured, "silentWindows": silent_windows,
                  "pcmSha256": hashlib.sha256(data).hexdigest().upper()}))
