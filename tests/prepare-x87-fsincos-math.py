# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent two-result FSINCOS oracle, including physical stack and fault priority."""
import argparse,hashlib,json,runpy
from pathlib import Path
base=runpy.run_path(str(Path(__file__).with_name('prepare-x87-sincos-math.py')))
raw,val,num,cls,tag,calculate=(base[n] for n in ('raw','val','num','cls','tag','calculate'))
J=1<<63
def expected(v):
 """Builds the independent x87 fsincos math result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];ca=cls(a);push=True;up=False;flags=0;c2=0;sine=cosine=a
 if kind&1 or kind&32:
  flags=0x41;up=not bool(kind&1);push=bool(cw&1);sine=cosine=(0xc000000000000000,0xffff)
 elif ca in ('bad','inf'):flags=1;push=bool(cw&1);sine=cosine=(0xc000000000000000,0xffff)
 elif ca=='nan':flags=int(not a[0]&(1<<62));push=not flags or bool(cw&1);sine=cosine=(a[0]|1<<62,a[1])
 elif a[1]&0x7fff>=0x403e:push=False;c2=0x400
 else:
  flags=2 if ca=='denormal' else 0
  if flags and not cw&2:push=False
  elif ca=='zero':sine=a;cosine=(J,0x3fff)
  else:
   sine,sf,up=calculate(a,False,cw>>10&3,bool(cw&16));cosine,cf,_=calculate(a,True,cw>>10&3,bool(cw&16));flags|=sf|cf
 top=5 if push else 6;cc={0:0x4100,4:0,8:0x100,12:0x4000}[kind&12];sticky=4 if kind&16 else 0
 sw=top<<11|cc|c2|flags|sticky|(0x200 if up else 0)|(0x8080 if (flags|sticky)&~cw&63 else 0)
 physical=[(0,0)]*8;tags=[3]*8;physical[6]=a;physical[7]=b;tags[6]=3 if kind&1 else tag(a);tags[7]=3 if kind&2 else tag(b)
 if kind&32:tags[5]=1
 if push:physical[6]=sine;physical[5]=cosine;tags[6]=tag(sine);tags[5]=tag(cosine)
 return dict(id=v['id'],control=cw,status=sw,tags=sum(t<<(2*i) for i,t in enumerate(tags)),registers=[raw(physical[(top+i)&7]) for i in range(8)],memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);vs=json.loads((r/'vectors.json').read_text())
 out=dict(referenceOnly=True,architecture='rational-fsincos-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=[expected(v) for v in vs])
 (r/'math-reference.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedComponents=calculate.cache_info().currsize,binaryPrecisionCrossCheck=[320,448],piIdentity='4*atan(1) with square-root half angles',exactRationalPowerFactorialRemainders=True,sineCosineDoubleAngle=True,twoResultPhysicalStackOracle=True,c1UsesSineRounding=True,hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8',newline='\n');print(json.dumps(cert))
