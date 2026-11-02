# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Set a freestanding x86 guest image's PE minimum version to Windows 9x (4.0)."""

import argparse
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("image", type=Path)
args = parser.parse_args()
data = bytearray(args.image.read_bytes())
if data[:2] != b"MZ":
    raise ValueError("Not a DOS/PE image")
pe = struct.unpack_from("<I", data, 0x3C)[0]
if data[pe : pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x14C:
    raise ValueError("Expected an x86 PE image")
optional = pe + 24
if struct.unpack_from("<H", data, optional)[0] != 0x10B:
    raise ValueError("Expected PE32")
for offset in (40, 48):
    struct.pack_into("<HH", data, optional + offset, 4, 0)
# The guest image is built without a C runtime; avoid flags introduced after Win9x.
struct.pack_into("<H", data, optional + 70, 0)
args.image.write_bytes(data)
