# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent DOS RAM/state comparison against actual native x87 numerical results."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct

NAMES='start code_end proof vectors results original restored body_start body_end kernel_sp gdt tss storage end'.split()


def verify_data(r,m,p,vectors,reference,system,protected,paging):
    """Validates x87 numeric evidence from actual guest state, immutable program bytes and independently expected results."""
    c=r['cpu'];out=r['status']['debugOutput'];managed=system.startswith('emm386')
    assert not (managed and protected) and (not paging or protected)
    assert r['status']['fault'] is None and r['scenario']['complete']
    assert r['scenario']['inputBytesSent']==r['scenario']['inputBytesTotal']
    assert r['ataSectorsWritten']==r['floppySectorsWritten']==r['hardwareResetCount']==0
    assert 'X87-NUMERIC-PASS\r\n' in out and 'X87-NUMERIC-FAIL' not in out
    assert ('FreeCom version 0.86' if system=='freedos' else 'MS-DOS Version 6.22') in out
    assert re.search(r'\r\nFPNUM-END\r\n(?:\r\n)?C:\\>$',out)
    markers=re.findall(r'X87-NUMERIC-PROOF=([0-9A-F]{8})\r\n',out);assert len(markers)==1
    address=int(markers[0],16);raw=lambda at:struct.unpack_from('<I',m,at)[0]
    def physical(at):
        """Walks the reported classic 4-KiB page tables to translate a guest linear proof address."""
        if not c['pagingEnabled']:return at
        pde=raw((c['cr3']&0xfffff000)+((at>>20)&0xffc));assert pde&1
        pte=raw((pde&0xfffff000)+((at>>10)&0xffc));assert pte&1
        return (pte&0xfffff000)|(at&4095)
    def read(at,size):"""Reads checked guest proof bytes through the actual reported page tables, including page crossings."""; return bytes(m[physical(at+i)] for i in range(size))
    proof=read(address,1024);u32=lambda off:struct.unpack_from('<I',proof,off)[0]
    assert proof[:8]==b'FPNM0001' and (u32(8),u32(12),u32(20),u32(232),u32(236))==(1,1,0,len(vectors),1)
    index=p.index(b'FPNMADDR')+8;assert struct.unpack_from('<I',p,index)[0]==len(NAMES)==14
    labels=dict(zip(NAMES,struct.unpack_from('<14I',p,index+4)));base=address-labels['proof'];at=lambda name:base+labels[name]
    assert base==u32(80) and base%16==0 and labels['end']==len(p)+0x100 and len(p)<65280
    assert read(base+0x100,labels['code_end']-0x100)==p[:labels['code_end']-0x100]
    assert (u32(96),u32(104))==(int(paging),int(protected))
    assert (u32(216),u32(220))==((24,32) if protected else (base>>4,base>>4))
    native={v['id']:v for v in reference['results']};assert reference['referenceOnly'] and reference['architecture']=='host-x87-x64'
    expected_esc=4;results=[];counts={};flags_seen=0;unmasked=0;rounded=0;active=0
    for i,v in enumerate(vectors):
        av,bv=v['a'],v['b'];kind=v['kind'];counts[kind]=counts.get(kind,0)+1
        source=struct.pack('<4HQHQHIQ',v['control'],v['operation'],kind,i,int(av['significand'],16),int(av['exponent'],16),
            int(bv['significand'],16),int(bv['exponent'],16),0,int(v['bits'],16))
        assert read(at('vectors')+i*40,40)==source
        b=read(at('results')+i*128,128);expected=native[v['id']]
        cw,sw,tw=(struct.unpack_from('<H',b,k)[0] for k in (0,4,8))
        assert (cw,sw,tw)==(expected['control'],expected['status'],expected['tags']),(i,v['id'],'control/status/tags')
        top=sw>>11&7;flags_seen|=sw&63;unmasked+=bool(sw&0x80);rounded+=bool(sw&0x200)
        active_bytes=[]
        for j in range(8):
            if (tw>>(((top+j)&7)*2)&3)==3:continue
            actual=struct.unpack_from('<QH',b,28+j*10);ex=expected['registers'][j]
            assert actual==(int(ex['significand'],16),int(ex['exponent'],16)),(i,v['id'],j,'80-bit register')
            active+=1
            active_bytes.append(b[28+j*10:38+j*10])
        if 4<=kind<=8:assert b[108:116].hex().upper()==expected['memory'],(i,v['id'],'converted memory')
        else:assert b[108:116]==bytes([0xa5])*8
        assert b[116:]==struct.pack('<III',0xc0de0000+i,0xcafe0000+i,0xbeef0000+i)
        expected_esc+=6 if kind==0 else 4 if kind in (1,2,3,9,10) else 5
        # Exclude undefined empty payload and mode-specific pointers from the cross-CPU digest.
        results.append(b[:10]+b''.join(active_bytes)+b[108:])
    assert set(counts)==set(range(15))
    assert (len(vectors),flags_seen,unmasked,rounded,active)==(220,63,30,40,190)
    original=read(at('original'),108);restored=read(at('restored'),108)
    assert original==restored and original[28:]==bytes(80)
    assert tuple(struct.unpack_from('<H',original,k)[0] for k in (0,4,8))==(0x37f,0,0xffff)
    for name in ('original','restored'):
        assert read(at(name)-4,4)==struct.pack('<I',0xface0000+108) and read(at(name)+108,4)==struct.pack('<I',0xcafe0000+108)
    assert c['x87Instructions']==expected_esc and (c['fpuControlWord'],c['fpuStatusWord'],c['fpuTagWord'],c['fpuTop'])==(0x37f,0,0xffff,0)
    assert c['fpuOpcode']==c['fpuInstructionPointer']==c['fpuDataPointer']==c['fpuCodeSelector']==c['fpuDataSelector']==0
    assert c['fpuRegisters']==[dict(logical=i,empty=True,significand='0000000000000000',signExponent='0000') for i in range(8)]
    assert not c['tripleFaulted'] and c['pageFaults']==c['alignmentFaults']==0
    assert c['protectedMode']==c['pagingEnabled']==managed
    if protected:
        assert c['tr']==72 and c['trCache']['base']==at('tss')
        assert c['cr0']==u32(24)==u32(28) and c['cr3']==u32(32)==u32(36)
        for a,b in ((40,44),(48,52),(56,60),(64,68),(72,76)):assert u32(a)==u32(b)
        if paging:
            directory,table=u32(88),u32(92);assert directory%4096==0 and table==directory+4096
            assert raw(directory)&0xfffff007==table|7
            for i in range(1024):
                pte=raw(table+4*i);assert pte&0xfffff007==i*4096|7 and pte&0xf98==0
    elif managed:assert c['virtual8086Instructions']>1000 and c['virtual8086Entries']-c['virtual8086Exits'] in (0,1)
    return dict(passed=True,system=system,protectedProbe=protected,privatePaging=paging,proofAddress=address,
        cases=len(vectors),allSixNumericalFlags=flags_seen,unmaskedEnvironments=unmasked,roundedUpCases=rounded,
        active80BitRegistersCompared=active,x87Instructions=expected_esc,kindCounts=counts,
        proofSha256=hashlib.sha256(proof).hexdigest().upper(),numericDigest=hashlib.sha256(b''.join(results)).hexdigest().upper(),
        fullFpuOriginalImageRestored=True,privateTrRemainsUntilReset=protected,hostDoubleUsed=False,
        hardwareReferenceIsPhysical486=False,allX87InstructionsAccepted=False,ferrBoardWiringAccepted=False,cleanDosReturn=True)


def negative_controls(r,m,p,vectors,reference,system,protected,paging,root):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    assert not system.startswith('emm386')
    good=verify_data(r,m,p,vectors,reference,system,protected,paging);a=good['proofAddress'];index=p.index(b'FPNMADDR')+12
    labels=dict(zip(NAMES,struct.unpack_from('<14I',p,index)));base=a-labels['proof'];changes=[];first={}
    for i,v in enumerate(vectors):first.setdefault(v['kind'],i)
    for key in (12,20,96,104,216,220,232,236):changes.append(('proof-'+str(key),'memory',a+key))
    for kind,i in first.items():
        at=base+labels['results']+i*128
        for off in (0,4,8):changes.append((f'kind-{kind}-state-{off}','memory',at+off))
        if kind not in (4,5,6,7,8):changes.append((f'kind-{kind}-low80','memory',at+28))
        else:changes.append((f'kind-{kind}-memory','memory',at+108))
    for i in range(0,len(vectors),23):
        for off in (116,120,124):changes.append((f'row-{i}-guard-{off}','memory',base+labels['results']+i*128+off))
    for name in ('original','restored'):
        changes.append((name+'-image','memory',base+labels[name]+28))
        changes.append((name+'-guard','memory',base+labels[name]-4))
    changes += [('source-vector','memory',base+labels['vectors']+8),('code','program',0),('cpu-count','cpu','x87Instructions'),('cpu-fpu','cpu','fpuStatusWord')]
    if paging:changes.append(('pte','memory',struct.unpack_from('<I',m,a+92)[0]+4))
    for name,kind,at in changes:
        rr=copy.deepcopy(r) if kind=='cpu' else r
        target=m if kind=='memory' else p
        if kind=='cpu':rr['cpu'][at]^=1
        else:target[at]^=1
        try:
            try:verify_data(rr,m,p,vectors,reference,system,protected,paging)
            except (AssertionError,ValueError,KeyError,struct.error):pass
            else:raise AssertionError('Oracle accepted '+name)
        finally:
            if kind!='cpu':target[at]^=1
    root=Path(root);root.mkdir(parents=True,exist_ok=False)
    summary=dict(rejected=len(changes),controls=[item[0] for item in changes])
    (root/'summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    return summary


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('report','memory','program','vectors','reference','system'):parser.add_argument('--'+name,required=True)
    parser.add_argument('--protected',action='store_true');parser.add_argument('--paging',action='store_true');parser.add_argument('--negative-controls')
    a=parser.parse_args();r=json.loads(Path(a.report).read_text(encoding='utf-8-sig'));m=bytearray(Path(a.memory).read_bytes());p=bytearray(Path(a.program).read_bytes())
    vectors=json.loads(Path(a.vectors).read_text(encoding='utf-8'));reference=json.loads(Path(a.reference).read_text(encoding='utf-8'))
    root=Path(a.reference).parent
    assert hashlib.sha256((root/'vectors.json').read_bytes()).hexdigest().upper()==reference['vectorsSha256']
    assert hashlib.sha256((root/'native-reference.bin').read_bytes()).hexdigest().upper()==reference['nativeCodeSha256']
    result=verify_data(r,m,p,vectors,reference,a.system,a.protected,a.paging)
    if a.negative_controls:result['negativeControls']=negative_controls(r,m,p,vectors,reference,a.system,a.protected,a.paging,a.negative_controls)
    print(json.dumps(result,indent=2))
