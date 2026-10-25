"""Independent assembled-byte/RAM/frame/descriptor/paging/DOS-return oracle."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path,memory_path,program_path,os_name,paging=False):
    report=json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory=Path(memory_path).read_bytes();program=Path(program_path).read_bytes()
    output=report['status']['debugOutput'];cpu=report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent']==report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount']==report['ataSectorsWritten']==report['floppySectorsWritten']==0
    assert report['ataSectorsRead']>0 and report['floppySectorsRead']>0
    assert 'CPU-CALL-PASS\r\n' in output and 'CPU-CALL-FAIL' not in output
    assert ('MS-DOS Version 6.22' if os_name=='msdos' else 'FreeCom version 0.86') in output
    assert re.search(r'\r\nCALL-GUEST-END\r\n(?:\r\n)?C:\\>$',output)
    markers=re.findall(r'CPU-CALL-PROOF=([0-9A-F]{8})\r\n',output);assert len(markers)==1
    address=int(markers[0],16);proof=memory[address:address+512]
    assert proof[:8]==b'CPCG0001'
    u32=lambda offset:struct.unpack_from('<I',proof,offset)[0]
    u16=lambda offset:struct.unpack_from('<H',proof,offset)[0]
    raw=lambda offset:struct.unpack_from('<I',memory,offset)[0]
    assert u32(8)==u32(12)==1 and u32(16)==4095 and u32(20)==0
    symbols=program.index(b'CPCGADDR')+8
    (start,code_end,proof_label,after_gdt,after_ldt,after_same,after_conform,bad,after_bad,absent,after_absent,
     after_word,after_from16,stack_a,stack_b,stack16,user32,user16,tss,gdt,ldt,storage,end)=struct.unpack_from('<23I',program,symbols)
    assert start==0x100 and end==len(program)+0x100 and program.index(b'CPCG0001')==proof_label-start
    basis=address-proof_label;assert basis==u32(80) and basis&15==0
    assert memory[basis+start:basis+code_end]==program[:code_end-start], 'Executed guest instruction bytes differ'
    assert program[after_gdt-start-6:after_gdt-start]==b'\x66\x9a\xef\xbe\x68\0'
    assert program[after_ldt-start-6:after_ldt-start-4]==b'\xff\x1d'
    assert program[after_word-start-8:after_word-start-6]==b'\x66\x9a'
    assert program[after_from16-start-5:after_from16-start-2]==b'\x9a\xef\xbe'
    for label,selector in ((bad,128),(absent,136)):
        assert program[label-start:label-start+7]==b'\x9a'+bytes(4)+struct.pack('<H',selector)
    assert after_bad==bad+7 and after_absent==absent+7
    for first,last in ((24,28),(32,36),(40,44),(48,52),(56,60),(64,68),(72,76)):
        assert u32(first)==u32(last),(first,last)
    assert u32(24)&0x80000001==0 and u32(96)==int(paging)
    assert u32(100)==2 and u32(104)==u32(108)==1 and u32(112)==176 and u32(116)==0
    assert u16(424)==u16(426)==0
    frame=lambda offset:struct.unpack_from('<6I',proof,offset)
    user_pointer=0x10000+user32
    assert frame(128)==(0x10000+after_gdt,51,0x12345678,0x89abcdef,user_pointer-8,67)
    assert frame(160)==(0x10000+after_ldt,51,0x13579bdf,0x2468ace0,user_pointer-8,67)
    assert frame(128)[0]>65535  # full EIP despite 16-bit CALL operand
    assert struct.unpack_from('<2I',proof,192)==(0x10000+after_same,51)
    assert struct.unpack_from('<2I',proof,208)==(0x10000+after_conform,51)
    assert u32(224)==u32(228)==u32(232)==user_pointer
    assert (u32(236),u32(240),u32(244))==(0x10000+bad,128,0x10002)
    assert (u32(248),u32(252),u32(256))==(0x10000+absent,136,0x10002)
    assert u32(260)==u32(264)==0x10000+stack_b-24
    assert struct.unpack_from('<6H',proof,288)==(after_word,91,0x1357,0x2468,user16-4,99)
    assert u32(304)==u32(308)==user16<65536
    assert frame(320)==(after_from16,91,0x11111111,0x22222222,user16-8,99)
    assert [u32(n) for n in range(352,400,4)]==[0xec,0xe4,0xec,0,80,80,40,40,3,3,67,67]
    assert all(u32(n)==2 for n in (400,404,408,412,416,420))
    assert [u32(n) for n in (460,464,468)]==[0xc0dea001,0xc0deb002,0xc0dec003]
    for stack,guard,count in ((stack_a,0xc0dea001,384),(stack_b,0xc0deb002,384),(stack16,0xc0dec003,384)):
        assert raw(basis+stack-count-4)==guard and raw(basis+stack)==(guard^0x0a200000)
    assert raw(basis+user32)==0xcafed004 and raw(basis+user16)==0xcafee005
    for selector in (104,112,120,128,136,144,152,168,192,200):
        offset=gdt-start+selector
        assert memory[basis+gdt+selector:basis+gdt+selector+8]==program[offset:offset+8],selector
    assert memory[basis+ldt:basis+ldt+8]==program[ldt-start:ldt-start+8]
    assert u32(84)==basis+tss
    assert raw(basis+tss+4)==stack16 and struct.unpack_from('<H',memory,basis+tss+8)[0]==80
    low,high=struct.unpack_from('<II',memory,basis+gdt+176)
    descriptor_base=(low>>16)|((high&255)<<16)|(high&0xff000000)
    assert descriptor_base==basis+tss and low&65535==0x67 and (high>>8)&255==0x8b
    assert not cpu['protectedMode'] and not cpu['pagingEnabled'] and not cpu['virtual8086'] and not cpu['tripleFaulted']
    assert cpu['cr0']==u32(24) and cpu['cr3']==u32(32) and cpu['currentPrivilegeLevel']==0
    assert cpu['protectedModeEntries']==cpu['protectedModeExits']==(2 if os_name=='msdos' else 1)
    assert cpu['callGateTransfers']==9 and cpu['privilegeStackSwitches']==cpu['outerPrivilegeReturns']==8
    assert cpu['tr']==176 and cpu['trCache']['base']==basis+tss and cpu['trCache']['limit']==0x67
    assert cpu['trCache']['access']==0x8b and cpu['trCache']['usable']
    assert cpu['ldtr']==0 and not cpu['ldtrCache']['usable']
    assert cpu['gdtr']['base']==u32(40) and cpu['gdtr']['limit']==u32(48)
    assert cpu['idtr']['base']==u32(56) and cpu['idtr']['limit']==u32(64)
    assert cpu['pageFaults']==0 and cpu['protectedInstructions']>100
    if paging:
        pd,pt=u32(88),u32(92)
        assert pd%4096==0 and pt==pd+4096
        assert basis+storage<=pd and pt+4096<=basis+end
        assert raw(pd)&~0x20==pt|7 and memory[pd+4:pd+4096]==bytes(4092)
        accessed=0
        for i in range(1024):
            entry=raw(pt+i*4);assert entry&~0x60==i*4096|7
            accessed+=bool(entry&0x20)
        assert accessed>=2 and cpu['pageWalks']>=2 and cpu['tlbInvalidations']>=4
    else:
        assert u32(88)==u32(92)==0 and cpu['pageWalks']==0
    return dict(passed=True,os=os_name,identityPaging=paging,proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),executableSha256=hashlib.sha256(program).hexdigest().upper(),
                actualGateTransfers=9,innerStackSwitches=8,outerReturns=8,wordAndDwordParameterFrames=True,
                operandWidthMismatchVerified=True,ldtGateIndexZero=True,sameAndConformingAndJumpGates=True,
                actualGpAndNpFrames=True,picTablesCr0Cr3Restored=True,privateTrRemainsUntilReset=True,
                cleanDosReturn=True,hardwareTaskSwitch=False,all486PrecisionAccepted=False)


def negative_controls(paths,directory):
    report_path,memory_path,program_path,os_name,paging=paths
    directory=Path(directory);directory.mkdir(exist_ok=False)
    report=json.loads(Path(report_path).read_text(encoding='utf-8-sig'));memory=Path(memory_path).read_bytes()
    address=int(re.search(r'CPU-CALL-PROOF=([0-9A-F]{8})',report['status']['debugOutput'])[1],16)
    rejected=[]
    def reject(name,args):
        try:verify(*args)
        except (AssertionError,ValueError,KeyError,IndexError,struct.error):rejected.append(name)
        else:raise AssertionError('Corrupted call gate acceptance was accepted: '+name)
    for name,offset in (('success',12),('completion-bits',16),('full-return-eip',128),('parameter-order',136),
                        ('old-user-esp',144),('gp-selector',240),('np-restart-ip',248),('word-frame',292),
                        ('dword-frame-on-word-stack',336),('same-level-cpl',384),('conforming-cpl',388),('guard',460)):
        changed=bytearray(memory);changed[address+offset]^=1
        path=directory/(name+'.ram');path.write_bytes(changed)
        reject(name,(report_path,path,program_path,os_name,paging))
    changed=copy.deepcopy(report);changed['cpu']['callGateTransfers']=0
    path=directory/'counter.json';path.write_text(json.dumps(changed),encoding='utf-8')
    reject('actual-gate-counter',(path,memory_path,program_path,os_name,paging))
    changed=bytearray(Path(program_path).read_bytes());changed[0]^=1
    path=directory/'code.com';path.write_bytes(changed)
    reject('executable-bytes',(report_path,memory_path,path,os_name,paging))
    changed=copy.deepcopy(report);changed['status']['debugOutput']=changed['status']['debugOutput'].replace('CALL-GUEST-END','FORGED-RETURN')
    path=directory/'return.json';path.write_text(json.dumps(changed),encoding='utf-8')
    reject('dos-return',(path,memory_path,program_path,os_name,paging))
    if paging:
        pt=struct.unpack_from('<I',memory,address+92)[0]
        for name,offset in (('paging-structure-address',address+88),('identity-pte',pt+18),('page-frame-rights',pt+4)):
            changed=bytearray(memory);changed[offset]^=4
            path=directory/(name+'.ram');path.write_bytes(changed)
            reject(name,(report_path,path,program_path,os_name,paging))
    return dict(passed=True,rejected=len(rejected),cases=rejected)


if __name__=='__main__':
    p=argparse.ArgumentParser()
    for key in ('report','memory','program'):p.add_argument('--'+key,type=Path,required=True)
    p.add_argument('--os',choices=('msdos','freedos'),required=True);p.add_argument('--paging',action='store_true')
    p.add_argument('--negative-controls',type=Path)
    a=p.parse_args();paths=(a.report,a.memory,a.program,a.os,a.paging)
    result=verify(*paths)
    if a.negative_controls:result['negativeControls']=negative_controls(paths,a.negative_controls)
    print(json.dumps(result,indent=2))
