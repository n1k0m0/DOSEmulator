# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Defined F2XM1 inputs and explicitly separate out-of-range diagnostics."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
def raw(s,e):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{s:016X}',exponent=f'{e:04X}')
def generate():
 """Builds deterministic x87 exp2m1 vectors vectors and original guest data tables; no original media are modified."""
 values=[(0,0),(J,0x3fff),(J,0x3ffe),(J+1,0x3ffe),((1<<64)-1,0x3ffe),(J,0x3ffd),(0xc000000000000000,0x3ffe),
  (1,0),(2,0),(J-1,0),(J,0),(J+1,0),((1<<64)-1,0),(J,1),(J+1,1),(0xb8aa3b295c17f0bb,1),
  (J,2),(J,0x3fc0),(J+1,0x3fc0),(J,0x3f7f),(J|1<<62|0x123,0x7fff),(J|0x123,0x7fff),(1,1)]
 cases=[];keys={};selected=[]
 def add(s,e,cw=0x37f,kind=0):
  """Adds one explicitly parameterized x87 exp2m1 vectors case to the deterministic test matrix."""
  key=s,e,cw,kind
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'exp2m1-{len(cases)}',operation=0,control=cw,kind=kind,a=raw(s,e),b=raw(J+0x1234,0x4003),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 for s,e in values:
  for sign in (0,0x8000):
   for pc in (0,2,3):
    for rc in range(4):
     for clear in (0,1,2,16,32,50):add(s,e|sign,(0x7f|pc<<8|rc<<10)&~clear)
 for rc in range(4):
  for clear in (0,1,32):
   for kind in (1,2,3,4,5,8,9,12,13,16):add(J,0x3ffe,(0x37f|rc<<10)&~clear,kind)
 rng=random.Random(0x486f0)
 for _ in range(500):
  e=rng.choice((0,1,2,0x3ffe,0x3ffd,0x3fc0,rng.randrange(3,0x3fff)))|rng.choice((0,0x8000))
  add(rng.getrandbits(64)|J,e,rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10,4)
 for pc in (0,2,3):
  for rc in range(4):
   for s,e in ((J,0x3ffe),(J,0xbffe),(J,0x3fff),(J,0xbfff)):
    for clear in (0,32):pick(s,e,(0x7f|pc<<8|rc<<10)&~clear)
 for rc in range(4):
  for s,e in ((1,0),(J,1),(J+1,0),(1,0x8000)):
   for clear in (0,2,16,32):pick(s,e,(0x37f|rc<<10)&~clear)
 for clear in (0,1):
  for s,e in ((J|0x123,0x7fff),(1,1),(0,0x8000),(J|1<<62|0x123,0x7fff)):pick(s,e,0x37f&~clear)
  for kind in (1,3,5,9,13,16):pick(J,0x3ffe,0x37f&~clear,kind)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 return cases,selected
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);cases,selected=generate()
 for n,v in (('vectors.json',cases),('guest-vectors.json',selected)):(r/n).write_text(json.dumps(v,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);s=f'%define VECTOR_COUNT {len(selected)}\n%macro exp2m1_vectors 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-exp2m1-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
