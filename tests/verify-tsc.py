# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent integer reconstruction of own TSC/PIT guest effects and negative oracles."""
import argparse
import copy
import hashlib
import json
import struct
from pathlib import Path


def require(value, message):
    """Reject any missing architectural observation, including mutation controls."""
    if not value:
        raise ValueError(message)


def digest(data):
    """Hash exact evidence bytes without importing VM implementation code."""
    return hashlib.sha256(data).hexdigest().upper()


def verify(program, ram, ledger, result, cut, final):
    """Derive ticks/deadlines from recorded real operations using Python integers."""
    require(result['passed'] and not result['fullPcSnapshot'] and not result['dosBoot'], 'Scope/completion')
    require(program[3:11] == b'DOSETSC1' and len(ram) == 2*1024*1024, 'Own format')
    require(ram[0x1000:0x1000+len(program)] == program, 'Immutable code')
    require(result['programSha256'] == digest(program) and result['ramSha256'] == digest(ram), 'Evidence hashes')
    require(struct.unpack_from('<I',ram,0x4ffc)[0] == 0xa5c33c5a and struct.unpack_from('<I',ram,0x5080)[0] == 0x5a3cc3a5, 'RAM guards')
    require(ram[0x5120:0x512a] == bytes.fromhex('35c26821a2da0fc90040'), 'Raw x87 pi')
    require(struct.unpack_from('<HIII',ram,0x5100) == (4,0x05240000,0x01350000,0x00040000), 'CPUID/CR4/ticks layout')
    require(ram[0x5140] == 1 and result['output'] == 'DOSE P54C TSC PIT OK\r\n', 'Guest completion')
    hz=result['frequency']; now=result['initialTime']; rem=0; samples=[]; pulses=[]; acks=[]; eois=[]; pit_writes=[]; commit=None
    require(hz in (33_000_000,90_000_000) and now == 50_000_000_000, 'Fixed source')
    for item in ledger:
        kind=item['kind']
        if kind == 'step':
            require(item['now'] == now and item['cycles'] == 4, 'Owner step clock')
            if item['rdtsc']:
                expected=(now*hz+rem)//1_000_000_000
                require(item['eax'] | item['edx'] << 32 == expected, 'Actual RDTSC registers')
                require(item['flags'] == 0x200e97, 'RDTSC flags')
                ip=item['ip']-0x1000; prefix=program[ip:ip+item['prefix']]
                require(prefix in (b'',b'\x66',b'\x67',b'\x66\x67') and program[ip+len(prefix):ip+len(prefix)+2] == b'\x0f\x31', 'Actual opcode')
                samples.append((expected,item['flags'],item['prefix']))
            delta,rem=divmod(4_000_000_000+rem,hz); now += delta
        elif kind == 'idle':
            require(item['now'] == now and item['target'] > now, 'Actual halted advance')
            now=item['target']
        elif kind == 'pulse':
            require(commit is not None and item['now'] == commit + (len(pulses)+1)*1537*1_000_000_000//1_193_182 and item['now'] <= now, 'Independent PIT deadline')
            pulses.append(item['now'])
        elif kind == 'ack':
            require(item['now'] == now and item['irq'] == 0 and item['vector'] == 8 and not item['spurious'], 'Physical PIC acknowledge')
            require(len(acks) < len(pulses) and now >= pulses[len(acks)], 'IRQ ordering'); acks.append(now)
        elif kind == 'port':
            require(item['now'] == now and item['write'] and item['width'] == 1, 'Actual port operation')
            if item['port'] == 0x40:
                pit_writes.append(item['value'])
                if len(pit_writes)==2: commit=now
            if item['port'] == 0x20 and item['value']==0x20: eois.append(now)
        else:
            raise ValueError('Unknown ledger kind')
    require(pit_writes == [1,6] and len(pulses)==len(acks)==len(eois)==4, 'Actual timer/IRQ counts')
    require(all(p<=a<=e for p,a,e in zip(pulses,acks,eois)), 'IRQ/EOI chronology')
    require(len(samples)==8 and [s[2] for s in samples]==[0,0,1,1,1,1,2,2], 'All operand/address prefix samples')
    ticks=[0,1,1,2,2,3,3,4]
    for index,(expected,flags,_) in enumerate(samples):
        low,high,stored_flags,irq=struct.unpack_from('<IIII',ram,0x5000+16*index)
        require(low | high<<32 == expected and stored_flags==flags and irq==ticks[index], 'Guest sample row')
    require(all(samples[i+1][0]>samples[i][0] for i in range(7)), 'Running and halted counter growth')
    require(now==result['finalTime']==final['scheduler']['now'] and rem==final['scheduler']['remainder'], 'Final exact phase')
    require(final['components']['cpu']['fields']['Cr4']==4 and final['components']['cpu']['fields']['TimestampFrequency']==hz, 'CPU clock/TSD state')
    require(cut['components']['cpu']['fields']['Halted'] and cut['scheduler']['events'] and cut['scheduler']['events'][0]['owner']=='pit', 'Live timer cut')
    require(cut['scheduler']['events'][0]['time']>cut['scheduler']['now'] and cut['scheduler']['remainder'] != 0, 'Pending deadline and fractional phase')
    return {'samples':len(samples),'pulses':len(pulses),'acknowledgements':len(acks),'integerLedgerEntries':len(ledger),'finalTime':now,'finalRemainder':rem}


def main():
    """Verify one real run, then require all isolated falsifications to fail."""
    parser=argparse.ArgumentParser();parser.add_argument('--program',type=Path,required=True);parser.add_argument('--evidence',type=Path,required=True);args=parser.parse_args()
    root=args.evidence;program=args.program.read_bytes();ram=(root/'ram-original.bin').read_bytes();ledger=json.loads((root/'ledger.json').read_bytes());result=json.loads((root/'result.json').read_bytes());cut=json.loads((root/'cut.json').read_bytes());final=json.loads((root/'final-original.json').read_bytes())
    require(ram==(root/'ram-restored.bin').read_bytes(), 'Restored entire RAM')
    for base in ('future','final'):
        require((root/(base+'-original.json')).read_bytes()==(root/(base+'-restored.json')).read_bytes(), 'Exact future '+base)
    proof=verify(program,ram,ledger,result,cut,final);negative=0
    # Refresh dependent hashes so every RAM mutation must fail an independent content rule.
    for offset in [*range(0x5000,0x5080,4),*range(0x5120,0x512a),0x4ffc,0x5080,0x1000,0x5140,0x5100,0x5104,0x5108,0x510c]:
        bad=bytearray(ram);bad[offset]^=1;r=copy.deepcopy(result);r['ramSha256']=digest(bad)
        try:verify(program,bad,ledger,r,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted RAM mutation '+hex(offset))
    for index,item in enumerate(ledger):
        if item['kind'] not in ('pulse','ack','idle') and not (item['kind']=='step' and item['rdtsc']):continue
        bad=copy.deepcopy(ledger);key='target' if item['kind']=='idle' else 'eax' if item.get('rdtsc') else 'now';bad[index][key]^=1
        try:verify(program,ram,bad,result,cut,final)
        except ValueError:negative+=1
        else:raise ValueError('Accepted ledger mutation')
    for key in ('remainder','now'):
        bad=copy.deepcopy(final);bad['scheduler'][key]^=1
        try:verify(program,ram,ledger,result,cut,bad)
        except ValueError:negative+=1
        else:raise ValueError('Accepted state mutation')
    proof.update(passed=True,negativeOracles=negative,fullPcSnapshot=False,dosBoot=False)
    (root/'oracle.json').write_text(json.dumps(proof,indent=2),encoding='utf-8');print(json.dumps(proof))


if __name__=='__main__':main()
