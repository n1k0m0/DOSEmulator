# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent real DOS/INT13 metadata, return, private scratch PIO and unchanged-program oracle."""
import argparse
import copy
import json
from pathlib import Path
import re
import struct


def require(value,reason):
    """Reject a missing or contradictory executed guest effect."""
    if not value:raise AssertionError(reason)


def verify(report,ram,events,program,manager):
    """Reconstruct twelve actual calls and all accepted private-disk word transfers."""
    require(report['status']['fault'] is None and 'AUDIT-BIOS-FAILED' not in report['status']['debugOutput'],'guest fault/failure')
    require('AUDIT-BIOS-OK' in report['status']['debugOutput'] and re.search(r'(?:^|\r?\n)AUDIT-BIOS-DOS-RETURN\r?\n(?:\r?\n)?C:\\>',report['text']),'actual DOS return')
    require(report['scenario']['complete'] and report['scenario']['inputBytesSent']==report['scenario']['inputBytesTotal'],'actual completed input')
    require(report['ataSectorsWritten']==2 and report['floppySectorsWritten']==0,'exact private write count')
    disks=report['status']['hardDisks'];require(len(disks)==2 and disks[1]['slot']==1 and disks[1]['bytes']==2097152 and disks[1]['writable'],'fresh private scratch binding')
    if manager:require(report['cpu']['pagingEnabled'] and report['cpu']['protectedMode'] and report['cpu']['virtual8086Instructions']>0,'actual manager path')
    match=re.search(r'AUDIT-BIOS-PROOF=([0-9A-F]{4}):([0-9A-F]{4})',report['status']['debugOutput']);require(match is not None,'actual proof pointer')
    segment,offset=(int(v,16) for v in match.groups());base=segment*16;proof_at=program.index(b'ABMETA01')
    require(offset==proof_at+256,'compiled proof offset');immutable=program.index(b'ABMUTBL1')+8

    def physical(address):
        """Walk only classic guest page tables, independently of emulator CPU helpers."""
        if not report['cpu']['pagingEnabled']:return address
        pde=struct.unpack_from('<I',ram,(report['cpu']['cr3']&~4095)+(address>>22)*4)[0];require(pde&1 and not pde&128,'classic resident directory')
        pte=struct.unpack_from('<I',ram,(pde&~4095)+(address>>12&1023)*4)[0];require(pte&1,'resident page');return (pte&~4095)+(address&4095)

    def read(address,size):
        """Read only the private proof's independently translated conventional RAM."""
        return bytes(ram[physical(a)] for a in range(address,address+size))

    require(read(base+256,immutable)==program[:immutable],'executed immutable code and case table')
    proof=read(base+offset,16+12*64+15+32+544)
    require(proof[:8]==b'ABMETA01','proof signature');done,count,allocation,owner=struct.unpack_from('<4H',proof,8)
    require((done,count,owner)==(0xbeef,12,segment) and allocation>segment,'resident proof complete and private allocation')
    cases_at=program.index(b'ABCASES1')+8
    cases=[struct.unpack_from('<BHB',program,cases_at+i*4) for i in range(12)]
    expected_cases=[(f,o,bad) for f,limit in [(0x43,0xfff0),(0x42,0xfff0),(0x48,0xffe6)] for o,bad in [(0x100,0),(limit,0),(limit+1,1),(0xffff,1)]]
    require(cases==expected_cases,'exact independent case table')
    data_offset=proof_at+256+847;stack_top=len(program)+256
    parameters=struct.pack('<HHIIIQH',26,2,4,16,63,4096,512)
    pattern=bytes((i&255)^0x5a for i in range(512))
    for i,(function,request_offset,bad) in enumerate(expected_cases):
        row=proof[16+i*64:16+(i+1)*64];f,cf,ax,si,ds_return,si_return,sp,flags,status,reserved=struct.unpack_from('<BBHHHHHHBB',row)
        require((f,cf,ax,si,ds_return,si_return,sp,status,reserved)==(function,bad,bad<<8,request_offset,allocation,request_offset,stack_top,bad,0),'actual BIOS result/register/stack/status row'+str(i))
        require(flags&0x400 and flags&1==bad,'actual DF/CF row'+str(i))
        metadata=bytearray([0x7d]*48);metadata[8:40]=struct.pack('<BBHHHQ',26 if function==0x48 else 16,0,1,data_offset,segment,8)+bytes([0x7d]*16)
        if function==0x48 and not bad:metadata[8:34]=parameters
        require(row[16:]==metadata,'all metadata bytes/guards row'+str(i))
    require(proof[831:847]==bytes([0xa7]*16) and proof[847:1359]==pattern and proof[1359:1375]==bytes([0xa7]*16),'private data readback and guards')
    mcb=read(base-16,16);require(mcb[0] in (ord('M'),ord('Z')) and struct.unpack_from('<H',mcb,1)[0]==segment and struct.unpack_from('<H',mcb,3)[0]*16>=len(program)+256,'retained own DOS block')
    extra=read(allocation*16-16,16);require(extra[0] in (ord('M'),ord('Z')) and struct.unpack_from('<H',extra,1)[0]==segment and struct.unpack_from('<H',extra,3)[0]>=0x1100,'retained own metadata allocation')
    text='';active=False;registers=[0]*8;transfers=[]
    for event in events:
        port,value,width,write=(event[k] for k in ('port','value','width','write'))
        if port==0xe9 and write:
            text=(text+chr(value&255))[-100:]
            if 'AUDIT-BIOS-BEGIN' in text:active=True
            if 'AUDIT-BIOS-PROOF=' in text:break
        if not active or not 0x1f0<=port<=0x1f7:continue
        at=port-0x1f0
        if write and width==1 and 1<=at<=6:registers[at]=value
        if at==7 and write:
            require(width==1 and value in (0x20,0x30),'actual ATA data command')
            lba=registers[3]|registers[4]<<8|registers[5]<<16|(registers[6]&15)<<24
            require(registers[6]&0xf0==0xf0 and lba==8 and registers[2]==1,'only private slave LBA8/count1')
            transfers.append(dict(write=value==0x30,words=[]))
        elif at==0:
            require(transfers and width==2 and write==transfers[-1]['write'],'actual word grant');transfers[-1]['words'].append(value)
    require(len(transfers)==4 and [t['write'] for t in transfers]==[True,True,False,False],'no ATA command for rejected metadata or AH48')
    words=list(struct.unpack('<256H',pattern));require(all(t['words']==words for t in transfers),'all1024 actual private ATA words')
    return dict(cases=12,acceptedCalls=6,rejectedCalls=6,ataCommands=4,ataWords=1024,metadataBytes=12*48,residentUntilReset=True)


def main():
    """Check actual guest evidence and independent corruptions of result, RAM and PIO."""
    parser=argparse.ArgumentParser();parser.add_argument('--report',type=Path,required=True);parser.add_argument('--memory',type=Path,required=True);parser.add_argument('--io',type=Path,required=True);parser.add_argument('--program',type=Path,required=True);parser.add_argument('--manager',action='store_true');parser.add_argument('--negative-controls',action='store_true');args=parser.parse_args()
    report=json.loads(args.report.read_text(encoding='utf-8-sig'));ram=args.memory.read_bytes();events=[json.loads(line) for line in args.io.read_text(encoding='utf-8-sig').splitlines()];program=args.program.read_bytes();proof=verify(report,ram,events,program,args.manager);negative=0
    if args.negative_controls:
        for key,value in [('ataSectorsWritten',1),('floppySectorsWritten',1),('text','C:\\>echo AUDIT-BIOS-DOS-RETURN\r\n')]:
            bad=copy.deepcopy(report);bad[key]=value
            try:verify(bad,ram,events,program,args.manager)
            except AssertionError:negative+=1
            else:raise AssertionError('accepted report corruption')
        match=re.search(r'AUDIT-BIOS-PROOF=([0-9A-F]{4}):([0-9A-F]{4})',report['status']['debugOutput']);seg,off=(int(v,16) for v in match.groups());base=seg*16
        def physical(address):
            """Resolve only own corruption addresses through the independently checked original tables."""
            if not report['cpu']['pagingEnabled']:return address
            pde=struct.unpack_from('<I',ram,(report['cpu']['cr3']&~4095)+(address>>22)*4)[0];pte=struct.unpack_from('<I',ram,(pde&~4095)+(address>>12&1023)*4)[0];return (pte&~4095)+(address&4095)
        targets=[256,off+8,off+10,off+12,*[off+16+i*64+j for i in range(12) for j in (1,2,6,8,10,12,14,16,24,32,48,63)],off+831,off+847,off+1359]
        for address in targets:
            bad=bytearray(ram);bad[physical(base+address)]^=1
            try:verify(report,bad,events,program,args.manager)
            except AssertionError:negative+=1
            else:raise AssertionError('accepted RAM corruption '+hex(address))
        # A changed word, command, or private-device selection must not pass via a matching status record.
        for field in ('value','width'):
            bad=copy.deepcopy(events);start=next(i for i,e in enumerate(bad) if e['port']==0x1f7 and e['write'] and e['value']==0x30)
            at=next(i for i in range(start,len(bad)) if bad[i]['port']==0x1f0);bad[at][field]^=1
            try:verify(report,ram,bad,program,args.manager)
            except AssertionError:negative+=1
            else:raise AssertionError('accepted PIO corruption')
    proof.update(passed=True,negativeOracles=negative);print(json.dumps(proof))


if __name__=='__main__':main()
