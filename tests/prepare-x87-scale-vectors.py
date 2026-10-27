# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Dyadic FSCALE operands and an explicit, representative DOS subset; no host float."""
import argparse,json,random,struct
from pathlib import Path

J=1<<63
def dyadic(n,scale=0):
 """Constructs a raw extended value from the specified exact dyadic integer/exponent pair."""
 if not n:return (0,0)
 negative=n<0;n=abs(n);bits=n.bit_length();assert bits<=64
 return (n<<(64-bits),(16383+scale+bits-1)|(0x8000 if negative else 0))
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')

def generate():
 """Builds deterministic x87 scale vectors vectors and original guest data tables; no original media are modified."""
 cases=[]
 def add(a,b,control,kind=0,family='finite'):
  """Adds one explicitly parameterized x87 scale vectors case to the deterministic test matrix."""
  cases.append(dict(id=f'scale-{len(cases)}',operation=0,control=control,a=raw(a),b=raw(b),kind=kind,bits='0000000000000000',family=family))
 finite=[]
 for a in ((J+1,0x3fff),((1<<64)-1,0x3fff),(J,1),(J+1,1),(J,0x7ffe),((1<<64)-1,0x7ffe),(1,0),(J-1,0),(J,0)):
  for n in (-2,-1,0,1,2):finite.append((a,dyadic(n)))
 for a in ((J+1,0x3fff),((1<<64)-1,0x4001),(J,0x7ffe),(J+1,1)):
  for n in (-7,-3,-1,1,3,7):finite.append((a,dyadic(n,-1)))
 for exponent in (1,0x2000,0x3fff,0x6000,0x7ffe):
  for n in (-65536,-57344,-40961,-40960,-40959,-32768,-24577,-24576,-16384,16384,24576,32768,40959,40960,40961,57344,65536):
   finite.append(((J+3,exponent),dyadic(n)))
 for n in (-1,1):
  for b in (dyadic(1,63),dyadic(-1,63),(J,0x7ffe),(J,0xfffe)):
   finite.append((dyadic(n),b))
 for pc in (0,2,3):
  for rc in range(4):
   for clear in (0,1,2,8,16,32,0x3b):
    control=(0x7f|pc<<8|rc<<10)&~clear
    for a,b in finite:
     add(a,b,control)
     if a[1] not in (0,0x8000):add((a[0],a[1]^0x8000),b,control)
 special=((0,0),(0,0x8000),dyadic(1),dyadic(-1),(J,0x7fff),(J,0xffff),
          (J|1<<62|0x42,0x7fff),(J|0x123,0x7fff),(1,0x1234),(1,0),(J,0),((1<<64)-1,0))
 for pc in (0,2,3):
  for rc in range(4):
   for clear in (0,1,2,0x3b):
    for a in special:
     for b in special:add(a,b,(0x7f|pc<<8|rc<<10)&~clear,family='special')
   for clear in (0,1,2,8,16,32,0x3b):
    for kind in (1,2,3,4,5,6,8,9,10,12,13,14,16,17,18):
     add(dyadic(3),dyadic(3,-1),(0x7f|pc<<8|rc<<10)&~clear,kind,'stack-seed')
 rng=random.Random(0x486fd)
 for i in range(1024):
  ae=rng.choice((0,1,2,0x3ffe,0x3fff,0x4000,0x7ffd,0x7ffe,0x7fff));a=(rng.getrandbits(64)|J,ae|(0x8000 if i&1 else 0))
  b=rng.choice((dyadic(rng.randrange(-70000,70001)),dyadic(rng.randrange(-31,32),-3),(rng.getrandbits(64)|J,rng.choice((0,0x3ffe,0x401e,0x7ffe,0x7fff)))))
  add(a,b,(0x7f|rng.choice((0,2,3))<<8|rng.randrange(4)<<10)&~rng.choice((0,1,2,8,16,32,0x3b)),rng.choice((0,2,4,8,12,16)),'random')
 return cases

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');args=p.parse_args();root=Path(args.directory);root.mkdir(parents=True,exist_ok=True)
 cases=generate();(root/'vectors.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8',newline='\n')
 selected=[]
 def pick(a,b,c,kind=0):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=next(x for x in cases if x['a']==raw(a) and x['b']==raw(b) and x['control']==c and x['kind']==kind)
  if v not in selected:selected.append(v)
 # Explicit RC/PC normal cases and masked/unmasked exponent answers.
 for pc in (0,2,3):
  for rc in range(4):
   for a,b in (((J+1,0x3fff),dyadic(3,-1)),((J+1,1),dyadic(-1)),(((1<<64)-1,0x7ffe),dyadic(1))):
    pick(a,b,0x7f|pc<<8|rc<<10)
   for clear in (8,16,32):pick((J+1,1),dyadic(-1),(0x7f|pc<<8|rc<<10)&~clear)
 for a,b in (((J,0x7ffe),dyadic(1)),((J+1,1),dyadic(-1)),(dyadic(1),dyadic(65536)),(dyadic(-1),dyadic(-65536))):
  # Not every custom pair is in the finite matrix; choose the same category from generated inputs.
  for clear in (0,8,16,32,0x3b):
   match=next((x for x in cases if x['a']==raw(a) and x['b']==raw(b) and x['control']==(0x37f&~clear) and x['kind']==0),None)
   if match is not None and match not in selected:selected.append(match)
 for a,b in (((1,0),dyadic(1)),((J,0),dyadic(0)),(dyadic(1),(1,0)),(dyadic(1),(J|0x123,0x7fff)),(dyadic(1),(J|1<<62|0x42,0x7fff)),(dyadic(1),(1,0x1234)),((0,0),(J,0x7fff)),((J,0x7fff),(J,0xffff))):
  for clear in (0,1,2,0x3b):pick(a,b,0x37f&~clear)
 for clear in (0,1,2,8,16,32,0x3b):
  for kind in (1,2,3,4,8,12,16):pick(dyadic(3),dyadic(3,-1),0x37f&~clear,kind)
 assert len(selected)<=220,len(selected)
 rng=random.Random(0xfd00)
 while len(selected)<220:
  v=rng.choice(cases)
  if v not in selected:selected.append(v)
 (root/'guest-vectors.json').write_text(json.dumps(selected,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],0,v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);source=f'%define VECTOR_COUNT {len(selected)}\n%macro scale_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-scale-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (source),encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
