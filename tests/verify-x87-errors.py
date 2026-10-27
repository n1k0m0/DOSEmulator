# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent IRQ/MF frame, pending-state, restart and DOS-return oracle."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct

NAMES=('start code_end proof rows original restored external_wait external_nop external_memory '
       'native_wait native_nop native_memory gdt tss paging_storage end divide empty_add '
       'old_timer old_numeric old_int2 phase operand').split()


def verify(report_path, memory_path, program_path, system, protected=False, paging=False):
    """Validates x87 errors evidence from actual guest state, immutable program bytes and independently expected results."""
    r=json.loads(Path(report_path).read_text(encoding='utf-8-sig'));m=Path(memory_path).read_bytes();p=Path(program_path).read_bytes()
    c=r['cpu'];managed=system.startswith('emm386');output=r['status']['debugOutput']
    assert not (managed and protected) and (not paging or protected)
    assert r['status']['fault'] is None and r['scenario']['complete']
    assert r['scenario']['inputBytesSent']==r['scenario']['inputBytesTotal']
    assert r['ataSectorsWritten']==r['floppySectorsWritten']==r['hardwareResetCount']==0
    assert 'X87-ERROR-PASS\r\n' in output and 'X87-ERROR-FAIL' not in output
    assert ('FreeCom version 0.86' if system=='freedos' else 'MS-DOS Version 6.22') in output
    assert re.search(r'\r\nFPERR-END\r\n(?:\r\n)?C:\\>$',output)
    markers=re.findall(r'X87-ERROR-PROOF=([0-9A-F]{8})\r\n',output);assert len(markers)==1
    address=int(markers[0],16);index=p.index(b'FPERADDR')+8
    assert struct.unpack_from('<I',p,index)[0]==len(NAMES)
    labels=dict(zip(NAMES,struct.unpack_from('<23I',p,index+4)));basis=address-labels['proof']
    raw=lambda at:struct.unpack_from('<I',m,at)[0]
    def physical(at):
        """Walks the reported classic 4-KiB page tables to translate a guest linear proof address."""
        if not c['pagingEnabled']:return at
        pde=raw((c['cr3']&0xfffff000)+((at>>20)&0xffc));assert pde&1
        pte=raw((pde&0xfffff000)+((at>>10)&0xffc));assert pte&1
        return (pte&0xfffff000)|(at&4095)
    read=lambda at,size:bytes(m[physical(at+i)] for i in range(size))
    at=lambda name:basis+labels[name]
    proof=read(address,256);u32=lambda off:struct.unpack_from('<I',proof,off)[0]
    assert proof[:8]==b'FPER0001' and (u32(8),u32(12),u32(20))==(1,1,0)
    assert basis==u32(80) and basis%16==0 and labels['end']==len(p)+0x100
    assert read(basis+0x100,labels['code_end']-0x100)==p[:labels['code_end']-0x100]
    assert u32(96)==int(paging) and u32(104)==int(protected)
    assert (u32(128),u32(132),u32(136),u32(140),u32(144),u32(148))==(3,3,0 if protected else 3,3 if protected else 0,2 if paging else 0,1 if protected else 0)
    assert u32(72)==u32(76)
    assert proof[165]&0x20==0 and proof[166]&5==0 and proof[167]&0x20==0
    original=read(at('original'),108);restored=read(at('restored'),108)
    assert original==restored
    assert (struct.unpack_from('<H',original,0)[0],struct.unpack_from('<H',original,4)[0],struct.unpack_from('<H',original,8)[0])==(0x37f,0,0xffff)
    assert original[28:]==bytes(80)
    rows=read(at('rows'),384)
    for i in range(6 if protected else 3):
        row=rows[i*64:(i+1)*64];d=lambda off:struct.unpack_from('<I',row,off)[0];w=lambda off:struct.unpack_from('<H',row,off)[0]
        native=i>=3;which=i%3;pending,cw,clear=(0x80c1,0x37e,0) if which==0 else (0xb084,0x37b,0x3000)
        name=('native_' if native else 'external_')+('wait','nop','memory')[which]
        ip=labels[name]+(0x10000 if protected else 0);cs=24 if protected else basis>>4
        assert (d(12),d(16))==(ip,cs),(i,'restart',hex(d(12)),hex(ip))
        assert d(20)&2 and bool(d(20)&0x10000)==native and bool(d(20)&0x200)==(not native)
        assert d(20)==w(50)|(0x10000 if native else 0x200)
        if native:assert row[:12]==bytes(12)
        else:assert (d(0),d(4),d(8))==(ip,cs,d(20))
        assert tuple(w(off) for off in (24,26,28,30))==(pending,cw,pending,cw)
        assert (w(32),w(34),w(36),w(38))==(clear if native else pending,clear,clear,cw)
        assert row[40:50]==(struct.pack('<QH',0xc000000000000000,0x4000) if which==2 else bytes(10))
        assert (d(52),d(56),d(60))==(0 if native else 1,1,1)
        opcode=p[labels[name]-0x100:]
        assert opcode.startswith((b'\x9b',b'\x66\xd9\xd0',b'\xd8\x07' if protected else b'\xd8\x06')[which])
    if not protected:
        assert rows[192:]==bytes(192)
        for name,vector in (('old_timer',8),('old_numeric',0x75),('old_int2',2)):
            assert read(at(name),4)==read(vector*4,4)
        # DOS may interpose its own IRQ75 entry. Check that actual RAM code;
        # the own DOSEBios ROM entry has a separately known byte contract.
        offset,segment=struct.unpack('<HH',read(at('old_numeric'),4));entry=segment*16+offset
        if entry>=0xf0000:assert proof[176:191]==bytes.fromhex('5030c0e6f0b020e6a0e620cd0258cf')
        else:assert proof[176:191]==read(entry,15)
    if protected:
        assert c['tr']==48 and c['trCache']['base']==at('tss') and u32(124)==48
        assert c['cr0']==u32(24)==u32(28) and c['cr3']==u32(32)==u32(36)
        if paging:
            directory,table=u32(88),u32(92);assert directory%4096==0 and table==directory+4096
            assert raw(directory)&0xfffff007==table|7 and u32(152)==0 and u32(156)==0x10000+labels['native_memory'] and u32(160)==0x300000
            assert raw(table+768*4)&0xfffff007==0x300007
    assert c['numericErrorWaits']==3 and c['numericErrorFaults']==(3 if protected else 0)
    assert not c['fpuErrorAsserted'] and not c['ignoreNumericError'] and not c['waitingForNumericError']
    assert r['numericError']==dict(ferrAsserted=False,interruptAsserted=False,ignoreNumericError=False,errorEdges=6 if protected else 3,acknowledgements=7 if protected else 3)
    assert c['x87Instructions']==(97 if protected else 51)
    assert (c['fpuControlWord'],c['fpuStatusWord'],c['fpuTagWord'],c['fpuTop'])==(0x37f,0,0xffff,0)
    assert c['fpuOpcode']==c['fpuInstructionPointer']==c['fpuDataPointer']==c['fpuCodeSelector']==c['fpuDataSelector']==0
    assert c['fpuRegisters']==[dict(logical=i,empty=True,significand='0000000000000000',signExponent='0000') for i in range(8)]
    assert not c['tripleFaulted'] and c['alignmentFaults']==0 and c['pageFaults']==(2 if paging else 0)
    assert c['protectedMode']==c['pagingEnabled']==managed
    if managed:assert c['virtual8086Instructions']>1000 and c['virtual8086Entries']-c['virtual8086Exits'] in (0,1)
    return dict(passed=True,system=system,protectedProbe=protected,privatePaging=paging,proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),imageDigest=hashlib.sha256(rows+original+restored).hexdigest().upper(),
                executableSha256=hashlib.sha256(p).hexdigest().upper(),actualBlockedExternalWaits=3,
                actualNativeMfFrames=3 if protected else 0,repairedOperandPageFaults=2 if paging else 0,
                actualLegacyBiosInt02Calls=0 if protected else 3,pendingFnopBypass=True,fpuOriginalImageRestored=True,
                cleanDosReturn=True,privateTrRemainsUntilReset=protected,allX87OrSiliconPrecisionAccepted=False)


def negative_controls(report_path,memory_path,program_path,system,protected,paging,directory):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    good=verify(report_path,memory_path,program_path,system,protected,paging)
    root=Path(directory);root.mkdir(parents=True,exist_ok=False)
    r=json.loads(Path(report_path).read_text(encoding='utf-8-sig'));m=Path(memory_path).read_bytes();p=Path(program_path).read_bytes()
    index=p.index(b'FPERADDR')+12;labels=dict(zip(NAMES,struct.unpack_from('<23I',p,index)))
    a=good['proofAddress'];basis=a-labels['proof'];changes=[]
    for off in (8,12,20,72,76,80,96,104,128,132,136,140,144,148):changes.append(('proof-'+str(off),'memory',a+off))
    changes.extend([('irq13-irr','memory5',a+165),('cascade-isr','memory2',a+166),('irq13-isr','memory5',a+167)])
    for i in range(6 if protected else 3):
        for off in (0,4,8,12,16,20,24,26,28,30,32,34,36,38,40,50,52,56,60):changes.append((f'row-{i}-{off}','memory',basis+labels['rows']+i*64+off))
    for name in ('original','restored'):
        for off in (0,4,8,28,98):changes.append((name+'-'+str(off),'memory',basis+labels[name]+off))
    for key in ('numericErrorWaits','numericErrorFaults','fpuErrorAsserted','ignoreNumericError','waitingForNumericError','x87Instructions','pageFaults','fpuControlWord','fpuStatusWord'):
        changes.append(('cpu-'+key,'cpu',key))
    for key in r['numericError']:changes.append(('board-'+key,'board',key))
    changes.append(('waiting-opcode','program',labels['external_wait']-0x100))
    if paging:
        for off in (152,156,160):changes.append(('pf-'+str(off),'memory',a+off))
    for name,kind,target in changes:
        rr=copy.deepcopy(r);mm=bytearray(m);pp=bytearray(p)
        if kind=='memory':mm[target]^=1
        elif kind=='memory5':mm[target]^=0x20
        elif kind=='memory2':mm[target]^=4
        elif kind in ('cpu','board'):
            part=rr['cpu' if kind=='cpu' else 'numericError'];part[target]=not part[target] if isinstance(part[target],bool) else part[target]^1
        else:pp[target]^=1
        rp=root/'bad.json';mp=root/'bad.private-ram.bin';pr=root/'bad.COM'
        rp.write_text(json.dumps(rr),encoding='utf-8');mp.write_bytes(mm);pr.write_bytes(pp)
        try:verify(rp,mp,pr,system,protected,paging)
        except (AssertionError,ValueError,KeyError,struct.error):pass
        else:raise AssertionError('Oracle accepted '+name)
    summary=dict(rejected=len(changes),controls=[x[0] for x in changes])
    (root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    return summary


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('report','memory','program','system'):parser.add_argument('--'+name,required=True)
    parser.add_argument('--protected',action='store_true');parser.add_argument('--paging',action='store_true');parser.add_argument('--negative-controls')
    args=parser.parse_args();result=verify(args.report,args.memory,args.program,args.system,args.protected,args.paging)
    if args.negative_controls:result['negativeControls']=negative_controls(args.report,args.memory,args.program,args.system,args.protected,args.paging,args.negative_controls)
    print(json.dumps(result,indent=2))
