"""Check native constants against separately derived high precision math and stack rules."""
import argparse,json,hashlib
from pathlib import Path

def verify(vectors,reference,derived):
 assert reference['referenceOnly'] and reference['architecture']=='host-x87-x64' and derived['passed']
 constants=derived['constants'];results={x['id']:x for x in reference['results']};overflows=unmasked=0;pcs=set();rcs=set();ops=set()
 for v in vectors:
  n=results[v['id']];op=v['operation'];cw=v['control'];kind=v['kind'];depth=kind&15;rc=cw>>10&3;pcs.add(cw>>8&3);rcs.add(rc);ops.add(op)
  initial=(kind&0xc0)>>6;conditions=(0,0x100,0x4000,0x4500)[initial];sticky=4 if kind&16 else 0
  overflow=depth==8;masked=bool(cw&1);top=((8-depth)-(not overflow or masked))&7
  # C0/C2/C3 are undefined. All remaining bits are defined by this bounded model.
  sw=(top<<11)|sticky|(0x241 if overflow else 0)|(0x8080 if overflow and not masked else 0)
  assert n['status']&~0x4500==sw and n['control']==cw
  assert not n['status']&0x20,'Constant load raised new Precision'
  c=constants[op];floor=int(c['floor'],16);near=int(c['nearest64'],16)
  value=near if rc==0 else floor+int(rc==2 and op in range(1,6))
  expected=(value,c['exponent']);active=depth if overflow else depth+1
  for i in range(active):
   if i==0 and not (overflow and not masked):target=(0xc000000000000000,0xffff) if overflow else expected
   else:
    old=i if overflow and not masked else i-1
    target=(int(v['b' if old%2==0 else 'a']['significand'],16),int(v['b' if old%2==0 else 'a']['exponent'],16))
   actual=n['registers'][i];assert (int(actual['significand'],16),int(actual['exponent'],16))==target,(v['id'],i,'constant/old raw register')
  overflows+=overflow;unmasked+=overflow and not masked
 assert ops==set(range(7)) and pcs=={0,2,3} and rcs==set(range(4))
 return dict(passed=True,certifiedCases=len(vectors),constants=7,overflowCases=overflows,unmaskedOverflows=unmasked,
  decimalPrecisionCrossCheck=derived['decimalPrecisionCrossCheck'],piFormula=derived['piFormula'],hostFloatUsed=False,
  originalInternal66BitRomRecovered=False,physical486=False)

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('directory');a=p.parse_args();root=Path(a.directory)
 def load(n):return json.loads((root/n).read_text(encoding='utf-8'))
 ref=load('native-reference.json');assert hashlib.sha256((root/'vectors.json').read_bytes()).hexdigest().upper()==ref['vectorsSha256']
 print(json.dumps(verify(load('vectors.json'),ref,load('derived-constants.json')),indent=2))
