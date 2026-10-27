# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Raw FXTRACT inputs. Expected numbers/state are supplied by separate oracles."""
import argparse,json,random,struct
from pathlib import Path

J=1<<63
B=dict(significand='8000000000001234',exponent='4003')

def generate():
 """Builds deterministic x87 extract vectors vectors and original guest data tables; no original media are modified."""
 cases=[];indices={}
 def add(s,e,cw=0x37f,kind=0x31):
  """Adds one explicitly parameterized x87 extract vectors case to the deterministic test matrix."""
  key=s,e,cw,kind
  if key not in indices:
   indices[key]=len(cases)
   cases.append(dict(id=f'extract-{len(cases)}',operation=0,control=cw,kind=kind,
    a=dict(significand=f'{s:016X}',exponent=f'{e:04X}'),b=B,bits='0000000000000000'))
  return cases[indices[key]]
 values=[(0,0),(J,0x7fff),(J|0x4000000000000123,0x7fff),(J|0x123,0x7fff),(1,1),(0,0x7fff),
  (1,0),(3,0),((1<<62)+1,0),(J-1,0),(J,0),(J+1,0),(J,1),(J+1,1),((1<<64)-1,1)]
 values += [(s,e) for e in (2,0x3ffe,0x3fff,0x4000,0x403e,0x7ffe) for s in (J,J+1,J+(1<<39)+3,(1<<64)-1)]
 for s,e in values:
  for sign in (0,0x8000):
   for pc in (0,2,3):
    for rc in range(4):
     for clear in (0,1,2,4,7):
      for depth in (1,7):add(s,e|sign,(0x7f|pc<<8|rc<<10)&~clear,depth|(0x30,0x60,0x80,0xc0)[(rc+clear)%4])
 for s,e in values[:15]+[(J+1,0x4003)]:
  for clear in (0,1,2,4,7):
   for depth in range(9):
    for holes in (0,0x100,0x200,0x300,0x400):add(s,e,0x37f&~clear,depth|holes|0xf0)
 rng=random.Random(0x486f4)
 for _ in range(700):
  e=rng.choice((0,1,0x3ffe,0x3fff,0x7ffe,rng.randrange(1,0x7fff)))|rng.choice((0,0x8000))
  add(rng.getrandbits(64)|J,e,rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10,rng.randrange(1,8)|0x30)
 selected=[]
 def pick(s,e,cw=0x37f,kind=0x31):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(s,e,cw,kind)
  if v not in selected:selected.append(v)
 for pc in (0,2,3):
  for rc in range(4):
   cw=0x7f|pc<<8|rc<<10
   for s,e in ((J+1,0x3fff),((1<<64)-1,0xfffe),(1,0),(J+1,0),(0,0x8000)):
    pick(s,e,cw,0x37)
 for s,e in values[:15]+[(J+1,0x4003)]:
  for clear in (0,1,2,4,7):pick(s,e,0x37f&~clear,0x31)
 for s,e in ((J+1,0x3fff),(0,0),(1,0),(J|0x123,0x7fff)):
  for clear in (0,1):
   for kind in (0,8,0x108,0x208,0x308,0x407):pick(s,e,0x37f&~clear,kind|0x30)
 for pc in (0,2,3):
  for rc in range(4):pick(J+3,0x4003,0x7f|pc<<8|rc<<10,2|((rc&3)<<6))
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 return cases,selected

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();root=Path(a.directory);root.mkdir(parents=True,exist_ok=True)
 cases,selected=generate()
 for name,data in (('vectors.json',cases),('guest-vectors.json',selected)):
  (root/name).write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);source=f'%define VECTOR_COUNT {len(selected)}\n%macro extract_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-extract-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (source),encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
