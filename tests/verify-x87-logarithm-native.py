# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Separate native approximation comparison; mathematical values remain the strict oracle."""
import argparse,json
from pathlib import Path
J=1<<63
def val(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def ordinal(v):"""Maps raw extended values to adjacent representable steps for diagnostic native-distance comparisons."""; return (max(1,v[1]&0x7fff)-1)*J+v[0]
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory)
 ref=json.loads((r/'native-reference.json').read_text());math=json.loads((r/'math-reference.json').read_text());vs=json.loads((r/'vectors.json').read_text())
 assert ref['referenceOnly'] and ref['vectorsSha256']==math['vectorsSha256'] and len(vs)==len(ref['results'])==len(math['results'])
 different=c1different=maximum=0;failures=[]
 for v,n,m in zip(vs,ref['results'],math['results']):
  try:
   assert n['id']==m['id']==v['id'] and n['control']==m['control']
   assert n['status']&~0x4700==m['status']&~0x4700,('flags',hex(n['status']),hex(m['status']))
   top=m['status']>>11&7
   for i in range(8):
    if m['tags']>>(((top+i)&7)*2)&3==3:continue
    nv,mv=val(n['registers'][i]),val(m['registers'][i])
    if i==0 and top==7 and (nv[1]&0x7fff)!=0x7fff and (mv[1]&0x7fff)!=0x7fff:
     assert nv[1]&0x8000==mv[1]&0x8000,'sign'
     delta=abs(ordinal(nv)-ordinal(mv));maximum=max(maximum,delta);different+=bool(delta)
     assert delta<=1,('approximation exceeds one representable step',nv,mv,delta)
    else:assert nv==mv,('preserved/special register',i,nv,mv)
   assert n['tags']==m['tags'] or top==7 and n['tags']&0x3fff==m['tags']&0x3fff,'tags'
   c1different+=bool((n['status']^m['status'])&0x200)
  except AssertionError as e:failures.append(dict(id=v['id'],error=str(e)))
 out=dict(passed=not failures,cases=len(vs),nativeValuesDifferentFromCorrectRounding=different,nativeC1DifferentFromMathematicalRounding=c1different,maximumRepresentableSteps=maximum,nativeReferenceUsedForExecution=False,referenceHostIsPhysical486=False,failed=len(failures),failures=failures)
 (r/'native-comparison.json').write_text(json.dumps(out,indent=2)+'\n')
 print(json.dumps({**out,'failures':failures[:20]},indent=2));raise SystemExit(bool(failures))
