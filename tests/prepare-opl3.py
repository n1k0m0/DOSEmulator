# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Own YMF262 functional cases. No external instruments or music data."""
import json
from pathlib import Path
import sys


def main(folder):
    """Runs the explicit opl3 preparation/check command and reports its reproducible evidence."""
    folder = Path(folder)
    rows = []

    def add(kind, channel=0, pair=0, algorithm=0, solo=1, routing=3, wave=0, drum=0):
        """Adds one explicitly parameterized opl3 case to the deterministic test matrix."""
        rows.append(dict(id=len(rows), kind=kind, channel=channel, pair=pair, algorithm=algorithm,
                         solo=solo, routing=routing, wave=wave, fnum=580, block=4, drum=drum,
                         ticks=3, ports=len(rows) % 3))

    for ch in range(18):
        add(0, channel=ch, routing=1 << (ch % 4))
    for mask in range(16):
        add(1, channel=10, routing=mask)
    for pair in range(6):
        for algorithm, carriers in enumerate(((3,), (1, 3), (0, 3), (0, 2, 3))):
            add(2, channel=pair if pair < 3 else pair + 6, pair=pair, algorithm=algorithm,
                solo=carriers[pair % len(carriers)], routing=1 << (pair % 4))
    for waveform in range(8):
        add(3, channel=9, wave=waveform)
    for drum in (16, 1, 8, 4, 2):
        add(4, routing=3, drum=drum)
    add(5, wave=2)  # OPL2 operation: no test-bit waveform gate, forced A/B.
    add(6, routing=1)  # Independent A/B frequencies.
    add(7)  # Active FM while DSP is reset, queried and used as a direct DAC.
    assert len(rows) == 74
    (folder / "vectors.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    fields = ("id", "kind", "channel", "pair", "algorithm", "solo", "routing", "wave", "fnum", "block", "drum", "ticks", "ports")
    text = [f"%define CASE_COUNT {len(rows)}", "%define ROW_BYTES 26", "case_table:"]
    text += ["dw " + ",".join(str(row[field]) for field in fields) for row in rows]
    (folder / "opl3-cases.inc").write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + ("\n".join(text) + "\n"), encoding="ascii")
    print(json.dumps(dict(cases=len(rows), onlyOwnData=True)))


if __name__ == "__main__":
    main(sys.argv[1])
