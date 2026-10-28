# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent original PCI BIOS/BIOS32/VCPI/ROM-frame and full-PC continuation proof."""
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
SPEC = importlib.util.spec_from_file_location('pci_bios_dos_media', REPO / 'scripts/verify-mscdex-test.py')
dos = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(dos)
RAM = 32 * 1048576
KEYS = ['ports', 'acks', 'services', 'firmware']
BDFS = [0, 0x18, 0x38, 0x39]
SEED = 0x8000f8a0


def check(value, why):
    """Reject a missing original instruction, frame, byte or continuation effect."""
    if not value:
        raise AssertionError(why)


def read(path):
    """Read only one named own observation."""
    return dos.read(path)


def sha(data):
    """Hash one explicitly named protected or own byte extent."""
    return hashlib.sha256(data).hexdigest().upper()


def u16(data, offset):
    """Decode an original little-endian word independently."""
    return struct.unpack_from('<H', data, offset)[0]


def u32(data, offset):
    """Decode an original little-endian dword independently."""
    return struct.unpack_from('<I', data, offset)[0]


def field(state, suffix):
    """Require one actual registered fixed device type."""
    return dos.old.field(state, suffix)


def clock(point, final):
    """Reconstruct complete100MHz phase without the host clock or VM time APIs."""
    check(point['epoch'] == 0 and 0 <= point['nanoseconds'] <= final['scheduler']['now'] and point['ticks'] == point['counter'] == point['nanoseconds'] // 10, 'whole virtual100MHz clock')


def metadata(program, raw):
    """Bind original assembled guest sites and passive raw-ROM entry/OUT offsets."""
    check(program.count(b'PCIBMET1') == 1, 'one own COM metadata')
    at = program.index(b'PCIBMET1')
    check(struct.unpack_from('<HH', program, at + 8) == (1, 40), 'own metadata version/count')
    t = struct.unpack_from('<40H', program, at + 12)
    check(t[3] == len(program) + 256 and t[1] - t[0] == 512 and t[2] < t[0] < t[3], 'complete own COM/proof extent')
    check(program[t[4]-256:t[4]-254] == b'\xcd\x1a' and program[t[6]-256:t[6]-254] == b'\xff\x9d', 'actual INT1A and protected FAR call')
    check(program[t[5]-256:t[5]-254] == b'\x66\x9c' and program[t[7]-256] == 0x9c, 'actual16/32 PUSHFD return prefixes')
    for i in [8, 10, 12, 20]:
        check(program[t[i]-256:t[i]-254] == b'\xff\x9d', 'actual BIOS32/server FAR opcode')
    for i in [14, 15, 16, 17, 18, 19]:
        check(program[t[i]-256:t[i]-254] == b'\xcd\x67', 'actual original VCPI INT')
    check(program.count(b'\x66\xb9\x00\x10\x00\x00\x67\xf3\xab') == 1, 'actual fullECX4096 and address32 REP STOSW clears exactly8192 bytes')
    check(len(raw) == 65536 and raw.count(b'PCIMETA1') == 1, 'whole original own ROM manifest')
    at = raw.index(b'PCIMETA1')
    check(struct.unpack_from('<HH', raw, at+8) == (1,14), 'original ROM metadata version/count')
    r = struct.unpack_from('<14I', raw, at+12)
    check(all(0 <= n < 65536 for n in r) and raw[r[4]:r[5]] == b'\x66\xef' and raw[r[6]:r[7]] == b'\xef', 'actual prefixed16/unprefixed32 OUT instructions')
    return t, r


def directory_contract(proof, firmware, r):
    """Verify real aligned BIOS32 scan, checksum, physical entry and original service description."""
    check(u32(proof,40) == 0xf0000+r[0] and u32(proof,44) == 0xf0000+r[1], 'actual scanned physical directory/entry')
    header = firmware[r[0]:r[0]+16]
    check(proof[48:64] == header and header[:4] == b'_32_' and u32(header,4) == 0xf0000+r[1] and header[8:10] == b'\0\1' and sum(header)%256 == 0 and not any(header[11:]) and r[0]%16 == 0, 'every actual directory byte and checksum')
    check(struct.unpack_from('<III',firmware,r[13]) == (0xf0000,0x10000,r[3]) and u32(firmware,r[12]) == 0x39381800, 'original service and fixed four-BDF table')


def owned(ram, state, program, t, base, which):
    """Prove complete MCB/PSP/JFT/table ownership and all1024 unmodified original DE01 PTEs."""
    cpu = state['components']['cpu']['fields']
    check(len(ram) == RAM and 16 <= base < 0xa0000 and base&15 == 0 and ram[base:base+2] == b'\xcd\x20', 'actual own PSP')
    paragraphs = (t[3]+15)//16
    check(ram[base-16] in [77,90] and struct.unpack_from('<HH',ram,base-15) == (base>>4,paragraphs), 'complete own COM MCB')
    check(ram[base+256:base+t[2]] == program[:t[2]-256], 'all original executing code unchanged')
    proof = ram[base+t[0]:base+t[1]]
    check(proof[:8] == b'PCIBIO01' and proof[-8:] == b'PCIBEND1' and u16(proof,12) == 0 and u16(proof,8) in [0,1] and u32(proof,16) == base and u16(proof,164) == base>>4 and u16(proof,166) == paragraphs and u32(proof,200) == 0, 'complete actual proof/owner and no failure')
    check(u32(proof,28) == SEED and struct.unpack_from('<II',proof,168) == (0x524,0x135) and u32(proof,228) == 0xe0000000 and u16(proof,224) == 3, 'fixed CPU subset and original S3 resources')
    handle, count = u16(ram,base+t[37]), u16(ram,base+0x32)
    jft = dos.far(ram,base+0x34)
    check(handle == u16(proof,160) and 5 <= handle < count <= 255 and dos.linear(ram,cpu,jft+handle,1) != b'\xff', 'actual still-open original DOS file/JFT')
    for page in range(base&~4095,(base+paragraphs*16+4095)&~4095,4096):
        check(dos.translate(ram,cpu,page) == page, 'every own original conventional page mapping')
    if which == 'bios32':
        check(u16(proof,10) == 6 and u16(proof,14) == 199 and u32(proof,136) == 1 and u32(proof,140) == 0, 'actual entered own protected client')
        check(u32(ram,base+t[28]+2) == base+t[27] == cpu['Gdtr']['base'], 'actual private GDT')
        if u16(proof,8):
            pd = u32(ram,base+t[25])
            storage = base+t[29]+2048
            check(base+t[26]-storage == 8192+4095 and pd == (storage+4095)&~4095 and not any(ram[storage:pd]) and not any(ram[pd+8192:base+t[26]]), 'exact8192 owned table bytes, alignment remainder stayszero')
            check(pd == u32(proof,152) and pd&4095 == 0 and base <= pd and pd+8192 <= base+t[3] and cpu['Cr3'] == pd, 'entire private PD/PT owned')
            check(u32(ram,pd)&~0x60 == (pd+4096)|7 and not any(ram[pd+4:pd+4096]), 'only original first4MiB mapped')
            for i in range(1024):
                before,after = u32(ram,base+t[26]+i*4),u32(ram,pd+4096+i*4)
                check(before&~0x60 == after&~0x60 and after&before == before, 'all original DE01 PTEs retained except added A/D')
            check(cpu['Cr0']&0x80000001 == 0x80000001, 'original VCPI paging active')
        else:
            check(cpu['Cr0']&0x80000001 == 1, 'actual real-DOS own protected entry')
    else:
        check(u16(proof,10) == 2 and u16(proof,14) == 99 and u16(proof,22) == 0, 'actual first16-bit invocation before protected entry')
    return proof


def rom_page_contract(data, previous, expected, reset_alias):
    """Bind every ROM byte, permitting only an original monitor's four reset-target operand bytes."""
    check(len(data) == len(previous) == len(expected) == 4096, 'complete actual ROM page extents')
    check(data == previous, 'entire original physical monitor ROM page unchanged since first cut')
    if reset_alias:
        check(data[0xff0] == expected[0xff0] == 0xea, 'original reset-vector FAR jump opcode retained')
        check(data[:0xff1] == expected[:0xff1] and data[0xff5:] == expected[0xff5:], 'all4092 original reset-alias bytes match expected firmware')
    else:
        check(data == expected, 'every actual mapped ROM byte matches expected firmware')


def mapped_rom_contract(rams, states, firmware, expected, mode):
    """Verify actual supplied ROM aliases against both the earlier monitor and independently patched ROM."""
    if not mode:
        return [], []

    def pte(memory, state, address):
        """Read the actual present4KiB entry from one saved original or private address space."""
        cpu = state['components']['cpu']['fields']
        check(cpu['Cr0'] & 0x80000001 == 0x80000001, 'actual original/private paged ROM address space')
        directory = cpu['Cr3'] & 0xfffff000
        check(directory + 4096 <= len(memory), 'whole actual ROM page directory in captured RAM')
        entry = u32(memory, directory + (address >> 22) * 4)
        table = entry & 0xfffff000
        check(entry & 1 and not entry & 0x80 and table + 4096 <= len(memory), 'actual present original4KiB ROM page table')
        value = u32(memory, table + ((address >> 12) & 1023) * 4)
        check(value & 1, 'actual supplied ROM page remains present')
        return value

    def physical_page(memory, physical):
        """Read a real physical ROM page or a bounded ordinary-RAM mirror, never hidden MMIO backing."""
        if 0xf0000 <= physical < 0x100000:
            return firmware[physical - 0xf0000:physical - 0xf0000 + 4096]
        check(physical < 0xa0000 or 0x100000 <= physical < RAM, 'original monitor alias targets ordinary physical RAM')
        check(physical + 4096 <= len(memory), 'whole original monitor alias lies in captured RAM')
        return memory[physical:physical + 4096]

    aliases, pages = [], []
    for address in range(0xf0000, 0x100000, 4096):
        before, after = pte(rams[0], states[0], address), pte(rams[1], states[1], address)
        physical = after & 0xfffff000
        check(before & 0xfffff000 == physical, 'original monitor physical ROM target retained by supplied DE01 mapping')
        previous, data = physical_page(rams[0], physical), physical_page(rams[1], physical)
        wanted = expected[address - 0xf0000:address - 0xf0000 + 4096]
        reset_alias = address == 0xff000 and physical != address
        rom_page_contract(data, previous, wanted, reset_alias)
        pages.append((data, previous, wanted, reset_alias))
        if physical != address:
            item = dict(linear=address, physical=physical, originalPte=before, clientPte=after, originalPageSha256=sha(previous), retainedPageSha256=sha(data))
            if reset_alias:
                item['originalResetFarTarget'] = '%04X:%04X' % (u16(previous, 0xff3), u16(previous, 0xff1))
                item['originalResetOperandOffsets'] = [0xffff1, 0xffff2, 0xffff3, 0xffff4]
            aliases.append(item)
    return aliases, pages


def call_specs():
    """Specify the caller functions/arguments/errors independently from recorded results."""
    return {
        1:(0xb101,0,0,0,0,0xc0de1234,0),2:(0xb102,0,0x122d,0x8086,0,0,0),3:(0xb102,0,0x8811,0x5333,0,0,0),
        4:(0xb102,0,0x122e,0x8086,0,0,0),5:(0xb102,0,0x1230,0x8086,0,0,0),6:(0xb102,0,0x122d,0x8086,1,0,0x86),
        7:(0xb102,0,0x122d,0xffff,0,0,0x83),8:(0xb103,0,0x060000,0,0,0,0),9:(0xb103,0,0x030000,0,0,0,0),
        10:(0xb103,0,0x060100,0,0,0,0),11:(0xb103,0,0x010180,0,0,0,0),12:(0xb103,0,0xffffff,0,0,0,0x86),
        13:(0xb108,0,0xa5a5a5a5,0,0,0,0),14:(0xb109,0,0xa5a5a5a5,0,0,0,0),15:(0xb10a,0,0xa5a5a5a5,0,0,0,0),
        16:(0xb109,0,0xa5a5a5a5,0,0,1,0x87),17:(0xb10a,0,0xa5a5a5a5,0,0,2,0x87),18:(0xb108,0,0xa5a5a5a5,0,0,0x100,0x87),
        19:(0xb10a,0xf8,0,0,0,0,0),20:(0xb106,0,0,0,0,0,0x81),21:(0xb109,0x18,0,0,0,4,0),22:(0xb10c,0x18,2,0,0,4,0),
        23:(0xb10d,0x18,0xffffffff,0,0,0x10,0),24:(0xb10a,0x18,0,0,0,0x10,0),25:(0xb10d,0x18,0xe0000000,0,0,0x10,0),
        26:(0xb10c,0x18,3,0,0,4,0),27:(0xb10a,0x18,0,0,0,0x10,0),28:(0xb10e,0,0,0,0,None,0x89),
        29:(0xb10e,0,0,0,0,None,0),30:(0xb10f,0x18,0x0b0a,0,0,0,0),31:(0xb10e,0,0,0,0,None,0),
        32:(0xb10f,0x18,0x0b0b,0,0,0,0x88),33:(0xb10b,0x38,0x80,0,0,0x60,0),34:(0xb10c,0,0x2000,0,0,6,0)}


def routing_bytes():
    """Specify all48 own Host/S3/PIIX routing bytes independently."""
    data = bytearray(48)
    data[17],data[18],data[33] = 0x18,0x60,0x38
    struct.pack_into('<H',data,19,0x800)
    return bytes(data)


def pci_calls(rows, ports, program, t, base, mode, old_status, final):
    """Check every exact BIOS result, register/flag preservation and actual atomic CF8/CFC transaction."""
    specs,counts = call_specs(),{}
    for kind,delta in [('pci16',0),('pci32',100)]:
        actual = [r for r in rows if r['kind'] == kind]
        counts[kind] = len(actual)
        cases = list(range(1,35)) if not old_status&0x2000 else list(range(1,34))
        cases.append(99)
        check(sorted(r['before']['case'] for r in actual) == sorted(i+delta for i in cases), 'all independently named BIOS cases exactly once')
        for row in actual:
            b,a = row['before'],row['after'];n = b['case']-delta
            check(a is not None and b['bases'][1] == a['bases'][1] == base and b['case'] == a['case'], 'actual original same-client service return')
            check(row['offset'] == t[4 if kind=='pci16' else 6] and row['returnOffset'] == t[5 if kind=='pci16' else 7] and a['eip'] == row['returnOffset']+(2 if kind=='pci16' else 1), 'actual INT/FAR site and first returned opcode')
            check(b['v86'] == bool(mode) if kind=='pci16' else not b['v86'] and b['segments'][1] == 8 and b['segments'][2] == 16, 'actual original real/V86 or own protected context')
            spec = specs[n] if n!=99 else (0xb10a,0 if kind=='pci16' else 0x39,0,0,0,0,0)
            function,bx,cx,dx,si,di,error = spec
            check([b['registers'][i] for i in [0,3,1,2,6]] == [function,bx,cx,dx,si], 'actual original independent call inputs')
            pointer = (t[32] if kind=='pci16' else base+t[33]) if di is None else di
            check(b['registers'][7] == pointer,'actual DI/EDI argument')
            expected = list(b['registers']);expected[0] = expected[0]&0xffff00ff | error<<8
            if not error:
                if n==1:
                    expected[0] = expected[0]&0xffffff00 | 1;expected[3] = expected[3]&0xffff0000 | 0x210;expected[1] &= 0xffffff00;expected[2] = 0x20494350
                if n in [2,3,4,5,8,9,10,11]:expected[3] = BDFS[([2,3,4,5].index(n) if n<=5 else [8,9,10,11].index(n))]
                if n==13:expected[1] = expected[1]&0xffffff00 | 0x86
                if n==14:expected[1] = expected[1]&0xffff0000 | 0x8086
                if n in [15,99]:expected[1] = 0x12308086 if n==99 and kind=='pci32' else 0x122d8086
                if n==19:expected[1] = 0xffffffff
                if n==21:expected[1] = 3
                if n==24:expected[1] = 0xff800000
                if n==27:expected[1] = 0xe0000000
                if n in [29,31]:expected[3] = 0 if n==29 else 0x800
            check(a['registers'] == expected and a['segments'] == b['segments'] and a['bases'] == b['bases'] and a['cr0'] == b['cr0'] and a['cr3'] == b['cr3'] and a['flags'] == (b['flags']&~1 | int(bool(error))), 'all actual GPR/segment/address/flags preservation and exact CF/AH')
            inside = [p for p in ports if b['instructions'] <= p['at']['instructions'] < a['instructions'] and 0xcf8 <= p['address'] <= 0xcff]
            if 0xb108 <= function <= 0xb10d:
                if error:check(not inside,'bad register rejected before mechanism I/O')
                else:
                    width = [1,2,4][(function-0xb108)%3];target = 0x80000000 | bx<<8 | (di&0xfc)
                    check([(p['address'],p['width'],p['write']) for p in inside] == [(0xcf8,4,False),(0xcf8,4,True),(0xcfc+(di&3),width,function>=0xb10b),(0xcf8,4,True)], 'real complete configuration-cycle width/lane/order')
                    check(inside[0]['value'] == inside[-1]['value'] == SEED and inside[1]['value'] == target, 'actual original CF8 save/select/restore')
                    data = (cx if function>=0xb10b else expected[1]) & ((1<<(width*8))-1)
                    check(inside[2]['value'] == data,'actual original config-data value')
            if kind=='pci16' and n in [28,29,30,31,32]:
                check(b['segments'][3] == a['segments'][3] == 0xf000 and b['bases'][3] == a['bases'][3] == 0xf0000, 'actual normative16-bit routing caller uses required ROM DS')
            if n in [28,29,31]:
                check(row['size'] == 48 and row['data'] is not None,'actual required IRQ-table size')
                data = (bytes([0xa5])*48 if n==28 and kind=='pci16' else routing_bytes())+bytes([0xa5])*16
                check(dos.old.decode(row['data']) == data,'all actual routing bytes and untouched complete tail')
            clock(b,final);clock(a,final)
    return counts


def directory_calls(rows,t,r,base,final):
    """Require real BIOS32 FAR queries and exact documented library outputs/errors."""
    for kind,index,status in [('directory',8,0),('missing-directory',10,0x80),('reserved-directory',12,0x81)]:
        found = [row for row in rows if row['kind']==kind];check(len(found)==1,'one actual BIOS32 library query '+kind)
        row=found[0];b,a=row['before'],row['after']
        check(row['offset']==t[index] and a is not None and a['eip']==t[index+1]+1 and b['segments'][1]==8 and not b['v86'] and not a['v86'],'actual BIOS32 FAR query/return')
        expected=list(b['registers']);expected[0]=expected[0]&0xffffff00 | status
        if status==0:expected[3],expected[1],expected[2]=0xf0000,0x10000,r[3]
        check(a['registers']==expected and a['segments']==b['segments'] and a['bases']==b['bases'] and a['flags']==b['flags'],'all actual BIOS32 GPR/flags outputs')
        clock(b,final);clock(a,final)


def cut_frame(state,ram,point,origin,pending,proof,t,r,base,which):
    """Bind actual changed CF8 and original firmware's real saved registers/frame/control locals."""
    cpu=state['components']['cpu']['fields'];source=origin['at'];window=dos.old.decode(origin['stackBytes'])
    check(point['eip']==cpu['Eip'] and point['registers']==cpu['_registers'] and point['flags']==cpu['Eflags'] and point['nanoseconds']==state['scheduler']['now'],'entire actual critical CPU point')
    target=0x80000000 if which=='bios16' else 0x80003900
    check(field(state,'PciConfigurationBus')['_address']==target and source['registers'][0]==target and source['registers'][2]&65535==0xcf8,'actual selected mechanism latch and OUT operands')
    check(source['phase']==point['phase']==(2 if which=='bios16' else 6) and origin['kind']==('out16' if which=='bios16' else 'out32'),'actual original invocation')
    check(source['bases'][1]+source['eip']==0xf0000+r[5 if which=='bios16' else 7],'actual original prefix/opcode at pre-OUT point')
    check(dos.linear(ram,dict(Cr0=source['cr0'],Cr3=source['cr3']),origin['stackLinear'],len(window))==window and u32(window,0)==1 and u32(window,4)==SEED,'actual original used1/saved-CF8 frame locals')
    b=pending['before']
    if which=='bios16':
        check(len(window)==52 and u16(window,46)==t[4]+2 and u16(window,48)==base>>4 and u16(window,50)&~1==b['flags']&65535&~1,'real original INT return IP/CS/flags')
        offsets=[(0,40),(1,36),(2,32),(3,28),(6,16),(7,12)]
    else:
        check(len(window)==64 and u32(window,56)==t[7] and u32(window,60)==8 and u32(window,52)&~1==b['flags']&~1,'real original32-bit FAR return IP/CS/flags')
        offsets=[(0,44),(1,40),(2,36),(3,32),(6,20),(7,16)]
        check(not cpu['Eflags']&0x20200 and cpu['_segmentCaches'][1]['base']==0 and cpu['Eip']==0xf0000+r[7],'actual direct32-bit ROM next-CFC boundary and IF0')
    for register,offset in offsets:check(u32(window,offset)==b['registers'][register],'actual original saved complete GPR')
    check(any(e['owner']=='pit' and e['time']>state['scheduler']['now'] for e in state['scheduler']['events']),'actual future PIT during original firmware operation')


def carried_contract(ledger):
    """Require every original/restored in-flight BIOS service to return independently."""
    a,b=ledger['carried16'],ledger['carried32']
    check(len(a)==2 and a[0]==a[1] and a[0]['after'] is not None and a[0]['before']['case']==99,'actual original/restored pending16-bit return')
    check(len(b)==3 and b[0]==b[1]==b[2] and b[0]['after'] is not None and b[0]['before']['case']==199,'actual all-three pending32-bit return')

def vcpi_contract(rows,proof,t,base,mode):
    """Bind actual original DE00/DE01/DE06/DE0C calls and genuine original server returns."""
    actual=[row for row in rows if row['kind'].startswith('vcpi-')]
    if not mode:
        check(not actual,'real DOS invents no original resident monitor');return
    for name in ['vcpi-presence','vcpi-pd','vcpi-pt','vcpi-interface','vcpi-enter','vcpi-return']:
        found=[row for row in actual if row['kind']==name]
        check(len(found)==1 and found[0]['after'] is not None,'actual original VCPI call/return '+name)
    for row in actual:
        b,a=row['before'],row['after'];check(a is not None,'every actual original VCPI call returns')
        if row['kind']=='vcpi-enter':
            check(b['v86'] and a['segments'][1]==8 and a['bases'][1]==base and a['eip']==t[21]+2 and not a['v86'],'actual original protected entry after66/B8')
        elif row['kind']=='vcpi-return':
            check(not b['v86'] and b['registers'][0]&65535==0xde0c and a['v86'] and a['bases'][1]==base and a['eip']==t[22]+1,'actual original server FAR return to original V86')
        else:
            check(b['v86'] and a['v86'] and a['registers'][0]>>8&255==0 and a['bases'][1]==base,'actual original resident VCPI success')
            if row['kind']=='vcpi-interface':
                check(b['registers'][0]&65535==0xde01 and b['segments'][0]==base>>4 and a['registers'][3]==u32(proof,148),'actual supplied server descriptors/entry')
            if row['kind']=='vcpi-identity':
                check(b['registers'][0]&65535==0xde06 and a['registers'][2]==b['registers'][7],'actual original conventional page translation')


def text_contract(state,ram,reported,font):
    """Decode actual text, visible glyphs and DAC contrast without a render call or image."""
    v=state['components']['video']['fields'];planes=[dos.old.decode(x) for x in v['_planes']]
    crtc,seq,gc,attr=[dos.old.decode(v[k]) for k in ['_crtc','_seq','_gc','_attr']]
    check(v['IsTrio64'] and len(planes)==4 and all(len(p)==524288 for p in planes) and v['_trioAdvanced']==0 and v['_displayEnabled'] and not seq[1]&32 and not gc[6]&1,'actual S3 legacy text')
    check(crtc[1]+1==80 and (crtc[9]&31)+1==16 and ram[0x449]==3 and u16(ram,0x44a)==80 and ram[0x484]==24,'actual original80x25 mode')
    start,stride=crtc[12]<<8|crtc[13],crtc[0x13]*2;check(stride==80,'actual text stride')
    chars=bytes(planes[0][(start+i)&0x7ffff] for i in range(2000));attrs=bytes(planes[1][(start+i)&0x7ffff] for i in range(2000))
    lines=[''.join(chr(c) if 32<=c<127 else ' ' for c in chars[y*80:(y+1)*80]).rstrip() for y in range(25)]
    text='\n'.join(lines).rstrip();check(text=='\n'.join(reported.splitlines()).rstrip() and 'PCI-BIOS-DONE' in text and 'PCI-BIOS-FAILED' not in text,'actual original completion text')
    echoes=[i for i,line in enumerate(lines) if line=='PCIBCONTINUED'];prompt=max(i for i,line in enumerate(lines) if line)
    check(len(echoes)==1 and echoes[0]<prompt and lines[prompt]=='A:\\>' and all(not s for s in lines[echoes[0]+1:prompt]),'actual new ECHO and following original prompt')
    check(u16(ram,0x450)==prompt*256+4 and (crtc[14]<<8|crtc[15])==start+prompt*80+4,'completed BDA/CRTC cursor4')
    positions=list(range(echoes[0]*80,echoes[0]*80+13))+list(range(prompt*80,prompt*80+4))
    check(all(not attrs[p]&128 for p in positions),'proof text is not blink-hidden')
    dos.glyph_contract(seq,chars,attrs,planes[2],positions,font);dos.color_contract(seq,attr,dos.old.decode(v['_dac']),v['_pelMask'],attrs,positions,v['_rgb'])
    return sha(bytes(x for pair in zip(chars,attrs) for x in pair))


def font_reference(root,system):
    """Parse the actual private CPI or own artwork by named fixture, never by observed glyph matching."""
    if system=='freedos':
        spec=importlib.util.spec_from_file_location('pci_own_font',REPO/'tests/verify-cp437-frame.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        font=module.artwork(16);return font,dict(source='own-cp437-artwork',fontSha256=sha(font))
    spec=importlib.util.spec_from_file_location('pci_original_cpi',REPO/'tests/verify-font-frame.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    drive=dos.old.diskio.disk(root/'bios32-cut.dossav'/'media.dosemedia');cpi=dos.fat_path(drive,[b'DOS        ',b'EGA     CPI']);font=module.cpi_fonts(cpi,850)[16]
    return font,dict(source='original-private-EGA.CPI',codepage=850,cpiSha256=sha(cpi),fontSha256=sha(font))


def file_contract(actual,expected):
    """Use the same complete actual FAT-byte path for positive and corrupted negative files."""
    check(actual.keys()==expected.keys(),'exact own original DOS file set')
    for name,data in actual.items():check(data==expected[name],'actual complete original DOS file '+name.decode('ascii'))

def verify(root,program_path,iso_path,raw_path,expected_path,negative=True):
    """Certify actual ROM/device/CPU/media futures, then reject discarded evidence mutations."""
    root=dos.old.media.safe(root);program,iso,raw,expected=[dos.old.media.safe(p).read_bytes() for p in [program_path,iso_path,raw_path,expected_path]]
    t,r=metadata(program,raw);result,ledger=read(root/'result.json'),read(root/'ledger.json');role=read(root.parent/'input.json')
    check(result['passed'] and result['returnedToDos'] and result['postPciBiosEcho'] and result['exits']==[True]*3 and result['system']==role['system'] and dos.old.media.safe(role['output'])==root and result['system'] in ['msdos','freedos','emm386-noems','emm386-ram'],'actual complete three-PC result/input role')
    check(result['programSha256']==sha(program) and result['isoSha256']==sha(iso) and result['rawFirmwareSha256']==sha(raw),'actual immutable caller/ISO/raw ROM identity')
    actual=(root/'firmware.bin').read_bytes()
    check(len(expected)==len(actual)==65536 and actual==expected and result['firmwareSha256']==sha(actual),'all actual65536 ROM bytes match separately configured own firmware')
    check(actual[r[0]:r[11]]==raw[r[0]:r[11]],'entire original PCI BIOS directory/code/table matches raw assembly independently')
    cuts=[read(root/(n+'-cut.dossav')/'state.json') for n in ['bios16','bios32']];rams=[(root/(n+'-cut.dossav')/'ram.bin').read_bytes() for n in ['bios16','bios32']]
    final,ram=read(root/'final-state.json'),(root/'final-ram.bin').read_bytes();base=result['programBase'];proofs=[]
    check(ledger['programBase']==ledger['original']['programBase']==base,'actual same original client')
    schema=read(REPO/'docs/state-schema-v1.json')['nodes']
    for name,state,memory,carried in zip(['bios16','bios32'],cuts,rams,[ledger['carried16'][0],ledger['carried32'][0]]):
        saved=root/(name+'-cut.dossav');manifest=read(saved/'snapshot.json')
        check(manifest==result[name+'Manifest'] and manifest['kind']=='DOSE-PC-STATE-1' and manifest['version']==1 and manifest['ramBytes']==RAM and manifest['stateSha256']==result[name+'StateSha256']==dos.digest(saved/'state.json') and manifest['ramSha256']==result[name+'RamSha256']==dos.digest(saved/'ram.bin') and manifest['mediaSha256']==dos.digest(saved/'media.dosemedia/media.json') and manifest['virtualNanoseconds']==state['scheduler']['now'],'every actual build-bound state/RAM/media seal')
        definition=read(saved/'media.dosemedia/media.json')['machine'];p=definition['hardware']
        check(p['id']=='dose-430fx-piix-s3-atapi-pcibios-dev-v1' and p['cpu']=='p54c-b5-subset-development' and p['cpuHz']==100000000 and p['ramMiB']==32 and p['opticalDrive']=='sff8020i-rev25-pio-development' and p['atapiSlot']==2,'exact actual constructor-bound board tuple')
        for c in state['components'].values():check(c['version']==1 and c['type'] in schema and set(c['fields'])=={f['name'] for f in schema[c['type']]['fields'] if f['mode'] not in ['children','excluded']},'every registered raw state field once')
        proof=owned(memory,state,program,t,base,name);proofs.append(proof)
        check(u16(proof,8)==int(result['system'].startswith('emm386')),'actual original selected manager')
        point=read(root/(name+'-point.json'));origin=read(root/(name+'-origin.json'));cut_frame(state,memory,point,origin,carried,proof,t,r,base,name);clock(point,final);clock(origin['at'],final)
        check(dos.old.diskio.fat_file(dos.old.diskio.disk(saved/'media.dosemedia'),b'PCIBIOS DAT')==b'PCIBIO01','actual ordinary original open DOS file at both cuts')
    rom_aliases, rom_pages = mapped_rom_contract(rams,cuts,actual,expected,u16(proofs[0],8))
    check(result['finalStateSha256']==dos.digest(root/'final-state.json') and result['finalRamSha256']==dos.digest(root/'final-ram.bin') and len(ram)==RAM,'entire actual final graph/RAM identity')
    original=ledger['original'];counts=pci_calls(original['services'],original['ports'],program,t,base,u16(proofs[0],8),u16(proofs[0],218),final)
    directory_contract(proofs[1],actual,r);directory_calls(original['services'],t,r,base,final);vcpi_contract(original['services'],proofs[1],t,base,u16(proofs[0],8));carried_contract(ledger)
    for i,key in enumerate(KEYS):
        check(original[key][ledger['counts16'][i]:ledger['counts32'][i]]==ledger['bios16Future'][key][:ledger['secondCounts32'][i]],'every actual first continued effect '+key)
        check(original[key][ledger['counts32'][i]:]==ledger['bios16Future'][key][ledger['secondCounts32'][i]:]==ledger['bios32Future'][key],'every actual later continued effect '+key)
    copied=dos.old.iso_contract(iso)['text'];cd=[s for s in original['services'] if s['kind']=='cd-read']
    check(len(cd)==1 and cd[0]['after'] is not None and not cd[0]['after']['flags']&1 and cd[0]['after']['registers'][0]&65535==197 and dos.old.decode(cd[0]['data'])==copied,'actual small original MSCDEX R-drive read after firmware')
    last=bytearray(proofs[1]);struct.pack_into('<HH',last,10,10,0);struct.pack_into('<H',last,14,134 if not u16(last,218)&0x2000 else 133)
    struct.pack_into('<H',last,22,counts['pci32']);struct.pack_into('<I',last,140,1);struct.pack_into('<H',last,162,197)
    raw_pi=struct.pack('<QH',0xc90fdaa22168c235,0x4000)
    expected_files={b'PCIBIOS DAT':bytes(last),b'PCIBRAW DAT':raw_pi,b'PCIBCD  TXT':copied};all_files=[]
    for i in range(3):
        path=root/('final-'+str(i)+'.dosemedia');sealed=read(path/'media.json');cds=[m for m in sealed['media'] if m['device']=='cdrom']
        check(len(cds)==1 and cds[0]['slot']==2 and cds[0]['sectors']==128 and cds[0]['sectorSize']==2048 and cds[0]['baseSha256']==sha(iso) and cds[0]['journalBytes']==0,'actual immutable own ISO unchanged')
        disk=dos.old.diskio.disk(path);files={name:dos.old.diskio.fat_file(disk,name) for name in expected_files};file_contract(files,expected_files);all_files.append(files)
    check(all_files[0]==all_files[1]==all_files[2],'all original/restored actual DOS file futures')
    check(field(final,'PciConfigurationBus')['_address']==u32(proofs[0],24) and dos.old.decode(field(final,'S3Trio64')['_config'])[4]&3==3 and dos.old.decode(field(final,'Intel82371FbIrq')['_config'])[0x60]==0x80,'actual original CF8/S3/PIIX controls restored')
    font,font_info=font_reference(root,result['system']);text_hash=text_contract(final,ram,result['finalScreen'],font)
    check('PCIBCONTINUED' not in result['beforeEcho'] and any(p['address']==0x60 and not p['write'] and p['value']==0x1c for p in original['ports'][result['echoAt']['ports']:]) and any(a['irq']==1 and not a['spurious'] for a in original['acks'][result['echoAt']['acks']:]),'actual new original ECHO Return/KBC/IRQ1')
    check(result['audioFuture']==result['restoredAudioFuture'][0]==result['restoredAudioFuture'][1],'actual published future PCM equality, no new active-audio claim')
    negatives=0
    if negative:
        for data, previous, wanted, reset_alias in rom_pages:
            if not reset_alias:
                continue
            for mutation in ['current-byte', 'earlier-byte', 'current-reset-target', 'reset-opcode', 'nonoperand-byte']:
                changed, earlier = bytearray(data), bytearray(previous)
                if mutation == 'current-byte': changed[0] ^= 1
                elif mutation == 'earlier-byte': earlier[0] ^= 1
                elif mutation == 'current-reset-target': changed[0xff1] ^= 1
                elif mutation == 'reset-opcode': changed[0xff0] ^= 1; earlier[0xff0] ^= 1
                else: changed[0xfff] ^= 1; earlier[0xfff] ^= 1
                try: rom_page_contract(bytes(changed),bytes(earlier),wanted,reset_alias)
                except AssertionError: negatives += 1
                else: raise AssertionError('Corrupted actual original monitor ROM alias accepted')
        for name in expected_files:
            for offset in (range(512) if name==b'PCIBIOS DAT' else [0,len(expected_files[name])//2,len(expected_files[name])-1]):
                bad=dict(all_files[0]);data=bytearray(bad[name]);data[offset]^=1;bad[name]=bytes(data)
                try:file_contract(bad,expected_files)
                except AssertionError:negatives+=1
                else:raise AssertionError('Corrupted real original DOS proof/copy file accepted')
        for mutation in ['missing-return','wrong-register']:
            bad=dict(ledger);bad['carried32']=[ledger['carried32'][0],ledger['carried32'][1],copy.deepcopy(ledger['carried32'][2])]
            if mutation=='missing-return':bad['carried32'][2]['after']=None
            else:bad['carried32'][2]['after']['registers'][1]^=1
            try:carried_contract(bad)
            except AssertionError:negatives+=1
            else:raise AssertionError('Missing actual restored BIOS32 return accepted')
        for ordinal in [0,1]:
            name=['bios16','bios32'][ordinal];point=read(root/(name+'-point.json'));origin=read(root/(name+'-origin.json'))
            for changed in [0,4]:
                bad=copy.deepcopy(origin);data=bytearray(dos.old.decode(bad['stackBytes']));data[changed]^=1;bad['stackBytes']=base64.b64encode(data).decode('ascii')
                try:cut_frame(cuts[ordinal],rams[ordinal],point,bad,[ledger['carried16'][0],ledger['carried32'][0]][ordinal],proofs[ordinal],t,r,base,name)
                except AssertionError:negatives+=1
                else:raise AssertionError('Corrupted actual used/saved-CF8 frame accepted')
    return dict(passed=True,system=result['system'],restoredContinuations=2,comparisons=result['comparisons'],fullComparisons=result['comparisons'],negativeOracles=negatives,
                biosCalls16=counts['pci16'],biosCalls32=counts['pci32'],bios32Queries=3,cdBytes=197,fileBytes=719,font=font_info,textCellsSha256=text_hash,vcpiRomAliases=rom_aliases,
                fileSha256={k.decode('ascii'):sha(v) for k,v in all_files[0].items()},rawFileHex=raw_pi.hex().upper(),proofFileSha256=sha(bytes(last)),
                limitations='Own fixed-four-function PCI BIOS2.1/BIOS32 subset; original DOS/VCPI retained, no generic PCI hotplug/Windows/OptionROM/native input claim.')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory',type=Path)
    parser.add_argument('--program',required=True,type=Path);parser.add_argument('--iso',required=True,type=Path)
    parser.add_argument('--firmware',required=True,type=Path);parser.add_argument('--expected-firmware',required=True,type=Path)
    args=parser.parse_args();print(json.dumps(verify(args.directory,args.program,args.iso,args.firmware,args.expected_firmware),indent=2))
