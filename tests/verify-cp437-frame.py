"""Original artwork -> independent full text scanout and guest-ROM-copy oracle.
No emulator imports, public font asset, OS font bytes or screenshot baseline.
The text art is the intended input; integration and geometry are checked here,
while semantic edge/density checks and visual review validate the artwork.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def artwork(points):
    source = Path(__file__).resolve().parents[1] / 'firmware/fonts/cp437-8x8.txt'
    rows = {}
    for line in source.read_text(encoding='utf-8').splitlines():
        values = line.partition('#')[0].split()
        if not values:
            continue
        code, *data = [int(word, 16) for word in values]
        assert code not in rows and len(data) == 8
        rows[code] = data
    assert sorted(rows) == list(range(256))
    result = bytearray()
    for code, scanlines in sorted(rows.items()):
        if points == 8:
            target = scanlines
        elif 0xb0 <= code <= 0xb2:
            target = (scanlines * 2)[:points]
        elif 0xb3 <= code <= 0xda:
            middle = points // 2
            target = scanlines[:2] + [scanlines[2]] * (middle-3) + scanlines[3:6] + [scanlines[6]] + [scanlines[7]] * (points-middle-3)
        elif 0xdb <= code <= 0xdf:
            target = [scanlines[0]] * (points//2) + [scanlines[7]] * (points//2)
        else:
            count = 8 if scanlines[7] else 7
            target = []
            for index in range(count):
                # Integer interval endpoints; body height is 14 in both tables.
                start = (index*14 + count-1)//count
                end = ((index+1)*14 + count-1)//count
                target.extend([scanlines[index]] * (end-start))
            if points == 16:
                target = [0] + target + [0]
        assert len(target) == points
        result.extend(target)
    return bytes(result)


def verify(args):
    font = artwork(args.points)
    report = json.loads(Path(args.report).read_text(encoding='utf-8-sig'))
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
    assert report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    assert report['ataSectorsRead'] > 0 and report['floppySectorsRead'] > 0
    debug = report['status']['debugOutput']
    assert 'CP437-FONT-FAILED' not in debug
    assert 'CP437-PLANE-AND-256-CELLS-OK' in debug and 'CP437-FONT-READY' in debug
    magic, dimensions, maximum, pixels = Path(args.frame).read_bytes().split(b'\n', 3)
    width, height = map(int, dimensions.split())
    rows = 400 // args.points
    assert magic == b'P6' and maximum == b'255' and (width, height) == (80*args.cell_width, rows*args.points)
    cells = [[(32,0x17) for _ in range(80)] for _ in range(rows)]
    def text(row, col, value, color=0x1f):
        if isinstance(value, str):
            value = value.encode('ascii')
        for code in value:
            cells[row][col] = (code,color)
            col += 1
    text(0,2,'ORIGINAL CP437 / REFERENCE VGA')
    text(1,2,'256 video glyphs / 8, 14 and 16 rows')
    text(2,2,'Own artwork / ROM bytes + real VGA plane 2')
    for col in range(16):
        text(3,6+4*col,f'{col:X}',0x1b)
    for code in range(256):
        cells[4+code//16][6+4*(code%16)] = (code,0x1e)
    for row in range(16):
        text(4+row,1,f'{row:X}0',0x1b)
    for row,first,last,bar in [(21,0xda,0xbf,0xc4),(23,0xc0,0xd9,0xc4)]:
        text(row,2,bytes([first]+[bar]*29+[last]))
    for row,first,last in [(21,0xc9,0xbb),(23,0xc8,0xbc)]:
        text(row,36,bytes([first]+[0xcd]*29+[last]))
    text(22,2,b'\xb3Gr\x81\xe1e: \x84 \x94 \x81 \x8e \x99 \x9a \xe1')
    text(22,32,b'\xb3')
    text(22,36,b'\xbaMixed: \xb5 \xb6 \xd1 \xd2 / \xb0\xb1\xb2\xdb\xdc\xdd\xde\xdf')
    text(22,66,b'\xba')
    text(24,2,'CP437-FONT-READY / ENTER RETURNS')
    palette = [(0,0,0),(0,0,170),(0,170,0),(0,170,170),(170,0,0),(170,0,170),(170,85,0),(170,170,170),
               (85,85,85),(85,85,255),(85,255,85),(85,255,255),(255,85,85),(255,85,255),(255,255,85),(255,255,255)]
    expected = bytearray()
    for y in range(height):
        for x in range(width):
            code,attr = cells[y//args.points][x//args.cell_width]
            column = x%args.cell_width
            bits = font[code*args.points + y%args.points]
            on = bool(bits & (128 >> column)) if column < 8 else 0xc0 <= code <= 0xdf and bool(bits & 1)
            expected.extend(palette[attr&15 if on else (attr>>4)&7])
    assert len(expected) == len(pixels)
    if expected != pixels:
        offset = next(i for i,(a,b) in enumerate(zip(expected,pixels)) if a!=b)
        raise AssertionError(f'Pixel mismatch at {offset//3%width}/{offset//3//width}')
    argb = bytearray()
    for r,g,b in zip(expected[0::3],expected[1::3],expected[2::3]):
        argb.extend((b,g,r,255))
    frame_hash = hashlib.sha256(argb).hexdigest().upper()
    assert frame_hash == report['frameHash']
    memory = Path(args.memory).read_bytes()
    assert memory[0x449] == 3 and memory[0x484] == rows-1 and memory[0x462] == 0
    assert struct.unpack_from('<H',memory,0x44a)[0] == 80
    assert struct.unpack_from('<H',memory,0x44c)[0] == rows*160
    assert struct.unpack_from('<H',memory,0x485)[0] == args.points
    assert struct.unpack_from('<H',memory,0x460)[0] == 0x2000
    marker = b'CP437-OWN-FONT-V1' + struct.pack('<HB',args.points,args.cell_width)
    candidates = []
    start = 0
    while True:
        found = memory.find(marker,start)
        if found < 0:
            break
        start = found+1
        data = found+len(marker)
        if memory[data:data+len(font)] == font:
            candidates.append(data)
    assert len(candidates) == 1, 'Missing/ambiguous actual guest copy of own ROM table'
    return dict(passed=True, points=args.points, cellWidth=args.cell_width, width=width,height=height,
                all256Glyphs=True, pixelsVerified=width*height, frameHash=frame_hash,
                fontSha256=hashlib.sha256(font).hexdigest().upper(), guestRomCopyMatchesOwnArtwork=True,
                displayQuerySubstituted='CP437-DISPLAY-QUERY-SUBSTITUTED' in debug,
                guestPlane2BytesVerified=256*args.points, guestWrites=0)


if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    for name in ['frame','report','memory']:
        parser.add_argument('--'+name,required=True)
    parser.add_argument('--points',type=int,choices=[8,14,16],required=True)
    parser.add_argument('--cell-width',type=int,choices=[8,9],required=True)
    print(json.dumps(verify(parser.parse_args()),indent=2))
