# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""FPTAN: reduction boundaries, precision/rounding, two destinations and faults."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
def raw(a):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{a[0]:016X}',exponent=f'{a[1]:04X}')
def generate():
 """Builds deterministic x87 tangent vectors vectors and original guest data tables; no original media are modified."""
 cases=[];keys={};selected=[]
 def add(a,cw=0x37f,kind=4,b=(J+7,0x4000)):
  """Adds one explicitly parameterized x87 tangent vectors case to the deterministic test matrix."""
  key=a,cw,kind,b
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'tangent-{len(cases)}',operation=0,control=cw,kind=kind,a=raw(a),b=raw(b),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 special=[(0,0),(0,0x8000),(J,0x7fff),(J,0xffff),(J|1<<62|0x456,0x7fff),(J|0x123,0xffff),(1,1),(1,0),(J-1,0),(J,0),(J,1)]
 values=[(J,0x3ffe),(J,0x3fff),(J+1,0x3fff),(J,0x3fbf),(J,0x3fbe),(J,0x403e),(J+1,0x403e),((1<<64)-1,0x403d)]
 # Adjacent 80-bit arguments around pi/4, pi/2, pi and large reductions.
 for sig,exp in [(0xc90fdaa22168c235,0x3ffe),(0xc90fdaa22168c235,0x3fff),(0xc90fdaa22168c235,0x4000),(0xabcdef0123456789,0x403d)]:
  values += [(sig+delta,exp) for delta in (-1,0,1)]
 for a in special+values:
  for sign in (0,0x8000):
   for pc in (0,2,3):
    for rc in range(4):
     for clear in (0,1,2,16,32):add((a[0],a[1]|sign),(0x7f|pc<<8|rc<<10)&~clear)
 for a in special+values[:8]:
  for kind in (0,1,2,3,5,6,7,8,12,16,36,37):
   for clear in (0,1,2):add(a,0x37f&~clear,kind)
 rng=random.Random(0x486d9f2)
 for _ in range(600):
  add((rng.getrandbits(64)|J,rng.choice((1,0x3fbf,0x3ffe,0x3fff,0x403d,0x403e,rng.randrange(1,0x403f)))|rng.choice((0,0x8000))),
      (rng.choice((0x7f,0x27f,0x37f))|rng.randrange(4)<<10)&~rng.choice((0,0,2,16,32)))
 for pc in (0,2,3):
  for rc in range(4):
   for sign in (0,0x8000):
    for clear in (0,32):pick((J,0x3ffe|sign),(0x7f|pc<<8|rc<<10)&~clear)
 for a in [(1,0),(J,0),(J,1),(J,0x403e),((1<<64)-1,0x403d)]:
  for rc in range(4):
   for clear in (0,2,16,32):pick(a,(0x37f|rc<<10)&~clear)
 for a in special[:7]:
  for clear in (0,1):pick(a,0x37f&~clear)
 for a in [values[0],(J,0x403e),(J,0x7fff)]:
  for kind in (5,36,37):
   for clear in (0,1):pick(a,0x37f&~clear,kind)
 for a in values[8:]:pick(a)
 pick((J,0x3ffe),0x37f,16)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 mf=[]
 for a,cw,kind in [((J,0x3ffe),0x35f,4),((1,0),0x36f,4),((1,0),0x37d,4),((J,0x7fff),0x37e,4),((J,0x3ffe),0x37e,5),((J,0x403e),0x37e,36)]:mf.append(add(a,cw,kind))
 return cases,selected,mf
def pack(v,i):
 """Serializes an explicit vector record to the guest's fixed binary table layout."""
 a,b=v['a'],v['b'];return struct.pack('<4HQHQHIQ',v['control'],0,v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);cases,selected,mf=generate()
 for name,vs in [('vectors.json',cases),('guest-vectors.json',selected),('mf-vectors.json',mf)]:(r/name).write_text(json.dumps(vs,indent=2)+'\n',encoding='utf-8',newline='\n')
 s=f'%define VECTOR_COUNT {len(selected)}\n%define MF_COUNT {len(mf)}\n'
 for macro,vs in [('tangent_vectors',selected),('tangent_mf_vectors',mf)]:
  data=b''.join(pack(v,i) for i,v in enumerate(vs));s+=f'%macro {macro} 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-tangent-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected),mfVectors=len(mf))))
