"""Independent rational interval certificate for actual native FSQRT results.

Checks adjacent representable outputs and their squared midpoints; it does not
compute a square root or use the emulator's Newton/remainder implementation.
"""
import argparse,json,hashlib
from fractions import Fraction
from pathlib import Path

J=1<<63
def number(sig,exp):
 scale=max(1,exp)-16383-63
 return Fraction(sig<<scale) if scale>=0 else Fraction(sig,1<<-scale)

def verify(vectors,reference):
 assert reference['referenceOnly'] and reference['architecture']=='host-x87-x64'
 results={x['id']:x for x in reference['results']};checked=aborted=exact=up=0;pcs=set();rcs=set()
 for v in vectors:
  sig=int(v['a']['significand'],16);word=int(v['a']['exponent'],16);exp=word&0x7fff;kind=v['kind'];cw=v['control']
  if kind&1 or word&0x8000 or not sig or exp==0x7fff or exp and sig<J:continue
  e=results[v['id']];out=e['registers'][0];ys=int(out['significand'],16);ye=int(out['exponent'],16)
  sw=e['status'];de=exp==0;sticky=4 if kind&16 else 0
  if de and not cw&2:
   assert (ys,ye)==(sig,word) and sw&0x23f==2|sticky
   aborted+=1;continue
  p={0:24,2:53,3:64}[cw>>8&3];rc=cw>>10&3;pcs.add(p);rcs.add(rc)
  assert 0<ye<0x7fff and ys>=J and ys&((1<<(64-p))-1)==0
  x=number(sig,exp);y=number(ys,ye);ulp=number(1<<(64-p),ye)
  low=y-(ulp/2 if ys==J else ulp);high=y+ulp
  if rc in (1,3):assert y*y<=x<high*high,(v['id'],'downward interval')
  elif rc==2:assert low*low<x<=y*y,(v['id'],'upward interval')
  else:
   lower=((low+y)/2)**2;upper=((y+high)/2)**2;even=(ys>>(64-p))%2==0
   assert lower<x<upper or even and x in (lower,upper),(v['id'],'nearest/even interval')
  inexact=y*y!=x;roundup=y*y>x
  assert sw&63==(2 if de else 0)|(32 if inexact else 0)|sticky,(v['id'],'exception certificate')
  assert bool(sw&0x200)==roundup,(v['id'],'C1 certificate')
  checked+=1;exact+=not inexact;up+=roundup
 assert checked>4000 and aborted>100 and pcs=={24,53,64} and rcs=={0,1,2,3}
 return dict(passed=True,certifiedFiniteResults=checked,denormalAborts=aborted,exactRoots=exact,roundedUp=up,
  precisionBits=sorted(pcs),roundingModes=sorted(rcs),method='exact rational adjacent-output squared intervals',hostFloatUsed=False)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('vectors');p.add_argument('reference');a=p.parse_args()
 vectors=json.loads(Path(a.vectors).read_text(encoding='utf-8'));ref=json.loads(Path(a.reference).read_text(encoding='utf-8'))
 assert hashlib.sha256(Path(a.vectors).read_bytes()).hexdigest().upper()==ref['vectorsSha256']
 print(json.dumps(verify(vectors,ref),indent=2))
