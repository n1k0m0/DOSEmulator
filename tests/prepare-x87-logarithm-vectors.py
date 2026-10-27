# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""FYL2X/FYL2XP1 inputs; finite XP1 inputs stay inside the documented range."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')
def generate():
 """Builds deterministic x87 logarithm vectors vectors and original guest data tables; no original media are modified."""
 cases=[];keys={};selected=[]
 def add(op,a,b=(J+0x1234,0x4003),cw=0x37f,kind=4):
  """Adds one explicitly parameterized x87 logarithm vectors case to the deterministic test matrix."""
  key=op,a,b,cw,kind
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'logarithm-{len(cases)}',operation=op,control=cw,kind=kind,a=raw(a),b=raw(b),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 nan=[(J|0x123,0x7fff),(J|1<<62|0x456,0xffff),(1,1)]
 xs=[[(0,0),(0,0x8000),(J,0x3fff),(J+1,0x3fff),(J-1,0x3ffe),(J,0x3ffe),(J,0x4000),(0xc000000000000000,0x3fff),
      (1,0),(J,0),(J+1,0),(J,1),(J,0x7ffe),((1<<64)-1,0x7ffe),(J,0xbfff),(J,0x7fff),(J,0xffff)]+nan,
     [(0,0),(0,0x8000),(J,0x3ffd),(J,0xbffd),(J+1,0x3ffd),(J+1,0xbffd),(0xd413cccfe7799211,0x3ffd),(0x95f619980c4336f7,0xbffd),
      (1,0),(1,0x8000),(J,0),(J,0x8000),(J,1),(J,0x8001),(J,0x3fc0),(J,0xbfc0)]+nan]
 ys=[(0,0),(0,0x8000),(J,0x3fff),(J+1,0xc003),(1,0),(J,0),(J,1),((1<<64)-1,0x7ffe),(J,0x7fff),(J,0xffff)]+nan
 for op in (0,1):
  for a in xs[op]:
   for b in ys:
    for clear in (0,1,2,4,8,16,32):add(op,a,b,0x37f&~clear)
  for rc in range(4):
   for pc in (0,2,3):
    for a in xs[op]:
     for b in ((J,0x3fff),(1,0),((1<<64)-1,0x7ffe)):add(op,a,b,0x7f|pc<<8|rc<<10)
  for kind in (0,1,2,3,5,6,7,8,9,10,11,12,13,14,15,16):
   for rc in range(4):
    for clear in (0,1,32):add(op,(J,0x3ffd),(J+0x1234,0x4003),(0x37f|rc<<10)&~clear,kind)
 rng=random.Random(0x486f1f9)
 for op in (0,1):
  for i in range(420):
   e=rng.choice((0,1,2,0x3ffc if op else 0x3ffd,0x3fc0,rng.randrange(3,0x3ffd if op else 0x7fff)))
   a=(rng.getrandbits(64)|J,e|(rng.choice((0,0x8000)) if op else 0))
   b=(rng.getrandbits(64)|J,rng.choice((0,1,0x3fff,0x7ffe,rng.randrange(1,0x7fff)))|rng.choice((0,0x8000)))
   add(op,a,b,(rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10)&~rng.choice((0,0,2,8,16,32)))
 for op in (0,1):
  for pc in (0,2,3):
   for rc in range(4):
    for a,b in (((J,0x3ffd),(J+0x1234,0x4003)),((J,0x3ffd),(1,0))):
     for clear in (0,32):pick(op,a,b,(0x7f|pc<<8|rc<<10)&~clear)
  for a,b,clear,kind in [((1,0),(J,0x3fff),2,4),((J,0x3ffd),(J,0x3fff),1,1),((J,0x3ffd),(J,0x3fff),0,1),
                         ((J,0x3ffd),(J,0x3fff),1,2),((J,0x3ffd),(J,0x3fff),0,2),((0,0),(J,0x7fff),1,4),
                         ((J|0x123,0x7fff),(J,0x3fff),1,4),((J|0x123,0x7fff),(J,0x3fff),0,4)]:pick(op,a,b,0x37f&~clear,kind)
  for rc in range(4):
   for a,b,clear in [((J,0x3ffd),(J,1),16),((J,0x3ffd),(J,1),0),((1,0),(J,0x3fff),0)]:pick(op,a,b,(0x37f|rc<<10)&~clear)
 for a,b,clear in [((J,0x4001),((1<<64)-1,0x7ffe),8),((J,0x4001),((1<<64)-1,0x7ffe),0),((0,0),(J,0x3fff),4),((0,0),(J,0x3fff),0),((J,0xbfff),(J,0x3fff),1),((J,0xbfff),(J,0x3fff),0)]:pick(0,a,b,0x37f&~clear)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 mf=[]
 for op,a,b,cw,kind in [(0,(0xc000000000000000,0x3fff),(J,0x3fff),0x35f,4),(1,(J,0x3ffd),(J,0x3fff),0x35f,4),
   (0,(J,0x4001),((1<<64)-1,0x7ffe),0x377,4),(1,(J,0x3ffd),(J,1),0x36f,4),(1,(1,0),(J,0x3fff),0x37d,4),
   (0,(0,0),(J,0x3fff),0x37b,4),(0,(J,0xbfff),(J,0x3fff),0x37e,4),(1,(J,0x3ffd),(J,0x3fff),0x37e,5),(0,(J,0x3ffd),(J,0x3fff),0x37e,6)]:mf.append(add(op,a,b,cw,kind))
 return cases,selected,mf
def pack(v,i):
 """Serializes an explicit vector record to the guest's fixed binary table layout."""
 a,b=v['a'],v['b'];return struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);cases,selected,mf=generate()
 for name,vs in [('vectors.json',cases),('guest-vectors.json',selected),('mf-vectors.json',mf)]:(r/name).write_text(json.dumps(vs,indent=2)+'\n',encoding='utf-8',newline='\n')
 s=f'%define VECTOR_COUNT {len(selected)}\n%define MF_COUNT {len(mf)}\n'
 for macro,vs in [('logarithm_vectors',selected),('logarithm_mf_vectors',mf)]:
  data=b''.join(pack(v,i) for i,v in enumerate(vs));s+=f'%macro {macro} 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-logarithm-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected),mfVectors=len(mf))))
