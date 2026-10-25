"""Independent private CPI -> full VGA graphics-font image oracle.
No emulator imports, host font encoder, reference screenshot or public font asset.
The guest copies its prepared text font through real VGA plane-2 reads, then
draws all 256 characters through INT10 graphics services and reads them back.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

spec = importlib.util.spec_from_file_location('font_oracle', Path(__file__).with_name('verify-font-frame.py'))
font_oracle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(font_oracle)


def verify(args):
    files, journal, allocation = font_oracle.installation(args.base, args.overlay)
    cpi = files['DOS/EGA.CPI']
    font = font_oracle.cpi_fonts(cpi, 850)[args.points]
    report = json.loads(Path(args.report).read_text(encoding='utf-8-sig'))
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
    assert report['ataSectorsRead'] > 0 and report['floppySectorsRead'] > 0
    assert report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    for marker in ['GFX-FONT-SERVICES-OK', 'GFX-FONT-256-GLYPHS-OK', 'GFX-FONT-READY']:
        assert marker in report['status']['debugOutput']
    assert 'GFX-FONT-FAILED' not in report['status']['debugOutput']
    magic, dimensions, maximum, pixels = Path(args.frame).read_bytes().split(b'\n', 3)
    width, height = map(int, dimensions.split())
    expected_dimensions = (640,480) if args.mode == 0x12 else (320,200)
    assert magic == b'P6' and maximum == b'255' and (width,height) == expected_dimensions
    rows, columns = height // args.points, width // 8
    cells = [[(32,0) for _ in range(columns)] for _ in range(rows)]

    def text(row, col, value, color):
        for ch in value.encode('ascii'):
            cells[row][col] = (ch,color)
            col += 1

    text(0,2,'VGA GRAPHICS FONT / BIOS',14)
    text(1,2,f'MODE {args.mode:02X} / 8x{args.points:02d} / {rows:02d} ROWS',11)
    text(2,2,'SCROLL / READBACK OK',7)
    for ch in range(256):
        cells[3+ch//32][2+(ch%32)*(2 if args.mode==0x12 else 1)] = (ch,ch%15+1)
    text(rows-1,2,'GFX-FONT-READY',14)
    palette = [(0,0,0),(0,0,170),(0,170,0),(0,170,170),(170,0,0),(170,0,170),(170,85,0),(170,170,170),
               (85,85,85),(85,85,255),(85,255,85),(85,255,255),(255,85,85),(255,85,255),(255,255,85),(255,255,255)]
    expected = bytearray()
    for y in range(height):
        row, glyph_y = divmod(y,args.points)
        for x in range(width):
            if row >= rows:
                expected.extend((0,0,0))
                continue
            col, bit = divmod(x,8)
            ch, color = cells[row][col]
            expected.extend(palette[color if font[ch*args.points+glyph_y] & (128>>bit) else 0])
    assert len(pixels) == len(expected)
    if pixels != expected:
        offset = next(i for i,(a,b) in enumerate(zip(pixels,expected)) if a!=b)
        y,x = divmod(offset//3,width)
        raise AssertionError(f'Pixel {x},{y}: actual {tuple(pixels[(y*width+x)*3:(y*width+x)*3+3])} != expected {tuple(expected[(y*width+x)*3:(y*width+x)*3+3])}')
    argb = bytearray()
    for r,g,b in zip(expected[0::3],expected[1::3],expected[2::3]):
        argb.extend((b,g,r,255))
    digest = hashlib.sha256(argb).hexdigest().upper()
    assert digest == report['frameHash']
    memory = Path(args.memory).read_bytes()
    font_offset, font_segment = struct.unpack_from('<HH',memory,0x10c)
    source = font_segment*16+font_offset
    assert memory[source:source+len(font)] == font, 'Guest VGA snapshot differs from the private CPI'
    assert memory[0x449] == args.mode and memory[0x462] == 0
    assert struct.unpack_from('<H',memory,0x44a)[0] == columns
    assert struct.unpack_from('<H',memory,0x44c)[0] == (0x9600 if args.mode==0x12 else 0xfa00)
    assert struct.unpack_from('<H',memory,0x450)[0] == 0
    assert memory[0x484] == rows-1 and struct.unpack_from('<H',memory,0x485)[0] == args.points
    return dict(passed=True,mode=f'{args.mode:02X}',points=args.points,rows=rows,columns=columns,
                pixelsVerified=width*height,frameHash=digest,all256Glyphs=True,guestSnapshotMatchesPrivateCpi=True,
                cpiSha256=hashlib.sha256(cpi).hexdigest().upper(),fontSha256=hashlib.sha256(font).hexdigest().upper(),
                guestFramebufferReadback=True,files=len(files),allocation=allocation,journal=journal)


if __name__ == '__main__':
    p=argparse.ArgumentParser()
    for name in ['base','overlay','frame','report','memory']:
        p.add_argument('--'+name,required=True)
    p.add_argument('--mode',type=lambda value:int(value,16),choices=[0x12,0x13],required=True)
    p.add_argument('--points',type=int,choices=[8,14,16],required=True)
    print(json.dumps(verify(p.parse_args()),indent=2))
