"""Independent guest-font and cursor-interval -> full RGB framebuffer oracle."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

def module(name,filename):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(filename))
    value=importlib.util.module_from_spec(spec);spec.loader.exec_module(value);return value

def verify(args):
    if args.guest=='msdos':
        fonts=module('cpi','verify-font-frame.py')
        files,_,_=fonts.installation(args.base,args.overlay)
        font=fonts.cpi_fonts(files['DOS/EGA.CPI'],850)[args.points]
    else:
        font=module('own','verify-cp437-frame.py').artwork(args.points)
    report=json.loads(Path(args.report).read_text(encoding='utf-8-sig'))
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent']==report['scenario']['inputBytesTotal']
    assert report['ataSectorsWritten']==report['floppySectorsWritten']==0
    assert report['ataSectorsRead']>0 and report['floppySectorsRead']>0
    assert 'CURSOR-POLICY-AND-SHAPE-OK' in report['status']['debugOutput'] and 'CURSOR-FAILED' not in report['status']['debugOutput']
    start,end = {8:(6,7),14:(10,13),16:(12,15)}[args.points] if args.enabled else (6,7)
    cursor_on=((report['status']['virtualNanoseconds']//14285714)&16)==0
    rows=400//args.points
    cells=[[(32,0x17) for _ in range(80)] for _ in range(rows)]
    def text(row,value):
        for col,code in enumerate(value.encode('ascii'),2): cells[row][col]=(code,0x1f)
    text(1,'BIOS CURSOR / AH12 BL34')
    text(3,'Same legacy request: CH=06 / CL=07')
    text(4,'EMULATION ENABLED' if args.enabled else 'EMULATION DISABLED')
    text(6,'AH03 retains the requested shape; CRTC receives native rows.')
    text(9,'Cursor in the colored blank cell below:')
    for col in range(4,20): cells[10][col]=(32,0x2c)
    text(12,'Green: cell background / red: real hardware cursor')
    text(14,'INFO bit, AH03 and AH1B state/capability verified.')
    text(16,'CURSOR-READY / ENTER RETURNS TO DOS')
    palette=[(0,0,0),(0,0,170),(0,170,0),(0,170,170),(170,0,0),(170,0,170),(170,85,0),(170,170,170),
             (85,85,85),(85,85,255),(85,255,85),(85,255,255),(255,85,85),(255,85,255),(255,255,85),(255,255,255)]
    expected=bytearray();cursor_pixels=0
    width,height=640,rows*args.points
    for y in range(height):
        for x in range(width):
            code,attr=cells[y//args.points][x//8]
            glyph=bool(font[code*args.points+y%args.points]&(128>>(x%8)))
            cursor=cursor_on and y//args.points==10 and x//8==6 and start<=y%args.points<=end
            cursor_pixels+=cursor
            expected.extend(palette[attr&15 if glyph or cursor else (attr>>4)&7])
    magic,dimensions,maximum,pixels=Path(args.frame).read_bytes().split(b'\n',3)
    assert magic==b'P6' and tuple(map(int,dimensions.split()))==(width,height) and maximum==b'255'
    if pixels!=expected:
        offset=next(i for i,(a,b) in enumerate(zip(pixels,expected)) if a!=b)
        raise AssertionError(f'Cursor frame pixel mismatch {offset//3%width}/{offset//3//width}')
    argb=bytearray()
    for r,g,b in zip(expected[0::3],expected[1::3],expected[2::3]): argb.extend((b,g,r,255))
    digest=hashlib.sha256(argb).hexdigest().upper()
    assert digest==report['frameHash']
    memory=Path(args.memory).read_bytes()
    assert struct.unpack_from('<H',memory,0x460)[0]==0x0607
    assert struct.unpack_from('<H',memory,0x450)[0]==0x0a06
    assert memory[0x487]&1==int(not args.enabled)
    assert struct.unpack_from('<H',memory,0x485)[0]==args.points and memory[0x484]==rows-1
    signature=b'CURSOR-STATE-V1'+struct.pack('<HBH',args.points,args.enabled,start*256+end)
    assert memory.count(signature)==1
    position=memory.index(signature)+len(signature)
    assert bool(memory[position+0x2d]&16)==bool(args.enabled)
    return dict(passed=True,guest=args.guest,points=args.points,enabled=bool(args.enabled),rawShape='06/07',crtcStart=start,crtcEnd=end,
                pixelsVerified=width*height,cursorVisible=cursor_on,cursorPixels=cursor_pixels,frameHash=digest,
                originalFontSource='private CP850 CPI' if args.guest=='msdos' else 'own CP437 artwork',guestWrites=0)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    for name in ['frame','report','memory']:p.add_argument('--'+name,required=True)
    p.add_argument('--guest',choices=['msdos','freedos'],required=True)
    p.add_argument('--points',type=int,choices=[8,14,16],required=True)
    p.add_argument('--enabled',type=int,choices=[0,1],required=True)
    p.add_argument('--base');p.add_argument('--overlay')
    print(json.dumps(verify(p.parse_args()),indent=2))
