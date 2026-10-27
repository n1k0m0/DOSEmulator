# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent exact-integer FXTRACT/state certificate; no host float or product code."""
import argparse,hashlib,json
from collections import Counter
from fractions import Fraction
from pathlib import Path

J=1<<63
INDEFINITE=(0xc000000000000000,0xffff)
def value(v):"""Decodes explicit raw significand/exponent fields without host floating-point conversion."""; return int(v['significand'],16),int(v['exponent'],16)
def exponent(v):"""Finds the exact floor binary exponent using integer/rational comparisons."""; return v[1]&0x7fff
def integer(n):
 """Converts between the explicit extended value and its exact integer exponent/significand representation without host floating point."""
 if not n:return 0,0
 bits=abs(n).bit_length();return abs(n)<<(64-bits),(bits-1+16383)|(0x8000 if n<0 else 0)
def tag(v):
 """Derives an x87 tag from the raw value rather than trusting the saved tag word."""
 s,e=v;x=e&0x7fff
 return 1 if x==0 and s==0 else 2 if x in (0,0x7fff) or not s&J else 0
def rational(v):
 """Reconstructs the exact signed rational represented by a finite raw extended value."""
 s,e=v;k=max(1,e&0x7fff)-16383-63
 return Fraction((-s if e&0x8000 else s)*(1<<max(0,k)),1<<max(0,-k))

def expected(v):
 """Builds the independent x87 extract native result, including raw register, stack, mask and status responses."""
 a,b=value(v['a']),value(v['b']);cw,kind=v['control'],v['kind'];depth=kind&15;top=0;regs=[None]*8
 for left in range(depth,0,-1):top=(top-1)%8;regs[top]=a if left==1 else b
 for bit,offset in ((0x100,0),(0x200,7),(0x400,3)):
  if kind&bit:regs[(top+offset)%8]=None
 target=(top-1)%8;flags=8 if kind&16 else 0;c1=0;family='finite';conserved=False
 if regs[top] is None or regs[target] is not None:
  c1=0 if regs[top] is None else 0x200;flags|=0x41;family='underflow' if not c1 else 'overflow'
  if cw&1:regs[top]=regs[target]=INDEFINITE;top=target
 else:
  s,e=a;x=e&0x7fff;neg=e&0x8000
  if x!=0 and not s&J:pair=(INDEFINITE,INDEFINITE);new_flags=1;family='unsupported'
  elif x==0x7fff and s!=J:
   quiet=(s|1<<62,e);pair=quiet,quiet;new_flags=0 if s&(1<<62) else 1;family='nan'
  elif x==0x7fff:pair=a,(J,0x7fff);new_flags=0;family='infinity'
  elif not s:pair=a,(J,0xffff);new_flags=4;family='zero'
  else:
   # Divide by the exact binary exponent. Check the independently reconstructed
   # signed rational number, rather than rounding a host float or using FSCALE.
   k=max(1,x)-16383+s.bit_length()-64
   unit=(s<<(64-s.bit_length()),0x3fff|neg);pair=unit,integer(k)
   new_flags=2 if x==0 else 0;family='denormal' if new_flags else 'finite'
   assert Fraction(1)<=abs(rational(unit))<2
   assert rational(unit)*Fraction(1<<max(0,k),1<<max(0,-k))==rational(a)
   assert rational(pair[1])==k;conserved=True
  flags|=new_flags
  if not new_flags&~cw:regs[top]=pair[1];regs[target]=pair[0];top=target
 sw=(top<<11)|flags|c1
 if flags&~cw&63:sw|=0x8080
 tw=sum((3 if v is None else tag(v))<<(i*2) for i,v in enumerate(regs))
 return cw,sw,tw,[regs[(top+i)%8] for i in range(8)],family,conserved

def verify(v,r):
 """Checks x87 extract native values, status and preserved raw registers against the independent mathematical contract."""
 cw,sw,tw,regs,family,conserved=expected(v)
 assert (r['id'],r['control'],r['status']&~0x4500,r['tags'])==(v['id'],cw,sw,tw),(v['id'],'CW/SW/TW',hex(sw),hex(r['status']))
 for i,raw in enumerate(regs):
  if raw is not None:assert value(r['registers'][i])==raw,(v['id'],i,'raw 80-bit register',raw,value(r['registers'][i]))
 assert r['memory80']=='A5'*10 and r['memoryGuard']=='A5'*2
 return family,conserved

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('vectors');p.add_argument('reference');p.add_argument('output');a=p.parse_args()
 vs=json.loads(Path(a.vectors).read_text(encoding='utf-8-sig'));ref=json.loads(Path(a.reference).read_text(encoding='utf-8-sig'))
 assert ref['referenceOnly'] and ref['architecture']=='host-x87-x64' and len(vs)==len(ref['results'])
 assert ref['vectorsSha256']==hashlib.sha256(Path(a.vectors).read_bytes()).hexdigest().upper()
 counts=Counter();conserved=0
 for v,r in zip(vs,ref['results']):
  family,ok=verify(v,r);counts[family]+=1;conserved+=ok
 result=dict(passed=True,vectors=len(vs),families=dict(counts),exactRationalReconstruction=conserved,hostFloatUsed=False,
  vectorsSha256=ref['vectorsSha256'],nativeCodeSha256=ref['nativeCodeSha256'],undefinedC0C2C3Excluded=True)
 Path(a.output).write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
