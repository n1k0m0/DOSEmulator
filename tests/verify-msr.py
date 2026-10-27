# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reconstruct TSC writes, reads, fault restarts and PIT deadlines independently with Python integers."""
import argparse
import copy
import hashlib
import json
import struct
from pathlib import Path

MASK=(1<<64)-1
VALUES=[0,0x0123456789abcdef,0xffffffff,0x100000000,0xfffffffffffffff0,0x8000000000000000,0xfedcba9876543210,1]


def require(value,message):
    """Reject an inconsistent actual observation or deliberate oracle falsification."""
    if not value:raise ValueError(message)


def digest(data):
    """Hash named own evidence bytes without calling the emulator's numeric implementation."""
    return hashlib.sha256(data).hexdigest().upper()


def verify(program,ram,ledger,result,cut,final):
    """Derive every MSR/TSC value and physical interrupt deadline from recorded real operations."""
    require(result['passed'] and result['msrGuest'] and not result['fullPcSnapshot'] and not result['dosBoot'],'Scope')
    require(program[3:11]==b'DOSEMSR1' and len(ram)==2*1024*1024,'Own format')
    gp_handler,ud_handler,table=struct.unpack_from('<HHH',program,11)
    require(list(struct.unpack_from('<8Q',program,table-0x1000))==VALUES,'Immutable input values')
    require(ram[0x1000:0x1000+len(program)]==program,'Immutable program')
    require(result['programSha256']==digest(program) and result['ramSha256']==digest(ram),'Evidence identities')
    require(struct.unpack_from('<I',ram,0x5ffc)[0]==0xa5c33c5a and struct.unpack_from('<I',ram,0x6240)[0]==0x5a3cc3a5,'RAM guards')
    require(ram[0x5120:0x512a]==bytes.fromhex('35c26821a2da0fc90040'),'Raw x87 value')
    require(struct.unpack_from('<H',ram,0x5100)[0]==4 and struct.unpack_from('<III',ram,0x5104)==(0x524,0x135,4),'CPUID/TSD/IRQ count')
    require(struct.unpack_from('<HH',ram,0x5110)==(4,2) and ram[0x5140]==1,'Recovered faults/completion')
    require(result['output']=='DOSE P54C MSR PIT OK\r\n','Guest output')
    hz=result['frequency'];now=result['initialTime'];rem=0;epoch=0;writes=[];reads=[];tsc=[];faults=[];pulses=[];acks=[];eois=[];data=[];commit=None;pending=None;entries=[];saved=None;e9=[]
    require(hz in (33_000_000,90_000_000) and now==50_000_000_000,'Clock binding')
    for index,item in enumerate(ledger):
        kind=item['kind']
        if kind=='step':
            require(item['now']==now and item['cycles']==4,'Owner clock step')
            operation=item['operation'];ip=item['ip']-0x1000
            if operation in (0x30,0x31,0x32):
                locked=program[ip]==0xf0;prefix=program[ip:ip+item['prefix']]
                require(prefix in (b'',b'\x66',b'\x67',b'\x66\x67'),'Prefix bytes')
                offset=ip+(1 if locked else len(prefix));require(program[offset:offset+2]==bytes([0x0f,operation]),'Actual opcode')
                raw=(now*hz+rem)//1_000_000_000;value=(raw-epoch)&MASK
                vector=6 if locked else 13 if operation!=0x31 and item['beforeCx']!=0x10 else None
                require(item['entered']==vector,'Fault classification')
                if vector is not None:
                    require(pending==vector and item['frameSp']==0x8ffa and item['frameIp']==item['ip'] and item['frameCs']==0 and item['frameFlags']==0xe97,'Actual real fault frame')
                    require(item['afterIp']==(gp_handler if vector==13 else ud_handler),'Actual fault target')
                    require(item['eax']==item['beforeAx'] and item['edx']==item['beforeDx'] and item['ecx']==item['beforeCx'],'Fault GPR effects')
                    faults.append(item);pending=None
                else:
                    require(item['flags']==0x200e97 and item['ecx']==item['beforeCx'] and item['afterIp']==item['ip']+item['prefix']+2,'Successful flags/register width')
                    if operation==0x30:
                        written=item['beforeAx'] | item['beforeDx']<<32
                        require(item['eax']==item['beforeAx'] and item['edx']==item['beforeDx'],'WRMSR register preservation')
                        epoch=(raw-written)&MASK;writes.append((written,item['flags'],item['prefix']))
                    else:
                        require(item['eax'] | item['edx']<<32==value,'Independently reconstructed TSC')
                        (tsc if operation==0x31 else reads).append((value,item['flags'],item['prefix']))
            else:require(item['entered'] is None,'Unexpected guest exception')
            delta,rem=divmod(4_000_000_000+rem,hz);now+=delta
        elif kind=='idle':
            require(item['now']==now and item['target']>now,'Real halted advance');now=item['target']
        elif kind=='pulse':
            require(commit is not None and item['now']==commit+(len(pulses)+1)*1537*1_000_000_000//1_193_182 and item['now']<=now,'PIT integer deadline');pulses.append(item['now'])
        elif kind=='ack':
            require(item['now']==now and item['irq']==0 and item['vector']==8 and not item['spurious'] and len(acks)<len(pulses),'Physical IRQ0 acknowledge');acks.append(now)
        elif kind=='entry':
            require(item['now']==now and item['vector'] in (6,8,13),'Actual interrupt entry');entries.append(item['vector'])
            if item['vector']!=8:require(pending is None,'Nested unexpected fault');pending=item['vector']
        elif kind=='port':
            require(item['now']==now and item['write'] and item['width']==1,'Guest port effect')
            if item['port']==0x40:
                data.append(item['value'])
                if len(data)==2:commit=now
            if item['port']==0x20 and item['value']==0x20:eois.append(now)
            if item['port']==0xe9:e9.append(item['value'])
        else:raise ValueError('Unknown ledger kind')
        if index+1==result['ledgerCut']:saved=(now,rem,epoch)
    require(data==[1,6] and len(pulses)==len(acks)==len(eois)==4 and all(p<=a<=e for p,a,e in zip(pulses,acks,eois)),'Timer/IRQ/EOI chronology')
    require(entries.count(8)==4 and entries.count(13)==4 and entries.count(6)==2 and pending is None,'Actual entry counts')
    require(bytes(e9).decode('ascii')==result['output'],'Actual output bytes')
    require([v[0] for v in writes]==VALUES and [v[2] for v in writes]==[0,1,1,2,0,1,1,2],'Eight complete MSR writes')
    require(len(reads)==13 and len(tsc)==8 and len(faults)==6,'Actual reads/faults')
    for row in range(8):
        values=struct.unpack_from('<10I',ram,0x6000+row*64);expected_read=reads[row+row//2]
        require(values[0] | values[1]<<32==VALUES[row] and values[2]==0x200e97,'Written value/flags in guest RAM')
        require(values[3] | values[4]<<32==expected_read[0] and values[5]==expected_read[1],'RDMSR guest result')
        require(values[6] | values[7]<<32==tsc[row][0] and values[8]==tsc[row][1] and values[9]==row//2,'RDTSC guest result/physical IRQ count')
        require(tsc[row][0]==(expected_read[0]+24)&MASK,'Sequential read clock delta')
    for row in range(4):
        lo,hi,flags,irq=struct.unpack_from('<4I',ram,0x6200+row*16);require(lo | hi<<32==reads[row*3+2][0] and flags==0x200e97 and irq==row+1,'Read after true HLT/IRQ')
    bad_addresses=[0,0x10010,0x11,0x80000010,0x10,0x10]
    for row,address in enumerate(bad_addresses):
        require(struct.unpack_from('<4I',ram,0x6400+row*16)==(0xdeadbeef,0x76543210,address,0x200e97),'Recoverable rejection GPRs/flags')
        vector=13 if row<4 else 6;frame_base=0x6300+row*4 if row<4 else 0x6320+(row-4)*4
        require(faults[row]['entered']==vector and struct.unpack_from('<HH',ram,frame_base)==(faults[row]['ip'],0xe97),'Guest-recorded fault restart')
    require(struct.unpack_from('<Q',ram,0x6500)[0]==reads[-1][0],'Failed WRMSR leaves the live epoch intact')
    require(saved is not None and saved==(cut['scheduler']['now'],cut['scheduler']['remainder'],cut['components']['cpu']['fields']['_tscEpochTicks']),'Written cut epoch/source phase')
    require(cut['components']['cpu']['fields']['Halted'] and saved[1]!=0 and saved[2]!=0 and cut['scheduler']['events'][0]['owner']=='pit' and cut['scheduler']['events'][0]['time']>saved[0],'Active timer cut')
    require(final['scheduler']['now']==now==result['finalTime'] and final['scheduler']['remainder']==rem and final['components']['cpu']['fields']['_tscEpochTicks']==epoch,'Final clock/epoch state')
    require(final['components']['cpu']['fields']['Cr4']==4 and final['components']['cpu']['fields']['TimestampFrequency']==hz,'Fixed clock/TSD state')
    return dict(writes=len(writes),msrReads=len(reads),rdtscReads=len(tsc),physicalIrqs=len(acks),faultReturns=len(faults),integerLedgerEntries=len(ledger),finalTime=now)


def main():
    """Verify one actual selected-graph continuation and require isolated falsifications to fail."""
    parser=argparse.ArgumentParser();parser.add_argument('--program',type=Path,required=True);parser.add_argument('--evidence',type=Path,required=True);args=parser.parse_args();root=args.evidence
    program=args.program.read_bytes();ram=(root/'ram-original.bin').read_bytes();ledger=json.loads((root/'ledger.json').read_bytes());result=json.loads((root/'result.json').read_bytes());cut=json.loads((root/'cut.json').read_bytes());final=json.loads((root/'final-original.json').read_bytes())
    require(ram==(root/'ram-restored.bin').read_bytes(),'Whole restored RAM')
    for name in ('future','final'):require((root/(name+'-original.json')).read_bytes()==(root/(name+'-restored.json')).read_bytes(),'Exact future '+name)
    require(json.loads((root/'future-original.json').read_bytes())==ledger[result['ledgerCut']:],'Future ledger slice')
    proof=verify(program,ram,ledger,result,cut,final);negative=0
    offsets=[*[0x6000+row*64+word*4 for row in range(8) for word in range(10)],*[0x6200+row*16+word*4 for row in range(4) for word in range(4)],
        *range(0x5120,0x512a),0x1000,0x5ffc,0x6240,0x5100,0x5104,0x5108,0x510c,0x5110,0x5112,0x5140,
        *range(0x6300,0x6310,2),*range(0x6320,0x6328,2),*range(0x6400,0x6460,4),0x6500,0x6504]
    for offset in offsets:
        bad=bytearray(ram);bad[offset]^=1;r=copy.deepcopy(result);r['ramSha256']=digest(bad)
        try:verify(program,bad,ledger,r,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted RAM mutation '+hex(offset))
    for index,item in enumerate(ledger):
        kind=item['kind']
        if kind not in ('pulse','ack','idle','entry') and not (kind=='step' and item['operation'] in (0x30,0x31,0x32)):continue
        bad=copy.deepcopy(ledger);key='target' if kind=='idle' else 'eax' if kind=='step' else 'now';bad[index][key]^=1
        try:verify(program,ram,bad,result,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted ledger mutation')
    for target,key in [('scheduler','remainder'),('scheduler','now'),('cpu','_tscEpochTicks')]:
        bad=copy.deepcopy(final)
        if target=='cpu':bad['components']['cpu']['fields'][key]^=1
        else:bad[target][key]^=1
        try:verify(program,ram,ledger,result,cut,bad)
        except ValueError:negative+=1
        else:raise ValueError('Accepted final state mutation')
    proof.update(passed=True,negativeOracles=negative,fullPcSnapshot=False,dosBoot=False);(root/'oracle.json').write_text(json.dumps(proof,indent=2),encoding='utf-8');print(json.dumps(proof))


if __name__=='__main__':main()
