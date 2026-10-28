# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent SFF8020i PIO/ISO9660/DOS/IRQ/state-continuation proof; no emulator imports or controller mutations."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('atapi_independent_media', REPO / 'scripts/verify-piix-ide.py')
diskio = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(diskio)
media = diskio.media
RAM = 32 * 1048576
KEYS = ['ports', 'words', 'acks', 'readbacks', 'services', 'boundaries', 'samples', 'transfers']


def check(value, reason):
    """Reject a missing original guest, phase, byte, media or ownership effect."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named own observation, without discovering user media."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def decode(value):
    """Decode complete raw bytes strictly, independently from the VM serializer."""
    return base64.b64decode(value, validate=True)


def sha(data):
    """Hash one actual explicit byte string."""
    return hashlib.sha256(data).hexdigest().upper()


def digest(path):
    """Hash only a named own file, excluding personal overlays before access."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def field(state, suffix):
    """Require exactly one constructor-fixed registered device type."""
    rows = [v['fields'] for v in state['components'].values() if v['type'].endswith(suffix)]
    check(len(rows) == 1, 'unique actual state component ' + suffix)
    return rows[0]


def metadata(program):
    """Bind every own cut, IRQ, service, readback and RDTSC site to unchanged assembled opcodes."""
    check(program.count(b'ATMETA01') == 1, 'unique own metadata')
    at = program.index(b'ATMETA01')
    check(struct.unpack_from('<HH', program, at + 8) == (1, 28), 'own metadata version/count')
    t = struct.unpack_from('<28H', program, at + 12)
    check(t[3] == len(program) + 256 and t[1] - t[0] == 256 and t[2] < t[0] < t[3], 'whole original COM extent')
    for index in [4, 5, 6, 7, 9, 24]:
        check(program[t[index] - 256] == 0x90, 'immutable actual observation NOP')
    check(program[t[8] - 256:t[8] - 254] == b'\x66\x60' and program[t[10] - 256] == 0xCF, 'real PUSHAD IRQ entry and IRET')
    for index in range(18, 22):
        check(program[t[index] - 256:t[index] - 254] == b'\xCD\x4B', 'actual original VDS INT site')
    for index in [26, 27]:
        check(program[t[index] - 256:t[index] - 254] == b'\x0F\x31', 'actual two RDTSC sites')
    return t


def clock(point, final):
    """Reconstruct the whole internal 100-MHz timestamp from integer scheduler time."""
    check(point['epoch'] == 0 and 0 <= point['nanoseconds'] <= final['scheduler']['now'] and
          point['ticks'] == point['counter'] == point['nanoseconds'] // 10, 'independent complete virtual TSC')


def iso_contract(data):
    """Parse real ISO9660 dual-endian geometry, path tables, finite root records, extents and every own file byte."""
    check(len(data) == 128 * 2048 and not any(data[:16 * 2048]), 'own cooked2048 system area/capacity')
    p = data[16 * 2048:17 * 2048]
    check(p[:7] == b'\x01CD001\x01' and p[8:40] == b'DOSE'.ljust(32,b' ') and
          p[40:72] == b'DOSE_OWN_ATAPI'.ljust(32,b' '), 'real primary volume descriptor/identity')
    for offset, expected in [(80,128),(132,10)]:
        check(struct.unpack_from('<I',p,offset)[0] == struct.unpack_from('>I',p,offset+4)[0] == expected, 'independent ISO duplicate32')
    for offset, expected in [(120,1),(124,1),(128,2048)]:
        check(struct.unpack_from('<H',p,offset)[0] == struct.unpack_from('>H',p,offset+2)[0] == expected, 'independent ISO duplicate16')
    check(struct.unpack_from('<I',p,140)[0] == 18 and struct.unpack_from('>I',p,148)[0] == 19 and
          data[17*2048:17*2048+7] == b'\xffCD001\x01', 'real path table locations and descriptor terminator')
    check(data[18*2048:18*2048+10] == b'\x01\x00\x14\x00\x00\x00\x01\x00\x00\x00' and
          data[19*2048:19*2048+10] == b'\x01\x00\x00\x00\x00\x14\x00\x01\x00\x00', 'both independently encoded root path tables')
    root_record = p[156:190]
    check(root_record[0] == 34 and root_record[25] == 2 and root_record[32:] == b'\x01\x00', 'actual root descriptor record')
    check(struct.unpack_from('<I',root_record,2)[0] == struct.unpack_from('>I',root_record,6)[0] == 20 and
          struct.unpack_from('<I',root_record,10)[0] == struct.unpack_from('>I',root_record,14)[0] == 2048, 'actual root extent/size')
    directory = data[20*2048:21*2048]
    records, at = {}, 0
    while at < 2048 and directory[at]:
        size = directory[at]
        check(34 <= size <= 255 and at + size <= 2048, 'finite in-sector ISO directory record')
        row = directory[at:at+size]
        extent, length = struct.unpack_from('<I',row,2)[0], struct.unpack_from('<I',row,10)[0]
        check(extent == struct.unpack_from('>I',row,6)[0] and length == struct.unpack_from('>I',row,14)[0] and
              struct.unpack_from('<H',row,28)[0] == struct.unpack_from('>H',row,30)[0] == 1, 'all actual directory endian duplicates')
        name = bytes(row[33:33+row[32]])
        check(name not in records and extent*2048+length <= len(data), 'unique finite actual ISO extent')
        records[name] = (extent,length,row[25])
        at += size
    check(not any(directory[at:]) and records == {b'\x00':(20,2048,2),b'\x01':(20,2048,2),
          b'IDENT.TXT;1':(21,197,0),b'PAYLOAD.BIN;1':(22,8205,0)}, 'complete actual ISO directory and intended files')
    text = (b'DOSE OWN ISO9660 CD. Immutable 2048-byte blocks, genuine PACKET PIO and original DOS.\r\n'*3)[:197]
    payload = bytes((29*i + 7*(i//256) + 0x53) % 256 for i in range(8205))
    check(data[21*2048:21*2048+197] == text and not any(data[21*2048+197:22*2048]), 'actual complete text extent and zero sector padding')
    check(data[22*2048:22*2048+8205] == payload and not any(data[22*2048+8205:]), 'every actual binary file byte and all following CD padding')
    return dict(text=text,payload=payload,pvd=p,root=directory)


def cdbs():
    """Specify the unchanged guest's exact twelve-byte commands independently from observed packets."""
    rows = {i:bytearray(12) for i in range(1,15)}
    rows[1][0],rows[1][4]=0x12,36
    for i in [2,7,9]:
        rows[i][0],rows[i][4]=3,18
    rows[4][0]=0x25
    rows[5][0],rows[5][8]=0x43,20
    rows[6][0]=255
    for i,lba,blocks in [(8,128,1),(10,16,1),(11,20,1),(12,22,5)]:
        rows[i][0]=0x28
        rows[i][2:6]=struct.pack('>I',lba)
        rows[i][7:9]=struct.pack('>H',blocks)
    rows[13][0]=0xA8
    rows[13][2:6]=struct.pack('>I',21)
    rows[13][6:10]=struct.pack('>I',1)
    return {key:bytes(value) for key,value in rows.items()}


def responses(iso):
    """Specify original inquiry/sense/capacity/TOC bytes and real immutable CD block contents."""
    inquiry = bytes([5,128,0,0x21,31,0,0,0])+b'DOSE    '+b'ATAPI PIO CD-ROM'.ljust(16,b' ')+b'0001'
    senses = []
    for key,asc in [(6,0x29),(5,0x20),(5,0x21)]:
        row=bytearray(18)
        row[0],row[2],row[7],row[12]=0x70,key,10,asc
        senses.append(bytes(row))
    toc = bytes([0,18,1,1,0,0x14,1,0])+bytes(4)+bytes([0,0x14,0xAA,0])+struct.pack('>I',128)
    return {1:inquiry,2:senses[0],3:b'',4:struct.pack('>II',127,2048),5:toc,6:b'',7:senses[1],8:b'',9:senses[2],
            10:iso[16*2048:17*2048],11:iso[20*2048:21*2048],12:iso[22*2048:27*2048],13:iso[21*2048:22*2048],14:b''}


def identify_contract(data):
    """Bind actual original ATAPI packet length, interrupt DRQ, removable CD class and no DMA advertising."""
    check(len(data)==512, 'whole actual IDENTIFY PACKET block')
    words=struct.unpack('<256H',data)
    check(words[0]==0x85A0 and words[49]==0x0200 and words[63]==0, 'original12byte PIO/interrupt-ready CD identity')
    expected=bytearray(512)
    struct.pack_into('<H',expected,0,0x85A0)
    struct.pack_into('<H',expected,98,0x0200)
    for word,length,value in [(10,20,b'DOSE-ATAPI-000000001'),(23,8,b'0001    '),(27,40,b'DOSE ATAPI PIO CD-ROM SUBSET')]:
        text=value.ljust(length,b' ')
        for index in range(0,length,2):
            expected[word*2+index:word*2+index+2]=text[index:index+2][::-1]
    check(data==expected,'all512 independent ATAPI PIO0 identification bytes and ATA-swapped own strings')


def readbacks(rows, iso, table, base, mode, final):
    """Verify every complete actual response and all real selected original-DOS instruction contexts."""
    check([r['phase'] for r in rows]==list(range(15)), 'all fifteen complete actual PIO responses')
    expected=responses(iso)
    for row in rows:
        phase,point,data=row['phase'],row['at'],decode(row['data'])
        clock(point,final)
        site=table[7] if phase==0 else table[24] if phase==14 else table[6]
        check(point['segments'][1]==base>>4 and point['bases'][1]==base and point['eip']==site+1 and
              point['v86']==bool(mode) and point['cr0']&0x80000001==(0x80000001 if mode else 0), 'actual immutable own PIO completion site/mode')
        if phase==0:
            identify_contract(data)
        else:
            check(data==expected[phase], 'every complete actual PACKET response/CD byte phase'+str(phase))
    return sum(len(decode(r['data'])) for r in rows)


def page(ram, cpu, linear):
    """Walk the original monitor tables without changing a PTE or setting A/D."""
    if not cpu['Cr0'] & 0x80000000:
        return linear
    pd=cpu['Cr3']&0xFFFFF000
    pde=struct.unpack_from('<I',ram,pd+(linear>>22)*4)[0]
    check(pde&0x87==7, 'original user/writable present4KiB PDE')
    pte=struct.unpack_from('<I',ram,(pde&0xFFFFF000)+((linear>>12)&1023)*4)[0]
    check(pte&7==7, 'original user/writable present PTE')
    return (pte&0xFFFFF000)|(linear&4095)


def ownership(ram,state,program,t,base,which,iso):
    """Prove complete MCB/PSP/JFT/guards, original mappings and exact own real/V86 cut context."""
    cpu=state['components']['cpu']['fields']
    proof=ram[base+t[0]:base+t[1]]
    check(len(ram)==RAM and base>16 and base%16==0 and ram[base:base+2]==b'\xCD\x20', 'actual whole RAM/own PSP')
    paragraphs=(t[3]+15)//16
    check(ram[base-16] in [77,90] and struct.unpack_from('<HH',ram,base-15)==(base>>4,paragraphs), 'whole own program/table/stack MCB')
    check(ram[base+256:base+t[2]]==program[:t[2]-256], 'every immutable executing own byte')
    mode=struct.unpack_from('<H',proof,8)[0]
    allocation=struct.unpack_from('<H',proof,24)[0]<<4
    buffer=struct.unpack_from('<I',proof,32)[0]
    check(mode in [0,1] and ram[allocation-16] in [77,90] and
          struct.unpack_from('<HH',ram,allocation-15)==(base>>4,0x4002) and struct.unpack_from('<HH',proof,26)==(base>>4,0x4002),
          'whole independent DOS data MCB and actual guest owner observation')
    check(base+paragraphs*16<=allocation-16 or allocation+0x40020<=base-16, 'disjoint full own MCB extents')
    check(buffer%131072==0 and allocation+16<=buffer and buffer+131088<=allocation+0x40020<=0xA0000,
          'entire aligned guarded128KiB region belongs to own DOS allocation')
    check(ram[buffer-16:buffer]==ram[buffer+131072:buffer+131088]==b'\xD6'*16, 'both real complete allocation guards')
    expected=bytearray(b'\xA5'*131072)
    expected[:2048]=iso[20*2048:21*2048]
    expected[0x18000:0x18200]=b'\xC0'*512
    if which=='data':
        expected[:128]=iso[22*2048:22*2048+128]
    check(ram[buffer:buffer+131072]==expected, 'all128KiB actual cut memory including untouched regions and partial read')
    check(struct.unpack_from('<IIHHI',ram,base+t[13])==(131072,0,buffer>>4,0,buffer) and proof[48:64]==ram[base+t[13]:base+t[13]+16],
          'whole actual no-bounce original VDS DDS')
    check(struct.unpack_from('<3H',proof,36)==(mode,0,mode), 'original VDS lock/unlock/translation counts at cut')
    handle=struct.unpack_from('<H',ram,base+t[16])[0]
    count,offset,segment=struct.unpack_from('<3H',ram,base+0x32)
    jft=(segment<<4)+offset
    check(5<=handle<count<=255 and jft+count<=0xA0000 and 3<=ram[jft+handle]<0x80, 'actual live ordinary own DOS handle/JFT')
    cs=base>>4
    check(cpu['_segments'][1]==cpu['_segments'][2]==cs and cpu['_segmentCaches'][1]['base']==cpu['_segmentCaches'][2]['base']==base and
          cpu['_registers'][4]&65535==t[12]-2 and cpu['Eip']==t[4 if which=='packet' else 5]+1 and not cpu['Halted'] and
          (mode or not cpu['Eflags']&0x200) and bool(cpu['Eflags']&0x20000)==bool(mode) and cpu['Cr0']&0x80000001==(0x80000001 if mode else 0),
          'actual own completed cut NOP/CS/SS/VM/SP, not original EMM386 monitor')
    check(struct.unpack_from('<H',ram,base+t[17])[0]==(0 if which=='packet' else 512), 'actual own PIO destination end for current phase')
    if which=='data':
        check(cpu['_segments'][0]==buffer>>4 and cpu['_segmentCaches'][0]['base']==buffer and
              cpu['_registers'][7]&65535==128 and cpu['_registers'][1]&65535==193,
              'actual ES:DI128 and remaining193 word iterations at the selected Data-In NOP')
    if mode:
        pages=set(range(base&~4095,(base+paragraphs*16+4095)&~4095,4096))|set(range((buffer-16)&~4095,(buffer+131088+4095)&~4095,4096))
        for linear in pages:
            check(page(ram,cpu,linear)==linear, 'every owned conventional page retains original identity mapping')
    check(struct.unpack_from('<H',proof,44)[0]==(37 if which=='packet' else 38), 'all actual committed IRQ records precede the selected cut')
    check(proof[:8]==b'ATAPIP01' and proof[248:]==b'ATEND001' and
          struct.unpack_from('<II',proof,16)==(0x524,0x135) and struct.unpack_from('<H',proof,10)[0]==12 and
          struct.unpack_from('<I',proof,72)[0]==0x12308086 and struct.unpack_from('<H',proof,80)[0]==3 and proof[82]==15 and proof[83]==0x40 and
          proof[84]==6 and struct.unpack_from('<5I',proof,88)==(20,22,8205,21,197) and
          struct.unpack_from('<H',proof,136)[0]==0 and proof[140:142]==b'\xFF\xFF' and struct.unpack_from('<H',proof,142)[0]==1,
          'independent actual proof/model/ISO/gates before return')
    return dict(mode=mode,base=base,buffer=buffer,allocation=allocation,proof=proof,handle=handle)


def critical_cut(state,point,owned,which,iso):
    """Bind the entire actual optical staging/cursor and still-live PCM/future PIT at both cuts."""
    check(state['components']['ata1/_atapi']['type'].endswith('.AtapiCdRom'), 'actual fixed secondary optical owner path')
    at=field(state,'.AtapiCdRom')
    diagnostic=point['optical']
    cpu=state['components']['cpu']['fields']
    check(point['nanoseconds']==state['scheduler']['now'] and point['instructions']==cpu['Instructions'] and point['flags']==cpu['Eflags'] and
          point['cr0']==cpu['Cr0'] and point['cr3']==cpu['Cr3'] and point['eip']==cpu['Eip'] and
          point['registers']==cpu['_registers'] and point['segments']==cpu['_segments'] and
          point['bases']==[cache['base'] for cache in cpu['_segmentCaches']] and
          point['v86']==bool(cpu['Eflags']&0x20000) and point['phase']==12 and
          point['primaryIrq']==state['components']['ata0']['fields']['_interrupt'] and
          point['secondaryIrq']==state['components']['ata1']['fields']['_interrupt'],
          'every real cut CPU/IRQ observation binds the same entire captured architectural state/time')
    phase=2 if which=='packet' else 5
    offset=0 if which=='packet' else 128
    check(at['DeviceNumber']==0 and at['HasDisc'] and at['DiscBlocks']==128 and at['_phase']==phase and at['Status']==0x48 and
          at['_packetOffset']==(10 if which=='packet' else 12) and at['_offset']==offset and
          at['_event']==at['_deadline']==0 and at['_selected'] and not at['_pendingInterrupt'] and
          not at['_trayOpen'] and at['_started'] and not at['_unitAttention'] and at['_senseKey']==at['_senseAsc']==at['_senseAscq']==0,
          'actual optical physical/media/packet/IRQ/cursor state at cut')
    task=decode(at['_task'])
    check(len(task)==8 and task[1]==0 and task[2]==(1 if which=='packet' else 2) and task[4:6]==b'\x00\x02' and
          at['_features']==at['_control']==0 and at['_byteLimit']==512, 'actual PIO features/control/Reason/512-byte task count')
    check(decode(at['_packet'])==cdbs()[12] and at['ReadBlockCount']==2 and at['PacketsCompleted']==11 and
          at['DataBytesRead']==4726+(0 if which=='packet' else 128), 'actual full12byte command staging and independently counted completed bytes')
    if which=='data':
        check(at['_payloadLength']==2048 and at['_payloadOffset']==0 and at['_phaseLength']==512 and at['_readCommand'] and
              at['_readLba']==22 and at['_remainingBlocks']==5 and decode(at['_data'])==iso[22*2048:23*2048],
              'every actual partially consumed first CD block/phase/remaining cursor')
    else:
        check(decode(at['_data'])==bytes(2048) and at['_payloadLength']==at['_payloadOffset']==at['_phaseLength']==at['_readLba']==at['_remainingBlocks']==0 and not at['_readCommand'], 'new actual PACKET admission clears old staging/cursors without reading CD early')
    check(diagnostic['phase']==phase and diagnostic['packetOffset']==(10 if which=='packet' else 12) and diagnostic['offset']==offset and
          diagnostic['status']==0x48 and not diagnostic['pendingInterrupt'] and diagnostic['generation']==at['Generation'],
          'independent complete saved graph matches actual nonmutating cut observation')
    expected_diagnostic=dict(phase=at['_phase'],status=at['Status'],error=task[1],reason=task[2],byteCount=task[4]|task[5]<<8,
        packetOffset=at['_packetOffset'],offset=at['_offset'],length=at['_phaseLength'],lba=at['_readLba'],remainingBlocks=at['_remainingBlocks'],
        pendingInterrupt=at['_pendingInterrupt'],selected=at['_selected'],trayOpen=at['_trayOpen'],preventRemoval=at['_preventRemoval'],
        started=at['_started'],unitAttention=at['_unitAttention'],senseKey=at['_senseKey'],senseAsc=at['_senseAsc'],senseAscq=at['_senseAscq'],
        eventToken=at['_event'],deadline=at['_deadline'],generation=at['Generation'])
    check(diagnostic==expected_diagnostic, 'all actual readonly optical phase diagnostics bind the complete same saved device fields')
    sb=field(state,'.SoundBlaster16')
    check(sb['RemainingSamples']>0 and sb['Dma8Grants']==sb['TransferredSamples']==512-sb['RemainingSamples'] and
          sb['Dma16Grants']==sb['CompletedBlocks']==0 and sb['_activeRate']==8000 and sb['_activePeriod']==0 and not sb['_recording'],
          'actual active real PCM partial phase at both cuts')
    now=state['scheduler']['now']
    events=[event for event in state['scheduler']['events'] if event['id'] not in state['scheduler']['cancelled']]
    check(any(e['owner']=='pit' and e['time']>now for e in events) and
          any(e['owner']=='sb16' and e['id']==sb['_sampleEvent'] and e['time']==sb['_sampleDeadline'] and now<e['time']<=now+125000 for e in events),
          'actual future PIT and live exact PCM sample token')
    channel=state['components']['dma/_channels/1']['fields']
    start=owned['buffer']+0x18000
    check((channel['Page']<<16|channel['BaseAddress'])==start and channel['BaseCount']==511 and channel['Mode']==0x49 and not channel['Masked'] and
          channel['Address']==(start+sb['TransferredSamples'])&65535 and channel['Count']==511-sb['TransferredSamples'],
          'real DMA1 points only at the complete owned512-byte sample block')


def services(rows,t,base,mode,program):
    """Bind original VDS calls/returns, entire ES:DI DDS, AX/BX/DX, CF and original V86 state."""
    expected=['lock','disable','enable','unlock'] if mode else []
    check([row['kind'] for row in rows]==expected, 'exact original VDS lifecycle without fallback')
    for index,row in enumerate(rows):
        before,after=row['before'],row['after']
        site=t[18+index]
        check(after is not None and before['registers'][0]&65535==[0x8103,0x810B,0x810C,0x8104][index] and
              before['eip']==site+1 and after['eip']==site+3, 'actual original VDS function and unchanged own INT/return')
        for point in [before,after]:
            check(point['v86'] and point['cr0']&0x80000001==0x80000001 and point['segments'][1]==base>>4 and point['bases'][1]==base,
                  'actual original EMM386 V86/PE/PG, not replaced monitor')
        check(not after['flags']&1, 'actual original VDS CF success')
        check(before['cr3']==after['cr3'] and before['cr0']&0x80000001==after['cr0']&0x80000001, 'same actual original client address space on these fixed DOS/EMM386 fixtures')
        if row['kind'] in ['lock','unlock']:
            for point in [before,after]:
                check(point['segments'][0]==base>>4 and point['bases'][0]==base and point['registers'][7]&65535==t[13],
                      'actual original VDS same ES selector/base plus DDS DI on entry and return')
            check(before['registers'][2]&65535==(0x2C if row['kind']=='lock' else 0), 'actual original VDS lock/unlock DX flags')
        else:
            check(before['registers'][3]&65535==1 and before['registers'][2]&65535==0, 'actual DMA1 original translation BX/DX')


def packet_words(rows,iso,base,mode,final):
    """Reconstruct all real ordered CD reads and twelve-byte CDB writes, including inert gated writes."""
    expected=responses(iso)
    for phase in range(15):
        words=[w for w in rows if w['phase']==phase]
        written=[w for w in words if w['write']]
        data=b''.join(struct.pack('<H',w['value']) for w in words if not w['write'])
        if phase==0:
            identify_contract(data)
            check(not written,'IDENTIFY does not accept packet words')
        else:
            correct=list(struct.unpack('<6H',cdbs()[phase]))
            actual=[w['value'] for w in written]
            if phase==12:
                correct=correct[:5]+[0xDEAD,0xDEAD]+correct[5:]
            check(actual==correct,'all six real little-endian CDB words and ignored gate writes phase'+str(phase))
            check(data==expected[phase],'every original PIO data word equals independent response/CD phase'+str(phase))
        for word in words:
            check(word['cs']==base>>4 and 256<=word['eip']<65536 and word['nanoseconds']<=final['scheduler']['now'],
                  'actual original own instruction/context for every real data-port word')
    check(len(rows)==8507+86,'all actual data/CDB/gate word transfers exactly once')


def irq_contract(ledger,t,base,iso,final):
    """Independently specify every command/data/terminal phase and all real frames/status/PIC acknowledgements."""
    expected=[]
    data=responses(iso)
    for phase in range(1,15):
        if phase!=14:
            expected.append((phase,1,1024 if phase==13 else 512,0x48))
        limit=1024 if phase==13 else 512
        for at in range(0,len(data[phase]),limit):
            expected.append((phase,2,min(limit,len(data[phase])-at),0x48))
        expected.append((phase,3,0,0x41 if phase in [6,8] else 0x40))
    check(len(expected)==63,'independent intended physical IRQ phase count')
    boundaries=[row for row in ledger['boundaries'] if row['before']['phase']>0 and row['kind'] in ['entry','commit','iret']]
    check([row['kind'] for row in boundaries]==['entry','commit','iret']*63,'all63 actual handler entry/commit/IRET boundaries')
    acks=[row for row in ledger['acks'] if row['irq']==15 and row['at']['phase']>0]
    check(len(acks)==63 and all(a['vector']==0x77 and not a['spurious'] for a in acks),'actual secondary MIRQ0 through physical IRQ15/PIC vector')
    records=[]
    for index,(phase,reason,count,status) in enumerate(expected):
        entry,commit,ret=boundaries[index*3:index*3+3]
        a,b,c=entry['before'],commit['before'],ret['before']
        acknowledge=acks[index]['at']
        clock(acknowledge,final)
        previous=boundaries[index*3-1]['after'] if index else None
        check(acknowledge['phase']==phase and acknowledge['secondaryIrq'] and acknowledge['optical']['pendingInterrupt'] and
              acknowledge['nanoseconds']<=a['nanoseconds'] and acknowledge['instructions']<=a['instructions'] and
              (previous is None or acknowledge['nanoseconds']>=previous['nanoseconds'] and acknowledge['instructions']>=previous['instructions']),
              'each original MIRQ0/IRQ15 ACK is associated with its exact following handler and previous real IRET')
        frame=decode(entry['frame'])
        check(len(frame)==6 and decode(ret['frame'])==frame and struct.unpack_from('<HH',frame)==(t[11],base>>4) and
              struct.unpack_from('<H',frame,4)[0]&0x300==0x200,'actual own IRQ frame returns after STI shadow with IF and no TF')
        for point,site in [(a,t[8]+2),(b,t[9]+1),(c,t[10]+1)]:
            clock(point,final)
            check(point['phase']==phase and point['segments'][1]==point['segments'][2]==base>>4 and
                  point['bases'][1]==point['bases'][2]==base and point['eip']==site and (point['v86'] or not point['flags']&0x200),
                  'actual own IRQ code/stack context; original V86 monitor retains its hardware IF policy')
        sp=a['registers'][4]&65535
        check(sp==t[12]-(8 if phase==14 else 10) and b['registers'][4]&65535==sp-34 and c['registers'][4]&65535==sp,
              'complete private CALL/IRQ/PUSHAD/DS stack widths')
        after=ret['after']
        check(after is not None and after['eip']==t[11] and after['segments']==c['segments'] and
              after['registers']==a['registers'][:4]+[(a['registers'][4]&0xFFFF0000)|((sp+6)&65535)]+a['registers'][5:] and after['flags']&0x200,
              'actual IRET restores registers/segments/SP and resumes original waiter')
        ports=[p for p in ledger['ports'] if p['at']['phase']==phase and a['instructions']<=p['at']['instructions']<=c['instructions']]
        actual=[(p['address'],p['value'],p['width'],p['write']) for p in ports]
        want=[(0x376,status,1,False),(0x172,reason,1,False),(0x174,count&255,1,False),(0x175,count>>8,1,False),
              (0x177,status,1,False),(0xA0,0x20,1,True),(0x20,0x20,1,True)]
        check(actual==want,'every actual independent phase/bytecount/Status ACK and ordered slave/master EOI')
        check(ports[0]['at']['optical']['pendingInterrupt'] is True and ports[-3]['at']['optical']['pendingInterrupt'] is False,
              'AltStatus preserves pending source and ordinary Status clears only device latch')
        row=bytearray(32)
        struct.pack_into('<8H',row,0,15,phase,index+1,t[11],base>>4,struct.unpack_from('<H',frame,4)[0],sp,base>>4)
        row[16:21]=bytes([status,reason,count&255,count>>8,status])
        records.append(bytes(row))
    return b''.join(records)


def port_contract(rows,final):
    """Bind actual PCI function/gates, configured channel/MIRQ and nIEN admission using original CPU OUTs."""
    cfg=[row for row in rows if row['write'] and row['address']==0xCF8]
    check([row['value'] for row in cfg]==[0x80003900,0x80003940,0x80003904,0x80003870,0x80003904,0x80003904,0x80003940,0x80003940],
          'every actual PCI mechanism-one address for original function/MIRQ/gates')
    check([(p['value'],p['width']) for p in rows if p['write'] and p['address']==0xCFC]==[(2,2),(3,2),(0x8000,4),(0x80008000,4)],
          'actual independent IOSE and secondary IDETIM DE disabled/restored')
    commands=[(p['at']['phase'],p['value']) for p in rows if p['write'] and p['address']==0x177]
    check(commands==[(0,0xA1)]+[(i,0xA0) for i in range(1,15)], 'all original actual PACKET wrapper/identify command bytes')
    for row in rows:
        if row['write'] and row['address']==0x177 and row['value']==0xA0:
            point,phase=row['at'],row['at']['optical']
            check(phase['phase']==1 and phase['deadline']==point['nanoseconds']+5000 and phase['eventToken']>0 and not phase['pendingInterrupt'], 'actual nominal5us command-ready token at original OUT')
    check([p['value'] for p in rows if p['write'] and p['address']==0x376]==[2,0,2,0],'actual nIEN only changes physical interrupt output')
    hidden=[p for p in rows if not p['write'] and p['value']==255 and p['at']['phase']==12 and p['address'] in [0x177,0x376]]
    check([p['address'] for p in hidden]==[0x177,0x376], 'actual ordinary and alternate registers float behind independent PCI/DE gates')
    for row in rows:
        clock(row['at'],final)


def pcm_contract(ledger,cuts,final,owned,counts,result):
    """Independently bind all512 raw DMA/DAC publications and actual positive future mixer duration/digests."""
    transfers,samples=ledger['transfers'],ledger['samples']
    check(len(transfers)==1 and len(samples)==512,'one actual DMA stream and all512 native sample publications')
    transfer=transfers[0]
    check({k:transfer[k] for k in ['rate','word','stereo','signed','recording','autoInit','fifoRequested','samples','dmaChannel','periodNanoseconds']}==
          dict(rate=8000,word=False,stereo=False,signed=False,recording=False,autoInit=False,fifoRequested=False,samples=512,dmaChannel=1,periodNanoseconds=0),
          'actual concrete DMA1 mono8/8000Hz/512 transfer')
    for i,sample in enumerate(samples,1):
        check(sample==dict(time=transfer['time']+125000*i,word=False,recording=False,right=False,raw=192,value=16384,dmaResult='Transferred'),
              'every original independently timed C0 byte and raw DAC component')
    for state,count in zip(cuts,counts):
        sb=field(state,'.SoundBlaster16')
        check(sb['TransferredSamples']==sum(s['time']<=state['scheduler']['now'] for s in samples)==count[6],
              'saved PCM cursor exactly equals all actual preceding native publications')
    sb=field(final,'.SoundBlaster16')
    check(sb['TransferredSamples']==sb['Dma8Grants']==512 and sb['RemainingSamples']==sb['Dma16Grants']==sb['_sampleEvent']==0 and sb['CompletedBlocks']==1,
          'native stream really completes before freeing DOS ownership')
    start,end=field(cuts[1],'.PcAudioMixer'),field(final,'.PcAudioMixer')
    check(start['GeneratedFrames']==cuts[1]['scheduler']['now']*48000//1000000000 and end['GeneratedFrames']==final['scheduler']['now']*48000//1000000000 and
          start['_count']%2==0 and end['_count']==0,'actual integer48kHz phase and completed final flush')
    frames=end['GeneratedFrames']-start['GeneratedFrames']+start['_count']//2
    audio=result['audioFuture']
    check(result['restoredAudioFuture']==[audio,audio] and audio['frames']==frames>480 and len(audio['sha256'])==64 and
          all(c in '0123456789ABCDEF' for c in audio['sha256']) and audio['sha256']!=sha(b''),'all three actual positive future whole-PCM identities and exact duration')
    check(samples[-1]['time']<=final['scheduler']['now'],'native future completes during actual continued execution')
    return frames


def text_contract(final,ram,reported):
    """Decode actual Trio64 legacy text cells/fonts/attributes/cursor independently, with no render call/image."""
    v=final['components']['video']['fields']
    planes=[decode(x) for x in v['_planes']]
    crtc,seq,gc,attr=(decode(v[k]) for k in ['_crtc','_seq','_gc','_attr'])
    check(v['IsTrio64'] and len(planes)==4 and all(len(p)==524288 for p in planes) and v['_trioAdvanced']==0 and
          v['_displayEnabled'] and not seq[1]&32 and not gc[6]&1, 'actual visible original standard VGA text')
    columns,height=crtc[1]+1,(crtc[9]&31)+1
    rows=(crtc[0x12]+((crtc[7]&2)<<7)+((crtc[7]&0x40)<<3)+1)//height
    check((columns,rows,height)==(80,25,16) and ram[0x449]==3 and struct.unpack_from('<H',ram,0x44A)[0]==80 and
          ram[0x484]==24 and ram[0x485]==16,'actual complete80x25 BIOS mode')
    start,stride=crtc[12]<<8|crtc[13],crtc[0x13]*2
    check(stride==80,'actual text stride')
    chars=bytes(planes[0][(start+y*80+x)&0x7FFFF] for y in range(25) for x in range(80))
    attrs=bytes(planes[1][(start+y*80+x)&0x7FFFF] for y in range(25) for x in range(80))
    lines=[''.join(chr(c) if 32<=c<127 else ' ' for c in chars[y*80:y*80+80]).rstrip() for y in range(25)]
    text='\n'.join(lines).rstrip()
    check(text=='\n'.join(reported.splitlines()).rstrip() and 'SNAPSHOT-DONE' in text and 'ATAPI-DONE' in text and text.endswith('A:\\>'),
          'actual raw-VRAM original DOS completion text')
    echoes=[y for y,line in enumerate(lines) if line=='ATAPICONTINUED']
    prompt=max(y for y,line in enumerate(lines) if line)
    check(len(echoes)==1 and echoes[0]<prompt and lines[prompt]=='A:\\>' and all(not line for line in lines[echoes[0]+1:prompt]),
          'new actual ECHO output line followed by original prompt')
    check((crtc[14]<<8|crtc[15])==start+prompt*80+4 and struct.unpack_from('<H',ram,0x450)[0]==prompt*256+4,
          'completed four-character BDA and CRTC cursor')
    for index in list(range(echoes[0]*80,echoes[0]*80+14))+list(range(prompt*80,prompt*80+4)):
        a=attrs[index]
        check(not a&128 and attr[(a&15)&attr[0x12]]!=attr[((a>>4)&7)&attr[0x12]] and any(planes[2][chars[index]*32:chars[index]*32+16]),
              'actual visible attributes and retained nonempty glyph')
    return sha(bytes(value for c,a in zip(chars,attrs) for value in (c,a)))


def file_contract(actual,expected):
    """Compare the actual independently decoded FAT file bytes against the same independent CD/proof/raw expectations."""
    check(set(actual)==set(expected)=={b'ATAPI   DAT',b'ATAPIIRQDAT',b'ATAPIRAWDAT',b'CD-COPY BIN',b'CD-TEXT TXT'},
          'exact five actual independent DOS files')
    for name,data in actual.items():
        check(isinstance(data,bytes) and data==expected[name], 'complete actual independent FAT content '+name.decode('ascii'))


def media_contract(root,iso,files):
    """Replay real sealed COW/FAT files, untouched own disks and all immutable2048-byte CD seals."""
    final=[]
    for index in range(3):
        path=root/('final-'+str(index)+'.dosemedia')
        metadata=read(path/'media.json')
        cd=[row for row in metadata['media'] if row['device']=='cdrom']
        check(len(cd)==1 and cd[0]['slot']==2 and cd[0]['sectors']==128 and cd[0]['sectorSize']==2048 and cd[0]['baseSha256']==sha(iso) and
              cd[0]['journalBytes']==0 and cd[0]['journalSha256'] is None and cd[0]['journalName'] is None,'one actual immutable ISO2048 seal, no CD overlay')
        drive=diskio.disk(path)
        actual={name:diskio.fat_file(drive,name) for name in files}
        file_contract(actual,files)
        for slot,seed in [(1,0x31),(3,0xD1)]:
            d=diskio.disk(path,slot)
            check(d.sectors==4096 and all(d.sector(s)==bytes((s*13+p*17+(p>>8)*7+seed)&255 for p in range(512)) for s in range(4096)),
                  'all actual own auxiliary/scratch sectors remain untouched')
        final.append(actual)
    check(final[0]==final[1]==final[2],'all three actual complete DOS file sets equal')
    return final[0]


def common(root,result,cuts,final,program,iso):
    """Bind actual manifests, strict schema/profile/build, complete RAM and immutable optical identity."""
    check(result['passed'] and result['returnedToDos'] and result['postAtapiEcho'] and result['exits']==[True]*3 and
          result['comparisons']>2 and result['futureSteps']>10000 and result['programSha256']==sha(program) and result['isoSha256']==sha(iso),
          'actual three-PC/DOS/positive future/program/ISO result')
    schema=read(REPO/'docs/state-schema-v1.json')['nodes']
    for name,state in zip(['packet','data'],cuts):
        saved=root/(name+'-cut.dossav')
        manifest=read(saved/'snapshot.json')
        check(manifest==result[name+'Manifest'] and manifest['kind']=='DOSE-PC-STATE-1' and manifest['version']==1 and manifest['ramBytes']==RAM and
              manifest['stateSha256']==result[name+'StateSha256']==digest(saved/'state.json') and
              manifest['ramSha256']==result[name+'RamSha256']==digest(saved/'ram.bin') and manifest['mediaSha256']==digest(saved/'media.dosemedia/media.json') and
              manifest['virtualNanoseconds']==state['scheduler']['now'],'actual whole build-bound snapshot and complete seals')
        definition=read(saved/'media.dosemedia/media.json')['machine']
        profile=definition['hardware']
        check(profile['id']=='dose-430fx-piix-s3-atapi-dev-v1' and profile['cpu']=='p54c-b5-subset-development' and profile['cpuHz']==100000000 and
              profile['ramMiB']==32 and profile['opticalDrive']=='sff8020i-rev25-pio-development' and profile['atapiSlot']==2 and
              [d['slot'] for d in definition['opticalDiscs']]==[2] and sorted(d['slot'] for d in definition['hardDisks'])==[0,1,3],
              'strict actual original board/model/clock and distinct optical slot')
        for component in state['components'].values():
            check(component['version']==1 and component['type'] in schema,'registered explicit state type')
            expected={f['name'] for f in schema[component['type']]['fields'] if f['mode'] not in ['children','excluded']}
            check(set(component['fields'])==expected,'all registered exact invariant/value fields once')
        cpu=state['components']['cpu']['fields']
        check(cpu['Model']==1 and cpu['TimestampFrequency']==100000000 and cpu['_tscEpochTicks']==0 and cpu['Cr4']&~12==0,
              'actual constructor-fixed100MHz CPU subset/source')
    check(result['finalStateSha256']==digest(root/'final-state.json') and result['finalRamSha256']==digest(root/'final-ram.bin'),
          'whole actual final graph/RAM identities')


def verify(root,program,iso_path,negative=True):
    """Certify all independent original-device/media/continuation contracts before testing discarded mutations."""
    root=media.safe(root)
    program=media.safe(program).read_bytes()
    iso=media.safe(iso_path).read_bytes()
    files=iso_contract(iso)
    t=metadata(program)
    result,ledger=read(root/'result.json'),read(root/'ledger.json')
    cuts=[read(root/(name+'-cut.dossav')/'state.json') for name in ['packet','data']]
    rams=[(root/(name+'-cut.dossav')/'ram.bin').read_bytes() for name in ['packet','data']]
    final,ram=read(root/'final-state.json'),(root/'final-ram.bin').read_bytes()
    common(root,result,cuts,final,program,iso)
    base=result['programBase']
    check(ledger['programBase']==ledger['original']['programBase']==base,'actual unchanged own COM identity')
    owned=[ownership(data,state,program,t,base,name,iso) for data,state,name in zip(rams,cuts,['packet','data'])]
    check(owned[0]['mode']==owned[1]['mode']==int(result['system'].startswith('emm386')),'actual original real/V86 manager selection')
    points=[read(root/(name+'-point.json')) for name in ['packet','data']]
    for state,point,owner,name in zip(cuts,points,owned,['packet','data']):
        critical_cut(state,point,owner,name,iso)
        clock(point,final)
    original=ledger['original']
    bytes_read=readbacks(original['readbacks'],iso,t,base,owned[0]['mode'],final)
    expected_responses=responses(iso)
    cd_phases=[phase for phase,command in cdbs().items() if command[0] in [0x28,0xA8] and expected_responses[phase]]
    cd_bytes=sum(len(decode(row['data'])) for row in original['readbacks'] if row['phase'] in cd_phases)
    expected_cd_bytes=sum(len(expected_responses[phase]) for phase in cd_phases)
    check(cd_bytes==expected_cd_bytes==16384 and cd_bytes%2048==0 and bytes_read==17014, 'separate actual full READ block bytes from all identify/query/sense PIO bytes')
    cd_blocks=cd_bytes//2048
    packet_words(original['words'],iso,base,owned[0]['mode'],final)
    irq_bytes=irq_contract(original,t,base,iso,final)
    port_contract(original['ports'],final)
    services(original['services'],t,base,owned[0]['mode'],program)
    frames=pcm_contract(original,cuts,final,owned,[ledger['packetCounts'],ledger['dataCounts']],result)
    for i,key in enumerate(KEYS):
        check(original[key][ledger['packetCounts'][i]:ledger['dataCounts'][i]]==ledger['packetFuture'][key][:ledger['packetDataCounts'][i]],
              'every actual first continued future '+key)
        check(original[key][ledger['dataCounts'][i]:]==ledger['packetFuture'][key][ledger['packetDataCounts'][i]:]==ledger['dataFuture'][key],
              'every actual second/third continued future '+key)
    raw=b'ATRAW001'+struct.pack('<QH',0xC90FDAA22168C235,0x4000)+struct.pack('<QH',0xFEDCBA9876543210,0x4001)+struct.pack('<HH',0x37F,0)
    proof=bytearray(owned[0]['proof'])
    struct.pack_into('<H',proof,10,15)
    struct.pack_into('<H',proof,38,owned[0]['mode'])
    struct.pack_into('<H',proof,44,63)
    struct.pack_into('<H',proof,120,62)
    proof[122]=0x40
    struct.pack_into('<H',proof,136,1)
    times=[row for row in original['boundaries'] if row['kind'].startswith('tsc-')]
    mode=owned[0]['mode']
    check([row['kind'] for row in times]==([] if mode else ['tsc-before','tsc-after']),
          'two actual real-mode RDTSC instructions or none in original V86')
    check(original['timestampAttempts']==len(times) and original['timestampGeneralProtections']==0 and
          original['lastTimestampGeneralProtection'] is None and
          ledger['packetFuture']['timestampAttempts']==ledger['dataFuture']['timestampAttempts']==(0 if mode else 1) and
          ledger['packetFuture']['timestampGeneralProtections']==ledger['dataFuture']['timestampGeneralProtections']==0,
          'actual original timestamp attempt/fault counters for all three PCs')
    if mode:
        check(not any(proof[112:120]) and not any(proof[128:136]), 'V86 never invents guest RDTSC result bytes')
    for row,site,destination in zip(times,[t[26],t[27]],[112,128]):
        point=row['before']
        clock(point,final)
        check(point['eip']==site+1 and point['segments'][1]==base>>4 and point['bases'][1]==base and
              point['v86']==bool(owned[0]['mode']),'actual exact timestamp opcode/context')
        struct.pack_into('<Q',proof,destination,point['counter'])
    files_out={b'ATAPI   DAT':bytes(proof),b'ATAPIIRQDAT':irq_bytes,b'ATAPIRAWDAT':raw,b'CD-COPY BIN':files['payload'],b'CD-TEXT TXT':files['text']}
    actual_files=media_contract(root,iso,files_out)
    for name in ['packet','data']:
        check(diskio.fat_file(diskio.disk(root/(name+'-cut.dossav')/'media.dosemedia'),b'ATAPI   DAT')==b'ATAPIP01',
              'real preexisting open DOS header survives both cuts')
    text_sha=text_contract(final,ram,result['finalScreen'])
    echo_at=result['echoAt']
    check(result['echoBytes']>0 and any(a['irq']==1 and a['at']['nanoseconds']>=echo_at['nanoseconds'] for a in original['acks']) and
          any(p['address']==0x60 and not p['write'] and p['value']==0x1C and p['at']['nanoseconds']>=echo_at['nanoseconds'] for p in original['ports']),
          'fresh actual accepted Enter make/physical IRQ1/IN60 before following prompt')
    last=field(final,'.AtapiCdRom')
    check(last['PacketsCompleted']==14 and last['ReadBlockCount']==8 and last['DataBytesRead']==17014 and last['_phase']==0 and
          not last['_pendingInterrupt'] and not last['_unitAttention'],'all actual terminal original CD counters/status')
    failures=0
    def reject(function,reason):
        """Require one deliberately discarded in-memory corruption to fail the independent contract."""
        nonlocal failures
        try:
            function()
        except (AssertionError,ValueError,KeyError,IndexError,struct.error):
            failures+=1
            return
        raise AssertionError('Accepted corrupt independent oracle: '+reason)
    if negative:
        for at in range(256):
            bad=dict(actual_files)
            data=bytearray(actual_files[b'ATAPI   DAT'])
            data[at]^=1
            bad[b'ATAPI   DAT']=bytes(data)
            reject(lambda value=bad:file_contract(value,files_out),'each actual decoded final FAT proof byte')
        for phase in [1,2,4,5,7,9,10,11,12,13]:
            rows=list(original['readbacks'])
            row=dict(rows[phase])
            data=bytearray(decode(row['data']))
            data[len(data)//2]^=1
            row['data']=base64.b64encode(data).decode()
            rows[phase]=row
            reject(lambda rows=rows:readbacks(rows,iso,t,base,owned[0]['mode'],final),'actual original PIO/CD byte')
        for member in ['eip','cr3','nanoseconds']:
            point=copy.deepcopy(points[1])
            point[member]^=1
            reject(lambda value=point:critical_cut(cuts[1],value,owned[1],'data',iso),'actual exact same cut point '+member)
        for member in ['_packetOffset','_offset','_phase','DataBytesRead','ReadBlockCount']:
            bad=copy.deepcopy(cuts[1])
            field(bad,'.AtapiCdRom')[member]^=1
            reject(lambda state=bad:critical_cut(state,points[1],owned[1],'data',iso),'saved original optical '+member)
        for at in [base+t[2]-1,owned[0]['buffer']-1,owned[0]['buffer']+131072,owned[0]['buffer']+65536]:
            bad=bytearray(rams[0])
            bad[at]^=1
            reject(lambda value=bad:ownership(value,cuts[0],program,t,base,'packet',iso),'actual code/allocation guard/unused byte')
        bad_words=list(original['words'])
        row=dict(bad_words[-1])
        row['value']^=1
        bad_words[-1]=row
        reject(lambda:packet_words(bad_words,iso,base,owned[0]['mode'],final),'actual data-port word')
        for member in ['frame','spurious','reason']:
            bad=dict(original)
            if member=='frame':
                bad['boundaries']=list(original['boundaries'])
                index=next(i for i,row in enumerate(bad['boundaries']) if row['kind']=='entry' and row['before']['phase']>0)
                row=dict(bad['boundaries'][index])
                frame=bytearray(decode(row['frame']))
                frame[0]^=1
                row['frame']=base64.b64encode(frame).decode()
                bad['boundaries'][index]=row
            elif member=='spurious':
                bad['acks']=copy.deepcopy(original['acks'])
                next(row for row in bad['acks'] if row['irq']==15 and row['at']['phase']>0)['spurious']=True
            else:
                bad['ports']=list(original['ports'])
                index=next(i for i,row in enumerate(bad['ports']) if row['address']==0x172 and not row['write'] and row['at']['phase']>0)
                row=dict(bad['ports'][index])
                row['value']^=1
                bad['ports'][index]=row
            reject(lambda rows=bad:irq_contract(rows,t,base,iso,final),'actual physical IRQ/frame/phase '+member)
        for member in ['value','address']:
            ports=list(original['ports'])
            index=next(i for i,p in enumerate(ports) if p['write'] and p['address']==0xCFC)
            row=dict(ports[index])
            row[member]^=1
            ports[index]=row
            reject(lambda rows=ports:port_contract(rows,final),'actual PCI gate transfer '+member)
        bad_ram=bytearray(ram)
        bad_ram[0x450]^=1
        reject(lambda:text_contract(final,bad_ram,result['finalScreen']),'complete actual DOS cursor')
        for member,value in [('frames',0),('frames',frames-1),('sha256','')]:
            bad=dict(result)
            bad['audioFuture']=dict(result['audioFuture'])
            bad['audioFuture'][member]=value
            reject(lambda value=bad:pcm_contract(original,cuts,final,owned,[ledger['packetCounts'],ledger['dataCounts']],value),'actual positive full PCM '+member)
        for index in [0,255,511]:
            bad=dict(original)
            bad['samples']=list(original['samples'])
            row=dict(bad['samples'][index])
            row['raw']^=1
            bad['samples'][index]=row
            reject(lambda rows=bad:pcm_contract(rows,cuts,final,owned,[ledger['packetCounts'],ledger['dataCounts']],result),'actual native PCM sample')
        for index in range(len(original['services'])):
            for key in ['ax','es','di','v86']:
                bad=copy.deepcopy(original['services'])
                point=bad[index]['before']
                if key=='ax':
                    point['registers'][0]^=1
                elif key=='v86':
                    point['v86']=not point['v86']
                elif bad[index]['kind'] not in ['lock','unlock']:
                    continue
                elif key=='es':
                    point['segments'][0]^=1
                else:
                    point['registers'][7]^=1
                reject(lambda rows=bad:services(rows,t,base,owned[0]['mode'],program),'actual original VDS '+key)
        for index in range(len(original['services'])):
            bad=copy.deepcopy(original['services'])
            bad[index]['after']['cr3']^=4096
            reject(lambda rows=bad:services(rows,t,base,owned[0]['mode'],program),'actual original VDS returned client CR3')
        for index in [16*2048+80,20*2048+2,21*2048,22*2048+8204,127*2048]:
            bad=bytearray(iso)
            bad[index]^=1
            reject(lambda value=bad:iso_contract(value),'actual ISO structure/extent/tail byte')
    return dict(passed=True,system=result['system'],guestV86=bool(owned[0]['mode']),cuts=2,restoredContinuations=2,
                readbacks=15,packetCommands=14,frames=63,verifiedPioDataBytes=bytes_read,verifiedCdBlocks=cd_blocks,verifiedCdBytes=cd_bytes,comparisons=result['comparisons'],
                actualGuestRdtsc=len(times),timestampFaults=original['timestampGeneralProtections'],
                negativeOracles=failures,rawFileHex=raw.hex().upper(),proofFileSha256=sha(proof),payloadSha256=sha(files['payload']),
                textSha256=sha(files['text']),irqFileSha256=sha(irq_bytes),consoleCellsSha256=text_sha,pcmFutureFrames=frames,
                limitations=['Direct own PIO client, not MSCDEX/original CD-driver/Windows95 acceptance.',
                             'Guest RDTSC executes only in real mode; original V86 uses saved CPU/scheduler clock-source oracles without a privilege bypass.',
                             'Native512 raw DAC/DMA samples independently reconstructed; published wholePCM compared by actual peer identity/digest.',
                             'Own standard data-only ISO; no raw/audio sectors, ATAPI DMA, overlap or physical host device.'])


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence',type=Path)
    parser.add_argument('--program',type=Path,required=True)
    parser.add_argument('--iso',type=Path,required=True)
    args=parser.parse_args()
    print(json.dumps(verify(args.evidence,args.program,args.iso),indent=2))
