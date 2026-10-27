# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent exact-rational atan oracle: sqrt half-angle reduction, alternating remainder bounds."""
import argparse,hashlib,json,math,runpy
from fractions import Fraction
from functools import lru_cache
from pathlib import Path
base=runpy.run_path(str(Path(__file__).with_name('prepare-x87-logarithm-math.py')))
J=1<<63
raw,val,num,cls,tag,encode=(base[n] for n in ('raw','val','num','cls','tag','encode'))

def sqrt_bounds(x,bits):
 """Bounds a rational square root with integer inequalities at the requested precision."""
 q=1<<bits;n=math.isqrt(x.numerator*q*q//x.denominator)
 return Fraction(n,q),Fraction(n if n*n*x.denominator==x.numerator*q*q else n+1,q)
def series_bounds(x,bits):
 # Evaluate each rational power independently, bounding the integer division
 # loss in both directions. This avoids expensive gcds of huge partial sums.
 """Bounds the angle series with exact rational terms and a signed alternating remainder."""
 q=1<<bits;n,d=x.numerator,x.denominator;pn,pd=n,d;low=high=0;j=0
 while True:
  term,rem=divmod(q*pn,(2*j+1)*pd);upper=term+bool(rem)
  if j&1:low-=upper;high-=term
  else:low+=term;high+=upper
  j+=1;pn*=n*n;pd*=d*d
  if q*pn<(2*j+1)*pd:return Fraction(low-1,q),Fraction(high+1,q)
@lru_cache(None)
def atan_bounds(x,bits):
 """Bounds the angle using rational half-angle reduction and an alternating-series remainder."""
 assert 0<x<=1
 if x<=Fraction(1,1<<64):return x-x**3/3,x-x**3/3+x**5/5
 lo=hi=x;doublings=0
 while hi>Fraction(1,16):
  _,sl=sqrt_bounds(1+lo*lo,bits+32);sh,_=sqrt_bounds(1+hi*hi,bits+32)
  lo,hi=lo/(1+sl),hi/(1+sh);doublings+=1
  q=1<<(bits+32)
  lo=Fraction(lo.numerator*q//lo.denominator,q);hi=Fraction((hi.numerator*q+hi.denominator-1)//hi.denominator,q)
 l,_=series_bounds(lo,bits+32);_,h=series_bounds(hi,bits+32)
 return l*(1<<doublings),h*(1<<doublings)
@lru_cache(None)
def pi_bounds(bits):
 """Bounds pi independently through atan(1), half-angle reduction and rational square-root bounds."""
 l,h=atan_bounds(Fraction(1),bits);return 4*l,4*h
@lru_cache(None)
def calculate(a,b,rc,um):
 """Rounds independently enclosed x87 atan math results at two precisions and rejects any ambiguous rounding boundary."""
 ca,cb=cls(a),cls(b);xneg=bool(a[1]&0x8000);yneg=bool(b[1]&0x8000);answers=[]
 for bits in (256,384):
  pl,ph=pi_bounds(bits)
  if cb=='zero' or ca=='inf' and cb!='inf':lo,hi=(pl,ph) if xneg else (Fraction(0),Fraction(0))
  elif ca=='zero' or cb=='inf' and ca!='inf':lo,hi=pl/2,ph/2
  elif ca==cb=='inf':lo,hi=(3*pl/4,3*ph/4) if xneg else (pl/4,ph/4)
  else:
   ratio=abs(num(b)/num(a));swapped=ratio>1
   if swapped:ratio=1/ratio
   lo,hi=atan_bounds(ratio,bits)
   if swapped:lo,hi=pl/2-hi,ph/2-lo
   if xneg:lo,hi=pl-hi,ph-lo
  if not lo and not hi:return ((0,0x8000 if yneg else 0),0,False)
  if yneg:lo,hi=-hi,-lo
  l=encode(lo,rc,True,um);h=encode(hi,rc,True,um);assert l==h,(a,b,rc,um,bits,'rounding boundary')
  answers.append(l)
 assert answers[0]==answers[1]
 return answers[0]
def expected(v):
 """Builds the independent x87 atan math result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];ca,cb=cls(a),cls(b);pop=True;up=False;result=b;flags=0
 if kind&3:flags=0x41;pop=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif 'bad' in (ca,cb):flags=1;pop=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif 'nan' in (ca,cb):
  ns=[x for x,c in ((a,ca),(b,cb)) if c=='nan'];quiet=[x for x in ns if x[0]&(1<<62)];chosen=max(quiet or ns,key=lambda x:(x[0],not bool(x[1]&0x8000)))
  flags=int(any(not x[0]&(1<<62) for x in ns));pop=not flags or bool(cw&1);result=(chosen[0]|1<<62,chosen[1])
 else:
  flags=2 if 'denormal' in (ca,cb) else 0
  if flags and not cw&2:pop=False
  else:result,f,up=calculate(a,b,cw>>10&3,bool(cw&16));flags|=f
 sticky=4 if kind&16 else 0;cc={0:0x4500,4:0,8:0x100,12:0x4000}[kind&12]
 sw=(0x3800 if pop else 0x3000)|cc|flags|sticky|(0x200 if up else 0)|(0x8080 if (flags|sticky)&~cw&63 else 0)
 if pop:tw=0x3fff|tag(result)<<14;regs=[raw(result)]+[raw((0,0)) for _ in range(7)]
 else:tw=0xfff|(3 if kind&1 else tag(a))<<12|(3 if kind&2 else tag(b))<<14;regs=[raw(a),raw(b)]+[raw((0,0)) for _ in range(6)]
 return dict(id=v['id'],control=cw,status=sw,tags=tw,registers=regs,memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);vs=json.loads((r/'vectors.json').read_text())
 out=dict(referenceOnly=True,architecture='rational-atan-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=[expected(v) for v in vs])
 (r/'math-reference.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedNumbers=calculate.cache_info().currsize,binaryPrecisionCrossCheck=[256,384],halfAngleSquareRoots=True,exactFractionAlternatingRemainders=True,piIdentity='4*atan(1)',hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8',newline='\n');print(json.dumps(cert))
