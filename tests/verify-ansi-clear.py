# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reconstruct every captured ANSI-clear pixel from original CP850 CPI bytes.

The expected text is fixed by our own DOS probe, never read back from the
emulator as an expected image. Source media are read-only and remain private.
"""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("font_oracle", Path(__file__).with_name("verify-font-frame.py"))
font_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(font_oracle)


def verify(frame, report, font, rows, points):
    """Compare the complete independent frame and require real probe/input/boot evidence."""
    magic, dimensions, maximum, pixels = frame.split(b"\n", 3)
    assert magic == b"P6" and maximum == b"255"
    width, height = map(int, dimensions.split())
    assert (width, height) == (640, rows * points)
    assert len(pixels) == width * height * 3
    lines = [b"ORIGINAL DOS ANSI / CLEAR VERIFIED", b"NO STALE CELLS / CP850"]
    cells = b"".join((lines[r] if r < len(lines) else b"").ljust(80) for r in range(rows))
    expected = bytearray(len(pixels))
    for y in range(height):
        for x in range(width):
            code = cells[(y // points) * 80 + x // 8]
            level = 170 if font[code * points + y % points] & (128 >> (x % 8)) else 0
            offset = (y * width + x) * 3
            expected[offset:offset + 3] = bytes([level]) * 3
    assert pixels == expected, "ANSI clear/font pixels differ from the independent complete expected frame"
    assert report["status"]["fault"] is None and not report["graphicsMode"]
    assert "ANSI-CLS-READY" in report["status"]["debugOutput"]
    assert "ANSI-CLS-FAILED" not in report["status"]["debugOutput"]
    assert report["scenario"]["complete"] and report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["ataSectorsRead"] > 0 and report["floppySectorsRead"] > 0
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    return dict(passed=True, pixels=width * height, rows=rows, points=points)


def main():
    """Load private evidence and optionally prove that changed pixels or success markers are rejected."""
    p = argparse.ArgumentParser()
    for name in ["base", "overlay", "frame", "report"]:
        p.add_argument("--" + name, required=True, type=Path)
    p.add_argument("--rows", required=True, type=int)
    p.add_argument("--points", required=True, type=int)
    p.add_argument("--negative-controls", action="store_true")
    a = p.parse_args()
    files, _, _ = font_oracle.installation(a.base, a.overlay)
    font = font_oracle.cpi_fonts(files["DOS/EGA.CPI"], 850)[a.points]
    frame = a.frame.read_bytes()
    report = json.loads(a.report.read_text(encoding="utf-8-sig"))
    result = verify(frame, report, font, a.rows, a.points)
    if a.negative_controls:
        rejected = 0
        magic, dim, maxval, pixels = frame.split(b"\n", 3)
        prefix = b"\n".join([magic, dim, maxval]) + b"\n"
        for offset in [0, 100, (2 * a.points * 640 + 3) * 3, len(pixels) - 1]:
            corrupt = bytearray(pixels)
            corrupt[offset] ^= 255
            try:
                verify(prefix + corrupt, report, font, a.rows, a.points)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError("Changed frame accepted")
        for field in ["debugOutput", "fault"]:
            corrupt = json.loads(json.dumps(report))
            corrupt["status"][field] = "corrupted"
            try:
                verify(frame, corrupt, font, a.rows, a.points)
            except AssertionError:
                rejected += 1
            else:
                raise AssertionError("Changed proof accepted")
        result["negativeControlsRejected"] = rejected
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
