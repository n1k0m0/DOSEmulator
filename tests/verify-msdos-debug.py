"""Verify original local MS-DOS DEBUG.EXE, its actual T output and test bytes."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct


def verify(report_path,memory_path,base_path,overlay_path,mode):
    spec=importlib.util.spec_from_file_location('dos_fat',Path(__file__).with_name('verify-msdos-install.py'))
    fat_module=importlib.util.module_from_spec(spec);spec.loader.exec_module(fat_module)
    disk,_=fat_module.materialize(base_path,overlay_path)
    start=struct.unpack_from('<I',disk,454)[0]*512
    files,_=fat_module.Fat(disk,start).files()
    debug_hash=hashlib.sha256(files['DOS/DEBUG.EXE']).hexdigest().upper()
    assert debug_hash=='091B2A580FC37CC689E69824218F1C38D80F316EECAAA30D3517DFD70C95B437'
    report=json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory=Path(memory_path).read_bytes();output=report['status']['debugOutput'];cpu=report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent']==report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount']==report['ataSectorsWritten']==report['floppySectorsWritten']==0
    assert 'MS-DOS Version 6.22' in output and re.search(r'\r\nMSDEBUG-END\r\n\r\nC:\\>$',output)
    assert 'C:\\>c:\\dos\\debug' in output and '-e 100 b8 34 12 40 90' in output and '-t=100' in output and '-q' in output
    pattern=(r'AX=([0-9A-F]{4})  BX=0000  CX=0000  DX=0000  SP=FFEE  BP=0000  SI=0000  DI=0000  \r\n'
             r'DS=([0-9A-F]{4})  ES=([0-9A-F]{4})  SS=([0-9A-F]{4})  CS=([0-9A-F]{4})  IP=([0-9A-F]{4})   '
             r'NV UP EI PL NZ NA (PO|PE) NC')
    registers=re.findall(pattern,output)
    assert len(registers)==3
    segment=None
    for i,row in enumerate(registers):
        ax,ds,es,ss,cs,ip,parity=row
        assert ax==('1234' if i==0 else '1235') and ip==('0103' if i==0 else '0104')
        assert parity==('PO' if i==0 else 'PE') and ds==es==ss==cs
        if segment is None:segment=int(cs,16)
        assert int(cs,16)==segment
    assert re.search(rf'{segment:04X}:0103 40\s+INC\s+AX',output)
    assert re.search(rf'{segment:04X}:0104 90\s+NOP',output)
    physical=segment*16+0x100
    if mode!='none':
        assert cpu['protectedMode'] and cpu['pagingEnabled'] and cpu['virtual8086Instructions']>0
        assert cpu['virtual8086Entries']-cpu['virtual8086Exits']==int(cpu['virtual8086'])
        assert cpu['virtual8086Exits']>0 and cpu['currentPrivilegeLevel']==(3 if cpu['virtual8086'] else 0)
        pde=struct.unpack_from('<I',memory,(cpu['cr3']&0xfffff000)+((physical>>20)&0xffc))[0]
        assert pde&1
        pte=struct.unpack_from('<I',memory,(pde&0xfffff000)+((physical>>10)&0xffc))[0]
        assert pte&1
        physical=(pte&0xfffff000)|(physical&4095)
        assert b'EMM386.EXE '+mode.encode('ascii') in files['CONFIG.SYS'].upper()
    else:
        assert not cpu['protectedMode'] and not cpu['pagingEnabled'] and not cpu['virtual8086']
    assert memory[physical:physical+5]==b'\xb8\x34\x12\x40\x90'
    assert cpu['singleStepTraps']==2 and cpu['dataBreakpointTraps']==cpu['instructionBreakpointFaults']==cpu['generalDetectFaults']==0
    assert cpu['dr6']&0x4000 and not cpu['tripleFaulted']
    return dict(passed=True,mode=mode,originalDebugExeSha256=debug_hash,traceSteps=2,
                registerSequence=['AX=1234 IP=0103','AX=1235 IP=0104'],parityChanges=True,
                testProgramSegment=segment,testPhysicalAddress=physical,actualProgramBytes=True,
                originalDebuggerReturnedToDos=True,completeDebuggerCompatibility=False)


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ('report','memory','base','overlay'):parser.add_argument('--'+name,required=True)
    parser.add_argument('--mode',choices=('none','NOEMS','RAM'),required=True)
    parser.add_argument('--negative-controls')
    args=parser.parse_args()
    result=verify(args.report,args.memory,args.base,args.overlay,args.mode)
    if args.negative_controls:
        root=Path(args.negative_controls);root.mkdir(parents=True,exist_ok=False)
        original=json.loads(Path(args.report).read_text(encoding='utf-8-sig'));memory=Path(args.memory).read_bytes()
        tests=['wrong-ax','wrong-ip','wrong-parity','wrong-code','missing-db','extra-db','reset','wrong-mode']
        for name in tests:
            r=copy.deepcopy(original);m=bytearray(memory)
            if name=='wrong-ax':r['status']['debugOutput']=r['status']['debugOutput'].replace('AX=1234','AX=4321')
            elif name=='wrong-ip':r['status']['debugOutput']=r['status']['debugOutput'].replace('IP=0103','IP=0102')
            elif name=='wrong-parity':r['status']['debugOutput']=r['status']['debugOutput'].replace('PO NC','PE NC')
            elif name=='wrong-code':m[result['testPhysicalAddress']+1]^=1
            elif name=='missing-db':r['cpu']['singleStepTraps']=1
            elif name=='extra-db':r['cpu']['instructionBreakpointFaults']=1
            elif name=='reset':r['hardwareResetCount']=1
            else:r['cpu']['pagingEnabled']=not r['cpu']['pagingEnabled']
            rp=root/'bad.json';mp=root/'bad.private-ram.bin'
            rp.write_text(json.dumps(r),encoding='utf-8');mp.write_bytes(m)
            try:verify(rp,mp,args.base,args.overlay,args.mode)
            except (AssertionError,ValueError,KeyError,struct.error):pass
            else:raise AssertionError('Oracle accepted '+name)
        result['negativeControls']={'rejected':len(tests),'controls':tests}
        (root/'summary.json').write_text(json.dumps(result['negativeControls'],indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,indent=2))
