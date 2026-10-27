# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent integer FSCALE certificates, including exponent bounds and source/stack state."""
import argparse,hashlib,json
from pathlib import Path

J=1<<63
def val(raw):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(raw['significand'],16),int(raw['exponent'],16)
def exp(v):"""Extracts the unsigned exponent field from a raw extended value."""; return v[1]&0x7fff
def sign(v):"""Reads the raw extended sign bit without losing signed zero."""; return v[1]&0x8000
def unsupported(v):"""Recognizes raw extended encodings outside the documented finite/NaN/infinity formats."""; return exp(v)!=0 and not v[0]&J
def nan(v):"""Recognizes NaN encodings independently from the product's value classifier."""; return exp(v)==0x7fff and v[0]>J
def inf(v):"""Recognizes signed extended infinity from the raw explicit-integer-bit encoding."""; return exp(v)==0x7fff and v[0]==J
def zero(v):"""Recognizes raw signed zero while retaining its sign for later oracle comparisons."""; return exp(v)==0 and v[0]==0
def denormal(v):"""Recognizes a true extended denormal from its raw exponent and explicit significand."""; return exp(v)==0 and v[0]!=0
def tag(v):"""Derives an x87 tag from the raw value rather than trusting the saved tag word."""; return 1 if zero(v) else 2 if exp(v) in (0,0x7fff) or not v[0]&J else 0
def integer(v):
 """Converts between the explicit extended value and its exact integer exponent/significand representation without host floating point."""
 shift=max(1,exp(v))-16383-63
 n=v[0]<<shift if shift>=0 else v[0]>>-shift
 return -n if sign(v) else n

def expected(a,b,cw,kind):
 """Builds the independent x87 scale native result, including raw register, stack, mask and status responses."""
 im=bool(cw&1);rc=cw>>10&3;up=False;empty0=bool(kind&1);empty1=bool(kind&2)
 if empty0 or empty1:
  result=(0xc000000000000000,0xffff) if im else a
  return result,0x41,False,empty0 and not im,empty1,'stack'
 if unsupported(a) or unsupported(b):result,flags=(0xc000000000000000,0xffff),1;family='invalid'
 elif nan(a) or nan(b):
  ns=[v for v in (a,b) if nan(v)];flags=int(any(not v[0]&(1<<62) for v in ns))
  quiet=[v for v in ns if v[0]&(1<<62)];chosen=max(quiet or ns,key=lambda v:(v[0],-sign(v)))
  result=(chosen[0]|1<<62,chosen[1]);family='nan'
 elif inf(b) and (inf(a) if sign(b) else zero(a)):result,flags=(0xc000000000000000,0xffff),1;family='invalid'
 else:
  flags=2 if denormal(a) or denormal(b) else 0
  if flags&~cw:return a,flags,False,False,False,'denormal-abort'
  if inf(b):result=(0,sign(a)) if sign(b) else (J,0x7fff|sign(a));family='infinite-scale'
  elif zero(a) or inf(a):result=a;family='zero-infinity'
  else:
   # Arbitrarily large scale integers are compared as integers before any
   # shift. No exponent is silently wrapped or clamped to a host int.
   bits=a[0].bit_length();exponent=bits-1+max(1,exp(a))-16383-63+integer(b);mantissa=a[0]<<(64-bits)
   if exponent>16383:
    family='overflow';flags|=8
    if not cw&8:
     adjusted=exponent-24576
     if adjusted>16383:result=(J,0x7fff|sign(a));flags|=32;up=True;family='double-overflow'
     else:result=(mantissa,adjusted+16383|sign(a))
    else:
     up=rc==0 or rc==1 and bool(sign(a)) or rc==2 and not sign(a)
     result=(J,0x7fff|sign(a)) if up else ((1<<64)-1,0x7ffe|sign(a));flags|=32
   elif exponent< -16382:
    family='tiny'
    if not cw&16:
     flags|=16;adjusted=exponent+24576
     if adjusted< -16382:result=(0,sign(a));flags|=32;family='double-underflow'
     else:result=(mantissa,adjusted+16383|sign(a))
    else:
     discarded=-16382-exponent
     if discarded<=64:q,rem=divmod(mantissa,1<<discarded);cmp=(2*rem>(1<<discarded))-(2*rem<(1<<discarded))
     else:q,rem,cmp=0,mantissa,-1
     up=bool(rem) and (rc==0 and (cmp>0 or cmp==0 and q&1) or rc==1 and bool(sign(a)) or rc==2 and not sign(a))
     q+=up;result=(q,(1 if q==J else 0)|sign(a))
     if rem:flags|=0x30
   else:result=(mantissa,exponent+16383|sign(a));family='normal-exact'
 if flags&~cw&3:result=a
 return result,flags,up,False,False,family

def verify(vectors,reference):
 """Checks x87 scale native values, status and preserved raw registers against the independent mathematical contract."""
 assert reference['referenceOnly'] and reference['architecture']=='host-x87-x64'
 by_id={r['id']:r for r in reference['results']};counts={};rounded=0;pending=0;pcs=set();rcs=set()
 for v in vectors:
  n=by_id[v['id']];a,b=val(v['a']),val(v['b']);cw=v['control'];kind=v['kind']
  result,flags,up,empty0,empty1,family=expected(a,b,cw,kind)
  flags|=4 if kind&16 else 0;sw=0x3000|flags|(0x200 if up else 0)|(0x8080 if flags&~cw&63 else 0)
  tw=0xfff|((3 if empty0 else tag(result))<<12)|((3 if empty1 else tag(b))<<14)
  assert (n['control'],n['status']&~0x4500,n['tags'])==(cw,sw,tw),(v['id'],'CW/SW/tags',hex(n['status']),hex(sw))
  if not empty0:assert val(n['registers'][0])==result,(v['id'],'ST0',val(n['registers'][0]),result)
  if not empty1:assert val(n['registers'][1])==b,(v['id'],'ST1 was changed')
  counts[family]=counts.get(family,0)+1;rounded+=bool(up);pending+=bool(sw&0x80);pcs.add(cw>>8&3);rcs.add(cw>>10&3)
 assert pcs=={0,2,3} and rcs=={0,1,2,3}
 assert counts['double-overflow']>100 and counts['double-underflow']>100 and counts['normal-exact']>1000
 return dict(passed=True,certifiedCases=len(vectors),families=counts,roundedUpCases=rounded,pendingUnmaskedCases=pending,
             method='independent integer exponent inequalities, exact discarded-bit rounding and source/stack contracts',
             fullExtendedPrecisionIndependentOfPc=True,hostFloatUsed=False,physical486=False)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();root=Path(a.directory)
 vectors=json.loads((root/'vectors.json').read_text(encoding='utf-8'));reference=json.loads((root/'native-reference.json').read_text(encoding='utf-8'))
 assert hashlib.sha256((root/'vectors.json').read_bytes()).hexdigest().upper()==reference['vectorsSha256']
 print(json.dumps(verify(vectors,reference),indent=2))
