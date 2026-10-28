# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent original-MSCDEX/DOS filesystem, resident SYS, cooked PIO and whole-PC continuation proof."""
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
SPEC = importlib.util.spec_from_file_location('mscdex_own_iso_and_media', REPO / 'scripts/verify-atapi-test.py')
old = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(old)
KEYS = ['ports', 'words', 'acks', 'services', 'criticals']
RAM = 32 * 1048576


def check(value, reason):
    """Reject a missing genuine filesystem, device, ownership or continuation effect."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named own observation with personal-overlay exclusion."""
    return json.loads(old.media.safe(path).read_text(encoding='utf-8-sig'))


def sha(data):
    """Hash one explicitly selected byte extent."""
    return hashlib.sha256(data).hexdigest().upper()


def digest(path):
    """Hash only an explicitly named protected evidence file."""
    with old.media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def u16(data, offset):
    """Read an actual little-endian DOS word independently."""
    return struct.unpack_from('<H', data, offset)[0]


def u32(data, offset):
    """Read an actual little-endian DOS dword independently."""
    return struct.unpack_from('<I', data, offset)[0]


def far(data, offset):
    """Resolve an actual DOS far pointer without masking its offset."""
    return u16(data, offset) + (u16(data, offset + 2) << 4)


def metadata(program):
    """Bind all own critical sites to the immutable COM and actual INT opcodes."""
    check(program.count(b'MSCMETA1') == 1, 'unique own COM metadata')
    at = program.index(b'MSCMETA1')
    check(struct.unpack_from('<HH', program, at + 8) == (1, 68), 'own COM metadata version/count')
    t = struct.unpack_from('<68H', program, at + 12)
    check(t[3] == len(program) + 256 and t[1] - t[0] == 128 and t[2] <= t[0] < t[3] and t[8] - t[7] == 16384, 'complete own COM/proof/buffer extents')
    for index in list(range(14, 44, 2)) + [44, 46, 48, 50]:
        vector = 0x2f if index in [14, 16, 40, 46, 48] else 0x21
        check(program[t[index] - 256:t[index] - 254] == bytes([0xcd, vector]), 'actual unchanged DOS/MSCDEX INT site')
    check(program[t[52] - 256] == 0x90 and program[t[53] - 256] == 0xcf and program[t[66] - 256] == 0xa5, 'actual own critical-entry/IRET and different rejected write byte')
    for index in [54, 56, 58, 60, 62, 64]:
        check(program[t[index] - 256:t[index] - 254] == b'\xcd\x21', 'actual original protection/vector DOS call site')
    return t


def translate(ram, cpu, address):
    """Walk the actual original classic page tables without setting any A/D bit."""
    physical = address
    if cpu['Cr0'] & 0x80000000:
        pd = (cpu['Cr3'] & 0xfffff000) + (address >> 22) * 4
        check(pd <= len(ram) - 4, 'actual original PDE within RAM')
        pde = u32(ram, pd)
        check(pde & 1 and not pde & 0x80, 'actual original present classic PDE')
        pt = (pde & 0xfffff000) + ((address >> 12) & 1023) * 4
        check(pt <= len(ram) - 4, 'actual original PTE within RAM')
        pte = u32(ram, pt)
        check(pte & 1, 'actual original present PTE')
        physical = (pte & 0xfffff000) | (address & 4095)
    check(0 <= physical < len(ram), 'actual mapped byte within installed RAM')
    return physical


def linear(ram, cpu, address, count):
    """Read actual original guest bytes through the saved original page tables."""
    return bytes(ram[translate(ram, cpu, address + i)] for i in range(count))


def proof_contract(proof, final=False):
    """Require concrete original DOS/MSCDEX function results and the complete own proof guards."""
    check(len(proof) == 128 and proof[:8] == b'MSCDP001' and proof[-8:] == b'MSCDEND1', 'own complete proof signature and tail')
    check(u16(proof, 8) in [0, 1] and u16(proof, 10) == (10 if final else 8) and u16(proof, 12) == 0, 'original mode/phase and no guest failure')
    for offset, expected in [(14, 1), (16, 17), (20, 0xadad), (26, 8205), (28, 197), (30, 8205), (32, 0),
                             (40, 2039), (42, 0), (44, 31), (46, 0), (56, 8205), (58, 1), (62, 18)]:
        check(u16(proof, offset) == expected, 'actual original DOS/MSCDEX result word ' + str(offset))
    check(u16(proof, 18) != 0 and not proof[34] & 0x10 and 5 <= u16(proof, 22) < 255 and 5 <= u16(proof, 24) < 255, 'actual installed R drive, ordinary file and handles')
    check(u16(proof, 104) >> 8 == 2, 'actual original MSCDEX major version from genuine150C')
    if final:
        for offset, expected in [(36, 2), (48, 0x100), (60, 1), (66, 1), (84, 1), (86, 0), (90, 0)]:
            check(u16(proof, offset) == expected, 'actual final missing/write-protection/request/completion result')
        check(u16(proof, 64) in [0, 1] and u16(proof, 68) in [5, 19] and u16(proof, 88) == (0x40 if u16(proof, 112) else 0x3c), 'actual rejected write/create and original DOS failure code')
        check((u16(proof, 64) == 0 and u16(proof, 112) == 1 and u16(proof, 38) == u16(proof, 72) >= 5 and u16(proof, 70) == 1) or
              (u16(proof, 64) == 1 and u16(proof, 112) == 0 and u16(proof, 38) == 5 and u16(proof, 72) == 0 and u16(proof, 70) == 0), 'actual original writable-OPEN alternatives, never an assumed denied OPEN')
    return proof


def ownership(ram, state, program, driver, t, base, final=False):
    """Bind complete own MCB/pages/guards and the genuine MSCDEX-returned resident SYS header and DOS arena."""
    cpu = state['components']['cpu']['fields']
    check(len(ram) == RAM and base & 15 == 0 and 16 <= base < 0xa0000 and ram[base:base + 2] == b'\xcd\x20', 'actual original client PSP')
    paragraphs = (t[3] + 15) // 16
    check(ram[base - 16] in [77, 90] and struct.unpack_from('<HH', ram, base - 15) == (base >> 4, paragraphs), 'complete own allocated COM MCB owner/size')
    check(ram[base + 256:base + t[2]] == program[:t[2] - 256], 'all original own executable bytes unchanged')
    check(ram[base + t[12] + 10:base + t[13] - 2048] == program[t[12] - 246:t[13] - 2304], 'all own static paths/text/rejected write byte unchanged')
    proof = proof_contract(ram[base + t[0]:base + t[1]], final)
    check(u16(proof, 52) == paragraphs and u16(proof, 54) == base >> 4, 'actual guest-observed COM allocation/PSP')
    check(ram[base + t[6]:base + t[7]] == ram[base + t[8]:base + t[8] + 16] == bytes([0xd6]) * 16, 'both complete private data guards')
    for p in range(base & ~4095, (base + paragraphs * 16 + 4095) & ~4095, 4096):
        check(translate(ram, cpu, p) == p and linear(ram, cpu, p, 4096) == ram[p:p + 4096], 'every actual original conventional client page')
    check(driver[10:18] == b'DOSECD01' and u16(driver, 4) == 0xc800 and driver[24:32] == b'DOSECDM1' and
          struct.unpack_from('<HH', driver, 32) == (1, 22), 'own original resident SYS header/model/metadata')
    d = struct.unpack_from('<22H', driver, 36)
    header = far(proof, 96)
    check(0 < header <= 0xa0000 - d[2] and ram[header + 4:header + 18] == driver[4:18] and
          ram[header + 24:header + 80] == driver[24:80], 'actual original MSCDEX1501 resident header, entry offsets and metadata')
    check(far(ram, base + t[45] + 1) == header and ram[base + t[45]] == 0, 'actual original1501 unit and far header result')
    code_begin = min(d[0], d[1])
    check(80 < code_begin < d[2] <= len(driver) and ram[header + code_begin:header + d[2]] == driver[code_begin:d[2]], 'every unchanged resident SYS instruction byte')
    lists = far(proof, 106)
    check(0 < lists < 0xa0000 and u16(ram, lists - 2) == u16(proof, 110), 'original AH52 returned arena anchor')
    arena, seen, containing = u16(proof, 110), set(), []
    while arena not in seen:
        seen.add(arena)
        address = arena << 4
        check(address <= 0xa0000 - 16 and ram[address] in [77, 90], 'finite actual original DOS MCB chain')
        owner, count = struct.unpack_from('<HH', ram, address + 1)
        end = address + 16 + count * 16
        check(end <= 0xa0000, 'bounded actual original DOS arena extent')
        if address + 16 <= header and header + d[2] <= end:
            containing.append((address, owner, count))
            break
        if ram[address] == 90:
            break
        arena += count + 1
    check(len(containing) == 1 and containing[0][1] != 0, 'one live original DOS allocation owns entire resident SYS')
    if not final:
        handle, count = u16(proof, 22), u16(ram, base + 0x32)
        jft = far(ram, base + 0x34)
        check(handle < count <= 255 and jft <= 0xa0000 - count and linear(ram, cpu, jft + handle, 1) != b'\xff', 'actual still-open original DOS CD handle and JFT slot')
        request = linear(ram, cpu, base + t[4], 27)
        check(request[:3] == b'\x1b\x00\x80' and not u16(request, 3) & 0x8000 and request[13] == 0 and
              far(request, 14) == base + t[11] and u16(request, 18) == 0 and u32(request, 20) == 22 and request[24:] == b'\0\0\0',
              'actual open original MSCDEX1510 cooked/HSG read-long request')
        check(far(ram, header + d[4]) == base + t[4] and ram[header + d[5]] == 1 and u32(ram, header + d[9]) == 22 and
              u32(ram, header + d[10]) == 22 and u16(ram, header + d[12]) == 1 and ram[header + d[19]] == 0x80 and
              u16(ram, header + d[20]) == 1 and u16(ram, header + d[21]) == 0 and u16(ram, header + d[15]) == 0,
              'actual busy resident SYS request/lba/cursor, not just a debug marker')
    ss = cpu['_segmentCaches'][2]
    sp = cpu['_registers'][4] & (0xffffffff if ss['flags'] & 4 else 65535)
    maximum = 0xffffffff if ss['flags'] & 4 else 65535
    expand_down = not ss['access'] & 8 and bool(ss['access'] & 4)
    bounded = sp > ss['limit'] and sp + 31 <= maximum if expand_down else sp + 31 <= ss['limit']
    check(ss['usable'] and bounded and len(linear(ram, cpu, ss['base'] + sp, 32)) == 32, 'actual currently mapped original DOS/SYS/monitor stack without replacement')
    return dict(proof=proof, mode=u16(proof, 8), driverBase=header, driverTable=d, driverArena=containing[0], psp=base >> 4, comParagraphs=paragraphs)


def critical_cut(state, point, which, iso):
    """Bind exact pending CDB10 and actual128-byte data consumption to a real complete optical phase."""
    c = old.field(state, 'AtapiCdRom')
    cpu = state['components']['cpu']['fields']
    check(point['v86'] == bool(cpu['Eflags'] & 0x20000), 'actual captured CPU mode')
    check(point['eip'] == cpu['Eip'] and point['flags'] == cpu['Eflags'] and point['cr0'] == cpu['Cr0'] and point['cr3'] == cpu['Cr3'] and
          point['registers'] == cpu['_registers'] and point['segments'] == cpu['_segments'] and point['bases'] == [v['base'] for v in cpu['_segmentCaches']], 'actual complete captured CPU point')
    check(point['phase'] == 8 and point['nanoseconds'] == state['scheduler']['now'] and c['_selected'] and not c['_hardwareResetHeld'] and not c['_srstActive'], 'actual original SYS request selected at exact capture boundary')
    expected = bytes([0x28, 0, 0, 0, 0, 22, 0, 0, 1, 0, 0, 0])
    packet = old.decode(c['_packet'])
    check(c['_byteLimit'] >= 128 and old.decode(c['_task'])[6] & 0x10 == 0, 'actual original selected cooked PIO target')
    if which == 'packet':
        check(c['_phase'] == 2 and c['_packetOffset'] == 10 and packet[:10] == expected[:10] and not any(packet[10:]) and
              c['_payloadLength'] == c['_payloadOffset'] == c['_offset'] == c['_remainingBlocks'] == c['_readLba'] == 0 and not any(old.decode(c['_data'])), 'actual incomplete CDB without invented preloaded CD bytes')
    else:
        check(c['_phase'] == 5 and c['_packetOffset'] == 12 and packet == expected and c['_offset'] == 128 and
              c['_phaseLength'] >= 128 and c['_payloadOffset'] == 0 and c['_payloadLength'] == 2048 and c['_readLba'] == 22 and c['_remainingBlocks'] == 1 and
              old.decode(c['_data']) == iso[22 * 2048:23 * 2048], 'actual precise original SYS DataIn128 and whole cooked block')
    check(any(e['owner'] == 'pit' and e['time'] > state['scheduler']['now'] for e in state['scheduler']['events']), 'actual future PIT event at original resident-driver cut')


def services(rows, t, base, mode, program, iso, files):
    """Require original DOS/MSCDEX calls and actual same-client returns with independent function results."""
    for row in rows:
        before, after = row['before'], row['after']
        check(program[row['offset'] - 256:row['offset'] - 254] == bytes([0xcd, row['vector']]) and before['eip'] == row['offset'] + 1,
              'every actual original call site opcode and observed fetch point')
        check(before['bases'][1] == before['bases'][2] == base and before['segments'][1] == before['segments'][2] == base >> 4 and before['v86'] == bool(mode), 'actual original real/V86 client entry')
        if row['offset'] == t[44]:
            check(before['registers'][0] & 65535 == 0x4c00 and after is None, 'actual own successful DOS exit without invented return')
            continue
        check(after is not None and after['eip'] == row['offset'] + 3 and after['bases'][1] == after['bases'][2] == base and
              after['segments'][1] == after['segments'][2] == base >> 4 and after['v86'] == bool(mode), 'actual original service next-instruction return')
        check(after['cr0'] == before['cr0'] and after['cr3'] == before['cr3'], 'actual selected original manager returns to same client address space')
    indexed = {}
    for row in rows:
        indexed.setdefault(row['offset'], []).append(row)
    for index, ax, vector in [(14, 0x1500, 0x2f), (16, 0x150b, 0x2f), (46, 0x1501, 0x2f), (48, 0x150c, 0x2f)]:
        found = indexed.get(t[index], [])
        check(len(found) == 1 and found[0]['vector'] == vector and found[0]['before']['registers'][0] & 65535 == ax, 'genuine original installation/header/version multiplex service')
    check(indexed[t[14]][0]['after']['registers'][3] & 65535 == 1 and indexed[t[14]][0]['after']['registers'][1] & 65535 == 17, 'one actual original MSCDEX drive R')
    check(indexed[t[16]][0]['before']['registers'][1] & 65535 == 17 and indexed[t[16]][0]['after']['registers'][3] & 65535 == 0xadad and
          indexed[t[16]][0]['after']['registers'][0] & 65535 != 0, 'original R drive installation magic')
    for index, count, pointer in [(24, 256, t[9]), (28, 8205, t[7]), (32, 31, t[10]), (36, 1, None)]:
        found = indexed.get(t[index], [])
        check(len(found) == 1 and found[0]['before']['registers'][0] >> 8 & 255 == 0x3f and found[0]['before']['registers'][1] & 65535 == count and
              (pointer is None or found[0]['before']['registers'][2] & 65535 == pointer) and not found[0]['after']['flags'] & 1 and
              found[0]['after']['registers'][0] & 65535 == ({24:197,28:8205,32:31,36:0}[index]), 'actual complete original DOS file read/EOF return')
        expected = {24: files['text'], 28: files['payload'], 32: files['payload'][2039:2070], 36: b''}[index]
        check(old.decode(found[0]['data']) == expected and found[0]['before']['bases'][3] == base and found[0]['before']['segments'][3] == base >> 4, 'every actual original DOS returned file/seek byte in its real DS:DX buffer')
    raw = indexed.get(t[40], [])
    check(len(raw) == 2 and [r['before']['phase'] for r in raw] == [5, 8], 'both actual original MSCDEX raw dispatches')
    for row in raw:
        p = row['before']
        check(p['registers'][0] & 65535 == 0x1510 and p['registers'][1] & 65535 == 17 and p['registers'][3] & 65535 == t[4] and
              p['segments'][0] == base >> 4 and p['bases'][0] == base and p['registers'][4] & 65535 == t[13] - 2,
              'actual original dispatcher ES:BX request and complete private CALL stack')
        lba = 16 if p['phase'] == 5 else 22
        check(old.decode(row['data']) == iso[lba * 2048:(lba + 1) * 2048], 'whole actual original driver-request returned cooked block')
    return indexed[t[48]][0]['after']['registers'][3] & 65535


def protection(rows, criticals, proof, t, base, original_vector, final_ram, mode, system):
    """Bind real original writable-OPEN semantics, rejected write/create, temporary INT24 and exact vector restoration."""
    index = {}
    for row in rows:
        index.setdefault(row['offset'], []).append(row)
    get, set_vector, restore, opened = [index[t[i]] for i in [62, 64, 60, 54]]
    check(all(len(row) == 1 for row in [get, set_vector, restore, opened]), 'one real original negative-operation/vector sequence')
    get, set_vector, restore, opened = [row[0] for row in [get, set_vector, restore, opened]]
    check(get['before']['registers'][0] & 65535 == 0x3524 and (get['after']['registers'][3] & 65535) == u16(original_vector, 0) and
          get['after']['segments'][0] == u16(original_vector, 2), 'actual original INT24 queried through DOS')
    check(set_vector['before']['registers'][0] & 65535 == 0x2524 and set_vector['before']['registers'][2] & 65535 == t[52] and
          set_vector['before']['segments'][3] == base >> 4, 'actual temporary own critical-error callback installed through DOS')
    check(restore['before']['registers'][0] & 65535 == 0x2524 and restore['before']['registers'][2] & 65535 == u16(original_vector, 0) and
          restore['before']['segments'][3] == u16(original_vector, 2) and final_ram[0x90:0x94] == original_vector,
          'exact actual original INT24 vector restored before DOS exit')
    check(opened['before']['registers'][0] & 65535 == 0x3d02 and opened['before']['bases'][3] == base, 'actual original writable OPEN requested')
    accepted = not opened['after']['flags'] & 1
    check(accepted or opened['after']['registers'][0] & 65535 == 5, 'actual original writable OPEN permitted or access-denied')
    selected = index.get(t[56] if accepted else t[58], [])
    check(len(selected) == 1 and t[58 if accepted else 56] not in index, 'one actual attempted write or create, not just OPEN')
    call = selected[0]
    b, a = call['before'], call['after']
    check(b['registers'][0] >> 8 & 255 == (0x40 if accepted else 0x3c) and b['bases'][3] == base and a['flags'] & 1,
          'actual ordinary DOS write/create returns failure')
    code = a['registers'][0] & 65535
    check(system in ['msdos', 'freedos', 'emm386-noems', 'emm386-ram'] and code == (19 if system == 'freedos' else 5), 'precise actual selected DOS write-protection error: FreeDOS19, original MS-DOS5')
    if accepted:
        check(b['registers'][3] & 65535 == opened['after']['registers'][0] & 65535 and b['registers'][1] & 65535 == 1 and
              b['registers'][2] & 65535 == t[66], 'actual one-byte write uses real returned RW handle and differing A5 byte')
    else:
        check(b['registers'][1] & 65535 == 0 and b['registers'][2] & 65535 == t[67], 'actual failed creation names own R drive file')
    for row in criticals:
        entry, commit, after = row['entry'], row['commit'], row['after']
        frame = old.decode(row['frame'])
        check(len(frame) == 6 and entry['phase'] == commit['phase'] == 9 and entry['bases'][1] == commit['bases'][1] == base and
              entry['eip'] == t[52] + 1 and commit['eip'] == t[53] + 1 and entry['v86'] == commit['v86'] == bool(mode), 'actual own original critical-error entry and IRET site')
        expected = list(entry['registers']); expected[0] = expected[0] & 0xffffff00 | 3
        check(commit['registers'] == expected and commit['segments'] == entry['segments'] and commit['bases'] == entry['bases'], 'actual AL3 FAIL with all other original handler registers intact')
        expected[4] = expected[4] & 0xffff0000 | ((expected[4] + 6) & 65535)
        check(after is not None and after['eip'] == u16(frame, 0) and after['segments'][1] == u16(frame, 2) and after['flags'] & 65535 == u16(frame, 4) and
              after['registers'] == expected and after['v86'] == bool(mode), 'actual original DOS critical-error frame return and balanced IRET stack')
        check(b['instructions'] < entry['instructions'] < after['instructions'] < a['instructions'], 'actual callback occurs within the real rejected original operation')
    values = {38: opened['after']['registers'][0] & 65535, 64: 0 if accepted else 1, 66: 1, 68: code, 70: 1 if accepted else 0,
              72: opened['after']['registers'][0] & 65535 if accepted else 0, 74: len(criticals), 76: u16(original_vector, 0),
              78: u16(original_vector, 2), 80: t[52], 82: base >> 4, 84: 1, 86: 0, 88: 0x40 if accepted else 0x3c, 90: 0, 112: int(accepted)}
    return values

def carried_contract(ledger, t, iso):
    """Require each independently observed original/restored pending1510 return and full cooked result."""
    carried = ledger['carriedService']
    check(carried == ledger['restoredCarriedServices'][0] == ledger['restoredCarriedServices'][1] == ledger['packetFuture']['carriedService'] == ledger['dataFuture']['carriedService'] and
          carried in ledger['original']['services'] and carried['offset'] == t[40] and carried['before']['phase'] == 8 and carried['after'] is not None and
          old.decode(carried['data']) == iso[22 * 2048:23 * 2048], 'actual original/restored pending1510 CPU return and complete2048 bytes on all three PCs')

def packets(words, iso):
    """Reconstruct every actual twelve-byte request and cooked READ payload from real PIO words."""
    groups, current = [], None
    for row in words:
        if row['write']:
            if current is None or current['data'] or len(current['cdb']) == 12:
                current = dict(cdb=bytearray(), data=bytearray(), phase=row['phase'])
                groups.append(current)
            current['cdb'].extend(struct.pack('<H', row['value']))
        else:
            check(current is not None and len(current['cdb']) == 12, 'actual complete CDB precedes every data word')
            current['data'].extend(struct.pack('<H', row['value']))
    total, blocks, by_phase = 0, 0, {}
    for g in groups:
        cdb, data = bytes(g['cdb']), bytes(g['data'])
        check(len(cdb) == 12 and cdb[0] in [0, 3, 0x12, 0x25, 0x28, 0x2b, 0x43, 0x5a, 0xa8], 'actual supported original read-only CDB')
        if cdb[0] in [0x28, 0xa8]:
            lba = int.from_bytes(cdb[2:6], 'big')
            n = int.from_bytes(cdb[7:9] if cdb[0] == 0x28 else cdb[6:10], 'big')
            check(n > 0 and lba + n <= 128 and data == iso[lba * 2048:(lba + n) * 2048], 'every actual cooked READ block and every payload byte')
            total += len(data)
            blocks += n
            by_phase.setdefault(g['phase'], []).append((lba, n))
        elif cdb[0] == 0x25:
            check(data == struct.pack('>II', 127, 2048), 'actual READ CAPACITY geometry')
        elif cdb[0] == 3:
            check(len(data) <= 18 and (not data or data[0] == 0x70), 'actual bounded original fixed sense')
        elif cdb[0] in [0, 0x2b]:
            check(not data, 'actual no-data command has no fake payload')
    check(by_phase.get(4) and by_phase.get(5) == [(16, 1)] and by_phase.get(8) == [(22, 1)], 'real filesystem READ plus both exact original SYS raw reads')
    return total, blocks, len(groups)


def glyph_contract(seq, chars, attrs, plane, positions, font):
    """Bind each actual selected SR3 font bank and all16 rows to independently parsed font-source bytes."""
    a = ((seq[3] >> 2) & 3) | ((seq[3] >> 3) & 4)
    b = (seq[3] & 3) | ((seq[3] >> 2) & 4)
    two_fonts = bool(seq[4] & 2) and a != b
    locations = []
    for position in positions:
        bank = a if two_fonts and attrs[position] & 8 else b
        address = (bank & 3) * 0x4000 + (bank >> 2) * 0x2000 + chars[position] * 32
        check(plane[address:address + 16] == font[chars[position] * 16:chars[position] * 16 + 16], 'exact actual active-font glyph rows, not merely nonzero bytes')
        locations.append(address)
    return locations


def color_contract(seq, attr, dac, mask, attributes, positions, rgb=None):
    """Require actual DAC RGB contrast through original attribute, plane, color-select and PEL masks."""
    a = ((seq[3] >> 2) & 3) | ((seq[3] >> 3) & 4)
    b = (seq[3] & 3) | ((seq[3] >> 2) & 4)
    two_fonts = bool(seq[4] & 2) and a != b
    check(len(dac) == 768 and all(c <= 63 for c in dac), 'actual complete six-bit VGA DAC')
    # Resolve only the actual character colors; different palette indices alone do not prove visible contrast.
    def index(color):
        """Resolve one original VGA attribute color into its actual DAC entry."""
        value = attr[color & attr[0x12] & 15] & 63
        if attr[0x10] & 128:
            value = value & 15 | (attr[0x14] & 3) << 4
        return (value | (attr[0x14] & 12) << 4) & mask
    for p in positions:
        value = attributes[p]
        foreground = index(value & (7 if two_fonts else 15))
        background = index((value >> 4) & (7 if attr[0x10] & 8 else 15))
        check(dac[foreground * 3:foreground * 3 + 3] != dac[background * 3:background * 3 + 3], 'actual foreground/background DAC RGB contrast')
        if rgb is not None:
            for entry in [foreground, background]:
                red, green, blue = [c * 4 + c // 16 for c in dac[entry * 3:entry * 3 + 3]]
                check(len(rgb) == 256 and rgb[entry] == 0xff000000 + red * 65536 + green * 256 + blue, 'actual RGB upload cache equals independently expanded DAC value')

def font_reference(root, system):
    """Select a font source by the actual named boot fixture, never by matching observed VRAM glyphs."""
    if system == 'freedos':
        spec = importlib.util.spec_from_file_location('mscdex_independent_own_font', REPO / 'tests/verify-cp437-frame.py')
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        font = module.artwork(16)
        return font, dict(source='own-cp437-artwork', fontSha256=sha(font))
    spec = importlib.util.spec_from_file_location('mscdex_independent_original_cpi', REPO / 'tests/verify-font-frame.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    drive = old.diskio.disk(root / 'data-cut.dossav' / 'media.dosemedia')
    cpi = fat_path(drive, [b'DOS        ', b'EGA     CPI'])
    font = module.cpi_fonts(cpi, 850)[16]
    return font, dict(source='original-private-EGA.CPI', codepage=850, cpiSha256=sha(cpi), fontSha256=sha(font))

def text_contract(state, ram, reported, font):
    """Decode genuine S3 text planes, visible glyphs and the completed new original DOS prompt independently."""
    v = state['components']['video']['fields']
    planes = [old.decode(x) for x in v['_planes']]
    crtc, seq, gc, attr = [old.decode(v[k]) for k in ['_crtc', '_seq', '_gc', '_attr']]
    check(v['IsTrio64'] and len(planes) == 4 and all(len(p) == 524288 for p in planes) and v['_trioAdvanced'] == 0 and
          v['_displayEnabled'] and not seq[1] & 32 and not gc[6] & 1, 'actual visible original S3 legacy text')
    check(crtc[1] + 1 == 80 and (crtc[9] & 31) + 1 == 16 and ram[0x449] == 3 and u16(ram, 0x44a) == 80 and ram[0x484] == 24, 'actual80x25 original DOS BDA and CRTC')
    start, stride = crtc[12] << 8 | crtc[13], crtc[0x13] * 2
    check(stride == 80, 'actual original text stride')
    chars = bytes(planes[0][(start + y * stride + x) & 0x7ffff] for y in range(25) for x in range(80))
    attrs = bytes(planes[1][(start + y * stride + x) & 0x7ffff] for y in range(25) for x in range(80))
    lines = [''.join(chr(x) if 32 <= x < 127 else ' ' for x in chars[y * 80:(y + 1) * 80]).rstrip() for y in range(25)]
    text = '\n'.join(lines).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip() and 'MSCDEX-DONE' in text and 'MSCDEX-FAILED' not in text, 'actual original guest completion text')
    echo = [i for i, line in enumerate(lines) if line == 'MSCDEXCONTINUED']
    prompt = max(i for i, line in enumerate(lines) if line)
    check(len(echo) == 1 and echo[0] < prompt and lines[prompt] == 'A:\\>' and all(not x for x in lines[echo[0] + 1:prompt]), 'new original ECHO output and following prompt')
    check((crtc[14] << 8 | crtc[15]) == start + prompt * 80 + 4 and u16(ram, 0x450) == prompt * 256 + 4, 'completed actual four-character BDA/CRTC prompt cursor')
    positions = list(range(echo[0] * 80, echo[0] * 80 + 15)) + list(range(prompt * 80, prompt * 80 + 4))
    for i in positions:
        a = attrs[i]
        check(not a & 128 and attr[(a & 15) & attr[0x12]] != attr[((a >> 4) & 7) & attr[0x12]], 'actual visible original text attribute')
    glyph_contract(seq, chars, attrs, planes[2], positions, font)
    color_contract(seq, attr, old.decode(v['_dac']), v['_pelMask'], attrs, positions, v['_rgb'])
    return sha(bytes(value for pair in zip(chars, attrs) for value in pair))


def file_contract(actual, expected):
    """Use the same complete real FAT-file comparison for positive and corrupted negative evidence."""
    check(set(actual) == set(expected), 'exact own final FAT file set')
    for name, data in actual.items():
        check(data == expected[name], 'complete independent actual DOS file ' + name.decode('ascii'))


def fat_path(drive, names):
    """Walk an explicitly named original FAT12/16 subdirectory and complete original file without a VM helper."""
    boot, start = drive.sector(0), 0
    if u16(boot, 11) != 512:
        starts = [u32(boot, 446 + i * 16 + 8) for i in range(4) if boot[450 + i * 16]]
        check(starts, 'actual DOS partition for original binary')
        start = starts[0]
        boot = drive.sector(start)
    bps, spc, reserved, fats, roots = struct.unpack_from('<HBHBH', boot, 11)
    total, fat_sectors = u16(boot, 19) or u32(boot, 32), u16(boot, 22)
    root_sectors = (roots * 32 + 511) // 512
    data_start = reserved + fats * fat_sectors + root_sectors
    clusters = (total - data_start) // spc
    check(bps == 512 and spc > 0 and 0 < clusters < 65525, 'actual original FAT geometry')
    fat = b''.join(drive.sector(start + reserved + i) for i in range(fat_sectors))
    directory = b''.join(drive.sector(start + reserved + fats * fat_sectors + i) for i in range(root_sectors))
    for ordinal, name in enumerate(names):
        entries = [directory[i:i + 32] for i in range(0, len(directory), 32) if directory[i] not in [0, 0xe5] and directory[i:i + 11] == name]
        check(len(entries) == 1, 'one actual original named FAT entry')
        entry = entries[0]
        is_directory = ordinal + 1 < len(names)
        check(bool(entry[11] & 0x10) == is_directory, 'actual original file/subdirectory type')
        cluster, size = struct.unpack_from('<HI', entry, 26)
        chain, seen = bytearray(), set()
        while cluster < (0xff8 if clusters < 4085 else 0xfff8):
            check(2 <= cluster < clusters + 2 and cluster not in seen, 'finite actual original binary FAT chain')
            seen.add(cluster)
            chain.extend(b''.join(drive.sector(start + data_start + (cluster - 2) * spc + i) for i in range(spc)))
            if not is_directory and len(chain) >= size:
                break
            if clusters < 4085:
                value = u16(fat, cluster + cluster // 2)
                cluster = value >> 4 if cluster & 1 else value & 0xfff
            else:
                cluster = u16(fat, cluster * 2)
        check(is_directory or len(chain) >= size, 'complete actual original binary extent')
        directory = bytes(chain) if is_directory else bytes(chain[:size])
    return directory

def verify(root, program_path, iso_path, driver_path, mscdex_path, negative=True):
    """Certify the entire original filesystem/device continuation before running discarded evidence mutations."""
    root = old.media.safe(root)
    program, iso, driver, mscdex = [old.media.safe(p).read_bytes() for p in [program_path, iso_path, driver_path, mscdex_path]]
    files, t = old.iso_contract(iso), metadata(program)
    result, ledger = read(root / 'result.json'), read(root / 'ledger.json')
    role = read(root.parent / 'input.json')
    check(result['system'] == role['system'] and result['system'] in ['msdos', 'freedos', 'emm386-noems', 'emm386-ram'] and
          old.media.safe(role['output']) == root, 'actual explicit matrix input role and evidence output binding')
    check(result['passed'] and result['returnedToDos'] and result['postMscdexEcho'] and result['exits'] == [True] * 3 and
          result['comparisons'] > 2 and result['futureSteps'] > 1000 and result['programSha256'] == sha(program) and result['isoSha256'] == sha(iso) and
          result['driverSha256'] == sha(driver) and result['mscdexSha256'] == sha(mscdex), 'actual complete three-PC/DOS/binary identity result')
    check('CDREADY' in result['bootScreen'] and 'MSCDEXCONTINUED' not in result['beforeEcho'] and result['echoBytes'] > 0, 'real initialized boot and fresh original command')
    cuts = [read(root / (name + '-cut.dossav') / 'state.json') for name in ['packet', 'data']]
    rams = [(root / (name + '-cut.dossav') / 'ram.bin').read_bytes() for name in ['packet', 'data']]
    final, ram = read(root / 'final-state.json'), (root / 'final-ram.bin').read_bytes()
    base = result['programBase']
    check(ledger['programBase'] == ledger['original']['programBase'] == base, 'actual unchanged client identity')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    owned = []
    for name, state, memory in zip(['packet', 'data'], cuts, rams):
        saved = root / (name + '-cut.dossav')
        manifest = read(saved / 'snapshot.json')
        check(manifest == result[name + 'Manifest'] and manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['ramBytes'] == RAM and
              manifest['stateSha256'] == result[name + 'StateSha256'] == digest(saved / 'state.json') and
              manifest['ramSha256'] == result[name + 'RamSha256'] == digest(saved / 'ram.bin') and manifest['mediaSha256'] == digest(saved / 'media.dosemedia/media.json') and
              manifest['virtualNanoseconds'] == state['scheduler']['now'], 'entire real snapshot, RAM and media seals')
        definition = read(saved / 'media.dosemedia/media.json')['machine']
        p = definition['hardware']
        check(p['id'] == 'dose-430fx-piix-s3-atapi-dev-v1' and p['cpuHz'] == 100000000 and p['ramMiB'] == 32 and
              p['opticalDrive'] == 'sff8020i-rev25-pio-development' and p['atapiSlot'] == 2, 'strict actual named CPU/board/optical profile')
        for c in state['components'].values():
            check(c['version'] == 1 and c['type'] in schema and set(c['fields']) == {f['name'] for f in schema[c['type']]['fields'] if f['mode'] not in ['children', 'excluded']}, 'all registered raw state fields exactly once')
        cpu = state['components']['cpu']['fields']
        check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == 100000000 and cpu['_tscEpochTicks'] == 0 and cpu['Cr4'] & ~12 == 0, 'actual constructor-fixed CPU/source/subset')
        o = ownership(memory, state, program, driver, t, base)
        check(o['mode'] == int(result['system'].startswith('emm386')), 'actual original selected real/V86 manager')
        critical_cut(state, read(root / (name + '-point.json')), name, iso)
        old.clock(read(root / (name + '-point.json')), final)
        owned.append(o)
    check(result['finalStateSha256'] == digest(root / 'final-state.json') and result['finalRamSha256'] == digest(root / 'final-ram.bin') and len(ram) == RAM, 'entire real final graph and RAM identity')
    original = ledger['original']
    version = services(original['services'], t, base, owned[0]['mode'], program, iso, files)
    for row in original['services']:
        old.clock(row['before'], final)
        if row['after'] is not None:
            old.clock(row['after'], final)
    cd_bytes, cd_blocks, packet_count = packets(original['words'], iso)
    carried_contract(ledger, t, iso)

    for i, key in enumerate(KEYS):
        check(original[key][ledger['packetCounts'][i]:ledger['dataCounts'][i]] == ledger['packetFuture'][key][:ledger['packetDataCounts'][i]], 'every original first future ' + key)
        check(original[key][ledger['dataCounts'][i]:] == ledger['packetFuture'][key][ledger['packetDataCounts'][i]:] == ledger['dataFuture'][key], 'every original later future ' + key)
    final_proof = bytearray(owned[0]['proof'])
    check(rams[0][0x90:0x94] == rams[1][0x90:0x94], 'original INT24 unchanged before both real captures')
    protected = protection(original['services'], original['criticals'], owned[0]['proof'], t, base, rams[0][0x90:0x94], ram, owned[0]['mode'], result['system'])
    for offset, value in list({10: 10, 36: 2, 48: 0x100, 60: 1, **protected}.items()):
        struct.pack_into('<H', final_proof, offset, value)
    final_proof = bytes(final_proof)
    proof_contract(final_proof, True)
    pi = struct.pack('<QH', 0xc90fdaa22168c235, 0x4000)
    expected = {b'MSCDEX  BIN': files['payload'], b'MSCDEX  TXT': files['text'], b'MSCDPRF BIN': final_proof, b'MSCDRAW BIN': pi}
    final_files = []
    for i in range(3):
        path = root / ('final-' + str(i) + '.dosemedia')
        sealed = read(path / 'media.json')
        cds = [m for m in sealed['media'] if m['device'] == 'cdrom']
        check(len(cds) == 1 and cds[0]['slot'] == 2 and cds[0]['sectors'] == 128 and cds[0]['sectorSize'] == 2048 and cds[0]['baseSha256'] == sha(iso) and
              cds[0]['journalBytes'] == 0 and cds[0]['journalName'] is None, 'actual immutable cooked ISO anchor, never a CD write overlay')
        disk = old.diskio.disk(path)
        actual = {name: old.diskio.fat_file(disk, name) for name in expected}
        file_contract(actual, expected)
        check(old.diskio.fat_file(disk, b'DOSECD  SYS') == driver and fat_path(disk, [b'MSCDEX  EXE'] if result['system'] == 'freedos' else [b'DOS        ', b'MSCDEX  EXE']) == mscdex, 'actual boot-installed own SYS and original MSCDEX files')
        final_files.append(actual)
    check(final_files[0] == final_files[1] == final_files[2], 'all actual original/restored filesystem futures equal')
    font, font_info = font_reference(root, result['system'])
    frame_hash = text_contract(final, ram, result['finalScreen'], font)
    echo_ports = original['ports'][result['echoAt']['ports']:]
    echo_acks = original['acks'][result['echoAt']['acks']:]
    check(any(p['address'] == 0x60 and not p['write'] and p['value'] == 0x1c for p in echo_ports) and any(a['irq'] == 1 and not a['spurious'] for a in echo_acks), 'actual new ECHO Return byte and physical keyboard IRQ')
    check(result['audioFuture'] == result['restoredAudioFuture'][0] == result['restoredAudioFuture'][1], 'actual whole future PCM publications equal without a new sound claim')
    negatives = 0
    if negative:
        wrong_system = 'msdos' if result['system'] == 'freedos' else 'freedos'
        try:
            protection(original['services'], original['criticals'], owned[0]['proof'], t, base, rams[0][0x90:0x94], ram, owned[0]['mode'], wrong_system)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('Wrong original DOS system role accepted its other write-protection error')
        for field, replacement in [('after', None), ('data', base64.b64encode(bytes(2048)).decode('ascii'))]:
            bad = dict(ledger)
            bad['restoredCarriedServices'] = [copy.deepcopy(ledger['restoredCarriedServices'][0]), ledger['restoredCarriedServices'][1]]
            bad['restoredCarriedServices'][0][field] = replacement
            try:
                carried_contract(bad, t, iso)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('Missing or corrupted actual pending1510 restored return was accepted')
        # Execute the same real positive comparison path for every intentionally corrupted complete DOS file.
        for name in expected:
            for at in (range(128) if name == b'MSCDPRF BIN' else sorted(set([0, len(expected[name]) // 2, len(expected[name]) - 1]))):
                bad = dict(final_files[0]); content = bytearray(bad[name]); content[at] ^= 1; bad[name] = bytes(content)
                try:
                    file_contract(bad, expected)
                except AssertionError:
                    negatives += 1
                else:
                    raise AssertionError('Corrupted real DOS file was accepted')
        for offset in list(range(8)) + [10, 12, 14, 16, 20, 26, 28, 30, 32, 40, 42, 44, 46, 56, 58, 60, 62, 105, 120, 127]:
            bad = bytearray(final_proof); bad[offset] ^= 1
            try:
                proof_contract(bad, True)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('Corrupted actual function proof was accepted: ' + str(offset))
        for which, state in zip(['packet', 'data'], cuts):
            key = next(k for k, value in state['components'].items() if value['type'].endswith('AtapiCdRom'))
            for field, value in [('_packetOffset', 9), ('_phase', 0), ('_selected', False)]:
                bad = copy.deepcopy(state); bad['components'][key]['fields'][field] = value
                try:
                    critical_cut(bad, read(root / (which + '-point.json')), which, iso)
                except AssertionError:
                    negatives += 1
                else:
                    raise AssertionError('Corrupted actual resident driver cut was accepted')
        v = final['components']['video']['fields']
        planes = [old.decode(x) for x in v['_planes']]
        seq, crtc = old.decode(v['_seq']), old.decode(v['_crtc'])
        start = crtc[12] << 8 | crtc[13]
        chars = bytes(planes[0][(start + i) & 0x7ffff] for i in range(2000))
        attrs = bytes(planes[1][(start + i) & 0x7ffff] for i in range(2000))
        positions = [i for i in range(2000) if chars[i] in set(b'MSCDEXCONTINUEDA:' + bytes([92, 62]))]
        attr, dac = old.decode(v['_attr']), old.decode(v['_dac'])
        for bad_dac, bad_mask in [(dac, 0), (bytes(768), v['_pelMask'])]:
            try:
                color_contract(seq, attr, bad_dac, bad_mask, attrs, positions)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('Invisible actual DAC/PEL contrast was accepted')
        locations = glyph_contract(seq, chars, attrs, planes[2], positions, font)
        changed = bytearray(planes[2]); changed[locations[0] + 1] ^= 1
        try:
            glyph_contract(seq, chars, attrs, changed, positions, font)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('Single wrong actual font row was accepted')
        for bank in range(8):
            changed_seq = bytearray(seq)
            changed_seq[3] = (bank & 3) | ((bank & 4) << 2) | ((bank & 3) << 2) | ((bank & 4) << 3)
            try:
                glyph_contract(changed_seq, chars, attrs, planes[2], positions, font)
            except AssertionError:
                negatives += 1
                break
        else:
            raise AssertionError('No differently populated active-bank negative could be established')
    return dict(passed=True, system=result['system'], font=font_info, writeProtection=dict(writableOpenAccepted=bool(protected[112]), operation=protected[88], error=protected[68], criticalCallbacks=protected[74], originalVectorRestored=True), restoredContinuations=2, negativeOracles=negatives, fullComparisons=result['comparisons'],
                cdBytes=cd_bytes, cdBlocks=cd_blocks, packets=packet_count, mscdexVersion=dict(major=version >> 8, minor=version & 255),
                fileBytes=sum(map(len, expected.values())), fileSha256={k.decode('ascii'): sha(v) for k, v in final_files[0].items()},
                rawFileHex=pi.hex().upper(), proofFileSha256=sha(final_proof), textCellsSha256=frame_hash,
                limits='Own data-only original MSCDEX/SYS path; original manager preserved; JFT and arena ownership, no complete SFT/Windows/native-input claim.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--program', required=True, type=Path)
    parser.add_argument('--iso', required=True, type=Path)
    parser.add_argument('--driver', required=True, type=Path)
    parser.add_argument('--mscdex', required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.program, args.iso, args.driver, args.mscdex), indent=2))
