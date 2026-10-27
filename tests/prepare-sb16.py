# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Own SB16 PCM cases, derived from the documented DSP4 command bit fields."""
import json
import sys
from pathlib import Path


def cases():
    # bits, stereo, signed, input, auto, rate, samples/block, DMA samples/buffer, blocks, fallback
    """Builds deterministic sb16 vectors and original guest data tables; no original media are modified."""
    rows = [
        (8, 0, 0, 0, 0, 8000, 16, 16, 1, 0),
        (8, 1, 1, 0, 0, 8000, 16, 16, 1, 0),
        (16, 0, 1, 0, 0, 8000, 16, 16, 1, 0),
        (16, 1, 0, 0, 0, 8000, 16, 16, 1, 0),
        (8, 1, 0, 1, 0, 8000, 16, 16, 1, 0),
        (16, 1, 1, 1, 0, 8000, 16, 16, 1, 0),
        (8, 0, 0, 0, 1, 8000, 16, 32, 3, 0),
        (16, 1, 1, 0, 1, 11025, 16, 32, 3, 0),
        (16, 1, 1, 0, 0, 11025, 32, 32, 1, 0),
        (8, 0, 0, 0, 0, 44100, 32, 32, 1, 0),
        (16, 0, 1, 0, 0, 22050, 32, 32, 1, 0),
        (16, 0, 0, 1, 0, 5000, 16, 16, 1, 0),
        (16, 0, 1, 0, 0, 8000, 16, 16, 1, 1),
    ]
    result = []
    for i, (bits, stereo, signed, recording, auto, rate, samples, buffer, blocks, fallback) in enumerate(rows):
        result.append(dict(id=i, bits=bits, stereo=bool(stereo), signed=bool(signed), recording=bool(recording),
                           auto=bool(auto), rate=rate, samples=samples, bufferSamples=buffer, blocks=blocks, fallback=bool(fallback),
                           command=(0xB0 if bits == 16 else 0xC0) | (8 if recording else 0) | (4 if auto else 0) | (2 if i % 2 else 0),
                           mode=32 * stereo + 16 * signed, asymmetricGain=i == 8))
    return result


def main(root):
    """Runs the explicit sb16 preparation/check command and reports its reproducible evidence."""
    root = Path(root)
    rows = cases()
    (root / "vectors.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    text = [f"%define CASE_COUNT {len(rows)}", "%define RECORD_WORDS 80", "case_table:"]
    for row in rows:
        text.append("dw " + ",".join(str(int(row[k])) for k in ("command", "mode", "samples", "bufferSamples", "rate", "blocks", "fallback", "asymmetricGain")))
    (root / "sb16-vectors.inc").write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + ("\n".join(text) + "\n"), encoding="ascii")
    print(json.dumps(dict(cases=len(rows), onlyOwnProgram=True)))


if __name__ == "__main__":
    main(sys.argv[1])
