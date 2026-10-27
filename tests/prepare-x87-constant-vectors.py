# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent high-precision constant derivation and x87 stack/control inputs."""
import argparse,json,struct
from decimal import Decimal,localcontext,ROUND_FLOOR,ROUND_HALF_EVEN
from pathlib import Path

def constants(precision):
 """Constructs the independently bounded mathematical constants and their directed binary roundings."""
 with localcontext() as ctx:
  ctx.prec=precision
  def atan_inv(n):
   """Bounds atan(1/n) with a rational alternating series for the independent constant oracle."""
   x=Decimal(1)/n;power=x;total=x;k=1
   while True:
    power*=-x*x;term=power/(2*k+1);next_value=total+term
    if next_value==total:return total
    total=next_value;k+=1
  ln2=Decimal(2).ln();ln10=Decimal(10).ln()
  values=[Decimal(1),ln10/ln2,1/ln2,16*atan_inv(5)-4*atan_inv(239),ln2/ln10,ln2,Decimal(0)]
  result=[]
  for v in values:
   if not v:result.append(dict(floor='0000000000000000',nearest64='0000000000000000',exponent=0));continue
   exponent=0;x=v
   while x>=2:x/=2;exponent+=1
   while x<1:x*=2;exponent-=1
   # Do not infer unobservable internal ROM/guard bits by double rounding.
   result.append(dict(floor=f'{int((x*(1<<63)).to_integral_value(rounding=ROUND_FLOOR)):016X}',exponent=16383+exponent,
    nearest64=f'{int((x*(1<<63)).to_integral_value(rounding=ROUND_HALF_EVEN)):016X}'))
  return result

def generate():
 """Builds deterministic x87 constant vectors vectors and original guest data tables; no original media are modified."""
 cases=[]
 for op in range(7):
  for pc in (0,2,3):
   for rc in range(4):
    for clear in (0,1,32,33):
     for depth in range(9):
      for seed in (0,0x30,0x60,0xb0,0xf0):
       cases.append(dict(id=f'const-{len(cases)}',operation=op,control=(0x7f|pc<<8|rc<<10)&~clear,kind=depth|seed,
        a=dict(significand='8000000000001234',exponent='4003'),b=dict(significand='C00000000000CAFE',exponent='BFFE'),bits='0000000000000000'))
 return cases

if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('directory');a=parser.parse_args();root=Path(a.directory);root.mkdir(parents=True,exist_ok=True)
 derived=constants(100);assert derived==constants(160)
 def write(name,v):"""Writes a deterministic generated vector JSON file in the explicitly supplied private output directory."""; root.joinpath(name).write_text(json.dumps(v,indent=2)+'\n',encoding='utf-8',newline='\n')
 write('derived-constants.json',dict(passed=True,constants=derived,decimalPrecisionCrossCheck=[100,160],hostFloatUsed=False,piFormula='16 atan(1/5) - 4 atan(1/239)'))
 cases=generate();write('vectors.json',cases);selected=[]
 def pick(op,pc,rc,clear=0,depth=2,seed=0x30):
  """Chooses a bounded, deterministic guest subset preserving the required architectural case families."""
  v=next(v for v in cases if v['operation']==op and v['control']==((0x7f|pc<<8|rc<<10)&~clear) and v['kind']==depth|seed)
  if v not in selected:selected.append(v)
 for op in range(7):
  for rc in range(4):
   for pc in (0,2,3):pick(op,pc,rc,32)
   pick(op,3,rc,0,0);pick(op,3,rc,0,8);pick(op,3,rc,1,8);pick(op,3,rc,0,7)
 for op in range(7):
  for seed in (0,0x60,0xb0):pick(op,3,0,33,2,seed)
 assert len(selected)<=220,len(selected)
 for v in cases:
  if len(selected)==220:break
  if v not in selected:selected.append(v)
 write('guest-vectors.json',selected);chunks=[]
 for i,v in enumerate(selected):
  a,b=v['a'],v['b'];chunks.append(struct.pack('<4HQHQHIQ',v['control'],v['operation'],v['kind'],i,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16),0,0))
 data=b''.join(chunks);source=f'%define VECTOR_COUNT {len(selected)}\n%macro constant_vectors 0\n'
 source+='\n'.join('db '+','.join(str(x) for x in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 (root/'x87-constant-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (source),encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(selected),derivedConstants=derived)))
