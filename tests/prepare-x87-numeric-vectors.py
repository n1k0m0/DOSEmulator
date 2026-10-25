"""Own deterministic raw numerical inputs. No operating-system/game data or host float."""
import argparse
import json
from pathlib import Path
import random

J=1<<63
ZERO=(0,0);ONE=(J,0x3fff);TWO=(J,0x4000);THREE=(3<<62,0x4000)
QNAN=(0xc000000000000042,0x7fff);SNAN=(0x8000000000000123,0x7fff)
INF=(J,0x7fff);MIN=(J,1);DEN=(1,0);MAX=((1<<64)-1,0x7ffe)
def neg(v):return (v[0],v[1]^0x8000)
def raw(v):return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')


def generate():
    cases=[]
    def add(name,kind,control=0x37f,operation=0,a=ONE,b=ONE,bits=0):
        cases.append(dict(id=name,kind=kind,control=control,operation=operation,a=raw(a),b=raw(b),bits=f'{bits:016X}'))
    pairs=[(ONE,TWO),(ONE,THREE),(ONE,(J,0x3fbf)),((J+1,0x3fff),(J,0x3fbf)),
           (ONE,(J,0x3fca)),(ONE,(J,0x3fe7)),(ONE,neg(ONE)),(neg(ONE),THREE),
           (ZERO,neg(ZERO)),(neg(ZERO),neg(ZERO)),(MAX,TWO),(MIN,TWO),(MIN,THREE),
           (DEN,TWO),((J-1,0),(J,0x3ffe)),(INF,ZERO),(INF,INF),(ONE,ZERO),
           (ZERO,ZERO),(QNAN,SNAN),(SNAN,ONE),(QNAN,(0xc000000000000043,0x7fff)),
           ((1,0x1234),ONE),((J,0),ONE)]
    controls=[0x7f|(pc<<8)|(rc<<10) for pc in (0,2,3) for rc in range(4)]
    for c in controls:
        for i,(a,b) in enumerate(pairs):
            for op in range(4):add(f'pair-{c:04x}-{i}-{op}',0,c,op,a,b)
    rng=random.Random(0x486387)
    for i in range(160):
        def finite():
            e=rng.choice([0,1,2,0x7ffe,rng.randrange(0x3f00,0x4100)])
            sig=rng.getrandbits(64)|(J if e else 0)
            return sig,e|(0x8000 if rng.randrange(2) else 0)
        add(f'random-{i}',0,rng.choice(controls),rng.randrange(4),finite(),finite())
    exceptions=[(1,SNAN,ONE,0),(2,DEN,ONE,0),(4,ONE,ZERO,3),(8,MAX,TWO,2),(16,MIN,THREE,3),(32,ONE,THREE,3)]
    for flag,a,b,op in exceptions:
        for rc in range(4):add(f'unmasked-{flag}-{rc}',0,(0x37f&~flag)|(rc<<10),op,a,b)
    binary32=[0,0x80000000,0x3f800000,0x3f800001,0x3eaaaaab,0x7f7fffff,0x00800000,1,0x7f800000,0xff800000,0x7fc00123,0x7f800123]
    binary64=[0,1<<63,0x3ff0000000000000,0x3ff0000000000001,0x3fd5555555555555,0x7fefffffffffffff,0x0010000000000000,1,0x7ff0000000000000,0xfff0000000000000,0x7ff8000000000123,0x7ff0000000000123]
    for kind,values in ((1,binary32),(2,binary64),(3,[0,1,(1<<63)-1,1<<63,(1<<64)-1]),(9,[0,1,0x7fffffff,0x80000000,0xffffffff]),(10,[0,1,32767,32768,65535])):
        for c in controls:
            for i,bits in enumerate(values):add(f'load-{kind}-{c:04x}-{i}',kind,c,bits=bits)
    stored=[ZERO,neg(ZERO),ONE,neg(ONE),(3<<62,0x3fff),neg((3<<62,0x3fff)),THREE,
            (J+1,0x3fff),MIN,DEN,MAX,INF,QNAN,SNAN,((1<<64)-1,0x3f80),
            (J,0x3f69),(J,0x3f81),(J,0x3bcd),(J,0x403e),neg((J,0x403e)),(1,0x1234)]
    for kind in range(4,9):
        for c in controls:
            for i,a in enumerate(stored):add(f'store-{kind}-{c:04x}-{i}',kind,c,a=a)
    for flag in (1,8,16,32):
        for kind in range(4,9):
            for i,a in enumerate((SNAN,MAX,MIN,(3<<62,0x3fff))):add(f'store-unmasked-{flag}-{kind}-{i}',kind,0x37f&~flag,a=a)
    for kind,bits in ((1,1),(2,1),(1,0x7f800001),(2,0x7ff0000000000001)):
        for flag in (1,2):add(f'load-unmasked-{kind}-{flag}-{bits}',kind,0x37f&~flag,bits=bits)
    for kind,values in ((11,binary32),(12,binary64),(13,[0,1,0xffffffff,0x7fffffff,0x80000000]),(14,[0,1,65535,32767,32768])):
        for c in controls:
            for i,bits in enumerate(values):
                for op in range(6):add(f'memory-{kind}-{c:04x}-{i}-{op}',kind,c,op,a=ONE,bits=bits)
    # Boundaries where tininess, rounding to minimum-normal, integer range and
    # simultaneous input/operation exceptions need independent hardware checks.
    boundary=[((J-1,0),ONE,0),((J,1),(0xffffffffffffffff,0x3ffe),2),
              ((0xffffffffffffffff,0x3f80),ONE,2),(QNAN,DEN,0),(SNAN,DEN,0),(DEN,ZERO,3),
              ((J,0),TWO,0),(QNAN,neg(QNAN),1)]
    for c in controls:
        for i,(a,b,op) in enumerate(boundary):add(f'boundary-{c:04x}-{i}',0,c,op,a,b)
    assert len({v['id'] for v in cases})==len(cases)
    return cases


def guest_cases(cases):
    by_id={v['id']:v for v in cases};ids=[]
    def take(key):
        if key not in ids:ids.append(key)
    for pc in (0,2,3):
        for rc in range(4):
            c=0x7f|(pc<<8)|(rc<<10)
            for pair,op in ((2,0),(4,0),(5,0),(1,3),(10,2),(12,3)):take(f'pair-{c:04x}-{pair}-{op}')
    for pair in range(15,24):
        for op in range(4):take(f'pair-037f-{pair}-{op}')
    for kind in (3,9,10):
        for index in (1,2,3):take(f'load-{kind}-037f-{index}')
    for kind in (1,2):
        for index in (3,7,10,11):take(f'load-{kind}-037f-{index}')
    for kind in range(4,9):
        for c in (0x37f,0x77f,0xb7f,0xf7f):
            for index in (4,5):take(f'store-{kind}-{c:04x}-{index}')
    for flag in (1,2,4,8,16,32):
        for rc in range(4):take(f'unmasked-{flag}-{rc}')
    for kind in range(11,15):
        for op in range(6):take(f'memory-{kind}-037f-1-{op}')
    take('boundary-037f-1')
    for kind,bits in ((1,1),(2,1),(1,0x7f800001),(2,0x7ff0000000000001)):
        take(f'load-unmasked-{kind}-{2 if bits==1 else 1}-{bits}')
    for kind in (4,5):take(f'store-unmasked-8-{kind}-1')
    assert len(ids)<=220
    return [by_id[key] for key in ids]


if __name__=='__main__':
    import struct
    p=argparse.ArgumentParser();p.add_argument('destination');p.add_argument('--guest-directory');a=p.parse_args()
    cases=generate();Path(a.destination).write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8')
    selected=[]
    if a.guest_directory:
        root=Path(a.guest_directory);root.mkdir(parents=True,exist_ok=True);selected=guest_cases(cases)
        Path(root/'guest-vectors.json').write_text(json.dumps(selected,indent=2)+'\n',encoding='utf-8')
        blocks=[]
        for i,v in enumerate(selected):
            av,bv=v['a'],v['b']
            blocks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,
                int(av['significand'],16),int(av['exponent'],16),int(bv['significand'],16),int(bv['exponent'],16),0,int(v['bits'],16)))
        data=b''.join(blocks);assert len(data)==40*len(selected)
        source=f'%define VECTOR_COUNT {len(selected)}\n%macro numeric_vectors 0\n'
        source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
        (root/'x87-numeric-data.inc').write_text(source,encoding='ascii')
    print(json.dumps(dict(passed=True,vectors=len(cases),guestVectors=len(selected),noHostFloat=True)))
