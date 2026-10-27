# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent CP850 text-page/cursor pixel oracle, no emulator imports/assets.
Fonts are read privately from the real DOS installation; only own sheet text
and documented VGA cell/palette/cursor rules determine the expected picture.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location('fonts', Path(__file__).with_name('verify-font-frame.py'))
fonts = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fonts)


def verify(args):
    """Validates text pages evidence from actual guest state, immutable program bytes and independently expected results."""
    files, _, allocation = fonts.installation(args.base, args.overlay)
    font = fonts.cpi_fonts(files['DOS/EGA.CPI'], 850)[args.points]
    report = json.loads(Path(args.report).read_text(encoding='utf-8-sig'))
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['floppySectorsRead'] > 0 and report['ataSectorsRead'] > 0
    assert report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    assert 'TEXT-PAGES-CHECKS-OK' in report['status']['debugOutput']
    assert 'TEXT-PAGES-FAILED' not in report['status']['debugOutput']
    image = Path(args.frame).read_bytes()
    magic, dimensions, maximum, pixels = image.split(b'\n', 3)
    width, height = map(int, dimensions.split())
    assert magic == b'P6' and maximum == b'255' and (width, height) == (640, args.points * args.rows)
    cells = [[(32, 0x17) for _ in range(80)] for _ in range(args.rows)]

    def text(row, col, content, attr):
        """Writes expected character/attribute cells into the independent frame oracle."""
        for char in content.encode('ascii'):
            cells[row][col] = (char, attr)
            col += 1

    text(1, 2, 'TEXT PAGES / BIOS INT10', 0x1f)
    text(3, 2, f'PAGE: {args.page} / PAGES: {args.pages} / ROWS: {args.rows:02d}', 0x1e)
    text(5, 2, 'Hidden pages keep their own cursor and attributes.', 0x17)
    for i, (char, attr) in enumerate(zip(b'COLOR', [0x1c,0x1e,0x1a,0x1b,0x1d])):
        cells[7][2+i] = (char, attr)
    text(9, 2, '=' * 60, 0x1e)
    text(11, 2, 'eeee', 0x1b)
    text(args.rows - 2, 2, 'TEXT-PAGES-READY', 0x1e)
    cells[13][5] = (32, 0x2c)
    # Standard VGA colors in the private CPI profile, RGBI -> six-bit DAC -> RGB8.
    palette = [(0,0,0),(0,0,170),(0,170,0),(0,170,170),(170,0,0),(170,0,170),(170,85,0),(170,170,170),
               (85,85,85),(85,85,255),(85,255,85),(85,255,255),(255,85,85),(255,85,255),(255,255,85),(255,255,255)]
    now = report['status']['virtualNanoseconds']
    cursor_visible = ((now // 14_285_714) & 16) == 0
    expected = bytearray()
    cursor_pixels = 0
    for y in range(height):
        row, glyph_y = divmod(y, args.points)
        for x in range(width):
            col, bit = divmod(x, 8)
            char, attr = cells[row][col]
            foreground = bool(font[char * args.points + glyph_y] & (128 >> bit))
            cursor = cursor_visible and (row, col) == (13, 5) and glyph_y >= args.points - 2
            cursor_pixels += cursor
            expected.extend(palette[attr & 15 if foreground or cursor else (attr >> 4) & 7])
    assert len(pixels) == len(expected)
    if pixels != expected:
        offset = next(i for i, pair in enumerate(zip(pixels, expected)) if pair[0] != pair[1])
        y, x = divmod(offset // 3, width)
        raise AssertionError(f'Pixel {x},{y}: RGB {tuple(pixels[(y*width+x)*3:(y*width+x)*3+3])} != {tuple(expected[(y*width+x)*3:(y*width+x)*3+3])}')
    argb = bytearray()
    for r,g,b in zip(expected[0::3], expected[1::3], expected[2::3]):
        argb.extend((b,g,r,255))
    digest = hashlib.sha256(argb).hexdigest().upper()
    assert digest == report['frameHash']
    assert 'TEXT-PAGES-READY' in report['text'] and f'PAGE: {args.page} / PAGES: {args.pages}' in report['text']
    if args.memory:
        memory = Path(args.memory).read_bytes()
        assert memory[0x462] == args.page
        page_bytes, start = struct.unpack_from('<HH', memory, 0x44c)
        assert start == page_bytes * args.page and page_bytes >= 160 * args.rows
        assert struct.unpack_from('<H', memory, 0x450 + args.page * 2)[0] == 0x0d05
        assert struct.unpack_from('<H', memory, 0x460)[0] == ((args.points-2) << 8 | args.points-1)
    return dict(passed=True, everyPixelVerified=True, pixels=width*height, frameHash=digest,
                codepage=850, points=args.points, rows=args.rows, page=args.page, pages=args.pages,
                cursorVisible=cursor_visible, cursorPixels=cursor_pixels, files=len(files), allocation=allocation)


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    for name in ['base','overlay','frame','report']:
        p.add_argument('--'+name, required=True)
    for name in ['points','rows','page','pages']:
        p.add_argument('--'+name, required=True, type=int)
    p.add_argument('--memory')
    print(json.dumps(verify(p.parse_args()), indent=2))
