# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently verify actual narrow/wide gate frames, stack effects and PIT continuation."""
import argparse
import copy
import hashlib
import json
import struct
from pathlib import Path


def require(value,message):
    """Reject inconsistent guest effects and deliberate oracle mutations."""
    if not value:raise ValueError(message)


def digest(data):
    """Hash exact own evidence bytes without loading any emulator implementation."""
    return hashlib.sha256(data).hexdigest().upper()


def verify(program,ram,ledger,result,cut,final):
    """Reconstruct historical pointer policy, actual frames and all timer deadlines with integers."""
    require(result['passed'] and not result['fullPcSnapshot'] and not result['dosBoot'],'Scope')
    pentium=result['model']=='IntelP54cB5Subset';require(pentium or result['model']=='Intel486Dx','Known explicit model')
    require(program[3:11]==b'DOSESEL1' and len(ram)==2*1024*1024,'Own format')
    template,gdt_size,user,irq_handler=struct.unpack_from('<4H',program,11);afters=struct.unpack_from('<6I',program,19)
    require(ram[0x1000:0x1000+len(program)]==program and result['programSha256']==digest(program) and result['ramSha256']==digest(ram),'Code/RAM identities')
    require(struct.unpack_from('<I',ram,0x5ffc)[0]==0xa5c33c5a and struct.unpack_from('<I',ram,0x6180)[0]==0x5a3cc3a5,'RAM guards')
    require(ram[0x5120:0x512a]==bytes.fromhex('35c26821a2da0fc90040') and ram[0x5140]==1,'Raw FPU/completion')
    # Accessed/busy bits may change only in the copied live descriptor table.
    initial=program[template-0x1000:template-0x1000+gdt_size];live=bytearray(ram[0x500:0x500+gdt_size]);expected=bytearray(initial)
    for offset in range(8,gdt_size,8):
        access=expected[offset+5]
        if access&0x10:live[offset+5]&=0xfe
        elif offset==0x48:live[offset+5]&=0xfd
    require(live==expected and struct.unpack_from('<H',ram,0x5100)[0]==4,'Live descriptor writes/IRQ count')
    now=result['initialTime'];rem=0;hz=result['frequency'];require(now==50_000_000_000 and hz==90_000_000,'Fixed source')
    pulses=[];acks=[];eois=[];writes=[];entries=[];hardware=[];enters=[];returns=[];irq_returns=[];e9=[];selector_count=0;commit=None;previous=None;saved=None
    for index,item in enumerate(ledger):
        kind=item['kind']
        if kind=='step':
            before=item['before'];after=item['after'];require(item['now']==now and item['cycles']==4,'Actual owner step')
            if previous is not None:require(before==previous,'Continuous CPU boundary chain')
            previous=after;physical=item['physical']
            if 0x1000<=physical<0x1000+len(program):
                opcode=program[physical-0x1000]
                cursor=physical-0x1000
                while program[cursor] in (0x66,0x67):cursor+=1
                actual=program[cursor]
                if before['cpl']==3 and physical>=afters[-1] and (actual in (6,14,22,30,7,23,31) or actual==15 and program[cursor+1] in (160,161,168,169)):selector_count+=1
                if before['cpl']==3 and after['cpl']==0:
                    require(opcode in (0x9a,0xcd) and item['frame'] is not None,'Actual gate instruction')
                    if opcode!=0xcd or program[physical-0x1000+1]!=0x34:enters.append(item)
                if before['cpl']==0 and after['cpl']==3:
                    require(opcode in (0xca,0xcb,0xcf),'Actual far return');
                    if after['ip']!=user:returns.append(item)
                if before['cs']==0x20 and after['cs']==0x28 and opcode==0xcf:irq_returns.append(item)
            delta,rem=divmod(4_000_000_000+rem,hz);now+=delta
        elif kind=='hardware':
            require(item['now']==now and item['after']['ip']==irq_handler and item['before']['cpl']==item['after']['cpl']==0,'Physical IRQ target')
            if previous is not None:require(item['before']==previous,'IRQ boundary chain')
            previous=item['after'];hardware.append(item)
            frame=bytes.fromhex(item['frame']['bytes']);lo,cs,flags=struct.unpack_from('<3I',frame)
            require((lo,cs,flags)==(item['before']['ip'],item['before']['cs'],item['before']['flags']),'Actual IRQ frame')
        elif kind=='idle':require(item['now']==now and item['target']>now,'Actual HLT deadline');now=item['target']
        elif kind=='pulse':
            require(commit is not None and item['now']==commit+(len(pulses)+1)*1537*1_000_000_000//1_193_182 and item['now']<=now,'Independent PIT deadline');pulses.append(item['now'])
        elif kind=='ack':
            require(item['now']==now and item['irq']==0 and item['vector']==0x20 and not item['spurious'] and len(acks)<len(pulses),'Physical PIC acknowledge');acks.append(now)
        elif kind=='entry':require(item['now']==now and item['vector'] in (0x20,0x30,0x31,0x32,0x34),'Actual interrupt entry');entries.append(item['vector'])
        elif kind=='port':
            require(item['now']==now and item['width']==1 and item['write'],'Guest port operation')
            if item['port']==0x40:
                writes.append(item['value'])
                if len(writes)==2:commit=now
            if item['port']==0x20 and item['value']==0x20:eois.append(now)
            if item['port']==0xe9:e9.append(item['value'])
        else:raise ValueError('Unknown ledger kind')
        if index+1==result['ledgerCut']:saved=(now,rem)
    require(writes==[1,6] and len(pulses)==len(acks)==len(eois)==len(hardware)==len(irq_returns)==4 and all(p<=a<=e for p,a,e in zip(pulses,acks,eois)),'Four real IRQ handler returns')
    require(entries.count(0x20)==4 and all(entries.count(v)==1 for v in (0x30,0x31,0x32,0x34)),'Entry vector counts')
    require(len(enters)==len(returns)==6 and selector_count==9,'Six complete interlevel gates')
    old_pointers=[0x1f000,0x2f000,0x1f010,0x1f020,0x1f100,0x1f200];widths=[2,2,2,2,4,4];kernel_ss=[0x18,0x30,0x18,0x30,0x18,0x30]
    for row,(entry,ret) in enumerate(zip(enters,returns)):
        width=widths[row];count=2 if row==1 else 0;interrupt=row in (2,3,5);slots=(5 if interrupt else 4+count)
        full_inner=(0xabcd0000 if kernel_ss[row]==0x30 else 0) | (0xe000-slots*width)
        expected_return=old_pointers[row] if width==4 else (0 if pentium else 0x000b0000) | (old_pointers[row]&0xffff)
        if row==1:expected_return+=4
        require(entry['phase']==row and entry['before']['esp']==old_pointers[row] and entry['before']['ss']==0x0b and entry['before']['cs']==0x13,'Actual outer source')
        require(entry['after']['esp']==full_inner and entry['after']['ss']==kernel_ss[row] and entry['after']['cs']==(0x28 if width==2 else 0x20),'Actual kernel stack')
        frame=bytes.fromhex(entry['frame']['bytes']);fmt='<'+('H' if width==2 else 'I')*slots;values=list(struct.unpack_from(fmt,frame))
        expected=[afters[row],0x13]
        if interrupt:expected.append(0x3202)
        if count:expected += [0x1234,0x5678]
        expected += [old_pointers[row] & (0xffff if width==2 else 0xffffffff),0x0b]
        require(values==expected and entry['frame']['physical']==(full_inner&0xffff),'Actual gate frame values/width')
        require(ret['phase']==row and ret['after']['ip']==afters[row] and ret['after']['cs']==0x13 and ret['after']['ss']==0x0b and ret['after']['cpl']==3,'Actual return target/context')
        require(ret['after']['esp']==expected_return,'Historical outer ESP policy')
        record=0x6000+row*64;require(struct.unpack_from('<IHH',ram,record)==(full_inner,kernel_ss[row],entry['after']['cs']),'Guest kernel pointer')
        require(ram[record+8:record+8+slots*width]==frame[:slots*width],'Guest saved frame')
        require(struct.unpack_from('<IHHII',ram,record+32)==(expected_return,0x0b,0x13,0x3202,0x12340000+row),'Guest observed user context')
        require(struct.unpack_from('<I',ram,expected_return-4)[0]==0x12340000+row,'Actual resumed stack write/read')
        if width==2:
            alternate=(0x000b0000 if pentium else 0) | (old_pointers[row]&0xffff)
            if row==1:alternate+=4
            require(struct.unpack_from('<I',ram,alternate-4)[0]==0,'No write through the other model pointer')
    require(struct.unpack_from('<6I',ram,0x6600)==(0x0b,0x13,0x0b,0x0b,0x0b,0x0b),'Actual selector pushes')
    require(struct.unpack_from('<I',ram,0x6618)[0]==0x000ba5a5 and struct.unpack_from('<H',ram,0x661c)[0]==0x0b and struct.unpack_from('<I',ram,0x6620)[0]==0x1d800,'Narrow selector/high discard/SP control')
    require(saved==(cut['scheduler']['now'],cut['scheduler']['remainder']) and saved[1]!=0 and cut['components']['cpu']['fields']['Halted'],'Exact active cut phase')
    require(cut['components']['cpu']['fields']['_segments'][1]==0x28 and cut['scheduler']['events'][0]['owner']=='pit' and cut['scheduler']['events'][0]['time']>saved[0],'Pending narrow gate/timer cut')
    require(final['scheduler']['now']==now==result['finalTime'] and final['scheduler']['remainder']==rem,'Final exact clock')
    cpu=final['components']['cpu']['fields'];require(cpu['Model']==(1 if pentium else 0) and cpu['TimestampFrequency']==(hz if pentium else 0) and cpu['_registers'][4]==previous['esp'] and cpu['Eip']==previous['ip'],'Final raw CPU')
    require(bytes(e9).decode('ascii')==result['output']=='DOSE SELECTOR GATES DONE\r\n','Actual bounded output')
    return dict(gateReturns=6,narrowReturns=4,wideReturns=2,physicalIrqs=4,selectorInstructions=selector_count,integerLedgerEntries=len(ledger),finalTime=now)


def main():
    """Verify one real reset/continuation and independently require mutated evidence to fail."""
    parser=argparse.ArgumentParser();parser.add_argument('--program',type=Path,required=True);parser.add_argument('--evidence',type=Path,required=True);args=parser.parse_args();root=args.evidence
    program=args.program.read_bytes();ram=(root/'ram-original.bin').read_bytes();ledger=json.loads((root/'ledger.json').read_bytes());result=json.loads((root/'result.json').read_bytes());cut=json.loads((root/'cut.json').read_bytes());final=json.loads((root/'final-original.json').read_bytes())
    require(ram==(root/'ram-restored.bin').read_bytes(),'Whole restored RAM')
    for base in ('future','final'):require((root/(base+'-original.json')).read_bytes()==(root/(base+'-restored.json')).read_bytes(),'Exact restored '+base)
    require(json.loads((root/'future-original.json').read_bytes())==ledger[result['ledgerCut']:],'Future ledger slice')
    proof=verify(program,ram,ledger,result,cut,final);negative=0
    offsets=[*[0x6000+row*64+word*2 for row in range(6) for word in range(24)],*range(0x5120,0x512a),0x1000,0x5ffc,0x6180,0x5100,0x5140,*range(0x6600,0x6624,2)]
    # Check only captured frame words that belong to an architectural frame; the rest are surrounding RAM.
    offsets=[o for o in offsets if o!=0x661e and not (0x6000<=o<0x6180 and 8<=((o-0x6000)%64)<32 and ((o-0x6000)%64)>=8+([8,12,10,10,16,20][(o-0x6000)//64]))]
    for offset in offsets:
        bad=bytearray(ram);bad[offset]^=1;r=copy.deepcopy(result);r['ramSha256']=digest(bad)
        try:verify(program,bad,ledger,r,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted RAM mutation '+hex(offset))
    for index,item in enumerate(ledger):
        if item['kind'] not in ('ack','pulse','idle','hardware') and not (item['kind']=='step' and item['before']['cpl']!=item['after']['cpl']):continue
        bad=copy.deepcopy(ledger)
        if item['kind']=='step':bad[index]['after']['esp']^=1
        elif item['kind']=='idle':bad[index]['target']^=1
        else:bad[index]['now']^=1
        try:verify(program,ram,bad,result,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted transition/deadline mutation')
    for key in ('now','remainder'):
        bad=copy.deepcopy(final);bad['scheduler'][key]^=1
        try:verify(program,ram,ledger,result,cut,bad)
        except ValueError:negative+=1
        else:raise ValueError('Accepted clock mutation')
    proof.update(passed=True,negativeOracles=negative,fullPcSnapshot=False,dosBoot=False);(root/'oracle.json').write_text(json.dumps(proof,indent=2),encoding='utf-8');print(json.dumps(proof))


if __name__=='__main__':main()
