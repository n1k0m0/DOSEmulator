"""Generate the host's ordinal/name table from the checked Glide 2 ABI list."""

import argparse
import csv
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("exports", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
rows = list(csv.DictReader(args.exports.open(newline="", encoding="ascii")))
if len(rows) < 100:
    raise ValueError("Incomplete Glide export table")
names = ["null"] * (max(int(row["ordinal"]) for row in rows) + 1)
words = ["0"] * len(names)
for row in rows:
    ordinal = int(row["ordinal"])
    decorated = row["name"]
    names[ordinal] = f'"{decorated[1:].split("@")[0]}"'
    words[ordinal] = str(int(row["stack_bytes"]) // 4)
source = [
    "// SPDX-License-Identifier: Apache-2.0",
    "// Generated from the installed Glide 2 export ABI.",
    "#nullable enable",
    "namespace DOSEmulator.Whpx;",
    "internal static class Glide2Exports",
    "{",
    "    internal static readonly string?[] Names = [",
    *[f"        {name}, // {i}" for i, name in enumerate(names)],
    "    ];",
    "    internal static readonly byte[] ArgumentWords = [",
    *[f"        {word}, // {i}" for i, word in enumerate(words)],
    "    ];",
    "}",
]
args.output.write_text("\n".join(source) + "\n", encoding="ascii")
