# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent Fraction remainder/canonical 80-bit state certificates, no product code or host floats."""
import argparse,json,hashlib
from collections import Counter
from fractions import Fraction
from pathlib import Path
J=1<<63
def val(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def exp(v):"""Extracts the unsigned exponent field from a raw extended value."""; return v[1]&0x7fff
def neg(v):"""Returns or applies the raw sign-bit transformation used by the independent vector construction."""; return bool(v[1]&0x8000)
def unsupported(v):"""Recognizes raw extended encodings outside the documented finite/NaN/infinity formats."""; return exp(v)!=0 and not v[0]&J
def nan(v):"""Recognizes NaN encodings independently from the product's value classifier."""; return exp(v)==0x7fff and v[0]>J
def inf(v):"""Recognizes signed extended infinity from the raw explicit-integer-bit encoding."""; return exp(v)==0x7fff and v[0]==J
def zero(v):"""Recognizes raw signed zero while retaining its sign for later oracle comparisons."""; return exp(v)==0 and v[0]==0
def denorm(v):"""Recognizes a true extended denormal from its raw exponent and explicit significand."""; return exp(v)==0 and v[0]!=0
def tag(v):"""Derives an x87 tag from the raw value rather than trusting the saved tag word."""; return 1 if zero(v) else 2 if exp(v) in (0,0x7fff) or not v[0]&J else 0
def number(v):
 """Reconstructs the exact signed rational represented by a finite extended operand."""
 k=max(1,exp(v))-16383-63
 return Fraction((-v[0] if neg(v) else v[0])*(1<<max(0,k)),1<<max(0,-k))
def exponent(x):
 """Finds the exact floor binary exponent using integer/rational comparisons."""
 assert x
 x=abs(x);n=x.numerator;d=x.denominator;k=n.bit_length()-d.bit_length()
 if k>=0 and n<d<<k or k<0 and n<<-k<d:k-=1
 return k
def encode(x,negative_zero=False,um=True):
 """Rounds an exact or bounded rational to the reference extended format with explicit RC and exception responses."""
 if not x:return (0,0x8000 if negative_zero else 0),0
 negative=x<0;x=abs(x);e=exponent(x);unit=e-63
 s=x/Fraction(1<<max(0,unit),1<<max(0,-unit));assert s.denominator==1 and J<=s<1<<64
 if e< -16382:
  if not um:return (s.numerator,e+24576+16383|(0x8000 if negative else 0)),16
  tiny=x/Fraction(1,1<<16445);assert tiny.denominator==1
  return (tiny.numerator,0x8000 if negative else 0),0
 return (s.numerator,e+16383|(0x8000 if negative else 0)),0
def step(a,b,op,cw,kind):
 # Conditions on invalid/denormal/stack exits are intentionally a reference
 # model contract; preserve C0/C3, clear C1/C2, just as the separate native host.
 """Evaluates one independently specified complete or partial remainder instruction, retaining C2 and quotient bits."""
 oldcc=(kind&12);oldcc={0:0x4100,4:0,8:0x100,12:0x4000}[oldcc]
 if kind&3:return ((0xc000000000000000,0xffff) if cw&1 else a),0x41,oldcc,False,'stack'
 if unsupported(a) or unsupported(b):value=(0xc000000000000000,0xffff);flags=1;family='unsupported'
 elif nan(a) or nan(b):
  ns=[v for v in (a,b) if nan(v)];quiet=[v for v in ns if v[0]&(1<<62)]
  chosen=max(quiet or ns,key=lambda v:(v[0],not neg(v)));value=(chosen[0]|1<<62,chosen[1]);flags=int(any(not v[0]&(1<<62) for v in ns));family='nan'
 elif inf(a) or zero(b):value=(0xc000000000000000,0xffff);flags=1;family='invalid'
 else:
  flags=2 if denorm(a) or denorm(b) else 0
  if flags&~cw:return a,flags,oldcc,False,'denormal-abort'
  if inf(b):
   value,extra=encode(number(a),neg(a),bool(cw&16));return value,flags|extra,0,False,'infinite-modulus'
  x,y=number(a),number(b)
  difference=exponent(x)-exponent(y) if x else -100000
  partial=difference>=64
  factor=difference-32-difference%32 if partial else 0
  scaled_y=y*Fraction(1<<factor)
  ratio=abs(x/scaled_y);q,rem=divmod(ratio.numerator,ratio.denominator)
  if op&1 and not partial and (rem*2>ratio.denominator or rem*2==ratio.denominator and q&1):q+=1
  signed_q=-q if (x<0)!=(scaled_y<0) else q
  remainder=x-signed_q*scaled_y
  assert remainder==x-signed_q*scaled_y
  if partial:
   assert remainder==0 or exponent(remainder)<=exponent(x)-32
   cc=0x400;family='partial'
  else:
   assert abs(remainder)<abs(y) if not op&1 else abs(remainder)*2<=abs(y)
   cc=(0x100 if q&4 else 0)|(0x4000 if q&2 else 0)|(0x200 if q&1 else 0);family='complete'
  value,extra=encode(remainder,neg(a),bool(cw&16))
  return value,flags|extra,cc,partial,family
 return (a if flags&~cw&7 else value),flags,oldcc,False,family

def expected(v):
 """Builds the independent x87 remainder native result, including raw register, stack, mask and status responses."""
 a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind'];op=v['operation'];sticky=4 if kind&16 else 0;iterations=0;flags=sticky
 while True:
  result,newflags,cc,partial,family=step(a,b,op,cw,kind);flags|=newflags;iterations+=1;a=result
  if not op&2 or flags&~cw&63 or flags&1 or not partial:break
  assert iterations<2048
  # Partial completion does not add invalid; condition seeds no longer matter
  # once there are normal numeric answers. Keep original kind for empty tags.
 pending=bool(flags&~cw&63);sw=0x3000|flags|cc|(0x8080 if pending else 0)
 empty_a=bool(kind&1) and not cw&1;empty_b=bool(kind&2)
 tw=0xfff|(3 if empty_a else tag(a))<<12|(3 if empty_b else tag(b))<<14
 return a,b,sw,tw,iterations,family

def verify(v,r):
 """Checks x87 remainder native values, status and preserved raw registers against the independent mathematical contract."""
 a,b,sw,tw,n,f=expected(v)
 assert (r['id'],r['control'],r['status'],r['tags'])==(v['id'],v['control'],sw,tw),(v['id'],'state',hex(sw),hex(r['status']))
 if (tw>>12&3)!=3:assert val(r['registers'][0])==a,(v['id'],'ST0',a,val(r['registers'][0]))
 if (tw>>14&3)!=3:assert val(r['registers'][1])==b,(v['id'],'ST1')
 assert int.from_bytes(bytes.fromhex(r['memory'])[:4],'little')==n,(v['id'],'iterations',n,r['memory'])
 assert r['memory80'][8:]=='A5'*6 and r['memoryGuard']=='A5'*2
 return f,n
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();root=Path(a.directory)
 vs=json.loads((root/'vectors.json').read_text(encoding='utf-8'));ref=json.loads((root/'native-reference.json').read_text(encoding='utf-8'))
 assert ref['referenceOnly'] and ref['vectorsSha256']==hashlib.sha256((root/'vectors.json').read_bytes()).hexdigest().upper()
 assert len(vs)==len(ref['results']);counts=Counter();steps=0;maximum=0
 for v,r in zip(vs,ref['results']):
  f,n=verify(v,r);counts[f]+=1;steps+=n;maximum=max(maximum,n)
 print(json.dumps(dict(passed=True,certifiedCases=len(vs),nativeInstructions=steps,maximumIterations=maximum,families=dict(counts),hostFloatUsed=False,exactRationalChecks=True)))
