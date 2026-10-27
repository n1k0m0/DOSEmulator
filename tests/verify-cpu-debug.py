# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent opcode/RAM/frame oracle for the private 486 CPUDEBUG guest."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct

SYMBOLS = ('start code_end proof gdt tss kernel_sp user_sp gd_read after_gd exec_inc after_exec '
           'data_read after_read data_write after_write tf_inc after_tf ss_load ss_next ss_after '
           'gp_read after_gp rep_copy after_rep page_read after_page watched rep_source rep_dest storage end').split()


def verify(report_path, memory_path, program_path, os_name, paging=False):
    """Validates cpu debug evidence from actual guest state, immutable program bytes and independently expected results."""
    report = json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory = Path(memory_path).read_bytes()
    program = Path(program_path).read_bytes()
    output, cpu = report['status']['debugOutput'], report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount'] == report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    assert report['ataSectorsRead'] > 0 and report['floppySectorsRead'] > 0
    assert 'CPU-DEBUG-PASS\r\n' in output and 'CPU-DEBUG-FAIL' not in output
    assert ('MS-DOS Version 6.22' if os_name == 'msdos' else 'FreeCom version 0.86') in output
    assert re.search(r'\r\nDEBUG-GUEST-END\r\n(?:\r\n)?C:\\>$', output)
    markers = re.findall(r'CPU-DEBUG-PROOF=([0-9A-F]{8})\r\n', output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address+1024]
    assert proof[:8] == b'CPDB0001'
    u32 = lambda off: struct.unpack_from('<I', proof, off)[0]
    u16 = lambda off: struct.unpack_from('<H', proof, off)[0]
    raw = lambda off: struct.unpack_from('<I', memory, off)[0]
    word = lambda off: struct.unpack_from('<H', memory, off)[0]
    symbols = program.index(b'CPDBADDR')+8
    assert struct.unpack_from('<I', program, symbols)[0] == len(SYMBOLS) == 31
    labels = dict(zip(SYMBOLS, struct.unpack_from('<31I', program, symbols+4)))
    basis = address-labels['proof']
    at = lambda name: basis+labels[name]
    full = lambda name: 0x10000+labels[name]
    assert labels['start'] == 0x100 and labels['end'] == len(program)+0x100
    assert basis == u32(80) and basis & 15 == 0
    ss_table = program.index(b'SSGDADDR') + 8
    ss_labels = struct.unpack_from('<8I', program, ss_table)
    ss_address = basis + ss_labels[0]
    ss_proof = memory[ss_address:ss_address+224]
    assert memory[basis+256+ss_table:basis+256+ss_table+32] == program[ss_table:ss_table+32]
    assert ss_proof[:8] == b'SSGD0001'
    count, stack, read_result, write_result, stack_segment, guard = struct.unpack_from('<6I', ss_proof, 8)
    assert (count, stack, read_result, write_result, stack_segment, guard) == (6, 0x10000+ss_labels[7]-4, 0, 0x12345678, 40, 0xc0de5584)
    for index, point in enumerate(ss_labels[1:7]):
        dr6, dr7, ip, cs, flags, eax, esp, ss = struct.unpack_from('<8I', ss_proof, 32+index*32)
        assert dr6 == (0xffff2ff0 if index < 2 else 0xffff4ff0) and dr7 == 0x400
        assert (ip,cs,esp,ss) == (0x10000+point,24,stack,40)
        assert flags == ((0x86 if paging else 2) if index < 2 else 0x146)
        if index >= 2:
            assert eax == 0
        if index < 2:
            assert eax == (0x2000 if index == 0 else 0x12345678)
            assert program[point-256:point-253] == (b'\x0f\x21\xc0' if index == 0 else b'\x0f\x23\xc0')
    assert memory[basis+0x100:basis+labels['code_end']] == program[:labels['code_end']-0x100]
    assert (u32(8), u32(12), u32(16), u32(20)) == (1, 1, 15, 0)
    for first, last in ((24,28),(32,36),(40,44),(48,52),(56,60),(64,68),(72,76)):
        assert u32(first) == u32(last), (first,last)
    for first in range(128,152,4):
        assert u32(first) == u32(first+24), first
    assert u32(24) & 0x80000001 == 0 and u32(96) == int(paging) and u32(120) == 0
    assert u32(84) == at('tss') and u16(124) == 72 and u16(126) == 0
    assert u32(192) == (u32(24)|1|(0x80010000 if paging else 0))
    assert [u32(n) for n in (100,104,108)] == [7+int(paging),1,int(paging)]
    assert [u32(n) for n in (200,204,208,212,216,220,224,228,232)] == [u32(128),8,0x89abcdef,9,full('user_sp'),0,
        labels['rep_source']+8,labels['rep_dest']+8,0x89abcdef if paging else 0]
    assert raw(at('watched')) == 0x11223344 and raw(at('watched')+4) == 0xc0de0101
    assert memory[at('rep_dest'):at('rep_dest')+8] == struct.pack('<II',0x11112222,0x33334444)
    assert memory[at('rep_source'):at('rep_source')+8] == struct.pack('<II',0x11112222,0x33334444)
    assert raw(at('rep_dest')+8) == 0xc0de0102

    instructions = {'gd_read':b'\x0f\x21\xc0','exec_inc':b'\x43','tf_inc':b'\x43',
                    'gp_read':b'\x0f\x21\xc0','ss_load':b'\x8e\xd0','ss_next':b'\xbc'+struct.pack('<I',full('user_sp')),
                    'rep_copy':b'\xf3\xa5','data_read':b'\xa1'+struct.pack('<I',labels['watched']),
                    'data_write':b'\xc7\x05'+struct.pack('<II',labels['watched'],0x11223344)}
    if paging:
        instructions['page_read'] = b'\x8b\x07'
    for name, opcode in instructions.items():
        offset=labels[name]-0x100
        assert program[offset:offset+len(opcode)] == opcode, name
    points = ['gd_read','exec_inc','after_read','after_write','after_tf','ss_after','rep_copy']+(['after_page'] if paging else [])
    causes = [0x2000,1,2,6,0x4000,0x4000,8]+([8] if paging else [])
    frames=[]
    for i,(point,cause) in enumerate(zip(points,causes)):
        record=struct.unpack_from('<12I',proof,256+i*48)
        status,control,ip,cs,flags,sp,ax,bx,cx,si,di,watched=record
        assert status == 0xffff0ff0|cause, (i,hex(status),hex(cause))
        assert control == (0x400 if i==0 else 0xf5f00459 if i==7 else 0xd5f00459)
        assert ip == full(point) and cs == (24 if i==0 else 51)
        assert flags & 0x30000 == (0x10000 if i==6 else 0)
        assert flags & 0x100 == (0x100 if i in (4,5) else 0)
        assert sp == (0 if i==0 else full('user_sp'))
        assert watched == (0x89abcdef if i<3 else 0x11223344), i
        if i==0:
            assert ax == 0x2000
        else:
            assert flags == (2 if i<4 else 0x106 if i<6 else 0x10006 if i==6 else 6)
            assert bx == (7 if i==1 else 8 if i<4 else 9)
            assert cx == (0x2468 if i<6 else 1 if i==6 else 0)
            assert ax == (0x13579bdf if i==1 else 0x89abcdef if i<5 or i==7 else 0x89ab0043)
            assert si == (0x10203040 if i<6 else labels['rep_source']+4 if i==6 else labels['rep_source']+8)
            assert di == (0x50607080 if i<6 else labels['rep_dest']+4 if i==6 else u32(116))
        frames.append(record)
    assert u16(640)==0 and u32(644)==full('gp_read') and u32(648)==51 and u32(652)==0x10006
    assert u32(656)==full('user_sp') and u32(660)==67
    for name,size,guard in (('kernel_sp',512,1),('user_sp',256,2)):
        assert raw(at(name)-size-4)==0xc0de0000+guard and raw(at(name))==0xcafe0000+guard
    low,high=struct.unpack_from('<II',memory,at('gdt')+72)
    assert ((low>>16)|((high&255)<<16)|(high&0xff000000))==at('tss')
    assert low&65535==0x67 and (high>>8)&255==0x8b
    assert raw(at('tss')+4)==full('kernel_sp') and word(at('tss')+8)==40
    assert memory[at('tss')+12:at('tss')+104]==bytes(92)
    assert not cpu['protectedMode'] and not cpu['pagingEnabled'] and not cpu['virtual8086'] and not cpu['tripleFaulted']
    assert cpu['cr0']==u32(24) and cpu['cr3']==u32(32) and cpu['tr']==72
    assert cpu['trCache']==dict(base=at('tss'),limit=0x67,access=0x8b,flags=0,usable=True)
    assert cpu['ldtr']==0 and not cpu['ldtrCache']['usable']
    assert cpu['gdtr']==dict(base=u32(40),limit=u32(48)) and cpu['idtr']==dict(base=u32(56),limit=u32(64))
    assert [cpu[k] for k in ('dr0','dr1','dr2','dr3','dr6','dr7')]==[u32(i) for i in range(128,152,4)]
    assert [cpu[k] for k in ('instructionBreakpointFaults','dataBreakpointTraps','singleStepTraps','generalDetectFaults')]==[1,3+int(paging),6,3]
    assert cpu['privilegeStackSwitches']==cpu['outerPrivilegeReturns']==8+2*int(paging)
    assert cpu['hardwareTaskSwitches']==cpu['callGateTransfers']==cpu['taskDebugTraps']==cpu['alignmentFaults']==0
    assert cpu['protectedModeEntries']==cpu['protectedModeExits']==(2 if os_name=='msdos' else 1)
    assert cpu['pageFaults']==int(paging)
    if paging:
        directory,table,page,offset=[u32(i) for i in (88,92,112,116)]
        assert directory%4096==0 and table==directory+4096 and page==directory+8192 and offset+basis==page
        assert u16(672)==4 and u32(676)==full('page_read') and u32(680)==51 and u32(684)==0x10006
        assert u32(688)==full('user_sp') and u32(692)==67 and u32(696)==cpu['cr2']==page
        assert raw(page)==0x89abcdef and raw(table+(page>>12)*4)==page|0x27
        assert raw(directory)==table|0x27
        assert memory[directory+4:directory+4096]==bytes(4092)
        for i in range(1024):
            pte=raw(table+i*4)
            assert pte&0xfffff000==i*4096 and pte&7==7 and pte&0xf98==0
    else:
        assert proof[672:700]==bytes(28) and u32(88)==u32(92)==0
    return dict(guestReturned=True,os=os_name,identityPaging=paging,proofSha256=hashlib.sha256(proof+ss_proof).hexdigest().upper(),
                proofAddress=address,executableSha256=hashlib.sha256(program).hexdigest().upper(),debugEntries=13+int(paging),ssContractFrames=6,
                instructionFaults=1,generalDetectFaults=3,dataTraps=3+int(paging),singleStepTraps=6,userGpFaults=1,
                actualPageFaults=int(paging),faultHasUnexecutedRegisters=True,trapHasCommittedMemory=True,
                rfRetry=True,ssShadowSuppressesInstructionFault=True,repKeepsCompletedElement=True,debugRegistersRestored=True,
                privateTrRemainsUntilReset=True,cleanDosReturn=True,allDebugOrSiliconPrecisionAccepted=False)


def negative_controls(report_path,memory_path,program_path,os_name,paging,directory):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    root=Path(directory);root.mkdir(parents=True,exist_ok=False)
    report=json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    original=Path(memory_path).read_bytes();program=Path(program_path).read_bytes()
    base=verify(report_path,memory_path,program_path,os_name,paging);address=base['proofAddress']
    symbols=program.index(b'CPDBADDR')+12
    labels=dict(zip(SYMBOLS,struct.unpack_from('<31I',program,symbols)))
    basis=address-labels['proof']
    changes=[]
    ss_table = program.index(b'SSGDADDR')+8
    ss_address = basis+struct.unpack_from('<I',program,ss_table)[0]
    def ram(name,offset):"""Reads the requested actual guest-memory proof through the reported paging translation."""; changes.append((name,'memory',offset))
    for offset in range(0,224,4):
        ram('ss-contract-'+str(offset),ss_address+offset)
    for offset in (12,16,20,28,36,44,52,60,68,76,100,104,108,124,152,168,172,200,204,208,212,216,220,224,228):
        ram('proof-'+str(offset),address+offset)
    for i in range(7+int(paging)):
        for field in (0,4,8,12,16,20,24,28,32,36,40,44):
            if i==0 and field in (16,20,28,32,36,40):continue
            ram(f'frame-{i}-{field}',address+256+i*48+field)
    for name,delta in (('watched',0),('watched',4),('rep_dest',0),('rep_dest',4),('rep_dest',8),('rep_source',0),('kernel_sp',0),('user_sp',0),('gdt',77),('tss',8)):
        ram(f'{name}-{delta}',basis+labels[name]+delta)
    for field in (644,648,652,656,660):ram('gp-'+str(field),address+field)
    if paging:
        for field in (232,676,680,684,688,692,696):ram('page-'+str(field),address+field)
        table=struct.unpack_from('<I',original,address+92)[0]
        ram('page-table',table+4)
    for key in ('instructionBreakpointFaults','dataBreakpointTraps','singleStepTraps','generalDetectFaults','pageFaults','dr0','dr6','dr7','privilegeStackSwitches','outerPrivilegeReturns'):
        changes.append(('cpu-'+key,'cpu',key))
    changes.append(('wrong-opcode','program',labels['gd_read']-0x100))
    for name,kind,target in changes:
        mutated=bytearray(original);changed=copy.deepcopy(report);code=bytearray(program)
        if kind=='memory':mutated[target]^=1
        elif kind=='cpu':changed['cpu'][target]^=1
        else:code[target]^=1
        mp=root/'bad.private-ram.bin';rp=root/'bad.json';pp=root/'bad.COM'
        mp.write_bytes(mutated);rp.write_text(json.dumps(changed),encoding='utf-8');pp.write_bytes(code)
        try:verify(rp,mp,pp,os_name,paging)
        except (AssertionError,ValueError,KeyError,struct.error):pass
        else:raise AssertionError('Oracle accepted corrupted evidence: '+name)
    result={'rejected':len(changes),'controls':[item[0] for item in changes]}
    (root/'summary.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('report','memory','program','os'):parser.add_argument('--'+name,required=True)
    parser.add_argument('--paging',action='store_true')
    parser.add_argument('--negative-controls')
    args=parser.parse_args()
    result=verify(args.report,args.memory,args.program,args.os,args.paging)
    if args.negative_controls:
        result['negativeControls']=negative_controls(args.report,args.memory,args.program,args.os,args.paging,args.negative_controls)
    print(json.dumps(result,indent=2))
