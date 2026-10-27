# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""FSIN/FCOS boundaries, quadrants, tiny arguments and preserved stack matrices."""
import argparse,json,random,struct,runpy
from pathlib import Path
J=1<<63
raw=lambda a:dict(significand=f'{a[0]:016X}',exponent=f'{a[1]:04X}')
def generate():
 """Builds deterministic x87 sincos vectors vectors and original guest data tables; no original media are modified."""
 cases=[];keys={};selected=[]
 def add(op,a,cw=0x37f,kind=4,b=(J+7,0x4000)):
  """Adds one explicitly parameterized x87 sincos vectors case to the deterministic test matrix."""
  key=op,a,cw,kind,b
  if key not in keys:
   keys[key]=len(cases);cases.append(dict(id=f'sincos-{len(cases)}',operation=op,control=cw,kind=kind,a=raw(a),b=raw(b),bits='0000000000000000'))
  return cases[keys[key]]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 # Reuse the established raw argument coverage, with separate opcodes/IDs.
 tan=runpy.run_path(str(Path(__file__).with_name('prepare-x87-tangent-vectors.py')))
 for v in tan['generate']()[0]:
  a=(int(v['a']['significand'],16),int(v['a']['exponent'],16))
  for op in (0,1):add(op,a,v['control'],v['kind'])
 for op in (0,1):
  for pc in (0,2,3):
   for rc in range(4):
    for sign in (0,0x8000):
     for clear in (0,32):pick(op,(J,0x3ffe|sign),(0x7f|pc<<8|rc<<10)&~clear)
  for a in [(1,0),(J,1)]:
   for rc in range(4):
    for clear in (0,16):pick(op,a,(0x37f|rc<<10)&~clear)
  for a in [(1,0),(J,0)]:
   for rc in (0,2):pick(op,a,(0x37f|rc<<10)&~2)
  for a in [(J,0x403e),((1<<64)-1,0x403d),(J,0x7fff),(J|1<<62|0x456,0x7fff),(J|0x123,0xffff),(1,1)]:
   for clear in (0,1):pick(op,a,0x37f&~clear)
  for kind in (5,7):
   for clear in (0,1):pick(op,(J,0x3ffe),0x37f&~clear,kind)
  for kind in (2,32,16):pick(op,(J,0x3ffe),0x37f,kind)
  for exp in (0x3ffe,0x3fff,0x4000):
   for delta in (-1,0,1):pick(op,(0xc90fdaa22168c235+delta,exp))
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 mf=[]
 for op,a,cw,kind in [(0,(J,0x3ffe),0x35f,4),(1,(J,0x3ffe),0x35f,4),(0,(1,0),0x36f,4),(0,(1,0),0x37d,4),
  (1,(1,0),0x37d,4),(1,(J,0x7fff),0x37e,4),(0,(J,0x3ffe),0x37e,5),(1,(J,0x3ffe),0x37e,5)]:mf.append(add(op,a,cw,kind))
 return cases,selected,mf
def pack(v,i):
 """Serializes an explicit vector record to the guest's fixed binary table layout."""
 a,b=v['a'],v['b'];return struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);vs,guest,mf=generate()
 for name,items in [('vectors.json',vs),('guest-vectors.json',guest),('mf-vectors.json',mf)]:(r/name).write_text(json.dumps(items,indent=2)+'\n',encoding='utf-8',newline='\n')
 s=f'%define VECTOR_COUNT {len(guest)}\n%define MF_COUNT {len(mf)}\n'
 for macro,items in [('sincos_vectors',guest),('sincos_mf_vectors',mf)]:
  data=b''.join(pack(v,i) for i,v in enumerate(items));s+=f'%macro {macro} 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-sincos-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(vs),guestVectors=len(guest),mfVectors=len(mf))))
