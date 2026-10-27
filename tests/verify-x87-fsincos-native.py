# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Native state contract and separate small/large argument approximation diagnostics."""
import argparse,json,runpy
from pathlib import Path
J=1<<63
base=runpy.run_path(str(Path(__file__).with_name('prepare-x87-logarithm-math.py')))
val,num,cls=base['val'],base['num'],base['cls']
def ordinal(v):"""Maps raw extended values to adjacent representable steps for diagnostic native-distance comparisons."""; return (max(1,v[1]&0x7fff)-1)*J+v[0]
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory)
 ref=json.loads((r/'native-reference.json').read_text());math=json.loads((r/'math-reference.json').read_text());vs=json.loads((r/'vectors.json').read_text())
 assert ref['referenceOnly'] and ref['vectorsSha256']==math['vectorsSha256'] and len(vs)==len(ref['results'])==len(math['results'])
 small=large=small_diff=large_diff=small_max=c1=large_signs=0;failures=[]
 for v,n,m in zip(vs,ref['results'],math['results']):
  try:
   assert v['id']==n['id']==m['id'] and n['control']==m['control']
   assert n['status']&~0x4300==m['status']&~0x4300,('defined status',n['status'],m['status'])
   assert n['tags']==m['tags'],'tags'
   top=m['status']>>11&7
   for i in range(8):
    if m['tags']>>(((top+i)&7)*2)&3==3:continue
    nv,mv=val(n['registers'][i]),val(m['registers'][i])
    if top==5 and i in (0,1) and not m['status']&0x41:
     delta=abs(ordinal(nv)-ordinal(mv));bounded=abs(num(val(v['a'])))<=1
     if bounded:
      small+=1;small_diff+=bool(delta);small_max=max(small_max,delta)
      assert nv[1]&0x8000==mv[1]&0x8000 and delta<=1,('small-angle approximation',nv,mv,delta)
     else:
      # Finite native pi reduction is not a mathematical large-angle oracle.
      large+=1;large_diff+=bool(delta);large_signs+=bool((nv[1]^mv[1])&0x8000)
    else:assert nv==mv,('preserved/special register',i,nv,mv)
   c1+=bool((n['status']^m['status'])&0x200)
  except AssertionError as e:failures.append(dict(id=v['id'],error=str(e)))
 out=dict(passed=not failures,cases=len(vs),smallComponentsAtMostOneRadian=small,smallNativeValuesDifferent=small_diff,smallMaximumRepresentableSteps=small_max,
          largeArgumentComponentDiagnostics=large,largeNativeValuesDifferent=large_diff,largeNativeSignDifferences=large_signs,nativeC1DifferentFromMathematicalRounding=c1,
          fullRangeNativeAccuracyClaimed=False,strictIndependentMathForAllCases=True,nativeReferenceUsedForExecution=False,referenceHostIsPhysical486=False,failed=len(failures),failures=failures)
 (r/'native-comparison.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({**out,'failures':failures[:20]},indent=2));raise SystemExit(bool(failures))
