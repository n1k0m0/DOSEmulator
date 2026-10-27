# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent Decimal ln enclosures and exact Fraction multiplication/80-bit rounding."""
import argparse,json,hashlib
from decimal import Decimal,localcontext,ROUND_FLOOR,ROUND_CEILING
from fractions import Fraction
from functools import lru_cache
from pathlib import Path
J=1<<63
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')
def val(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def power(k):"""Constructs an exact positive or negative power of two for rational arithmetic."""; return Fraction(1<<max(k,0),1<<max(-k,0))
def num(v):"""Reconstructs the exact signed rational represented by a finite extended operand."""; return (-1 if v[1]&0x8000 else 1)*v[0]*power(max(1,v[1]&0x7fff)-16383-63)
def cls(v):
 """Classifies raw extended values without converting them through host floating point."""
 s,e=v;e&=0x7fff
 return 'bad' if e and not s&J else 'nan' if e==0x7fff and s>J else 'inf' if e==0x7fff else 'zero' if not s else 'denormal' if not e else 'finite'
def tag(v):"""Derives an x87 tag from the raw value rather than trusting the saved tag word."""; return 1 if cls(v)=='zero' else 0 if cls(v)=='finite' else 2
def exponent(x):
 """Finds the exact floor binary exponent using integer/rational comparisons."""
 k=x.numerator.bit_length()-x.denominator.bit_length()
 if x<power(k):k-=1
 return k
def rnd(x,rc,negative):
 """Applies the requested explicit integer rounding mode, including nearest-even ties."""
 q,r=divmod(x.numerator,x.denominator);up=bool(r) and (rc==0 and (2*r>x.denominator or 2*r==x.denominator and q&1) or rc==1 and negative or rc==2 and not negative)
 return q+up,bool(up)
def encode(value,rc,om,um):
 """Rounds an exact or bounded rational to the reference extended format with explicit RC and exception responses."""
 negative=value<0;x=abs(value);e=exponent(x);q,up=rnd(x/power(e-63),rc,negative)
 if q==1<<64:q>>=1;e+=1
 flags=32
 if e>16383:
  flags|=8
  if not om:e-=24576
  else:
   infinite=rc==0 or rc==1 and negative or rc==2 and not negative
   return (J if infinite else (1<<64)-1,(0x7fff if infinite else 0x7ffe)|(0x8000 if negative else 0)),flags,bool(infinite)
 if e< -16382:
  flags|=16
  if not um:e+=24576
  else:
   q,up=rnd(x/power(-16445),rc,negative)
   return (q,(1 if q==J else 0)|(0x8000 if negative else 0)),flags,up
 return (q,e+16383|(0x8000 if negative else 0)),flags,up

def enclosure(a,b,op,precision):
 """Computes independent lower/upper mathematical bounds at the requested Decimal precision."""
 x=num(a);y=num(b);z=1+x if op else x
 with localcontext() as c:
  c.prec=precision;c.Emin=-40000;c.Emax=40000
  log=c.ln(Decimal(2));ll=Fraction(c.next_minus(log));lh=Fraction(c.next_plus(log))
  if op and abs(x)<power(-128):
   # Integral ln(1+x)/x = integral_0^1 1/(1+t*x) dt.
   # For |x|<=1/2 its distance from 1 is <=|x|.
   lo=(1-abs(x))/lh;hi=(1+abs(x))/ll
   factor=x*y
  else:
   c.rounding=ROUND_FLOOR;zl=c.divide(Decimal(z.numerator),Decimal(z.denominator))
   c.rounding=ROUND_CEILING;zh=c.divide(Decimal(z.numerator),Decimal(z.denominator))
   nl=Fraction(c.next_minus(c.ln(zl)));nh=Fraction(c.next_plus(c.ln(zh)))
   lo=nl/(ll if z<1 else lh);hi=nh/(lh if z<1 else ll);factor=y
  return (lo*factor,hi*factor) if factor>0 else (hi*factor,lo*factor)

@lru_cache(None)
def calculate(a,b,op,rc,om,um):
 """Rounds independently enclosed x87 logarithm math results at two precisions and rejects any ambiguous rounding boundary."""
 z=1+num(a) if op else num(a)
 if z.numerator&(z.numerator-1)==0 and z.denominator&(z.denominator-1)==0:
  k=z.numerator.bit_length()-z.denominator.bit_length()
  return encode(k*num(b),rc,om,um)
 answers=[]
 for p in (100,160):
  lo,hi=enclosure(a,b,op,p);l=encode(lo,rc,om,um);h=encode(hi,rc,om,um)
  assert l==h,(a,b,op,rc,om,um,p,'rounding boundary')
  answers.append(l)
 assert answers[0]==answers[1]
 return answers[0]

def expected(v):
 """Builds the independent x87 logarithm math result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];op=v['operation'];ca,cb=cls(a),cls(b)
 flags=0;up=False;result=b;pop=True
 if kind&3:
  flags=0x41;pop=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif 'bad' in (ca,cb):flags=1;pop=bool(cw&1);result=(0xc000000000000000,0xffff)
 elif 'nan' in (ca,cb):
  ns=[x for x,c in ((a,ca),(b,cb)) if c=='nan'];quiet=[x for x in ns if x[0]&(1<<62)];chosen=max(quiet or ns,key=lambda x:(x[0],not bool(x[1]&0x8000)))
  flags=int(any(not x[0]&(1<<62) for x in ns));pop=not flags or bool(cw&1);result=(chosen[0]|1<<62,chosen[1])
 else:
  z=None if ca=='inf' else 1+num(a) if op else num(a)
  zeroLog=(ca=='zero') if op else z==1
  invalid=bool(ca=='inf' and a[1]&0x8000) or z is not None and z<0 or z==0 and cb=='zero' or zeroLog and cb=='inf' or ca=='inf' and cb=='zero'
  negativeLog=bool(a[1]&0x8000) if op else z is not None and z<1
  negative=negativeLog ^ bool(b[1]&0x8000)
  if invalid:flags=1;pop=bool(cw&1);result=(0xc000000000000000,0xffff)
  elif z==0:
   flags=0 if cb=='inf' else 4;pop=not flags or bool(cw&4);result=(J,0x7fff|(0 if b[1]&0x8000 else 0x8000))
  else:
   flags=2 if 'denormal' in (ca,cb) else 0
   if flags and not cw&2:pop=False
   elif ca=='inf' or cb=='inf':result=(J,0x7fff|(0x8000 if negative else 0))
   elif cb=='zero' or zeroLog:result=(0,0x8000 if negative else 0)
   else:
    result,f,up=calculate(a,b,op,cw>>10&3,bool(cw&8),bool(cw&16));flags|=f
 sticky=4 if kind&16 else 0;cc={0:0x4500,4:0,8:0x100,12:0x4000}[kind&12]
 sw=(0x3800 if pop else 0x3000)|cc|flags|sticky|(0x200 if up else 0)|(0x8080 if (flags|sticky)&~cw&63 else 0)
 if pop:tw=0x3fff|tag(result)<<14;regs=[raw(result)]+[raw((0,0)) for _ in range(7)]
 else:
  tw=0xfff|(3 if kind&1 else tag(a))<<12|(3 if kind&2 else tag(b))<<14;regs=[raw(a),raw(b)]+[raw((0,0)) for _ in range(6)]
 return dict(id=v['id'],control=cw,status=sw,tags=tw,registers=regs,memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');args=p.parse_args();r=Path(args.directory);vs=json.loads((r/'vectors.json').read_text(encoding='utf-8'))
 # All finite XP1 architectural vectors are within the primary manual's range.
 for v in vs:
  a=val(v['a'])
  if v['operation'] and cls(a) in ('finite','denormal','zero'):
   z=1+num(a);assert z>0 and Fraction(1,2)<=z*z<=2
 out=dict(referenceOnly=True,architecture='decimal-interval-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=[expected(v) for v in vs])
 (r/'math-reference.json').write_text(json.dumps(out,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedNumbers=calculate.cache_info().currsize,decimalPrecisionCrossCheck=[100,160],hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True,finiteXp1InputsInDocumentedRange=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8',newline='\n');print(json.dumps(cert))
