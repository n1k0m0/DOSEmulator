# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent integer/Fraction BCD inputs; no emulator tables or host float."""
import argparse,json,struct
from fractions import Fraction
from pathlib import Path

def raw(sig,exp):"""Serializes raw significand/exponent fields without numeric conversion."""; return dict(significand=f'{sig:016X}',exponent=f'{exp:04X}')
def binary(value,negative_zero=False):
 """Encodes an exactly representable signed rational as raw extended precision, including negative zero."""
 negative=value<0 or negative_zero;v=abs(Fraction(value))
 if not v:return raw(0,0x8000 if negative else 0)
 e=v.numerator.bit_length()-v.denominator.bit_length()
 if v<Fraction(2)**e:e-=1
 q=v*Fraction(2)**(63-e);assert q.denominator==1
 return raw(q.numerator,16383+e+(0x8000 if negative else 0))
def packed(value,negative=False,padding=0):
 """Encodes eighteen decimal digits and the sign into the independent ten-byte packed-BCD operand."""
 digits=f'{value:018d}'[::-1]
 return bytes(int(digits[i])+16*int(digits[i+1]) for i in range(0,18,2))+bytes([padding|(128 if negative else 0)])
def record(v,index):
 """Serializes one BCD vector into the fixed original guest input record."""
 a,b=v['a'],v['b']
 return struct.pack('<4HQHQH',v['control'],v['operation'],v['kind'],index,int(a['significand'],16),int(a['exponent'],16),int(b['significand'],16),int(b['exponent'],16))+bytes.fromhex(v['packed'])+bytes(2)

def generate():
 """Builds deterministic x87 bcd vectors vectors and original guest data tables; no original media are modified."""
 cases=[]
 def add(op,cw,depth,a,b,data,label,undefined=False):
  """Adds one explicitly parameterized x87 bcd vectors case to the deterministic test matrix."""
  seed=(0,0x20,0x60,0xb0,0xf0)[len(cases)%5]
  cases.append(dict(id=f'bcd-{len(cases)}',operation=op,control=cw,kind=depth|seed,a=a,b=b,bits='0000000000000000',packed=data.hex().upper(),label=label,undefinedDigits=undefined))
 loads=[0,1,9,10,99,100,101,123456789,999999999,1000000001,123456789012345678,999999999999999998,999999999999999999]
 for number in loads:
  for sign in (False,True):
   for padding in (0,0x7f):
    for pc in (0,2,3):
     for rc in range(4):
      for depth,clear in ((0,0),(2,32),(7,1),(8,0),(8,1)):
       add(0,(0x7f|pc<<8|rc<<10)&~clear,depth,raw(0x8000000000001234,0x4003),raw(0xc00000000000cafe,0xbffe),packed(number,sign,padding),f'load-{number}-{int(sign)}-{padding}')
 stores=[('zero',binary(0)),('negzero',binary(0,True))]
 for value in (Fraction(1,4),Fraction(1,2),Fraction(3,4),Fraction(3,2),Fraction(5,2),Fraction(7,2),Fraction(31,16),Fraction(33,16),2**53+1,2**59-1,10**18-1,Fraction(10**18*16-17,16),Fraction(10**18*16-9,16),Fraction(10**18*16-8,16),Fraction(10**18*16-7,16),10**18,10**18+1,2**63):
  for sign in (1,-1):stores.append((str(value*sign),binary(value*sign)))
 for name,s,e in [('tiny',1,0),('tinyneg',1,0x8000),('maxden',0x7fffffffffffffff,0),('pseudo',0x8000000000000000,0),('pseudoneg',0x8000000000000123,0x8000),('minnormal',0x8000000000000000,1),('huge',0xffffffffffffffff,0x7ffe),('nan',0xc000000000001234,0x7fff),('snan',0x8000000000001234,0x7fff),('negnan',0xffffffffffffffff,0xffff),('inf',0x8000000000000000,0x7fff),('neginf',0x8000000000000000,0xffff),('unnormal',1,0x4000),('pseudoinf',0,0x7fff)]:stores.append((name,raw(s,e)))
 for name,a in stores:
  for pc in (0,2,3):
   for rc in range(4):
    for depth,clear in ((1,0),(2,1),(2,2),(8,32),(1,33)):
     add(1,(0x7f|pc<<8|rc<<10)&~clear,depth,a,a,bytes([0xa5])*10,name)
 for pc in (0,2,3):
  for rc in range(4):
   for clear in (0,1,32):add(1,(0x7f|pc<<8|rc<<10)&~clear,0,binary(1),binary(1),bytes([0xa5])*10,'empty')
 # Invalid digits are diagnostic only, never architectural acceptance evidence.
 for i in range(18):
  for digit in (10,15):
   data=bytearray(10);data[i//2]=digit<<(4*(i%2));add(0,0x37f,0,binary(1),binary(1),data,'undefined-nibble',True)
 return cases

def select(cases):
 """Chooses defined BCD architecture cases while excluding undefined digit diagnostics."""
 cases=[v for v in cases if not v['undefinedDigits']]
 selected=[]
 def take(predicate):
  """Adds the requested case once to the bounded guest subset."""
  v=next(v for v in cases if predicate(v) and v not in selected);selected.append(v)
 for op in range(2):
  for pc in (0,2,3):
   for rc in range(4):
    take(lambda v:v['operation']==op and v['control']>>8&3==pc and v['control']>>10&3==rc and (v['kind']&15)==(2 if op==0 else 1) and (v['control']&32)==(0 if op==0 else 32))
 for label in sorted(set(v['label'] for v in cases if v['operation']==1)):
  for pending in (False,True):
   candidates=[v for v in cases if v['operation']==1 and v['label']==label and v['control']>>8&3==3 and bool(v['control']&33!=33)==pending and v not in selected]
   if candidates:selected.append(candidates[0])
 for rc in range(4):
  for depth in (0,7,8):
   for masked in (False,True):
    candidates=[v for v in cases if v['operation']==0 and v['control']>>10&3==rc and v['kind']&15==depth and bool(v['control']&1)==masked and v not in selected]
    if candidates:selected.append(candidates[-1])
 for sign in ('load-0-0-127','load-0-1-127','load-999999999999999999-0-0','load-999999999999999999-1-127'):
  take(lambda v:v['label']==sign and (v['kind']&15)<8)
 for v in cases:
  if len(selected)>=220:break
  if not v['undefinedDigits'] and v not in selected:selected.append(v)
 assert len(selected)==220 and not any(v['undefinedDigits'] for v in selected)
 return selected

if __name__=='__main__':
 parser=argparse.ArgumentParser();parser.add_argument('directory');args=parser.parse_args();root=Path(args.directory);root.mkdir(parents=True,exist_ok=True)
 cases=generate();guest=select(cases)
 for name,values in [('vectors.json',cases),('guest-vectors.json',guest)]:root.joinpath(name).write_text(json.dumps(values,indent=2)+'\n',encoding='utf-8',newline='\n')
 data=b''.join(record(v,i) for i,v in enumerate(guest));source=f'%define VECTOR_COUNT {len(guest)}\n%macro bcd_vectors 0\n'
 source+='\n'.join('db '+','.join(str(b) for b in data[i:i+40]) for i in range(0,len(data),40))+'\n%endmacro\n'
 root.joinpath('x87-bcd-data.inc').write_text('; SPDX-License-Identifier: Apache-2.0\n; Copyright 2026 Nils Kopal\n' + (source),encoding='ascii',newline='\n')
 print(json.dumps(dict(vectors=len(cases),guestVectors=len(guest),undefinedDigitDiagnostics=sum(v['undefinedDigits'] for v in cases),hostFloatUsed=False)))
