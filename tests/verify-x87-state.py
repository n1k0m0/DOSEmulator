# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent raw 80-bit/legacy environment oracle; no host floating arithmetic."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct

NAMES=('start code_end proof values rotate load save94 save108 env14 env28 mutated drain constants empty original restored '
       'body_start body_end kernel_sp gdt tss storage end pop store_empty empty_value absolute_value constant_one').split()
VALUES=[(0x8000000000000001,0x3fff),(0,0x8000),(0x8000000000000123,0xbffd),(0xffffffffffffffff,0x7ffe),
        (1,0),(0x8000000000000000,0x7fff),(0xc000000000000042,0x7fff),(0xffffffffffffffff,0x3fff)]
INDEFINITE=(0xc000000000000000,0xffff)


def verify(report_path,memory_path,program_path,system,protected=False,paging=False):
    """Validates x87 state evidence from actual guest state, immutable program bytes and independently expected results."""
    r=json.loads(Path(report_path).read_text(encoding='utf-8-sig'));m=Path(memory_path).read_bytes();p=Path(program_path).read_bytes()
    cpu=r['cpu'];output=r['status']['debugOutput'];managed=system.startswith('emm386')
    assert not (protected and managed) and (not paging or protected)
    assert r['status']['fault'] is None and r['scenario']['complete']
    assert r['scenario']['inputBytesSent']==r['scenario']['inputBytesTotal']
    assert r['ataSectorsWritten']==r['floppySectorsWritten']==r['hardwareResetCount']==0
    assert 'X87-STATE-PASS\r\n' in output and 'X87-STATE-FAIL' not in output
    assert ('FreeCom version 0.86' if system=='freedos' else 'MS-DOS Version 6.22') in output
    assert re.search(r'\r\nFPSTATE-END\r\n(?:\r\n)?C:\\>$',output)
    markers=re.findall(r'X87-STATE-PROOF=([0-9A-F]{8})\r\n',output);assert len(markers)==1
    address=int(markers[0],16)
    raw=lambda at:struct.unpack_from('<I',m,at)[0]
    def physical(at):
        """Walks the reported classic 4-KiB page tables to translate a guest linear proof address."""
        if not cpu['pagingEnabled']:return at
        pde=raw((cpu['cr3']&0xfffff000)+((at>>20)&0xffc));assert pde&1
        pte=raw((pde&0xfffff000)+((at>>10)&0xffc));assert pte&1
        return (pte&0xfffff000)|(at&4095)
    read=lambda at,size:bytes(m[physical(at+i)] for i in range(size))
    proof=read(address,1024);u32=lambda off:struct.unpack_from('<I',proof,off)[0]
    u16=lambda off:struct.unpack_from('<H',proof,off)[0]
    assert proof[:8]==b'FPST0001' and (u32(8),u32(12),u32(20))==(1,1,0)
    index=p.index(b'FPSTADDR')+8
    assert struct.unpack_from('<I',p,index)[0]==len(NAMES)==28
    labels=dict(zip(NAMES,struct.unpack_from('<28I',p,index+4)))
    basis=address-labels['proof'];at=lambda name:basis+labels[name]
    assert basis==u32(80) and basis%16==0 and labels['end']==len(p)+0x100
    assert read(basis+0x100,labels['code_end']-0x100)==p[:labels['code_end']-0x100]
    assert u32(96)==int(paging) and u32(104)==int(protected)
    assert (u16(192),u16(196),u16(200),u16(204),u16(208),u16(212))==(0,0,0x3841,0x3800,0x841,0x800)
    assert u32(232)==u32(236)==1
    assert u32(216)==(24 if protected else basis>>4) and u32(220)==(32 if protected else basis>>4)
    assert bool(u32(228)&1)==(protected or managed)
    assert read(at('values'),80)==b''.join(struct.pack('<QH',*value) for value in VALUES)
    image=lambda name:read(at(name),{'env14':14,'env28':28,'save94':94,'drain':80,'empty_value':10}.get(name,108))
    def env(name,wide):
        """Decodes the selected 14/28-byte x87 environment format and its real/protected pointer convention."""
        b=image(name);step=4 if wide else 2;word=lambda off:struct.unpack_from('<H',b,off)[0]
        dword=lambda off:struct.unpack_from('<I',b,off)[0]
        result={'cw':word(0),'sw':word(step),'tags':word(step*2)}
        if protected:
            result.update(ip=dword(12) if wide else word(6),cs=word(16 if wide else 8),
                          data=dword(20) if wide else word(10),ds=word(24 if wide else 12))
            if wide:result['opcode']=word(18)&0x7ff
        else:
            result.update(ip=word(step*3)|((dword(step*4)>>12)&0xf0000 if wide else (word(step*4)&0xf000)<<4),
                          data=word(step*5)|((dword(step*6)>>12)&0xf0000 if wide else (word(step*6)&0xf000)<<4),
                          opcode=word(step*4)&0x7ff)
        return result
    expected={'cw':0xb7f,'sw':0x3800,'tags':0x10a8}
    fip=(0x10000+labels['rotate']) if protected else at('rotate')&0xfffff
    fdp=labels['values']+70 if protected else (at('values')+70)&0xfffff
    if protected:expected.update(ip=fip,cs=24,data=fdp,ds=32)
    else:expected.update(ip=fip,data=fdp,opcode=0x1f6)
    for name,wide in (('env14',False),('env28',True),('save94',False),('save108',True)):
        wanted=dict(expected)
        if protected:
            if not wide:wanted['ip']&=65535;wanted['data']&=65535
            else:wanted['opcode']=0 if name=='save108' else 0x1f6
            if name=='save108':wanted['ip']&=65535
        assert env(name,wide)==wanted,(name,env(name,wide),wanted)
    unpack=lambda b,offset=0:[struct.unpack_from('<QH',b,offset+i*10) for i in range(8)]
    original_stack=[VALUES[i] for i in (0,7,6,5,4,3,2,1)]
    assert unpack(image('save94'),14)==unpack(image('save108'),28)==original_stack
    modified_stack=[VALUES[i] for i in (0,6,0,5,4,3,2,6)]
    assert unpack(image('mutated'),28)==modified_stack
    def metadata(label,opcode,data):
        """Decodes saved x87 instruction/data pointers and opcode under the selected environment format."""
        if protected:return dict(ip=0x10000+labels[label],cs=24,data=data,ds=32,opcode=opcode)
        return dict(ip=at(label)&0xfffff,data=(basis+data)&0xfffff,opcode=opcode)
    assert env('mutated',True)==dict(cw=0xb7f,sw=0x3800,tags=0x30a2,**metadata('absolute_value',0x1e1,labels['values']+70))
    assert unpack(image('drain'))==modified_stack[:7]+[INDEFINITE]
    constant=image('constants')
    assert struct.unpack_from('<QH',constant,28)==(0x8000000000000000,0x3fff)
    assert struct.unpack_from('<QH',constant,38)==(0,0)
    constant_environment=metadata('constant_one',0x1e8,0)
    constant_environment['data']=0
    if protected:constant_environment['ds']=0
    assert env('constants',True)==dict(cw=0x37f,sw=0x3000,tags=0x4fff,**constant_environment)
    empty=env('empty',True)
    assert (empty['cw'],empty['sw'],empty['tags'])==(0x37f,0x800,0xffff)
    assert empty==dict(cw=0x37f,sw=0x800,tags=0xffff,**metadata('store_empty',0x33d if protected else 0x33e,labels['empty_value']))
    assert image('empty_value')==struct.pack('<QH',*INDEFINITE)
    assert image('original')==image('restored')
    assert (struct.unpack_from('<H',image('original'),0)[0],struct.unpack_from('<H',image('original'),4)[0],struct.unpack_from('<H',image('original'),8)[0])==(0x37f,0,0xffff)
    assert image('original')[28:]==bytes(80)
    for name,size in [('env14',14),('env28',28),('save94',94),('save108',108),('mutated',108),('drain',80),('constants',108),('empty',108),('empty_value',10),('original',108),('restored',108)]:
        assert read(at(name)-4,4)==struct.pack('<I',0xface0000+size)
        assert read(at(name)+size,4)==struct.pack('<I',0xcafe0000+size)
    rotate=labels['rotate']-0x100;assert p[rotate:rotate+2]==b'\xd9\xf6'
    assert cpu['x87Instructions']==51 and (cpu['fpuControlWord'],cpu['fpuStatusWord'],cpu['fpuTagWord'],cpu['fpuTop'])==(0x37f,0,0xffff,0)
    assert cpu['fpuOpcode']==cpu['fpuInstructionPointer']==cpu['fpuDataPointer']==cpu['fpuCodeSelector']==cpu['fpuDataSelector']==0
    assert cpu['fpuRegisters']==[dict(logical=i,empty=True,significand='0000000000000000',signExponent='0000') for i in range(8)]
    assert not cpu['tripleFaulted'] and cpu['pageFaults']==cpu['alignmentFaults']==0
    assert cpu['protectedMode']==cpu['pagingEnabled']==managed
    if protected:
        assert cpu['tr']==72 and cpu['trCache']['base']==at('tss')
        assert cpu['cr0']==u32(24)==u32(28) and cpu['cr3']==u32(32)==u32(36)
        for a,b in ((40,44),(48,52),(56,60),(64,68),(72,76)):assert u32(a)==u32(b)
        if paging:
            directory,table=u32(88),u32(92);assert directory%4096==0 and table==directory+4096
            assert raw(directory)&0xfffff007==table|7
            for i in range(1024):
                pte=raw(table+4*i);assert pte&0xfffff007==i*4096|7 and pte&0xf98==0
    elif managed:
        assert cpu['virtual8086Instructions']>1000 and cpu['virtual8086Entries']-cpu['virtual8086Exits'] in (0,1)
    return dict(passed=True,system=system,protectedProbe=protected,privatePaging=paging,proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),executableSha256=hashlib.sha256(p).hexdigest().upper(),
                imageDigest=hashlib.sha256(b''.join(image(n) for n in ('env14','env28','save94','save108','mutated','drain','constants','empty','original','restored'))).hexdigest().upper(),
                x87Instructions=51,actual80BitLowBits=True,allEightRegistersSavedRestored=True,physicalTagsAndLogicalOrder=True,
                legacy14_28_94_108ByteFormats=True,fpuOriginalImageRestored=True,privateTrRemainsUntilReset=protected,
                arithmeticAccepted=False,ferrBoardWiringAccepted=False,cleanDosReturn=True)


def negative_controls(report_path,memory_path,program_path,system,protected,paging,directory):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    result=verify(report_path,memory_path,program_path,system,protected,paging)
    assert not system.startswith('emm386')
    root=Path(directory);root.mkdir(parents=True,exist_ok=False)
    r=json.loads(Path(report_path).read_text(encoding='utf-8-sig'));m=Path(memory_path).read_bytes();p=Path(program_path).read_bytes()
    a=result['proofAddress'];idx=p.index(b'FPSTADDR')+12;labels=dict(zip(NAMES,struct.unpack_from('<28I',p,idx)))
    basis=a-labels['proof'];changes=[]
    for offset in (12,20,96,104,192,196,200,204,208,212,216,220,228,232,236):changes.append(('proof-'+str(offset),'memory',a+offset))
    for name in ('save94','save108','mutated','drain'):
        env=14 if name=='save94' else 0 if name=='drain' else 28
        for i in range(8):changes.append((name+'-low-'+str(i),'memory',basis+labels[name]+env+i*10))
    for name in ('env14','env28','save94','save108','mutated','constants','empty','original','restored'):
        step=2 if name in ('env14','save94') else 4
        for offset in (0,step,step*2,step*3,step*5):changes.append((name+'-field-'+str(offset),'memory',basis+labels[name]+offset))
    for name,size in [('env14',14),('env28',28),('save94',94),('save108',108),('mutated',108),('drain',80),('constants',108),('empty',108),('original',108),('restored',108),('empty_value',10)]:
        for offset in (-4,size):changes.append((name+'-guard-'+str(offset),'memory',basis+labels[name]+offset))
    changes += [('source-low-bit','memory',basis+labels['values']),('empty-indefinite','memory',basis+labels['empty_value'])]
    for key in ('x87Instructions','fpuControlWord','fpuStatusWord','fpuTagWord','fpuTop','fpuOpcode','fpuInstructionPointer'):
        changes.append(('cpu-'+key,'cpu',key))
    changes += [('raw-register','register',0),('wrong-instruction','program',labels['rotate']-0x100)]
    if paging:changes.append(('wrong-pte','memory',struct.unpack_from('<I',m,a+92)[0]+4))
    for name,kind,target in changes:
        rr=copy.deepcopy(r);mm=bytearray(m);pp=bytearray(p)
        if kind=='memory':mm[target]^=1
        elif kind=='cpu':rr['cpu'][target]^=1
        elif kind=='register':rr['cpu']['fpuRegisters'][0]['significand']='0000000000000001'
        else:pp[target]^=1
        rp=root/'bad.json';mp=root/'bad.private-ram.bin';pr=root/'bad.COM'
        rp.write_text(json.dumps(rr),encoding='utf-8');mp.write_bytes(mm);pr.write_bytes(pp)
        try:verify(rp,mp,pr,system,protected,paging)
        except (AssertionError,ValueError,KeyError,struct.error):pass
        else:raise AssertionError('Oracle accepted '+name)
    summary=dict(rejected=len(changes),controls=[item[0] for item in changes])
    (root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    return summary


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('report','memory','program','system'):parser.add_argument('--'+name,required=True)
    parser.add_argument('--protected',action='store_true');parser.add_argument('--paging',action='store_true');parser.add_argument('--negative-controls')
    args=parser.parse_args();result=verify(args.report,args.memory,args.program,args.system,args.protected,args.paging)
    if args.negative_controls:result['negativeControls']=negative_controls(args.report,args.memory,args.program,args.system,args.protected,args.paging,args.negative_controls)
    print(json.dumps(result,indent=2))
