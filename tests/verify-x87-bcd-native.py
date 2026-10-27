# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent rational BCD/result/stack oracle for the actual native instruction."""
import argparse,json,struct,hashlib
from fractions import Fraction
from pathlib import Path

MASK=(1<<64)-1
INDEFINITE=dict(significand='C000000000000000',exponent='FFFF')
def words(v):"""Decodes fixed little-endian words for the independent packed-BCD/state comparison."""; return int(v['significand'],16),int(v['exponent'],16)
def tag(v):
 """Derives an x87 tag from the raw value rather than trusting the saved tag word."""
 s,e=words(v);e&=0x7fff
 return 1 if not s and not e else 2 if e in (0,0x7fff) or not s>>63 else 0

def expected(v):
 """Builds the independent x87 bcd native result, including raw register, stack, mask and status responses."""
 cw,kind=v['control'],v['kind'];depth=kind&15;top=(-depth)&7
 active=[v['b'] if i%2==0 else v['a'] for i in range(depth)]
 sw=(top<<11)|((kind&32)<<4)|{0:0,0x40:0x100,0x80:0x4000,0xc0:0x4500}[kind&0xc0]|(4 if kind&16 else 0)
 sw&=~0x200;data=bytes.fromhex(v['packed']);flags=0;up=False
 if v['operation']==0:
  if depth==8:
   flags=0x41;up=True
   if cw&1:top=(top-1)&7;active=[INDEFINITE]+active[:7]
  else:
   n=sum(((data[i//2]>>(4*(i%2)))&15)*10**i for i in range(18))
   if n:
    shift=64-n.bit_length();value=dict(significand=f'{n<<shift:016X}',exponent=f'{16383+63-shift|(0x8000 if data[9]&128 else 0):04X}')
   else:value=dict(significand='0000000000000000',exponent='8000' if data[9]&128 else '0000')
   top=(top-1)&7;active=[value]+active
 else:
  if depth:
   s,e=words(active[0]);ex=e&0x7fff;neg=bool(e&0x8000)
   invalid=ex==0x7fff or ex!=0 and not s>>63
   if not invalid:
    value=Fraction(s)*Fraction(2)**(max(1,ex)-16383-63);q,rem=divmod(value.numerator,value.denominator);rc=cw>>10&3
    up=bool(rem) and (rem*2>value.denominator or rem*2==value.denominator and q%2==1) if rc==0 else bool(rem) and ((rc==1 and neg) or (rc==2 and not neg))
    n=q+int(up);invalid=n>=10**18
   if invalid:flags=1;up=False
   else:
    flags=32 if rem else 0;digits=f'{n:018d}'[::-1]
    data=bytes(int(digits[i])+16*int(digits[i+1]) for i in range(0,18,2))+bytes([128 if neg else 0])
  else:flags=0x41
  if flags&1:
   if cw&1:data=bytes.fromhex('00000000000000C0FFFF')
   else:data=bytes.fromhex(v['packed'])
  if not flags&1 or cw&1:
   active=active[1:];top=(top+1)&7
 sw=(sw&~0x3800)|(top<<11)|flags|(0x200 if up else 0)
 if sw&~cw&63:sw|=0x8080
 tw=0xffff
 for i,value in enumerate(active):
  physical=(top+i)&7;tw=(tw&~(3<<(2*physical)))|(tag(value)<<(2*physical))
 return dict(control=cw,status=sw,tags=tw,registers=active,memory80=data.hex().upper())

def verify(root):
 """Checks x87 bcd native values, status and preserved raw registers against the independent mathematical contract."""
 root=Path(root);vectors=json.loads((root/'vectors.json').read_text(encoding='utf-8'));ref=json.loads((root/'native-reference.json').read_text(encoding='utf-8'))
 assert ref['referenceOnly'] and ref['architecture']=='host-x87-x64'
 assert hashlib.sha256((root/'vectors.json').read_bytes()).hexdigest().upper()==ref['vectorsSha256']
 assert len(vectors)==len(ref['results'])
 counts=dict(definedCases=0,undefinedDigitDiagnostics=0,loadCases=0,storeCases=0,pendingPrecisionCommits=0,pendingInvalidNoCommits=0,negativeZeroResults=0)
 for v,r in zip(vectors,ref['results']):
  e=expected(v);assert v['id']==r['id']
  assert (e['control'],e['status']&~0x4500,e['tags'])==(r['control'],r['status']&~0x4500,r['tags']),(v['id'],'CW/SW/TW',e,r)
  assert e['memory80']==r['memory80'] and r['memoryGuard']=='A5A5',(v['id'],'memory',e,r)
  for i,value in enumerate(e['registers']):assert value==r['registers'][i],(v['id'],i,'raw80',value,r['registers'][i])
  counts['undefinedDigitDiagnostics' if v['undefinedDigits'] else 'definedCases']+=1
  counts['loadCases' if v['operation']==0 else 'storeCases']+=1
  if e['status']&0xa0==0xa0:counts['pendingPrecisionCommits']+=1
  if e['status']&0x81==0x81:counts['pendingInvalidNoCommits']+=1
  if v['operation']==1 and e['memory80']=='00000000000000000080':counts['negativeZeroResults']+=1
 return dict(passed=True,**counts,hostFloatUsed=False,independentPythonFraction=True,undefinedDigitsAreArchitectural=False,referenceIsPhysical486=False)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();print(json.dumps(verify(a.directory),indent=2))
