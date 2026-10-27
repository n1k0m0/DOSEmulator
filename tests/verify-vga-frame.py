# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent pixel oracle for tests/guests/vga-test.asm. Python standard library only."""
import hashlib
import json
import pathlib
import sys

mode, ppm_file, report_file = sys.argv[1:]
width, height = (320, 200) if mode == "13" else (640, 480)
raw = pathlib.Path(ppm_file).read_bytes()
header = f"P6\n{width} {height}\n255\n".encode("ascii")
assert raw.startswith(header), "Unexpected PPM dimensions/format"
pixels = raw[len(header):]
assert len(pixels) == width * height * 3, "Truncated frame"
text_palette = [
    (0, 0, 0), (0, 0, 170), (0, 170, 0), (0, 170, 170),
    (170, 0, 0), (170, 0, 170), (170, 85, 0), (170, 170, 170),
    (85, 85, 85), (85, 85, 255), (85, 255, 85), (85, 255, 255),
    (255, 85, 85), (255, 85, 255), (255, 255, 85), (255, 255, 255),
]
bgra = bytearray()
for y in range(height):
    for x in range(width):
        if mode == "13":
            color = (x + y) & 255
            components = (color & 63, color >> 2, (63 - color) & 63)
            rgb = tuple((v << 2) | (v >> 4) for v in components)
            if color == 255 or (x, y) == (319, 199):
                rgb = (255, 0, 255)  # BIOS DAC set/read and pixel write/read probe
        else:
            rgb = text_palette[x // 40]
            if (x, y) == (639, 479):
                rgb = text_palette[4]  # BIOS write 3, XOR 7, read 4
        offset = (y * width + x) * 3
        assert pixels[offset:offset + 3] == bytes(rgb), f"Wrong pixel at {x},{y}: {pixels[offset:offset+3].hex()} != {rgb}"
        bgra.extend((rgb[2], rgb[1], rgb[0], 255))
report = json.loads(pathlib.Path(report_file).read_text(encoding="utf-8-sig"))
digest = hashlib.sha256(bgra).hexdigest().upper()
assert report["frameHash"] == digest, "Frame/report mismatch"
assert (report["width"], report["height"]) == (width, height)
print(json.dumps({"passed": True, "mode": mode, "pixelsVerified": width * height, "frameHash": digest}))
