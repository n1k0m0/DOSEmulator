# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Own DOS fixture vectors. No OS files or emulator implementation are read."""
import json
import sys
from pathlib import Path

POSITIONS = list(range(8)) + list(range(0x7FF8, 0x8008)) + list(range(0xFFF8, 0x10000))


def vectors(managed=False):
    """Builds deterministic dma16 vectors and original guest data tables; no original media are modified."""
    rows = []
    # channel, mode (without channel select), start word, encoded count, source seed,
    # device attempts, initially masked/disabled, page-bit-zero, resume, direction.
    cases = [
        ("read-64k", 0x48, 0x7FFE, 3, 5, 0, 0, 0, 1),
        ("write-64k", 0x44, 0x7FFE, 3, 5, 0, 1, 0, 2),
        ("read-128k-wrap", 0x48, 0xFFFE, 3, 4, 0, 1, 0, 1),
        ("write-128k-wrap", 0x44, 0xFFFE, 3, 4, 0, 0, 0, 2),
        ("read-decrement-wrap", 0x68, 1, 3, 5, 0, 0, 0, 1),
        ("write-decrement-wrap", 0x64, 1, 3, 5, 0, 1, 0, 2),
        ("read-autoinit", 0x58, 0xFFFE, 2, 8, 0, 1, 0, 1),
        ("write-autoinit", 0x54, 0xFFFE, 2, 8, 0, 0, 0, 2),
        ("read-mask-resume", 0x48, 0, 1, 3, 1, 0, 1, 1),
        ("write-disable-resume", 0x44, 0, 1, 3, 2, 1, 1, 2),
        ("verify-is-not-data", 0x40, 0, 1, 3, 0, 0, 0, 1),
        ("wrong-direction", 0x48, 0, 1, 3, 0, 0, 0, 2),
        ("illegal-operation", 0x4C, 0, 1, 3, 0, 0, 0, 1),
        ("cascade-is-not-data", 0xC4, 0, 1, 3, 0, 0, 0, 2),
        ("demand-word-write", 0x04, 0x7FFE, 3, 4, 0, 0, 0, 2),
        ("block-word-read", 0x88, 0xFFFE, 3, 4, 0, 1, 0, 1),
    ]
    for channel in (5, 6, 7):
        for name, mode, address, count, attempts, controls, odd, resume, direction in cases:
            # The original monitor rejects controller-disable, verify, illegal and cascade programming.
            # It also moves a decrement buffer's programmed start to its far end, despite 810B.
            # Test raw 8237 decrement semantics without that monitor, and retain strict oracles here.
            if managed and (controls & 2 or mode & 0x20 or mode in (0x40, 0x4C, 0xC4)):
                continue
            rows.append(dict(name=f"dma{channel}-{name}", channel=channel, mode=mode,
                             address=address, count=count, seed=0x1234 + 17 * len(rows),
                             attempts=attempts, controls=controls, odd=odd, resume=resume, direction=direction))
    return rows


def main(root, managed=False):
    """Runs the explicit dma16 preparation/check command and reports its reproducible evidence."""
    root = Path(root)
    data = vectors(managed)
    (root / "vectors.json").write_text(json.dumps(dict(managed=managed, positions=POSITIONS, cases=data), indent=2), encoding="utf-8")
    text = [f"%define CASE_COUNT {len(data)}", f"%define POSITION_COUNT {len(POSITIONS)}",
            "%define RECORD_WORDS (11+POSITION_COUNT)", "positions: dw " + ",".join(map(str, POSITIONS)), "case_table:"]
    for row in data:
        text.append("dw " + ",".join(str(row[k]) for k in ("channel", "mode", "address", "count", "seed", "attempts", "controls", "odd", "resume", "direction")))
    (root / "dma16-vectors.inc").write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + ("\n".join(text) + "\n"), encoding="ascii")
    print(json.dumps(dict(cases=len(data), positions=len(POSITIONS))))


if __name__ == "__main__":
    main(sys.argv[1], "--managed" in sys.argv[2:])
