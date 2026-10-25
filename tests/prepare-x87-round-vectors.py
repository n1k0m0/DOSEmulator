"""Exact dyadic FRNDINT inputs, independent of the emulator and host floating point."""
import argparse,json,random,struct
from pathlib import Path

J=1<<63
def dyadic(n,scale=0,negative=False):
 if n==0:return (0,0x8000 if negative else 0)
 bits=n.bit_length();assert bits<=64
 return (n<<(64-bits),(16383+scale+bits-1)|(0x8000 if negative else 0))
def raw(v):return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')

def generate():
 positive=[dyadic(0),dyadic(1),dyadic(2),dyadic(3),dyadic(1,-1),dyadic(3,-1),dyadic(5,-1),dyadic(7,-1),dyadic(3,-2),
  ((1<<64)-1,0x3ffd),(J+1,0x3ffe),(J+1,0x3fff),((1<<64)-1,0x3ffe),(J+1,0x4000),
  dyadic((1<<24)+1,-1),dyadic((1<<53)+1,-1),dyadic((1<<63)+1,-1),dyadic((1<<64)-1,-1),
  dyadic((1<<63)+1),dyadic((1<<64)-1),(J,0x7ffe),((1<<64)-1,0x7ffe),
  (J,1),(1,0),((1<<63)-1,0),(J,0),((1<<64)-1,0),
  (J,0x7fff),(J|1<<62|0x42,0x7fff),(J|0x123,0x7fff),(1,0x1234),(0,1),(1,0x7fff)]
 values=positive+[(sig,e|0x8000) for sig,e in positive]
 cases=[]
 def add(v,c,kind=0):cases.append(dict(id=f'rnd-{len(cases)}',operation=0,control=c,a=raw(v),b=raw((J+0x1234,0x4003)),kind=kind,bits='0000000000000000'))
 for pc in (0,2,3):
  for rc in range(4):
   for clear in (0,1,2,32,3,33,34,35):
    c=(0x7f|pc<<8|rc<<10)&~clear
    for v in values:add(v,c)
    for kind in (1,2,3,4,5,8,9,12,13,16,17):add(dyadic(3,-1),c,kind)
 rng=random.Random(0x486f0)
 for i in range(768):
  e=rng.choice((0,1,0x3fbd,0x3ffe,0x3fff,0x4000,0x4016,0x403d,0x403e,0x403f,0x7ffe,0x7fff))
  sig=rng.getrandbits(64)
  if e!=0 and i%17:sig|=J
  v=(sig,e|(0x8000 if i&1 else 0));c=(0x7f|rng.choice((0,2,3))<<8|rng.randrange(4)<<10)&~rng.choice((0,1,2,32,3,33,34,35))
  add(v,c,rng.choice((0,2,4,8,12,16)))
 return cases

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');args=p.parse_args();root=Path(args.directory);root.mkdir(parents=True,exist_ok=True)
 cases=generate();(root/'vectors.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8',newline='\n')
 selected=[]
 def pick(v,c,kind=0):
  match=next(x for x in cases if x['a']==raw(v) and x['control']==c and x['kind']==kind)
  if match not in selected:selected.append(match)
 for rc in range(4):
  for negative in (False,True):
   for n in (1,3,5):
    for clear in (0,32):pick(dyadic(n,-1,negative),(0x37f|rc<<10)&~clear)
   for clear in (0,2,32,34):pick((1,0x8000 if negative else 0),(0x37f|rc<<10)&~clear)
 for pc in (0,2,3):
  for rc in range(4):
   for v in (dyadic((1<<53)+1,-1),dyadic((1<<63)+1),dyadic((1<<64)-1,-1)):pick(v,0x7f|pc<<8|rc<<10)
 for clear in (0,1,2,32,35):
  for v in ((J|1<<62|0x42,0x7fff),(J|0x123,0x7fff),(1,0x1234),(J,0x7fff),(0,0x8000),(J,0)):
   pick(v,0x37f&~clear)
  for kind in (1,3,5,9,13,16):pick(dyadic(3,-1),0x37f&~clear,kind)
 assert len(selected)<=220,len(selected)
 rng=random.Random(0xf0c0)
 while len(selected)<220:
  v=rng.choice(cases)
  if v not in selected:selected.append(v)
 (root/'guest-vectors.json').write_text(json.dumps(selected,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);source=f'%define VECTOR_COUNT {len(selected)}\n%macro round_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-round-data.inc').write_text(source,encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
