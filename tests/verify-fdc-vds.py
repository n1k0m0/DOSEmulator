"""Independent guest/MMU/VDS/FDC/journal oracle; no emulator implementation imports."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import re
import struct


def module(name):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(name+'.py'))
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result)
    return result


def verify(report_path,memory_path,program_path,base_path,cow_path,trace_path,mode):
    report=json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory=Path(memory_path).read_bytes();program=Path(program_path).read_bytes()
    output=report['status']['debugOutput'];cpu=report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent']==report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount']==report['ataSectorsWritten']==cpu['pageFaults']==0
    assert report['ataSectorsRead']>0 and report['floppySectorsRead']>0
    assert re.search(r'\r\nVDS-GUEST-RETURN\r\n\r\nC:\\>$',output)
    marker=re.findall(r'FDC-VDS-PROOF=([0-9A-F]{8})\r\n',output)
    assert len(marker)==1
    address=int(marker[0],16)
    raw=lambda a:struct.unpack_from('<I',memory,a)[0]

    def physical(linear):
        if not cpu['pagingEnabled']:return linear
        pde=raw((cpu['cr3']&~4095)+(linear>>22)*4);assert pde&1
        pte=raw((pde&~4095)+((linear>>12)&1023)*4);assert pte&1
        result=(pte&~4095)|(linear&4095);assert result<len(memory)
        return result

    read=lambda a,n:bytes(memory[physical(a+i)] for i in range(n))
    proof=read(address,1280);assert proof[:8]==b'CPVDS001'
    u32=lambda n:struct.unpack_from('<I',proof,n)[0]
    u16=lambda n:struct.unpack_from('<H',proof,n)[0]
    assert u32(8)==1
    labels=program.index(b'CPVDADDR')+8
    start,allocation,proof_label,dds,ems_allocated,map_saved,locked,disabled=struct.unpack_from('<8H',program,labels)
    assert start==0x100 and program.index(b'CPVDS001')==proof_label-start
    origin=address-proof_label
    assert read(origin+start,allocation-start)==program[:allocation-start], 'Guest instruction bytes differ'
    assert read(origin+allocation,2)==b'\0\0'
    for label in (ems_allocated,map_saved,locked,disabled):assert read(origin+label,1)==b'\0'
    packets=module('verify-floppy-format').parse_protocol(trace_path)
    operations=[p for p in packets if (p['command'][0]&31) in (5,6,13) and p['command'][1]&3==1]
    disk,journal=module('verify-msdos-install').materialize(base_path,cow_path)
    base=Path(base_path).read_bytes();assert len(disk)==len(base)==1474560
    if mode=='unavailable':
        assert not cpu['pagingEnabled'] and not cpu['virtual8086']
        assert u32(12)==u32(16)==0 and u32(24)==1
        assert 'FDC-VDS-FAIL\r\n' in output and 'FDC-VDS-PASS' not in output
        assert not operations and disk==base and report['floppySectorsWritten']==0
        return dict(passed=True,mode=mode,noVdsReportedAsUnavailable=True,noScratchIo=True,guestCodeVerified=True)
    ems=mode=='RAM';regions=2 if ems else 1;transfers=regions*2
    # The stop marker can be rendered while an EMM386 IRQ handler is active.
    assert cpu['pagingEnabled'] and cpu['virtual8086Entries']>10
    assert u32(12)==1 and u32(16)==(0x7f if ems else 0x67) and u32(24)==4
    assert u32(20)==u32(68)==0
    assert 'FDC-VDS-PASS\r\n' in output and 'FDC-VDS-FAIL' not in output
    assert u16(28)==0x100 and u16(30)==1 and u16(34)&~15==0 and u32(36)>=16384
    assert u16(64)==struct.unpack('<H',read(0x40e,2))[0] and u16(64)!=0x9fc0
    assert u32(148)==u32(152)==u32(156)==transfers
    assert u32(160)==u32(164)==regions and u32(168)==1
    conventional=(u16(40)<<4)+u32(44)
    assert conventional&65535==65520
    assert physical(conventional)==conventional and physical(conventional+511)==conventional+511
    pattern=bytes((0x31+i*0x17)&255 for i in range(512))
    assert proof[256:768]==pattern
    if ems:
        assert 0xc000<=u16(48)<=0xe000 and u16(52)==u16(54)>2
        assert all(p>=1048576 and p%4096==0 for p in (u32(56),u32(60)))
        assert u32(60)!=u32(56)+4096 and u32(56)!=(u16(48)<<4)+0x3000
        assert proof[768:1280]==pattern
    else:
        assert proof[48:64]==bytes(16) and proof[768:1280]==bytes(512)
    direct_buffers=[]
    for i in range(transfers):
        size,offset,segment,buffer_id,physical_buffer=struct.unpack_from('<IIHHI',proof,80+i*16)
        assert size==512 and buffer_id!=0
        if i<2:assert segment==u16(40) and offset==u32(44)
        else:assert segment==u16(48) and offset==0x3ff0
        assert physical_buffer!=segment*16+offset
        assert physical_buffer+512<=min(1<<24,len(memory))
        assert (physical_buffer&65535)+512<=65536
        direct_buffers.append(physical_buffer)
    expected=[]
    for i in range(regions):
        expected.extend([(0x45,2,0,i+1),(0x46,2,0,i+1),(0x45,2,0,i+3),(0x46,2,0,i+3),(0x46,2,0,i+3)])
    assert len(operations)==len(expected)+1
    bios_bounce=(u16(64)<<4)+0x200
    for i,(packet,(command,cylinder,head,sector)) in enumerate(zip(operations,expected)):
        assert packet['command']==[command,1,cylinder,head,sector,2,sector,0x1b,0xff]
        assert packet['result'][0]&0xc0==0 and packet['result'][1:3]==[0,0]
        assert packet['completedAt']>packet['time']
        within=i%5;region=i//5
        buffer=direct_buffers[region*2+within] if within<2 else physical(bios_bounce)
        assert packet['dma']==dict(address=buffer&65535,count=511,page=buffer>>16,mode=0x4a if command==0x45 else 0x46,masked=False)
    formatted=operations[-1]
    assert formatted['command']==[0x4d,5,2,18,0x6c,0xf6]
    assert formatted['result'][0]&0xc0==0 and formatted['result'][1:3]==[0,0]
    assert formatted['maskedAfterResult']
    assert formatted['dma']==dict(address=physical(bios_bounce)&65535,count=71,page=physical(bios_bounce)>>16,mode=0x4a,masked=False)
    assert report['floppySectorsWritten']==18+regions*2
    expected_sectors={72+i:pattern for i in range(regions)}|{74+i:pattern for i in range(regions)}
    expected_sectors.update({i:b'\xf6'*512 for i in range(126,144)})
    for sector in range(2880):
        assert disk[sector*512:(sector+1)*512]==expected_sectors.get(sector,base[sector*512:(sector+1)*512]),sector
    # Every append record is checked as well as the final materialized image.
    cow=Path(cow_path).read_bytes();records=[]
    for pos in range(48,len(cow),552):
        lba=struct.unpack_from('<q',cow,pos)[0];records.append(lba)
        assert lba in expected_sectors and cow[pos+8:pos+520]==expected_sectors[lba]
    assert len(records)==len(expected_sectors) and set(records)==set(expected_sectors)
    return dict(passed=True,mode=mode,vdsVersion='1.0',providerProduct=u16(30),bufferLimit=u32(36),
                privateEbdaSegment=u16(64),bufferedTransfers=transfers,balancedTranslationCalls=transfers,
                unlocks=transfers,noncontiguousEms=ems,emsFreePagesRestored=ems,physicalDmaAddresses=direct_buffers,
                biosWriteReadVerifyRegions=regions,formatTracks=1,all2880ScratchSectorsChecked=True,
                guestInstructionBytesVerified=True,cleanDosReturn=True,journal=journal)


def negative_controls(paths,root):
    root=Path(root);root.mkdir(exist_ok=False)
    report,memory,program,base,cow,trace=map(Path,paths[:6]);mode=paths[6]
    original=json.loads(report.read_text(encoding='utf-8-sig'));ram=memory.read_bytes()
    # Accepted probes run in conventional identity-mapped memory.
    address=int(re.search(r'FDC-VDS-PROOF=([0-9A-F]{8})',original['status']['debugOutput'])[1],16)
    mutations=[('pass',address+12,0),('flags',address+16,0),('balance',address+156,0),
               ('buffer-id',address+90,0),('buffer-address',address+95,0),('data-capture',address+256,0)]
    if mode=='RAM':mutations.extend([('ems-mapping',address+56,0),('ems-free',address+54,0)])
    rejected=[]
    def reject(label,args):
        try:verify(*args)
        except (AssertionError,ValueError,KeyError,IndexError,struct.error):rejected.append(label)
        else:raise AssertionError('Corrupt acceptance was accepted: '+label)
    for label,offset,value in mutations:
        changed=bytearray(ram);changed[offset]=value if changed[offset]!=value else 255
        path=root/(label+'.ram');path.write_bytes(changed)
        reject(label,(report,path,program,base,cow,trace,mode))
    changed=copy.deepcopy(original);changed['status']['debugOutput']=changed['status']['debugOutput'].replace('VDS-GUEST-RETURN','FORGED-RETURN')
    path=root/'return.json';path.write_text(json.dumps(changed),encoding='utf-8')
    reject('dos-return',(path,memory,program,base,cow,trace,mode))
    changed=bytearray(program.read_bytes());changed[0]^=1;path=root/'code.com';path.write_bytes(changed)
    reject('guest-code',(report,memory,path,base,cow,trace,mode))
    # Fully hash-valid overlay damage must be caught by expected data, not just its checksum.
    import hashlib
    changed=bytearray(cow.read_bytes());changed[56]^=1
    changed[568:600]=hashlib.sha256(changed[48:568]).digest()
    path=root/'data.cow';path.write_bytes(changed)
    reject('journal-content',(report,memory,program,base,path,trace,mode))
    return dict(passed=True,rejected=len(rejected),cases=rejected)


if __name__=='__main__':
    p=argparse.ArgumentParser()
    for key in ('report','memory','program','base','cow','trace'):p.add_argument('--'+key,type=Path,required=True)
    p.add_argument('--mode',choices=('NOEMS','RAM','unavailable'),required=True)
    p.add_argument('--negative-controls',type=Path)
    a=p.parse_args();paths=(a.report,a.memory,a.program,a.base,a.cow,a.trace,a.mode)
    result=verify(*paths)
    if a.negative_controls:result['negativeControls']=negative_controls(paths,a.negative_controls)
    print(json.dumps(result,indent=2))
