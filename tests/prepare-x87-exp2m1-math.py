# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent Decimal exp/log enclosures and exact Fraction rounding. No product code/host floats."""
import argparse,json,hashlib
from decimal import Decimal,localcontext,ROUND_FLOOR,ROUND_CEILING
from fractions import Fraction
from functools import lru_cache
from pathlib import Path
J=1<<63
def raw(v):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{v[0]:016X}',exponent=f'{v[1]:04X}')
def value(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def tag(v):
 """Derives an x87 tag from the raw value rather than trusting the saved tag word."""
 s,e=v;x=e&0x7fff
 return 1 if not x and not s else 2 if x in (0,0x7fff) or not s&J else 0
def power(k):"""Constructs an exact positive or negative power of two for rational arithmetic."""; return Fraction(1<<max(k,0),1<<max(-k,0))
def number(s,e):"""Reconstructs the exact signed rational represented by a finite extended operand."""; return (-1 if e&0x8000 else 1)*s*power(max(1,e&0x7fff)-16383-63)
def exponent(x):
 """Finds the exact floor binary exponent using integer/rational comparisons."""
 n,d=x.numerator,x.denominator;k=n.bit_length()-d.bit_length()
 if (n<d<<k) if k>=0 else (n<<-k<d):k-=1
 return k
def rnd(x,rc,negative):
 """Applies the requested explicit integer rounding mode, including nearest-even ties."""
 q,r=divmod(x.numerator,x.denominator)
 up=bool(r) and (rc==0 and (2*r>x.denominator or 2*r==x.denominator and q&1) or rc==1 and negative or rc==2 and not negative)
 return q+up,bool(up)
def encode(x,rc,um):
 """Rounds an exact or bounded rational to the reference extended format with explicit RC and exception responses."""
 negative=x<0;x=abs(x);e=exponent(x);q,up=rnd(x/power(e-63),rc,negative)
 if q==1<<64:q>>=1;e+=1
 tiny=e< -16382;flags=32|(16 if tiny else 0)
 if tiny and not um:e+=24576
 elif tiny:
  q,up=rnd(x/power(-16445),rc,negative)
  return (q,(1 if q==J else 0)|(0x8000 if negative else 0)),flags,up
 return (q,e+16383|(0x8000 if negative else 0)),flags,up

def enclosure(s,e,precision):
 """Computes independent lower/upper mathematical bounds at the requested Decimal precision."""
 x=number(s,e)
 with localcontext() as c:
  c.prec=precision;c.Emin=-30000;c.Emax=30000
  # Decimal ln/exp are correctly rounded with ROUND_HALF_EVEN. Adjacent
  # decimal values enclose their exact results. All other arithmetic here
  # uses directed rounding, independent of the product's integer Taylor code.
  log=c.ln(Decimal(2));ll=c.next_minus(log);lh=c.next_plus(log)
  c.rounding=ROUND_FLOOR;xl=c.divide(Decimal(x.numerator),Decimal(x.denominator))
  c.rounding=ROUND_CEILING;xh=c.divide(Decimal(x.numerator),Decimal(x.denominator))
  if abs(x)<power(-128):
   # H(x)=(2^x-1)/x and |H(x)-ln2|<|x| on [-1,1], from the
   # integral of t*exp(x*t) on [0,ln2]. This avoids cancellation.
   mag=max(abs(xl),abs(xh))
   c.rounding=ROUND_FLOOR;hl=c.subtract(ll,mag)
   c.rounding=ROUND_CEILING;hh=c.add(lh,mag)
   c.rounding=ROUND_FLOOR;lo=c.multiply(xl,hh if x<0 else hl)
   c.rounding=ROUND_CEILING;hi=c.multiply(xh,hl if x<0 else hh)
  else:
   c.rounding=ROUND_FLOOR;tl=c.multiply(xl,lh if x<0 else ll)
   c.rounding=ROUND_CEILING;th=c.multiply(xh,ll if x<0 else lh)
   el=c.next_minus(c.exp(tl));eh=c.next_plus(c.exp(th))
   c.rounding=ROUND_FLOOR;lo=c.subtract(el,Decimal(1))
   c.rounding=ROUND_CEILING;hi=c.subtract(eh,Decimal(1))
  return Fraction(lo),Fraction(hi)

@lru_cache(None)
def calculate(s,e,rc,um):
 """Rounds independently enclosed x87 exp2m1 math results at two precisions and rejects any ambiguous rounding boundary."""
 answers=[]
 for p in (100,160):
  lo,hi=enclosure(s,e,p);a=encode(lo,rc,um);b=encode(hi,rc,um);assert a==b,(s,e,rc,um,p,'interval crosses rounding boundary')
  answers.append(a)
 assert answers[0]==answers[1]
 return answers[0]

def expected(v):
 """Builds the independent x87 exp2m1 math result, including raw register, stack, mask and status responses."""
 s,e=value(v['a']);b=value(v['b']);cw=v['control'];kind=v['kind'];x=e&0x7fff;flags=4 if kind&16 else 0;up=False;result=(s,e);empty=bool(kind&1)
 if empty:
  flags|=0x41
  if cw&1:result=(0xc000000000000000,0xffff);empty=False
 elif x and not s&J:
  flags|=1
  if cw&1:result=(0xc000000000000000,0xffff)
 elif x==0x7fff and s>J:
  invalid=not s&(1<<62);flags|=int(invalid)
  if not invalid or cw&1:result=(s|1<<62,e)
 elif x==0 and s==0:pass
 elif x==0x7fff:result=(J,0xbfff) if e&0x8000 else (s,e)
 elif x>0x3fff or x==0x3fff and s!=J:flags|=32
 else:
  denormal=x==0;flags|=2 if denormal else 0
  if not denormal or cw&2:
   if x==0x3fff:result=(J,0xbffe) if e&0x8000 else (J,0x3fff);flags|=32
   else:
    result,f,up=calculate(s,e,cw>>10&3,bool(cw&16));flags|=f
 cc={0:0x4500,4:0,8:0x100,12:0x4000}[kind&12]
 sw=0x3000|cc|flags|(0x200 if up else 0)|(0x8080 if flags&~cw&63 else 0)
 tw=0xfff|(3 if empty else tag(result))<<12|(3 if kind&2 else tag(b))<<14
 regs=[raw(result),raw(b)]+[raw((0,0)) for _ in range(6)]
 return dict(id=v['id'],control=cw,status=sw,tags=tw,registers=regs,memory='A5'*8,memory80='A5'*10,memoryGuard='A5'*2)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();r=Path(a.directory);vs=json.loads((r/'vectors.json').read_text(encoding='utf-8'));results=[expected(v) for v in vs]
 ref=dict(referenceOnly=True,architecture='decimal-interval-math',vectorsSha256=hashlib.sha256((r/'vectors.json').read_bytes()).hexdigest().upper(),results=results)
 (r/'math-reference.json').write_text(json.dumps(ref,indent=2)+'\n',encoding='utf-8',newline='\n')
 cert=dict(passed=True,certifiedCases=len(vs),independentRoundedNumbers=calculate.cache_info().currsize,decimalPrecisionCrossCheck=[100,160],hostFloatUsed=False,productCodeUsed=False,outwardIntervals=True,allNumericEndpointsAgree=True)
 (r/'math-certificates.json').write_text(json.dumps(cert,indent=2)+'\n',encoding='utf-8');print(json.dumps(cert))
