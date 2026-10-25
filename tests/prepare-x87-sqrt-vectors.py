"""Exact dyadic FSQRT inputs; no host floating-point arithmetic."""
import argparse,json,random,struct
from pathlib import Path

J=1<<63
def dyadic(n,scale=0,negative=False):
 if not n:return (0,0x8000 if negative else 0)
 bits=n.bit_length();assert bits<=64
 return (n<<(64-bits),(16383+scale+bits-1)|(0x8000 if negative else 0))
def raw(v):return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')

def generate():
 positive=[dyadic(n) for n in (0,1,2,3,4,9,16,25,36,49,81,100)]+[dyadic(1,-2),dyadic(9,-2),
  (J-1,0),(1,0),(2,0),(J,0),((1<<64)-1,0),(J,1),(J+1,1),(J,2),(J+1,0x3fff),(J+3,0x3fff),
  ((1<<64)-1,0x7ffe),(J,0x7ffe),(J,0x7fff),(J|1<<62|0x42,0x7fff),(J|0x123,0x7fff),
  (1,0x1234),(0,1),(1,0x7fff)]
 # Squares adjacent to representable root midpoints, including exact ties at PC24.
 for p in (24,53,64):
  for q in ((1<<(p-1)),(1<<(p-1))+1,(1<<p)-1):
   mid=(2*q+1)**2;shift=max(0,mid.bit_length()-64)
   for delta in (-1,0,1):
    n=(mid>>shift)+delta
    if 0<n<1<<64:positive.append(dyadic(n,shift-2*p))
 positive+= [(J,exp) for exp in (3,0x1ffe,0x1fff,0x2000,0x3ffe,0x3fff,0x4000,0x5ffe,0x5fff,0x6000,0x7ffd)]
 positive=list(dict.fromkeys(positive));values=positive+[(s,e|0x8000) for s,e in positive];cases=[]
 def add(v,c,kind=0):cases.append(dict(id=f'sqrt-{len(cases)}',operation=0,control=c,a=raw(v),b=raw((J+0x1234,0x4003)),kind=kind,bits='0000000000000000'))
 for pc in (0,2,3):
  for rc in range(4):
   for clear in (0,1,2,32,3,33,34,35):
    c=(0x7f|pc<<8|rc<<10)&~clear
    for v in values:add(v,c)
    for kind in (1,2,3,4,5,8,9,12,13,16,17):add(dyadic(2),c,kind)
 rng=random.Random(0x486fa)
 for i in range(1024):
  exp=rng.choice((0,1,2,0x1fff,0x3ffe,0x3fff,0x4000,0x4016,0x403e,0x7ffe,0x7fff))
  sig=rng.getrandbits(64)
  if exp and i%17:sig|=J
  add((sig,exp|(0x8000 if i%4==0 else 0)),(0x7f|rng.choice((0,2,3))<<8|rng.randrange(4)<<10)&~rng.choice((0,1,2,32,3,33,34,35)),rng.choice((0,2,4,8,12,16)))
 return cases

if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('directory');args=parser.parse_args();root=Path(args.directory);root.mkdir(parents=True,exist_ok=True)
 cases=generate();(root/'vectors.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8',newline='\n')
 selected=[]
 def pick(v,c,kind=0):
  match=next(x for x in cases if x['a']==raw(v) and x['control']==c and x['kind']==kind)
  if match not in selected:selected.append(match)
 for pc in (0,2,3):
  for rc in range(4):
   for n in (2,4,9):
    for clear in (0,32):pick(dyadic(n),(0x7f|pc<<8|rc<<10)&~clear)
 for rc in range(4):
  for clear in (0,2,32,34):
   for v in ((1,0),(J,0)):pick(v,(0x37f|rc<<10)&~clear)
 for pc in (0,2,3):
  for clear in (0,32):
   for v in ((J+1,0x3fff),(J+3,0x3fff)):pick(v,(0x7f|pc<<8|2<<10)&~clear)
 for clear in (0,1,2,32,35):
  for v in ((J|1<<62|0x42,0x7fff),(J|0x123,0x7fff),(1,0x1234),(J,0x7fff),(0,0x8000),dyadic(1,0,True),(1,0x8000)):
   pick(v,0x37f&~clear)
  for kind in (1,3,5,9,13,16):pick(dyadic(2),0x37f&~clear,kind)
 assert len(selected)<=220,len(selected)
 rng=random.Random(0xfa00)
 while len(selected)<220:
  v=rng.choice(cases)
  if v not in selected:selected.append(v)
 (root/'guest-vectors.json').write_text(json.dumps(selected,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],0,v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);source=f'%define VECTOR_COUNT {len(selected)}\n%macro sqrt_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-sqrt-data.inc').write_text(source,encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
