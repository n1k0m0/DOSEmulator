# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent stdlib oracle: private FAT16/CPI bytes -> every pixel of our font sheet.
No emulator imports, exported ROM/font assets or reference images. CPI structure:
Microsoft MS-DOS Programmer's Reference (1991), chapter 6.6, pp. 93ff.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location("fat_oracle", Path(__file__).with_name("verify-msdos-install.py"))
fat_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fat_oracle)


def installation(base, cow):
    """Locates and validates the private DOS installation used to obtain the original font resource."""
    disk, journal = fat_oracle.materialize(Path(base), Path(cow))
    partition = struct.unpack_from("<I", disk, 454)[0] * 512
    files, allocation = fat_oracle.Fat(disk, partition).files()
    return files, journal, allocation


def cpi_fonts(data, codepage):
    """Extracts the requested font from the locally supplied DOS CPI structure after validating its offsets."""
    assert data[:8] == b"\xffFONT   "
    assert struct.unpack_from("<HB", data, 16) == (1, 1)  # one absolute offset pointer
    offset = struct.unpack_from("<I", data, 19)[0]
    entries = struct.unpack_from("<H", data, offset)[0]
    assert 1 <= entries <= 16
    offset += 2
    visited = set()
    for _ in range(entries):
        assert offset not in visited and 0 <= offset <= len(data) - 28
        visited.add(offset)
        assert struct.unpack_from("<H", data, offset)[0] == 28
        following = struct.unpack_from("<I", data, offset + 2)[0]
        device, = struct.unpack_from("<H", data, offset + 6)
        number, = struct.unpack_from("<H", data, offset + 16)
        if number == codepage and device == 1:
            font_offset, = struct.unpack_from("<I", data, offset + 24)
            version, count, length = struct.unpack_from("<HHH", data, font_offset)
            assert version == 1 and 1 <= count <= 3 and font_offset + 6 + length <= len(data)
            font_offset += 6
            fonts = {}
            for _ in range(count):
                height, width, _, _, chars = struct.unpack_from("<BBBBH", data, font_offset)
                assert width == 8 and height in (8, 14, 16) and chars == 256 and height not in fonts
                font_offset += 6
                end = font_offset + height * chars
                assert end <= len(data)
                fonts[height] = data[font_offset:end]
                font_offset = end
            return fonts
        offset = following
    raise AssertionError(f"No screen font for codepage {codepage}")


def verify_frame(base, cow, frame, points, rows, report):
    """Reconstructs and compares the complete text/font output from original glyph bytes and palette values."""
    files, _, _ = installation(base, cow)
    cpi = files["DOS/EGA.CPI"]
    font = cpi_fonts(cpi, 850)[points]
    image = Path(frame).read_bytes()
    magic, dimensions, maximum, pixels = image.split(b"\n", 3)
    assert magic == b"P6" and maximum == b"255"
    width, height = map(int, dimensions.split())
    assert (width, height) == (640, rows * points) and len(pixels) == width * height * 3
    screen = [bytearray(b" " * 80) for _ in range(rows)]

    def text(row, column, value):
        """Writes expected character/attribute cells into the independent frame oracle."""
        screen[row][column:column + len(value)] = value

    text(0, 2, b"VGA FONT / ORIGINAL DOS DISPLAY.SYS + MODE")
    text(1, 2, b"256 GLYPHS / CP850 / ESC OR ENTER RETURNS TO DOS")
    for character in range(256):
        screen[3 + character // 16][2 + character % 16 * 4] = character
    text(20, 2, b"CP850: \x84 \x94 \x81 \x8e \x99 \x9a \xe1")
    text(21, 2, b"\xda" + b"\xc4" * 40 + b"\xbf")
    text(22, 2, b"\xb3DOS-Zeichensatz: \x84\x94\x81 / \x8e\x99\x9a / \xe1")
    screen[22][43] = 0xb3
    text(23, 2, b"\xc0" + b"\xc4" * 40 + b"\xd9")
    text(24, 2, b"Guest font source: C:\\DOS\\EGA.CPI")
    expected = bytearray(len(pixels))
    for y in range(height):
        for x in range(width):
            glyph = screen[y // points][x // 8]
            value = 170 if font[glyph * points + y % points] & (128 >> (x % 8)) else 0
            offset = (y * width + x) * 3
            expected[offset:offset + 3] = bytes([value]) * 3
    if expected != pixels:
        first = next(i for i, (a, b) in enumerate(zip(expected, pixels)) if a != b)
        raise AssertionError(f"Font pixel mismatch at x={first // 3 % width}, y={first // 3 // width}; expected {expected[first]}, actual {pixels[first]}")
    result = json.loads(Path(report).read_text(encoding="utf-8-sig"))
    assert result["status"]["fault"] is None and not result["graphicsMode"]
    assert result["scenario"]["complete"] and result["scenario"]["inputBytesSent"] == result["scenario"]["inputBytesTotal"]
    assert result["ataSectorsRead"] > 0 and result["floppySectorsRead"] > 0
    assert result["ataSectorsWritten"] == result["floppySectorsWritten"] == 0
    return {"passed": True, "codepage": 850, "points": points, "rows": rows, "width": width, "height": height,
            "glyphs": 256, "verifiedPixels": width * height, "cpiSha256": hashlib.sha256(cpi).hexdigest().upper(),
            "fontSha256": hashlib.sha256(font).hexdigest().upper(), "noFontExport": True}


def verify_ansi(base, original, modified):
    """Checks the original ANSI driver's observed mode/font behavior against the independent expected text state."""
    before, _, _ = installation(base, original)
    after, journal, allocation = installation(base, modified)
    assert set(after) == set(before) | {"CONFIG.PRE"}
    assert after["CONFIG.PRE"] == before["CONFIG.SYS"]
    assert after["CONFIG.SYS"].startswith(before["CONFIG.SYS"])
    assert after["CONFIG.SYS"][len(before["CONFIG.SYS"]):].rstrip(b" \r\n") == b"DEVICE=C:\\DOS\\ANSI.SYS"
    for name in before:
        if name != "CONFIG.SYS":
            assert after[name] == before[name], f"Unexpected guest file change: {name}"
    return {"passed": True, "files": len(after), "onlyChange": "CONFIG.SYS append ANSI.SYS + byte-identical CONFIG.PRE backup",
            "allocation": allocation, "journal": journal}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True)
    parser.add_argument("--overlay", required=True)
    parser.add_argument("--original-overlay")
    parser.add_argument("--frame")
    parser.add_argument("--points", type=int, choices=(8, 14, 16))
    parser.add_argument("--rows", type=int)
    parser.add_argument("--report")
    args = parser.parse_args()
    result = verify_ansi(args.base, args.original_overlay, args.overlay) if args.original_overlay else verify_frame(
        args.base, args.overlay, args.frame, args.points, args.rows, args.report)
    print(json.dumps(result, indent=2))
