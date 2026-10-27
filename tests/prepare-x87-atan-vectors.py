# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""FPATAN full-domain quadrant, boundary, tiny-ratio and exception matrices."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')
def generate():
 """Builds deterministic x87 atan vectors vectors and original guest data tables; no original media are modified."""
 cases=[];keys={};selected=[]
 def add(a,b,cw=0x37f,kind=4):
  """Adds one explicitly parameterized x87 atan vectors case to the deterministic test matrix."""
  key=a,b,cw,kind
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'atan-{len(cases)}',operation=0,control=cw,kind=kind,a=raw(a),b=raw(b),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 special=[(0,0),(0,0x8000),(J,0x3fff),(J,0xbfff),(J,0x7fff),(J,0xffff),(1,0),(J,0),(J,1),(J|0x123,0x7fff),(J|1<<62|0x456,0xffff),(1,1)]
 for a in special:
  for b in special:
   for clear in (0,1,2,16,32):add(a,b,0x37f&~clear)
 pairs=[((J,0x3fff),(J,0x3fff)),((J+1,0x3fff),(J,0x3fff)),((J,0x3fff),(J+1,0x3fff)),
  ((J,0x4000),(J,0x3fff)),((J,0x3fff),(J,0x4000)),((J,0x3fff),(J,1)),((J,0x3fff),(1,0)),
  (((1<<64)-1,0x7ffe),(1,0)),((J,0x3fff),(J,0x3fc0)),((J,0x3fff),(J,0x3f7f))]
 for a,b in pairs:
  for sx in (0,0x8000):
   for sy in (0,0x8000):
    for pc in (0,2,3):
     for rc in range(4):
      for clear in (0,1,2,16,32):add((a[0],a[1]|sx),(b[0],b[1]|sy),(0x7f|pc<<8|rc<<10)&~clear)
 for kind in (0,1,2,3,5,6,7,8,9,10,11,12,13,14,15,16):
  for rc in range(4):
   for clear in (0,1,32):add((J,0x4000),(J,0x3fff),(0x37f|rc<<10)&~clear,kind)
 rng=random.Random(0x486f3)
 for _ in range(480):
  pair=[]
  for i in range(2):pair.append((rng.getrandbits(64)|J,rng.choice((0,1,0x3fff,0x7ffe,rng.randrange(1,0x7fff)))|rng.choice((0,0x8000))))
  add(*pair,(rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10)&~rng.choice((0,0,2,16,32)))
 for pc in (0,2,3):
  for rc in range(4):
   for sx in (0,0x8000):
    for sy in (0,0x8000):pick((J,0x4000|sx),(J,0x3fff|sy),(0x7f|pc<<8|rc<<10))
 for rc in range(4):
  for a,b in [((J,0x3fff),(1,0)),((J,0x3fff),(J,1)),(((1<<64)-1,0x7ffe),(1,0))]:
   for clear in (0,2,16,32):pick(a,b,(0x37f|rc<<10)&~clear)
 for a in special[:6]:
  for b in special[:6]:pick(a,b)
 for missing in (1,2,3):
  for clear in (0,1):pick((J,0x4000),(J,0x3fff),0x37f&~clear,4|missing)
 pick((J,0x4000),(J,0x3fff),0x37f,16)
 for a,b in [((J|0x123,0x7fff),(J,0x3fff)),((1,1),(J,0x3fff)),((1,0),(J,0x3fff)),((J,0x3fff),(J|1<<62|0x456,0xffff))]:
  for clear in (0,1,2):pick(a,b,0x37f&~clear)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 mf=[]
 for a,b,cw,kind in [((J,0x4000),(J,0x3fff),0x35f,4),((J,0x3fff),(1,0),0x36f,4),((1,0),(J,0x3fff),0x37d,4),
  ((J|0x123,0x7fff),(J,0x3fff),0x37e,4),((J,0x4000),(J,0x3fff),0x37e,5),((J,0x4000),(J,0x3fff),0x37e,6)]:mf.append(add(a,b,cw,kind))
 return cases,selected,mf
def pack(v,i):
 """Serializes an explicit vector record to the guest's fixed binary table layout."""
 a,b=v['a'],v['b'];return struct.pack('<4HQHQHIQ',v['control'],0,v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);cases,selected,mf=generate()
 for name,vs in [('vectors.json',cases),('guest-vectors.json',selected),('mf-vectors.json',mf)]:(r/name).write_text(json.dumps(vs,indent=2)+'\n',encoding='utf-8',newline='\n')
 s=f'%define VECTOR_COUNT {len(selected)}\n%define MF_COUNT {len(mf)}\n'
 for macro,vs in [('atan_vectors',selected),('atan_mf_vectors',mf)]:
  data=b''.join(pack(v,i) for i,v in enumerate(vs));s+=f'%macro {macro} 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-atan-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected),mfVectors=len(mf))))
