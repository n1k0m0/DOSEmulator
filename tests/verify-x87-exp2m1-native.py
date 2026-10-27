# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Compare actual native approximations with the independent mathematical reference."""
import argparse,json
from pathlib import Path
J=1<<63
def val(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def ordinal(v):"""Maps raw extended values to adjacent representable steps for diagnostic native-distance comparisons."""; return (max(1,v[1]&0x7fff)-1)*J+v[0]
def finite(v):"""Builds a controlled finite raw extended operand from explicit sign, exponent and significand fields."""; return (v[1]&0x7fff)!=0x7fff
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory)
 ref=json.loads((r/'native-reference.json').read_text(encoding='utf-8'));math=json.loads((r/'math-reference.json').read_text(encoding='utf-8'));vs=json.loads((r/'vectors.json').read_text(encoding='utf-8'))
 assert ref['referenceOnly'] and ref['vectorsSha256']==math['vectorsSha256'] and len(vs)==len(ref['results'])==len(math['results'])
 different=0;c1different=0;maximum=0
 for v,n,m in zip(vs,ref['results'],math['results']):
  assert n['id']==m['id']==v['id'] and n['control']==m['control']
  assert n['status']&~0x4700==m['status']&~0x4700,(v['id'],'flags',hex(n['status']),hex(m['status']))
  assert n['tags']&~0x3000==m['tags']&~0x3000
  for i in range(1,8):
   if n['tags']>>(((6+i)&7)*2)&3!=3:assert val(n['registers'][i])==val(m['registers'][i])
  if n['tags']>>12&3!=3:
   nv,mv=val(n['registers'][0]),val(m['registers'][0])
   if finite(nv) and finite(mv):
    assert (nv[1]&0x8000)==(mv[1]&0x8000),(v['id'],'sign')
    delta=abs(ordinal(nv)-ordinal(mv));assert delta<=1,(v['id'],'native approximation > one representable step',nv,mv,delta)
    maximum=max(maximum,delta);different+=bool(delta)
   else:assert nv==mv
  c1different+=bool((n['status']^m['status'])&0x200)
 print(json.dumps(dict(passed=True,cases=len(vs),nativeValuesDifferentFromCorrectRounding=different,nativeC1DifferentFromMathematicalRounding=c1different,maximumRepresentableSteps=maximum,nativeReferenceUsedForExecution=False,referenceHostIsPhysical486=False)))
