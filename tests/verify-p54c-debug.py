# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently verify P54C I/O-debug effects, committed frames and selected continuation."""
import argparse
import copy
import hashlib
import json
import struct
from pathlib import Path


def require(value,message):
    """Reject an inconsistent actual guest effect or deliberately corrupted oracle input."""
    if not value:raise ValueError(message)


def digest(data):
    """Hash exact evidence bytes without importing emulator code."""
    return hashlib.sha256(data).hexdigest().upper()


def verify(program,ram,ledger,result,cut,final):
    """Reconstruct real I/O operations, all debug/fault frames and virtual timer phase with integers."""
    require(result['passed'] and result['model']=='IntelP54cB5Subset' and not result['fullPcSnapshot'] and not result['dosBoot'],'Explicit selected graph scope')
    require(program[3:11]==b'DOSEDBG1' and len(ram)==2*1024*1024,'Own guest format')
    template,gdt_size,irq,db,ud,gp,user,finish=struct.unpack_from('<8H',program,11)
    labels=struct.unpack_from('<14I',program,27)
    udf,uda,gdf,gda,outnone,outword,indword,repout,repoutafter,repin,repinafter,userout,userafter,waitirq=labels
    pop32,pop32after,pop16,pop16after=struct.unpack_from('<4I',program,83)
    require(ram[0x1000:0x1000+len(program)]==program and result['programSha256']==digest(program) and result['ramSha256']==digest(ram),'Immutable code and full RAM identities')
    require(program[udf-0x1000:udf-0x1000+3]==b'\x0f\x21\xe0' and uda==udf+3 and program[gdf-0x1000:gdf-0x1000+3]==b'\x0f\x21\xf0' and gda==gdf+3,'Exact DR4/DR6 instruction encodings')
    require(program[outnone-0x1000]==0xee and program[outword-0x1000:outword-0x1000+2]==b'\x66\xef' and program[indword-0x1000]==0xed and program[userout-0x1000]==0xef,'Exact scalar I/O encodings')
    require(sorted(program[repout-0x1000:repoutafter-0x1000])==sorted(b'\xf3\x6e') and sorted(program[repin-0x1000:repinafter-0x1000])==sorted(b'\x66\xf3\x6d'),'Exact bounded REP I/O encodings')
    require(struct.unpack_from('<I',ram,0x5ffc)[0]==0xa5c33c5a and struct.unpack_from('<I',ram,0x6680)[0]==0x5a3cc3a5,'Proof RAM guards')
    require(ram[0x5120:0x512a]==bytes.fromhex('35c26821a2da0fc90040') and ram[0x5114]==1,'Raw 80-bit operand and completion')
    require(struct.unpack_from('<5I',ram,0x5100)==(4,9,8,1,1),'Actual IRQ/phase/debug/UD/GP counts')
    require(struct.unpack_from('<2I',ram,0x5118)==(0xffff0ff0,0x400),'DR4/DR5 aliases with DE clear')
    require(struct.unpack_from('<I',ram,0x5140)[0]==0x89abcdef and struct.unpack_from('<4I',ram,0x5148)==(0,0x5184,0,8),'Committed input/REP counters/pointer/CR4')
    require(struct.unpack_from('<2I',ram,0x5158)==(0xffff0ff0,0xc001d00d) and ram[0x5180:0x5184]==b'\xef\xcd\xef\xcd','GD retry and CPL3 fault return, actual INSW bytes')
    require(program[pop32-0x1000:pop32after-0x1000]==b'\x8f\x04\x24' and program[pop16-0x1000:pop16after-0x1000]==b'\x66\x8f\x44\x24\x04','Exact POP [ESP] and word POP [ESP+4] encodings')
    require(struct.unpack_from('<5I',ram,0x5190)==(0x11223344,0xbeef5a5a,0x5304,0x5342,0x9000),'Guest-observed post-increment POP destinations and preserved privileged ESP')
    require(struct.unpack_from('<4I',ram,0x52fc)==(0xa5c33c5a,0x11223344,0x11223344,0x5a3cc3a5),'Actual dword POP source/destination and adjacent guards')
    require(struct.unpack_from('<4I',ram,0x533c)==(0x8badf00d,0xc0debeef,0xbeef5a5a,0xd00dfeed),'Actual word POP displacement destination and adjacent guards')
    live=bytearray(ram[0x500:0x500+gdt_size]);expected=bytearray(program[template-0x1000:template-0x1000+gdt_size])
    for offset in range(8,gdt_size,8):
        if expected[offset+5]&0x10:live[offset+5]&=0xfe
        elif offset==0x28:live[offset+5]&=0xfd
    require(live==expected,'Own live descriptor writes')
    now=result['initialTime'];rem=0;hz=result['frequency'];require(now==50_000_000_000 and hz in (33_000_000,90_000_000),'Fixed virtual source')
    pulses=[];acks=[];eois=[];pitwrites=[];entries=[];hardware=[];fixture=[];debug=[];invalid=[];protection=[];e9=[];commit=None;previous=None;saved=None;userreturns=0;popsteps=[]
    for index,item in enumerate(ledger):
        kind=item['kind']
        if kind=='step':
            before=item['before'];after=item['after'];require(item['now']==now and item['cycles']==4,'Actual instruction phase')
            if previous is not None:require(before==previous,'Continuous architectural boundaries')
            previous=after;physical=item['physical']
            if physical in (pop32,pop16):
                oldsp,newsp,nextip=(0x5300,0x5304,pop32after) if physical==pop32 else (0x5340,0x5342,pop16after)
                require(before['registers'][4]==oldsp and after['registers'][4]==newsp and after['ip']==nextip and before['cs']==after['cs']==0x20 and before['ss']==after['ss']==0x18,'Actual POP instruction stack commit')
                require(before['registers'][:4]+before['registers'][5:]==after['registers'][:4]+after['registers'][5:] and before['flags']==after['flags'] and before['cr4']==after['cr4']==8 and before['dr7']==after['dr7']==0x22401,'POP preserves flags, other GPRs and pending debug configuration')
                popsteps.append(physical)
            if after['ip'] in (db,ud,gp):
                require(item['frame'] is not None and after['cs']==0x20 and after['cpl']==0,'Actual exception handler entry')
                frame=bytes.fromhex(item['frame']['bytes'])
                if after['ip']==db:
                    debug.append(item);row=len(debug)-1;phase=[2,4,5,6,6,6,7,7][row];fault=row==0
                    require(item['phase']==phase and after['dr6']==(0xffff2ff0 if fault else 0xffff0ff1) and after['dr7']==0x20401,'Independent debug status and general-detect clearing')
                    expected_ip=physical if fault else physical+len(program[physical-0x1000:physical-0x1000+(2 if phase==4 else 1)])
                    expected_flags=before['flags'] if fault else before['flags']&~0x10000
                    if phase==6:
                        require(physical==repout and after['registers'][1]==before['registers'][1]-1 and after['registers'][6]==before['registers'][6]+1,'Committed REP OUTSB iteration')
                        expected_ip=repout if after['registers'][1] else repoutafter
                        if after['registers'][1]:expected_flags|=0x10000
                    if phase==7:
                        require(physical==repin and after['registers'][1]==before['registers'][1]-1 and after['registers'][7]==before['registers'][7]+2,'Committed REP INSW iteration')
                        expected_ip=repin if after['registers'][1] else repinafter
                        if after['registers'][1]:expected_flags|=0x10000
                    require((physical==gdf and before['dr7']==0x22401) if fault else phase in (4,5,6,7),'Fault versus completed I/O classification')
                    require(struct.unpack_from('<3I',frame)==(expected_ip,0x20,expected_flags),'Exact debug EIP/CS/flags frame')
                    require(item['frame']['physical']==before['registers'][4]-12,'Actual debug frame stack slot')
                    record=struct.unpack_from('<11I',ram,0x6000+row*64)
                    require(record==(phase,after['dr6'],after['dr7'],expected_ip,0x20,expected_flags,after['registers'][0],after['registers'][1],after['registers'][2],after['registers'][6],after['registers'][7]),'Guest-captured exact debug frame and committed registers')
                elif after['ip']==ud:
                    invalid.append(item);require(physical==udf and before['cr4']==8 and before['dr7']==0x22401 and after['dr7']==0x22401,'DE #UD has priority over GD without clearing GD')
                    require(struct.unpack_from('<3I',frame)==(udf,0x20,before['flags']|0x10000),'Actual invalid-opcode frame')
                    require(ram[0x6500:0x650c]==frame[:12],'Guest captured invalid-opcode frame')
                else:
                    protection.append(item);require(physical==userout and before['cpl']==3 and before['cs']==0x13 and before['ss']==0x0b and before['registers'][4]==0x1f000,'Actual denied user OUT context')
                    require(struct.unpack_from('<6I',frame)==(0,userout,0x13,before['flags']|0x10000,0x1f000,0x0b),'Real interlevel I/O #GP frame')
                    require(ram[0x6540:0x6558]==frame[:24],'Guest captured protection frame')
            if before['cpl']==0 and after['cpl']==3 and after['ip']==userafter:
                require(program[physical-0x1000]==0xcf and after['registers'][4]==0x1f000 and after['ss']==0x0b,'Actual #GP IRETD return');userreturns+=1
            delta,rem=divmod(4_000_000_000+rem,hz);now+=delta
        elif kind=='hardware':
            require(item['now']==now and item['after']['ip']==irq and item['after']['cpl']==0,'Actual physical IRQ target')
            if previous is not None:require(item['before']==previous,'IRQ boundary chain')
            previous=item['after'];hardware.append(item)
            require(struct.unpack_from('<3I',bytes.fromhex(item['frame']['bytes']))==(item['before']['ip'],item['before']['cs'],item['before']['flags']),'Actual IRQ frame')
        elif kind=='idle':require(item['now']==now and item['target']>now,'Actual HLT deadline');now=item['target']
        elif kind=='pulse':
            require(commit is not None and item['now']==commit+(len(pulses)+1)*1537*1_000_000_000//1_193_182 and item['now']<=now,'Independent PIT pulse deadline');pulses.append(item['now'])
        elif kind=='ack':require(item['now']==now and item['irq']==0 and item['vector']==0x20 and not item['spurious'] and len(acks)<len(pulses),'Physical PIC acknowledge');acks.append(now)
        elif kind=='entry':require(item['now']==now and item['vector'] in (1,6,13,0x20,0x34),'Actual exception/interrupt entry');entries.append(item['vector'])
        elif kind=='port':
            require(item['now']==now,'Actual port phase')
            if 0x2e0<=item['port']<=0x2e3:fixture.append((item['port'],item['value'],item['width'],item['write']))
            else:
                require(item['width']==1 and item['write'],'Own setup/output byte port')
                if item['port']==0x40:
                    pitwrites.append(item['value'])
                    if len(pitwrites)==2:commit=now
                if item['port']==0x20 and item['value']==0x20:eois.append(now)
                if item['port']==0xe9:e9.append(item['value'])
        else:raise ValueError('Unknown ledger entry')
        if index+1==result['ledgerCut']:saved=(now,rem)
    require(fixture==[(0x2e0,0x78,1,True),(0x2e0,0x5678,2,True),(0x2e0,0x89abcdef,4,False),*( (0x2e1,v,1,True) for v in (0x11,0x22,0x33) ),(0x2e0,0xcdef,2,False),(0x2e0,0xcdef,2,False),(0x2e0,0xaabbccdd,4,True)],'Independent exact I/O bytes/bus widths and denied-access absence')
    require(len(debug)==8 and len(invalid)==len(protection)==userreturns==1 and entries.count(1)==8 and entries.count(6)==entries.count(13)==entries.count(0x34)==1,'Debug/fault entries and actual returns')
    require(popsteps==[pop32,pop16],'Two actual POP memory instruction executions')
    require(pitwrites==[1,6] and len(pulses)==len(acks)==len(eois)==len(hardware)==entries.count(0x20)==4 and all(p<=a<=e for p,a,e in zip(pulses,acks,eois)),'Four physical IRQs and EOIs')
    require(struct.unpack_from('<I',ram,0x5144)[0]==debug[3]['before']['registers'][6]+3,'Final actual OUTSB source progress')
    require(saved==(cut['scheduler']['now'],cut['scheduler']['remainder']) and saved[1]!=0,'Exact active cut phase')
    cpu=cut['components']['cpu']['fields'];require(cpu['Halted'] and cpu['Cr4']==8 and cpu['Dr7']==0x22401 and cpu['_debugAddresses'][0]==0x2e1,'Persisted DE/GD/comparator cut')
    require(cut['scheduler']['events'][0]['owner']=='pit' and cut['scheduler']['events'][0]['time']>saved[0],'Pending timer survives active debug cut')
    require(final['scheduler']['now']==now==result['finalTime'] and final['scheduler']['remainder']==rem,'Final exact virtual phase')
    cpu=final['components']['cpu']['fields'];require(cpu['Model']==1 and cpu['TimestampFrequency']==hz and cpu['Cr4']==8 and cpu['Dr7']==0x20401 and cpu['Dr6']==0xffff0ff0 and cpu['GeneralDetectFaults']==1 and cpu['DataBreakpointTraps']==7 and cpu['SingleStepTraps']==0,'Final raw CPU debug state')
    require(cpu['Eip']==previous['ip'] and cpu['_registers']==previous['registers'],'Final architectural context')
    require(bytes(e9).decode('ascii')==result['output']=='DOSE P54C DEBUG DONE\r\n','Actual completion output')
    return dict(ioOperations=len(fixture),ioDebugTraps=7,generalDetectFaults=1,invalidOpcodeReturns=1,protectionReturns=1,physicalIrqs=4,popMemoryOperations=2,integerLedgerEntries=len(ledger),finalTime=now)


def main():
    """Verify the exact continuation and require independent evidence corruptions to be rejected."""
    parser=argparse.ArgumentParser();parser.add_argument('--program',type=Path,required=True);parser.add_argument('--evidence',type=Path,required=True);args=parser.parse_args();root=args.evidence
    program=args.program.read_bytes();ram=(root/'ram-original.bin').read_bytes();ledger=json.loads((root/'ledger.json').read_bytes());result=json.loads((root/'result.json').read_bytes());cut=json.loads((root/'cut.json').read_bytes());final=json.loads((root/'final-original.json').read_bytes())
    require(ram==(root/'ram-restored.bin').read_bytes(),'Whole restored RAM')
    for base in ('future','final'):require((root/(base+'-original.json')).read_bytes()==(root/(base+'-restored.json')).read_bytes(),'Exact restored '+base)
    require(json.loads((root/'future-original.json').read_bytes())==ledger[result['ledgerCut']:],'Future ledger slice')
    proof=verify(program,ram,ledger,result,cut,final);negative=0
    offsets=[*[0x6000+row*64+word*4 for row in range(8) for word in range(11)],*range(0x5120,0x512a),0x1000,0x5ffc,0x6680,0x5114,*range(0x5100,0x5114,4),0x5118,0x511c,*range(0x5140,0x5160,4),*range(0x5180,0x5184),*range(0x6500,0x650c,4),*range(0x6540,0x6558,4),*range(0x5190,0x51a4,4),*range(0x52fc,0x530c,4),*range(0x533c,0x534c,4)]
    for offset in offsets:
        bad=bytearray(ram);bad[offset]^=1;r=copy.deepcopy(result);r['ramSha256']=digest(bad)
        try:verify(program,bad,ledger,r,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted RAM mutation '+hex(offset))
    for index,item in enumerate(ledger):
        if item['kind'] not in ('ack','pulse','idle','hardware') and not (item['kind']=='step' and item['frame'] is not None) and not (item['kind']=='port' and 0x2e0<=item['port']<=0x2e3):continue
        bad=copy.deepcopy(ledger)
        if item['kind']=='step':bad[index]['after']['dr6']^=1
        elif item['kind']=='idle':bad[index]['target']^=1
        elif item['kind']=='port':bad[index]['value']^=1
        else:bad[index]['now']^=1
        try:verify(program,ram,bad,result,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted transition/I/O/deadline mutation')
    for which in ('cut','final'):
        for key in ('now','remainder'):
            c=copy.deepcopy(cut);f=copy.deepcopy(final);(c if which=='cut' else f)['scheduler'][key]^=1
            try:verify(program,ram,ledger,result,c,f)
            except ValueError:negative+=1
            else:raise ValueError('Accepted clock mutation')
    proof.update(passed=True,negativeOracles=negative,fullPcSnapshot=False,dosBoot=False);(root/'oracle.json').write_bytes(json.dumps(proof,indent=2).encode('utf-8'));print(json.dumps(proof))


if __name__=='__main__':main()
