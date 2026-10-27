# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent rational tangent intervals: full-pi reduction, small-angle powers and doubling."""
import argparse,hashlib,json,math,runpy
from fractions import Fraction
from functools import lru_cache
from pathlib import Path
base=runpy.run_path(str(Path(__file__).with_name('prepare-x87-logarithm-math.py')))
atan=runpy.run_path(str(Path(__file__).with_name('prepare-x87-atan-math.py')))
raw,val,num,cls,tag,encode=(base[n] for n in ('raw','val','num','cls','tag','encode'))
J=1<<63
def powers(z,bits,sine):
 # Independent rational powers and factorials; no product recurrence.
 """Accumulates independently evaluated rational powers used by the mathematical series oracle."""
 q=1<<bits;n,d=z.numerator,z.denominator;l=h=0;j=0
 while True:
  k=2*j+int(sine);nn=pow(n,k)*q;dd=pow(d,k)*math.factorial(k);a,rem=divmod(nn,dd);b=a+bool(rem)
  if j&1:l-=b;h-=a
  else:l+=a;h+=b
  j+=1;k=2*j+int(sine)
  if pow(n,k)*q<pow(d,k)*math.factorial(k):return Fraction(l-1,q),Fraction(h+1,q)
def reduced(z,bits):
 """Bounds the reduced argument with an independent whole-pi reduction and exact rational arithmetic."""
 if z<0:
  lo,hi=reduced(-z,bits);return -hi,-lo
 halvings=0
 while z>Fraction(1,32):z/=2;halvings+=1
 sl,sh=powers(z,bits+32,True);cl,ch=powers(z,bits+32,False);lo,hi=sl/ch,sh/cl
 q=1<<(bits+32)
 for _ in range(halvings):
  assert 0<=lo<=hi<1
  lo,hi=2*lo/(1-lo*lo),2*hi/(1-hi*hi)
  lo=Fraction(lo.numerator*q//lo.denominator,q);hi=Fraction(-(-hi.numerator*q//hi.denominator),q)
 return lo,hi
@lru_cache(None)
def calculate(a,rc,um):
 """Rounds independently enclosed x87 tangent math results at two precisions and rejects any ambiguous rounding boundary."""
 x=num(a);negative=x<0;x=abs(x);answers=[]
 for bits in (320,448):
  if x<Fraction(1,1<<64):lo,hi=x+x**3/3,x+x**3
  else:
   pl,ph=atan['pi_bounds'](bits+80)
   # Floor reduction by pi, followed by reflection about pi/2. This is
   # independent of the product's nearest pi/2 and Machin pi identity.
   kl=x//ph;kh=x//pl;assert kl==kh
   lo,hi=x-kl*ph,x-kl*pl
   if lo>ph/2:lo,hi=lo-ph,hi-pl
   else:assert hi<pl/2
   if lo>ph/4:
    rl,rh=reduced(pl/2-hi,bits)[0],reduced(ph/2-lo,bits)[1];lo,hi=1/rh,1/rl
   elif hi< -ph/4:
    rl,rh=reduced(pl/2+lo,bits)[0],reduced(ph/2+hi,bits)[1];lo,hi=-1/rl,-1/rh
   else:lo,hi=reduced(lo,bits)[0],reduced(hi,bits)[1]
  if negative:lo,hi=-hi,-lo
  l=encode(lo,rc,True,um);h=encode(hi,rc,True,um);assert l==h,(a,rc,um,bits,'rounding boundary')
  answers.append(l)
 assert answers[0]==answers[1]
 return answers[0]
def expected(v):
 """Builds the independent x87 tangent math result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];ca=cls(a);push=True;up=False;flags=0;c2=0;result=a;pushed=(J,0x3fff)
 if kind&1 or kind&32:
  flags=0x41;up=not bool(kind&1);push=bool(cw&1);result=pushed=(0xc000000000000000,0xffff)
 elif ca in ('bad','inf'):
  flags=1;push=bool(cw&1);result=pushed=(0xc000000000000000,0xffff)
 elif ca=='nan':
  flags=int(not a[0]&(1<<62));push=not flags or bool(cw&1);result=pushed=(a[0]|1<<62,a[1])
 elif a[1]&0x7fff>=0x403e:push=False;c2=0x400
 else:
  flags=2 if ca=='denormal' else 0
  if flags and not cw&2:push=False
  elif ca!='zero':result,f,up=calculate(a,cw>>10&3,bool(cw&16));flags|=f
 top=5 if push else 6;sticky=4 if kind&16 else 0;cc={0:0x4100,4:0,8:0x100,12:0x4000}[kind&12]
 sw=top<<11|cc|c2|flags|sticky|(0x200 if up else 0)|(0x8080 if (flags|sticky)&~cw&63 else 0)
 physical=[(0,0)]*8;tags=[3]*8;physical[6]=a;physical[7]=b;tags[6]=3 if kind&1 else tag(a);tags[7]=3 if kind&2 else tag(b)
 if kind&32:tags[5]=1
 if push:physical[6]=result;physical[5]=pushed;tags[6]=tag(result);tags[5]=tag(pushed)
 return dict(id=v['id'],control=cw,status=sw,tags=sum(t<<(2*i) for i,t in enumerate(tags)),registers=[raw(physical[(top+i)&7]) for i in range(8)],memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);vs=json.loads((r/'vectors.json').read_text())
 out=dict(referenceOnly=True,architecture='rational-tangent-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=[expected(v) for v in vs])
 (r/'math-reference.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedNumbers=calculate.cache_info().currsize,binaryPrecisionCrossCheck=[320,448],piIdentity='4*atan(1) with square-root half angles',exactRationalPowerFactorialRemainders=True,tangentDoubleAngle=True,hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8',newline='\n');print(json.dumps(cert))
