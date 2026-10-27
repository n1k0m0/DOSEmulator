# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent sin/cos intervals: full-pi reduction, direct rational powers, angle doubling."""
import argparse,hashlib,json,runpy
from fractions import Fraction
from functools import lru_cache
from pathlib import Path
base=runpy.run_path(str(Path(__file__).with_name('prepare-x87-logarithm-math.py')))
tan=runpy.run_path(str(Path(__file__).with_name('prepare-x87-tangent-math.py')))
raw,val,num,cls,tag,encode=(base[n] for n in ('raw','val','num','cls','tag','encode'))
J=1<<63
def reduced(z,bits,cosine):
 """Bounds the reduced argument with an independent whole-pi reduction and exact rational arithmetic."""
 if z<0:
  lo,hi=reduced(-z,bits,cosine);return (lo,hi) if cosine else (-hi,-lo)
 halvings=0
 while z>Fraction(1,32):z/=2;halvings+=1
 sl,sh=tan['powers'](z,bits+32,True);cl,ch=tan['powers'](z,bits+32,False);q=1<<(bits+32)
 for _ in range(halvings):
  # cos(2z)=1-2sin(z)^2, sin(2z)=2sin(z)cos(z); inputs remain in [0,pi/2].
  sl,sh,cl,ch=2*sl*cl,2*sh*ch,1-2*sh*sh,1-2*sl*sl
  sl=Fraction(sl.numerator*q//sl.denominator,q);sh=Fraction(-(-sh.numerator*q//sh.denominator),q)
  cl=Fraction(cl.numerator*q//cl.denominator,q);ch=Fraction(-(-ch.numerator*q//ch.denominator),q)
 return (cl,ch) if cosine else (sl,sh)
@lru_cache(None)
def calculate(a,cosine,rc,um):
 """Rounds independently enclosed x87 sincos math results at two precisions and rejects any ambiguous rounding boundary."""
 x=num(a);negative=x<0 and not cosine;x=abs(x);answers=[]
 for bits in (320,448):
  if x<Fraction(1,1<<64):
   lo,hi=(1-x*x/2,1-x*x/2+x**4/24) if cosine else (x-x**3/6,x-x**3/6+x**5/120)
  else:
   pl,ph=tan['atan']['pi_bounds'](bits+80);kl=x//ph;kh=x//pl;assert kl==kh
   lo,hi=x-kl*ph,x-kl*pl;sign=bool(kl&1)
   if lo>ph/2:lo,hi=pl-hi,ph-lo;sign^=cosine
   else:assert hi<pl/2
   if lo>ph/4:
    lo,hi=pl/2-hi,ph/2-lo;op=not cosine
   else:assert hi<pl/4;op=cosine
   if op:lo,hi=reduced(hi,bits,True)[0],reduced(lo,bits,True)[1]
   else:lo,hi=reduced(lo,bits,False)[0],reduced(hi,bits,False)[1]
   if sign:lo,hi=-hi,-lo
  if negative:lo,hi=-hi,-lo
  l=encode(lo,rc,True,um);h=encode(hi,rc,True,um);assert l==h,(a,cosine,rc,um,bits,'rounding boundary')
  answers.append(l)
 assert answers[0]==answers[1]
 return answers[0]
def expected(v):
 """Builds the independent x87 sincos math result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];ca=cls(a);op=bool(v['operation']);commit=True;flags=0;up=False;c2=0;result=a
 if kind&1:flags=0x41;commit=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif ca in ('bad','inf'):flags=1;commit=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif ca=='nan':flags=int(not a[0]&(1<<62));commit=not flags or bool(cw&1);result=(a[0]|1<<62,a[1])
 elif a[1]&0x7fff>=0x403e:commit=False;c2=0x400
 else:
  flags=2 if ca=='denormal' else 0
  if flags and not cw&2:commit=False
  elif ca=='zero':result=(J,0x3fff) if op else a
  else:result,f,up=calculate(a,op,cw>>10&3,bool(cw&16));flags|=f
 cc={0:0x4100,4:0,8:0x100,12:0x4000}[kind&12];sticky=4 if kind&16 else 0
 sw=0x3000|cc|c2|flags|sticky|(0x200 if up else 0)|(0x8080 if (flags|sticky)&~cw&63 else 0)
 physical=[(0,0)]*8;tags=[3]*8;physical[6]=result if commit else a;physical[7]=b
 tags[6]=tag(result) if commit else 3 if kind&1 else tag(a);tags[7]=3 if kind&2 else tag(b)
 if kind&32:tags[5]=1
 return dict(id=v['id'],control=cw,status=sw,tags=sum(t<<(2*i) for i,t in enumerate(tags)),registers=[raw(physical[(6+i)&7]) for i in range(8)],memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);vs=json.loads((r/'vectors.json').read_text())
 out=dict(referenceOnly=True,architecture='rational-sincos-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=[expected(v) for v in vs])
 (r/'math-reference.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedNumbers=calculate.cache_info().currsize,binaryPrecisionCrossCheck=[320,448],piIdentity='4*atan(1) with square-root half angles',exactRationalPowerFactorialRemainders=True,sineCosineDoubleAngle=True,hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8',newline='\n');print(json.dumps(cert))
