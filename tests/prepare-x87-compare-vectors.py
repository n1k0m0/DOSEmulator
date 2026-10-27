# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent raw comparison inputs; no host floating point or guest media."""
import argparse,json,random,struct
from pathlib import Path
J=1<<63
VALUES=[(0,0),(0,0x8000),(J,0x3fff),(J,0xbfff),(J+1,0x3fff),(J,1),((1<<64)-1,0x7ffe),
        (1,0),(J,0),(J,0x7fff),(J,0xffff),(0xc000000000000042,0x7fff),(J+0x123,0x7fff),(1,0x1234)]
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')
def generate():
 """Builds deterministic x87 compare vectors vectors and original guest data tables; no original media are modified."""
 cases=[]
 def add(op,c,a,b,kind=0,bits=0):"""Adds one explicitly parameterized x87 compare vectors case to the deterministic test matrix."""; cases.append(dict(id=f'cmp-{len(cases)}',operation=op,control=c,a=raw(a),b=raw(b),kind=kind,bits=f'{bits:016X}'))
 for c in (0x37f,0x37e,0x37d,0x37c):
  for a in VALUES:
   for b in VALUES:
    for op in range(7):add(op,c,a,b)
  for kind in (1,2,3):
   for op in range(7):add(op,c,VALUES[2],VALUES[3],kind)
  for kind in (1,2,3):
   for op in range(7,15):add(op,c,VALUES[2],VALUES[3],kind)
  for a in VALUES:
   for op in range(7,15):
    vals=([0,0x3f800000,1,0x7f800000,0x7fc00123,0x7f800123] if op<9 else
          [0,0x3ff0000000000000,1,0x7ff0000000000000,0x7ff8000000000123,0x7ff0000000000123] if op<11 else
          [0,1,0x7fffffff,0x80000000,0xffffffff] if op<13 else [0,1,32767,32768,65535])
    for bits in vals:add(op,c,a,VALUES[2],0,bits)
 for c in (0x37f,0x37e):
  for seed in (4,8,12):
   for op in range(7):
    for a in (VALUES[11],VALUES[12],VALUES[13]):add(op,c,a,VALUES[2],seed)
    for empty in (1,2,3):add(op,c,VALUES[2],VALUES[3],empty|seed)
   for op in range(7,11):
    for bits in ((0x7fc00123,0x7f800123) if op<9 else (0x7ff8000000000123,0x7ff0000000000123)):add(op,c,VALUES[2],VALUES[2],seed,bits)
 for c in (0x7f,0x27f,0x77f,0xb7f,0xf7f):
  for op in range(7):
   for a in VALUES:add(op,c,a,VALUES[2])
 return cases
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();root=Path(a.directory);root.mkdir(parents=True,exist_ok=True)
 cases=generate();(root/'vectors.json').write_text(json.dumps(cases,indent=2)+'\n',encoding='utf-8')
 chosen=[]
 def pick(op,c,a,b,kind=0,bits=0):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  match=next(v for v in cases if v['operation']==op and v['control']==c and v['a']==raw(a) and v['b']==raw(b) and v['kind']==kind and int(v['bits'],16)==bits)
  if match not in chosen:chosen.append(match)
 for op in range(7):
  for a,b in ((VALUES[0],VALUES[2]),(VALUES[2],VALUES[2]),(VALUES[4],VALUES[2]),(VALUES[3],VALUES[2]),(VALUES[1],VALUES[0]),(VALUES[10],VALUES[9])):pick(op,0x37f,a,b)
  for c in (0x37f,0x37e):
   for a in (VALUES[11],VALUES[12]):pick(op,c,a,VALUES[2],4 if c==0x37e else 8)
   for kind in (1,2,3):pick(op,c,VALUES[2],VALUES[3],kind|(12 if c==0x37e else 8))
  for c in (0x37f,0x37d):
   for a,b in ((VALUES[2],VALUES[7]),(VALUES[7],VALUES[2])):pick(op,c,a,b)
 for op in range(7,15):
  for bits in (0,1,0xffffffff if op<13 else 65535):
   # Real formats use a finite negative operand rather than an all-one NaN.
   if op<9 and bits==0xffffffff:bits=0xbf800000
   if 9<=op<11 and bits==0xffffffff:bits=0xbff0000000000000
   if not any(v['operation']==op and int(v['bits'],16)==bits for v in cases):continue
   pick(op,0x37f,VALUES[2],VALUES[2],bits=bits)
  for c in (0x37f,0x37e):pick(op,c,VALUES[2],VALUES[3],1)
  if op<11:
   for c in (0x37f,0x37e):
    for bits in ((0x7fc00123,0x7f800123) if op<9 else (0x7ff8000000000123,0x7ff0000000000123)):pick(op,c,VALUES[2],VALUES[2],4 if c==0x37e else 8,bits=bits)
   pick(op,0x37d,VALUES[2],VALUES[2],bits=1)
 for c in (0x7f,0x27f,0x77f,0xb7f,0xf7f):
  for op in (0,6):pick(op,c,VALUES[4],VALUES[2])
 assert len(chosen)<=220,len(chosen)
 rng=random.Random(0x486c0)
 while len(chosen)<220:
  v=rng.choice(cases)
  if v not in chosen:chosen.append(v)
 (root/'guest-vectors.json').write_text(json.dumps(chosen,indent=2)+'\n',encoding='utf-8')
 blocks=[]
 for i,v in enumerate(chosen):
  av,bv=v['a'],v['b'];blocks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(av['significand'],16),int(av['exponent'],16),int(bv['significand'],16),int(bv['exponent'],16),0,int(v['bits'],16)))
 data=b''.join(blocks);source=f'%define VECTOR_COUNT {len(chosen)}\n%macro compare_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-compare-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (source),encoding='ascii');print(json.dumps(dict(vectors=len(cases),guestVectors=len(chosen))))
