# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""FSINCOS pair/state vectors with independent rounding bits and atomic faults."""
import argparse,json,runpy,struct
from pathlib import Path
J=1<<63
raw=lambda a:dict(significand=f'{a[0]:016X}',exponent=f'{a[1]:04X}')
def generate():
 """Builds deterministic x87 fsincos vectors vectors and original guest data tables; no original media are modified."""
 cases=runpy.run_path(str(Path(__file__).with_name('prepare-x87-tangent-vectors.py')))['generate']()[0]
 for i,v in enumerate(cases):v['id']=f'fsincos-{i}'
 keys={(v['a']['significand'],v['a']['exponent'],v['control'],v['kind']):v for v in cases};selected=[]
 def add(a,cw=0x37f,kind=4):
  """Adds one explicitly parameterized x87 fsincos vectors case to the deterministic test matrix."""
  a=raw(a);key=a['significand'],a['exponent'],cw,kind
  if key not in keys:
   v=dict(id=f'fsincos-{len(cases)}',operation=0,control=cw,kind=kind,a=a,b=raw((J+7,0x4000)),bits='0000000000000000');cases.append(v);keys[key]=v
  return keys[key]
 def pick(*args):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=add(*args)
  if v not in selected:selected.append(v)
 for pc in (0,2,3):
  for rc in range(4):
   for sign in (0,0x8000):
    for clear in (0,32):pick((J,0x3ffe|sign),(0x7f|pc<<8|rc<<10)&~clear)
 for a in [(1,0),(J,0),(J,1)]:
  for rc in range(4):
   for clear in (0,2,16,32):pick(a,(0x37f|rc<<10)&~clear)
 for sign in (0,0x8000):
  for rc in range(4):
   for clear in (0,32):pick((J,0x3ffd|sign),(0x37f|rc<<10)&~clear)
 for a in [(0,0),(0,0x8000),(J,0x7fff),(J,0xffff),(J|1<<62|0x456,0x7fff),(J|0x123,0xffff),(1,1)]:
  for clear in (0,1):pick(a,0x37f&~clear)
 for a in [(J,0x3ffe),(J,0x403e),(J,0x7fff)]:
  for kind in (5,36,37):
   for clear in (0,1):pick(a,0x37f&~clear,kind)
 for exp in (0x3ffe,0x3fff,0x4000):
  for delta in (-1,0,1):
   for rc in range(4):pick((0xc90fdaa22168c235+delta,exp),0x37f|rc<<10)
 for a in [(J,0x403e),(J,0xc03e),((1<<64)-1,0x403d),((1<<64)-1,0xc03d)]:
  for rc in range(4):pick(a,0x37f|rc<<10)
 pick((J,0x3ffe),0x37f,16)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 mf=[add(a,cw,kind) for a,cw,kind in [((J,0x3ffd),0x35f,4),((1,0),0x36f,4),((1,0),0x37d,4),((J,0x7fff),0x37e,4),
  ((J|0x123,0x7fff),0x37e,4),((J,0x3ffe),0x37e,5),((J,0x3ffe),0x37e,37),((J,0x403e),0x37e,36)]]
 return cases,selected,mf
def pack(v,i):
 """Serializes an explicit vector record to the guest's fixed binary table layout."""
 a,b=v['a'],v['b'];return struct.pack('<4HQHQHIQ',v['control'],0,v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);r.mkdir(parents=True,exist_ok=True);vs,guest,mf=generate()
 for name,items in [('vectors.json',vs),('guest-vectors.json',guest),('mf-vectors.json',mf)]:(r/name).write_text(json.dumps(items,indent=2)+'\n',encoding='utf-8',newline='\n')
 s=f'%define VECTOR_COUNT {len(guest)}\n%define MF_COUNT {len(mf)}\n'
 for macro,items in [('fsincos_vectors',guest),('fsincos_mf_vectors',mf)]:
  data=b''.join(pack(v,i) for i,v in enumerate(items));s+=f'%macro {macro} 0\n'+'\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (r/'x87-fsincos-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (s),encoding='ascii',newline='\n');print(json.dumps(dict(vectors=len(vs),guestVectors=len(guest),mfVectors=len(mf))))
