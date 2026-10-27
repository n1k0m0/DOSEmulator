# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Raw FPREM/FPREM1 inputs, including single partial and repeated complete reduction."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
def val(s,e):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{s:016X}',exponent=f'{e:04X}')
def generate():
 """Builds deterministic x87 remainder vectors vectors and original guest data tables; no original media are modified."""
 cases=[];keys={};selected=[]
 def add(a,b,op=0,cw=0x37f,kind=0):
  """Adds one explicitly parameterized x87 remainder vectors case to the deterministic test matrix."""
  key=a,b,op,cw,kind
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'remainder-{len(cases)}',operation=op,control=cw,kind=kind,a=val(*a),b=val(*b),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 for op in range(4):
  for pc in (0,2,3):
   for rc in range(4):
    for d in (-2,-1,0,1,31,63,64,65,95,96,127,256,4096):
     for neg in (0,0x8000):
      a=(0xabcdef0123456789,(0x3fff+d)|neg);b=(0xc123456789abcdef,0x3fff)
      add(a,b,op,0x7f|pc<<8|rc<<10,(rc&3)<<2)
 special=[(0,0),(0,0x8000),(1,0),(J-1,0),(J+1,0),(J,1),(J+1,1),(J,0x3fff),(J+1,0x3fff),(J,0x7fff),(J,0xffff),(J|0x4000000000000123,0x7fff),(J|0x123,0xffff),(1,1)]
 for a in special:
  for b in special:
   for op in range(4):
    for clear in (0,1,2,16,19):add(a,b,op,0x37f&~clear,0)
 for n in range(1,32):
  a=(n<<(64-n.bit_length()),n.bit_length()-1+0x3fff)
  for neg in (0,0x8000):
   for bneg in (0,0x8000):
    for op in range(4):add((a[0],a[1]|neg),(J,0x4000|bneg),op)
 for op in range(4):
  for clear in (0,1):
   for empty in (1,2,3):
    for cc in (0,4,8,12):add((J+1,0x4003),(J,0x3fff),op,0x37f&~clear,cc|empty)
 rng=random.Random(0x486f8)
 for _ in range(500):
  a=(rng.getrandbits(64)|J,rng.choice((1,0x3fff,0x7ffe,rng.randrange(1,0x7fff)))|rng.choice((0,0x8000)))
  b=(rng.getrandbits(64)|J,rng.choice((1,0x3fff,0x7ffe,rng.randrange(1,0x7fff)))|rng.choice((0,0x8000)))
  for op in range(4):add(a,b,op,rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10,4)
 for op in range(4):
  for pc in (0,2,3):
   for rc in range(4):
    cw=0x7f|pc<<8|rc<<10
    pick((0xe000000000000000,0x4001),(J,0x4000),op,cw,0)
    pick((0xabcdef0123456789,0x4040),(0xc123456789abcdef,0x3fff),op,cw,16)
 for op in range(4):
  for a,b in (((1,0),(J,0x3fff)),((J+1,1),(J,1)),((J,0x7fff),(J,0x3fff)),((J,0x3fff),(0,0)),((J|0x123,0x7fff),(J,0x3fff)),((J+1,0),(J+1,1))):
   for clear in (0,1,2,16):pick(a,b,op,0x37f&~clear,4)
 for op in (0,1):
  for empty in (1,2,3):
   for clear in (0,1):pick((J,0x3fff),(J,0x3fff),op,0x37f&~clear,empty)
 for op in (2,3):
  for gap in (64,127,256,1024,4096,16383):pick((0xabcdef0123456789,0x3fff+gap),(0xc123456789abcdef,0x3fff),op,0x37f,4)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 return cases,selected
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);cases,selected=generate()
 for name,data in (('vectors.json',cases),('guest-vectors.json',selected)):(r/name).write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8',newline='\n')
 chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);s=f'%define VECTOR_COUNT {len(selected)}\n%macro remainder_vectors 0\n'
 s+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-remainder-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected))))
