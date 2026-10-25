"""Independent code/RAM/fault/restart oracle for the controlled 486 LOCK/AC guest."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


SYMBOLS = ('start code_end proof gdt tss kernel_sp user_sp ud_reg after_ud_reg ud_cmp after_ud_cmp '
           'ud_mov after_ud_mov ud_fpu after_ud_fpu ac_load after_load ac_xadd after_xadd ac_rep '
           'after_rep ac_sgdt after_sgdt ac_stack after_stack ac_page after_page unaligned rep_source '
           'rep_dest good bad storage end cmp_equal cmp_unequal').split()


def verify(report_path, memory_path, program_path, os_name, paging=False):
    report = json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory = Path(memory_path).read_bytes()
    program = Path(program_path).read_bytes()
    output, cpu = report['status']['debugOutput'], report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount'] == report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    assert report['ataSectorsRead'] > 0 and report['floppySectorsRead'] > 0
    assert 'CPU-LOCK-PASS\r\n' in output and 'CPU-LOCK-FAIL' not in output
    assert ('MS-DOS Version 6.22' if os_name == 'msdos' else 'FreeCom version 0.86') in output
    assert re.search(r'\r\nLOCK-GUEST-END\r\n(?:\r\n)?C:\\>$', output)
    markers = re.findall(r'CPU-LOCK-PROOF=([0-9A-F]{8})\r\n', output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address+1024]
    assert proof[:8] == b'CPLA0001'
    u32 = lambda off: struct.unpack_from('<I', proof, off)[0]
    u16 = lambda off: struct.unpack_from('<H', proof, off)[0]
    raw = lambda off: struct.unpack_from('<I', memory, off)[0]
    word = lambda off: struct.unpack_from('<H', memory, off)[0]
    labels = dict(zip(SYMBOLS, struct.unpack_from('<36I', program, program.index(b'CPLAADDR')+8)))
    basis = address-labels['proof']
    at = lambda name: basis+labels[name]
    full = lambda name: 0x10000+labels[name]
    assert labels['start'] == 0x100 and labels['end'] == len(program)+0x100
    assert basis == u32(80) and basis & 15 == 0 and program.index(b'CPLA0001')+0x100 == labels['proof']
    assert memory[basis+0x100:basis+labels['code_end']] == program[:labels['code_end']-0x100]
    assert (u32(8), u32(12), u32(16), u32(20)) == (1, 1, 15, 0)
    for first, last in ((24, 28), (32, 36), (40, 44), (48, 52), (56, 60), (64, 68), (72, 76)):
        assert u32(first) == u32(last), (first, last)
    assert u32(24) & 0x80000001 == 0 and u32(96) == int(paging) and u32(120) == 0
    assert u32(84) == at('tss') and u16(124) == 72 and u16(126) == 0
    active_cr0 = u32(24) | 0x40009 | (0x80010000 if paging else 0)
    assert u32(192) == active_cr0
    assert [u32(n) for n in (100, 104, 108)] == [5+int(paging), 4, int(paging)]
    assert [u32(n) for n in range(128, 192, 4)] == [1, 1, 3, 3, 0x40046, 9, 9, 0x40093, 9, 9, 12, 0, 13, 3, 0x40002, 0x19]
    assert (u32(616), u32(624), u32(632)) == (8, 0, 0)
    assert tuple(u32(off) & 1 for off in (620, 628, 636)) == (0, 1, 0)
    assert [u32(n) for n in range(196, 228, 4)] == [0x12345678, 0x12345678, 0x12345681, 0,
        labels['rep_source']+8, labels['rep_dest']+9, full('user_sp')-7, full('user_sp')]
    assert (u32(228), u32(232)) == (0xa5a5a5a5, 0xa5a5)  # no partial SGDT write on the reference preflight path
    assert u32(244) == 0 and u32(248) == 67 and u32(252) == full('user_sp')

    # Check the actual forbidden opcode bytes, not just a success message.
    forbidden = (('ud_reg', 'after_ud_reg', b'\xf0\x01\xc8'),
                 ('ud_cmp', 'after_ud_cmp', b'\xf0\x39\x0d'+struct.pack('<I', labels['unaligned']+1)),
                 ('ud_mov', 'after_ud_mov', b'\xf0\x89\x0d'+struct.pack('<I', labels['unaligned']+1)),
                 ('ud_fpu', 'after_ud_fpu', b'\xf0\xdb\xe3'))
    for i, (name, after, opcode) in enumerate(forbidden):
        offset = labels[name]-0x100
        assert program[offset:offset+len(opcode)] == opcode and labels[after] == labels[name]+len(opcode)
        ip, cs, flags, ds, ax, cx = struct.unpack_from('<6I', proof, 256+i*24)
        assert (ip, cs, ds, ax, cx) == (full(name), 51, (56, 56, 0, 56)[i], (0x11223344, 0x11223344, 0, 56)[i], 0x55667788)
        assert flags == (0x50002 if i < 2 else 0x50046)
    for name in ('cmp_equal', 'cmp_unequal'):
        offset = labels[name]-0x100
        assert program[offset:offset+4] == b'\xf0\x0f\xb1\x1d'
        target = struct.unpack_from('<I', program, offset+4)[0]
        assert raw(basis+target) == 13
    assert memory[at('unaligned')] == 0xa5 and raw(at('unaligned')+1) == 0x12345681 and memory[at('unaligned')+5] == 0x5a
    assert memory[at('rep_dest')] == 0xa5 and memory[at('rep_dest')+9] == 0x5a
    assert memory[at('rep_dest')+1:at('rep_dest')+9] == struct.pack('<II', 0x11112222, 0x33334444)
    assert memory[at('rep_source'):at('rep_source')+8] == struct.pack('<II', 0x11112222, 0x33334444)
    pseudo = struct.pack('<HI', 79, at('gdt'))
    assert at('good') % 4 == 2 and at('bad') % 4 == 0
    assert memory[at('good'):at('good')+6] == memory[at('bad'):at('bad')+6] == pseudo
    assert memory[at('good')+6:at('good')+8] == memory[at('bad')+6:at('bad')+8] == b'\xa5\xa5'

    points = ['ac_load', 'ac_xadd', 'ac_rep', 'ac_sgdt', 'ac_stack'] + (['ac_page'] if paging else [])
    expected_ax = [0x2468ace0, 0x12345678, 0x12345681, 0x12345681, 0x13579bdf, 0x13579bdf]
    expected_cx = [0x55667788, 9, 2, 0, 0, 0]
    for i, name in enumerate(points):
        error, ip, cs, flags, sp, ss, ax, cx = struct.unpack_from('<8I', proof, 384+i*32)
        assert error & 65535 == 0 and (ip, cs, ss) == (full(name), 51, 67)
        assert flags & 0x50000 == 0x50000 and flags & 0x20000 == 0
        assert sp == full('user_sp')-(7 if name == 'ac_stack' else 0)
        assert ax == expected_ax[i] and cx == expected_cx[i]
    for name, size, guard in (('kernel_sp', 512, 1), ('user_sp', 256, 2)):
        assert raw(at(name)-size-4) == 0xc0de0000+guard and raw(at(name)) == 0xcafe0000+guard
    low, high = struct.unpack_from('<II', memory, at('gdt')+72)
    tss_base = (low >> 16) | ((high & 255) << 16) | (high & 0xff000000)
    assert tss_base == at('tss') and low & 65535 == 0x67 and (high >> 8) & 255 == 0x8b
    assert raw(at('tss')+4) == full('kernel_sp') and word(at('tss')+8) == 40
    assert memory[at('tss')+12:at('tss')+104] == bytes(92)
    assert not cpu['protectedMode'] and not cpu['pagingEnabled'] and not cpu['virtual8086'] and not cpu['tripleFaulted']
    assert not cpu['alignmentCheckEnabled'] and cpu['cr0'] == u32(24) and cpu['cr3'] == u32(32)
    assert cpu['tr'] == 72 and cpu['trCache'] == dict(base=at('tss'), limit=0x67, access=0x8b, flags=0, usable=True)
    assert cpu['ldtr'] == 0 and not cpu['ldtrCache']['usable']
    assert cpu['gdtr'] == dict(base=u32(40), limit=u32(48)) and cpu['idtr'] == dict(base=u32(56), limit=u32(64))
    assert cpu['invalidLockPrefixes'] == 4 and cpu['alignmentFaults'] == 5+int(paging)
    assert cpu['lockedMemoryInstructions'] >= 10  # DOS also performs implicitly locked XCHGs
    assert cpu['x87Instructions'] == cpu['hardwareTaskSwitches'] == cpu['callGateTransfers'] == 0
    assert cpu['privilegeStackSwitches'] == cpu['outerPrivilegeReturns'] == 10+2*int(paging)
    assert cpu['pageFaults'] == int(paging)
    if paging:
        pd, pt, page, page_offset = u32(88), u32(92), u32(112), u32(116)
        assert pd % 4096 == 0 and pt == pd+4096 and page == pd+8192 and basis+page_offset == page
        assert at('storage') <= pd and page+4096 <= at('end')
        assert u32(236) == raw(page+1) == 0x89abcdef and u32(240) == cpu['cr2'] == page+1
        error, ip, cs, flags, sp, ss, cr2, pte = struct.unpack_from('<8I', proof, 576)
        assert (error & 65535, ip, cs, sp, ss, cr2, pte) == (4, full('ac_page'), 51, full('user_sp'), 67, page+1, page | 7)
        assert flags & 0x50000 == 0x10000  # same instruction: AC cleared, then real PF, then retry
        assert raw(pd) & ~0x20 == pt | 7 and memory[pd+4:pd+4096] == bytes(4092)
        assert all(raw(pt+i*4) & ~0x60 == i*4096 | 7 for i in range(1024))
        assert raw(pt+(page >> 12)*4) & 0x20 and cpu['tlbInvalidations'] >= 5
    else:
        assert u32(88) == u32(92) == u32(112) == u32(116) == u32(236) == u32(240) == 0
        assert memory[address+544:address+608] == bytes(64) and cpu['cr2'] == cpu['pageWalks'] == 0
    return dict(passed=True, os=os_name, identityPaging=paging, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), executableSha256=hashlib.sha256(program).hexdigest().upper(),
                ownLockedMemoryUpdates=10, actualUdFaults=4, actualAcFaults=5+int(paging), actualPageFaults=int(paging),
                innerStackSwitches=10+2*int(paging), outerReturns=10+2*int(paging),
                cmpxchgEqualAndUnequal=True, xaddAndImplicitXchg=True, retryKeepsInstructionAndRegisters=True,
                wordThenDword486PseudoDescriptor=True, referenceAcBeforePfSequence=paging,
                picTablesCr0Cr3Restored=True, privateTrRemainsUntilReset=True, cleanDosReturn=True,
                all486BusOrFaultPrecisionAccepted=False)


def negative_controls(paths, directory):
    rp, mp, pp, os_name, paging = paths
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    report = json.loads(Path(rp).read_text(encoding='utf-8-sig'))
    memory, program = Path(mp).read_bytes(), Path(pp).read_bytes()
    address = int(re.search(r'CPU-LOCK-PROOF=([0-9A-F]{8})', report['status']['debugOutput'])[1], 16)
    labels = dict(zip(SYMBOLS, struct.unpack_from('<36I', program, program.index(b'CPLAADDR')+8)))
    basis = address-labels['proof']
    rejected = []

    def reject(name, args):
        try:
            verify(*args)
        except (AssertionError, ValueError, KeyError, IndexError, struct.error):
            rejected.append(name)
        else:
            raise AssertionError('Corrupt LOCK/AC acceptance was accepted: '+name)

    for name, offset in (('success', 12), ('stage-bits', 16), ('ac-count', 100), ('ud-count', 104), ('xadd-source', 132),
                         ('cmp-equal-flags', 144), ('cmp-mismatch-accumulator', 152), ('implicit-xchg', 164),
                         ('restart-load', 196), ('restart-xadd', 204), ('rep-count', 208), ('stack-restart-esp', 220),
                         ('pseudo-partial-write', 228), ('ud-eip', 256), ('ud-null-selector', 316),
                         ('ac-error', 384), ('ac-restart-eip', 388), ('ac-user-cpl', 392), ('ac-saved-flag', 398),
                         ('ac-old-stack', 400), ('ac-rep-counter', 476), ('bit-set-result', 616),
                         ('bit-toggle-result', 624), ('bit-test-carry', 628)):
        changed = bytearray(memory)
        changed[address+offset] ^= 1
        path = directory/(name+'.ram')
        path.write_bytes(changed)
        reject(name, (rp, path, pp, os_name, paging))
    for name, field in (('actual-ud-counter', 'invalidLockPrefixes'), ('actual-ac-counter', 'alignmentFaults'),
                        ('actual-stack-counter', 'privilegeStackSwitches')):
        changed = copy.deepcopy(report)
        changed['cpu'][field] = 0
        path = directory/(name+'.json')
        path.write_text(json.dumps(changed), encoding='utf-8')
        reject(name, (path, mp, pp, os_name, paging))
    for name, physical in (('user-stack-guard', basis+labels['user_sp']), ('rep-bytes', basis+labels['rep_dest']+1),
                           ('busy-tss', basis+labels['gdt']+77)):
        changed = bytearray(memory)
        changed[physical] ^= 1
        path = directory/(name+'.ram')
        path.write_bytes(changed)
        reject(name, (rp, path, pp, os_name, paging))
    changed = bytearray(program)
    changed[0] ^= 1
    path = directory/'changed-code.com'
    path.write_bytes(changed)
    reject('executed-bytes', (rp, mp, path, os_name, paging))
    changed = copy.deepcopy(report)
    changed['status']['debugOutput'] = changed['status']['debugOutput'].replace('LOCK-GUEST-END', 'FAKE-END')
    path = directory/'return.json'
    path.write_text(json.dumps(changed), encoding='utf-8')
    reject('dos-return', (path, mp, pp, os_name, paging))
    if paging:
        pt = struct.unpack_from('<I', memory, address+92)[0]
        for name, physical in (('pf-code', address+576), ('pf-restart-eip', address+580), ('cr2', address+600),
                               ('repaired-pte', address+604), ('pt-frame', pt+18), ('page-table-rights', pt+4)):
            changed = bytearray(memory)
            changed[physical] ^= 4
            path = directory/(name+'.ram')
            path.write_bytes(changed)
            reject(name, (rp, path, pp, os_name, paging))
    return dict(passed=True, rejected=len(rejected), cases=rejected)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    for key in ('report', 'memory', 'program'):
        parser.add_argument('--'+key, type=Path, required=True)
    parser.add_argument('--os', choices=('msdos', 'freedos'), required=True)
    parser.add_argument('--paging', action='store_true')
    parser.add_argument('--negative-controls', type=Path)
    args = parser.parse_args()
    paths = (args.report, args.memory, args.program, args.os, args.paging)
    result = verify(*paths)
    if args.negative_controls:
        result['negativeControls'] = negative_controls(paths, args.negative_controls)
    print(json.dumps(result, indent=2))
