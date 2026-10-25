"""Independent oracle: executed bytes, TSS RAM, actual faults, task counters and DOS return."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


SYMBOLS = ('start code_end proof gdt ldt main child word jump bad handler mainsp childsp wordsp '
           'jumpsp badsp handlersp after_child after_local after_gdt busy after_busy np after_np '
           'after_bad bad_entry after_jump child_entry word_ret jump_ret paging end child_ret bad_ret handler_ret').split()


def verify(report_path, memory_path, program_path, os_name, paging=False):
    report = json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    memory = Path(memory_path).read_bytes()
    program = Path(program_path).read_bytes()
    output, cpu = report['status']['debugOutput'], report['cpu']
    assert report['status']['fault'] is None and report['scenario']['complete']
    assert report['scenario']['inputBytesSent'] == report['scenario']['inputBytesTotal']
    assert report['hardwareResetCount'] == report['ataSectorsWritten'] == report['floppySectorsWritten'] == 0
    assert report['ataSectorsRead'] > 0 and report['floppySectorsRead'] > 0
    assert 'CPU-TASK-PASS\r\n' in output and 'CPU-TASK-FAIL' not in output
    assert ('MS-DOS Version 6.22' if os_name == 'msdos' else 'FreeCom version 0.86') in output
    assert re.search(r'\r\nTASK-GUEST-END\r\n(?:\r\n)?C:\\>$', output)
    markers = re.findall(r'CPU-TASK-PROOF=([0-9A-F]{8})\r\n', output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address+512]
    assert proof[:8] == b'CPTS0001'
    u32 = lambda off: struct.unpack_from('<I', proof, off)[0]
    u16 = lambda off: struct.unpack_from('<H', proof, off)[0]
    raw = lambda off: struct.unpack_from('<I', memory, off)[0]
    word = lambda off: struct.unpack_from('<H', memory, off)[0]
    symbols = program.index(b'CPTSADDR') + 8
    labels = dict(zip(SYMBOLS, struct.unpack_from('<35I', program, symbols)))
    basis = address - labels['proof']
    assert basis == u32(80) and basis & 15 == 0
    assert labels['start'] == 0x100 and labels['end'] == len(program) + 0x100
    assert program.index(b'CPTS0001') + 0x100 == labels['proof']
    assert memory[basis+0x100:basis+labels['code_end']] == program[:labels['code_end']-0x100]
    at = lambda label: basis + labels[label]
    full = lambda label: 0x10000 + labels[label]
    assert (u32(8), u32(12), u32(16), u32(20)) == (1, 1, 127, 0)
    for first, last in ((24, 28), (32, 36), (40, 44), (48, 52), (56, 60), (64, 68), (72, 76)):
        assert u32(first) == u32(last), (first, last)
    assert u32(24) & 0x80000001 == 0 and u32(96) == int(paging) and u32(100) == 0
    assert u32(84) == at('main')
    assert program[labels['after_child']-0x100-6:labels['after_child']-0x100] == b'\x66\x9a\xef\xbe\x48\x00'
    assert program[labels['after_local']-0x100-6:labels['after_local']-0x100-4] == b'\xff\x1d'
    assert program[labels['after_gdt']-0x100-6:labels['after_gdt']-0x100] == b'\x66\x9a\xef\xbe\x73\x00'
    for label, selector in (('busy', 64), ('np', 136)):
        offset = labels[label]-0x100
        assert program[offset:offset+7] == b'\x9a\xef\xbe\xad\xde'+struct.pack('<H', selector)

    # The child actually runs at CPL3, with EIP/ESP > 64 KiB and its own LDT.
    assert struct.unpack_from('<8I', proof, 128) == (
        0x11112222, 0x33334444, 0x55556666, 0x12345678, full('childsp'), 0x77778888, 0x9999aaaa, 0xbbbbcccc)
    assert u32(160) == 0x4002 and u32(164) & 0x1f == 0x19
    assert struct.unpack_from('<8H', proof, 168) == (51, 123, 72, 128, 56, 56, 0, 0)
    assert u32(184) == full('childsp') and u16(188) == 128
    # Both task gates reach a real 16-bit TSS. Undefined upper GPR halves are
    # intentionally not an architecture oracle; the low words are contractual.
    for offset, ax in ((192, 0x123), (224, 72)):
        assert u16(offset) == ax and u16(offset+4) == 0x456 and u16(offset+8) == labels['wordsp']
        assert u32(offset+12) == 0x4002 and u32(offset+16) & 0x1f == 0x19
        assert struct.unpack_from('<5H', proof, offset+20) == (8, 16, 80, 0, 72)
    assert u32(340) == 2
    assert (u32(256), u32(260), u32(264), u32(268) & 65535) == (full('busy'), 24, 0x10002, 64)
    assert (u32(272), u32(276), u32(280), u32(284) & 65535) == (full('np'), 24, 0x10002, 136)
    assert u32(268) >> 16 == u32(284) >> 16 == 0xa5a5  # 486 reserves, does not zero this word
    assert [u32(n) for n in range(288, 316, 4)] == [104, 0xa5a5fff8, 96, full('bad_entry'), 0xbadc0033, 0xfff8, 0x4002]
    active_cr0 = u32(24) | 9 | (0x80010000 if paging else 0)
    assert u32(316) == active_cr0
    assert [u32(n) for n in range(320, 340, 4)] == [1, 96, 32, 0x4002, 0xbadc0033]
    assert [u32(n) for n in range(344, 384, 4)] == [88, 2, 0, 0x89, full('mainsp'), 64, 2, 0x11, 64, 0]

    # Distinguish saved integer state from static TSS fields and descriptor caches.
    specs = (('main', 64, 0x67, 0x8b, 0), ('child', 72, 0x67, 0x89, 64),
             ('word', 80, 0x2b, 0x81, 72), ('jump', 88, 0x67, 0x89, 0),
             ('bad', 96, 0x67, 0x89, 64), ('handler', 104, 0x67, 0x89, 96))
    for name, selector, limit, access, backlink in specs:
        low, high = struct.unpack_from('<II', memory, at('gdt') + selector)
        base = (low >> 16) | ((high & 255) << 16) | (high & 0xff000000)
        assert base == at(name) and low & 65535 == limit and (high >> 8) & 255 == access, name
        assert word(at(name)) == backlink, name
        if name != 'word':
            assert raw(at(name)+0x1c) == u32(88)  # static CR3 is not overwritten with outgoing CR3
            assert word(at(name)+0x64) == 0 and word(at(name)+0x66) == 104
    assert raw(at('main')+0x20) == full('after_jump') and raw(at('main')+0x38) == full('mainsp')
    assert raw(at('child')+0x20) == full('child_ret') and raw(at('child')+0x38) == full('childsp')
    assert [word(at('child')+off) for off in (0x48, 0x4c, 0x50, 0x54, 0x58, 0x5c, 0x60)] == [56, 51, 123, 56, 0, 0, 128]
    assert word(at('word')+0x0e) == labels['word_ret'] and word(at('word')+0x1a) == labels['wordsp']
    assert raw(at('jump')+0x20) == full('jump_ret') and raw(at('jump')+0x24) == 6  # OR completion bits sets PF
    assert raw(at('bad')+0x20) == full('bad_ret') and raw(at('bad')+0x34) == 0xbadc0033 and word(at('bad')+0x54) == 32
    assert raw(at('handler')+0x20) == full('handler_ret')
    for name in ('child', 'bad', 'handler'):
        assert raw(at(name)+0x24) & 0x4000 == 0  # IRET cleared outgoing NT before save
    for name in ('main', 'child', 'jump', 'bad', 'handler'):
        assert all(word(at(name)+offset) == 0 for offset in (2, 0x4a, 0x4e, 0x52, 0x56, 0x5a, 0x5e, 0x62))
    for name, number in zip(('mainsp', 'childsp', 'wordsp', 'jumpsp', 'badsp', 'handlersp'), range(1, 7)):
        assert raw(at(name)-388) == 0xc0de0000+number and raw(at(name)) == 0xcafe0000+number
    assert word(at('handlersp')-4) == 0xfff8 and word(at('mainsp')-16) == 136
    for selector in (112,):
        assert memory[at('gdt')+selector:at('gdt')+selector+8] == program[labels['gdt']-0x100+selector:labels['gdt']-0x100+selector+8]
    assert memory[at('ldt'):at('ldt')+8] == program[labels['ldt']-0x100:labels['ldt']-0x100+8]

    assert not cpu['protectedMode'] and not cpu['pagingEnabled'] and not cpu['virtual8086'] and not cpu['tripleFaulted']
    assert cpu['cr0'] == u32(24) and cpu['cr3'] == u32(32) and cpu['currentPrivilegeLevel'] == 0
    assert cpu['hardwareTaskSwitches'] == 12 and cpu['hardwareTaskReturns'] == 5 and cpu['taskGateTransfers'] == 3
    assert cpu['taskDebugTraps'] == cpu['privilegeStackSwitches'] == cpu['outerPrivilegeReturns'] == cpu['callGateTransfers'] == 0
    assert cpu['tr'] == 64 and cpu['trCache'] == dict(base=at('main'), limit=0x67, access=0x8b, flags=0, usable=True)
    assert cpu['ldtr'] == 0 and not cpu['ldtrCache']['usable']
    assert cpu['gdtr'] == dict(base=u32(40), limit=u32(48)) and cpu['idtr'] == dict(base=u32(56), limit=u32(64))
    assert cpu['pageFaults'] == 0 and cpu['protectedInstructions'] >= 170
    if paging:
        pd, pt = u32(88), u32(92)
        assert pd % 4096 == 0 and pt == pd+4096 and at('paging') <= pd and pt+4096 <= at('end')
        assert raw(pd) & ~0x20 == pt | 7 and memory[pd+4:pd+4096] == bytes(4092)
        assert all(raw(pt+i*4) & ~0x60 == i*4096 | 7 for i in range(1024))
        assert cpu['tlbInvalidations'] >= 14 and cpu['pageWalks'] >= 10
    else:
        assert u32(88) == u32(92) == cpu['pageWalks'] == 0
    return dict(passed=True, os=os_name, identityPaging=paging, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), executableSha256=hashlib.sha256(program).hexdigest().upper(),
                hardwareTaskSwitches=12, taskReturns=5, taskGateTransfers=3,
                actual16And32BitTasks=True, nestedRing3ToRing0Tasks=True, ldtIndexZero=True,
                preCommitGpNpVerified=True, committedTaskFaultRepairedAndResumed=True,
                busyBacklinkNtTsVerified=True, picTablesCr0Cr3Restored=True,
                privateTrRemainsUntilReset=True, cleanDosReturn=True, all486PrecisionAccepted=False)


def negative_controls(paths, directory):
    rp, mp, pp, os_name, paging = paths
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    report = json.loads(Path(rp).read_text(encoding='utf-8-sig'))
    memory = Path(mp).read_bytes()
    address = int(re.search(r'CPU-TASK-PROOF=([0-9A-F]{8})', report['status']['debugOutput'])[1], 16)
    rejected = []

    def reject(name, arguments):
        try:
            verify(*arguments)
        except (AssertionError, ValueError, KeyError, IndexError, struct.error):
            rejected.append(name)
        else:
            raise AssertionError('Corrupt task-switch acceptance was accepted: '+name)

    for name, offset in (('success', 12), ('stages', 16), ('child-eax', 128), ('child-full-esp', 144),
                         ('child-cpl', 168), ('child-tr', 172), ('child-ldt', 174), ('word-task-ip-stack', 200),
                         ('word-backlink', 220), ('gp-restart', 256), ('np-selector', 284),
                         ('committed-handler-tr', 288), ('task-error-slot', 292), ('committed-backlink', 296),
                         ('committed-eip', 300), ('committed-ebx', 304), ('invalid-ds', 308),
                         ('repaired-ds', 328), ('jump-nt', 348), ('outgoing-busy-clear', 356)):
        changed = bytearray(memory)
        changed[address+offset] ^= 1
        path = directory/(name+'.ram')
        path.write_bytes(changed)
        reject(name, (rp, path, pp, os_name, paging))
    for name, field in (('actual-task-counter', 'hardwareTaskSwitches'), ('actual-return-counter', 'hardwareTaskReturns'),
                        ('actual-gate-counter', 'taskGateTransfers')):
        changed = copy.deepcopy(report)
        changed['cpu'][field] = 0
        path = directory/(name+'.json')
        path.write_text(json.dumps(changed), encoding='utf-8')
        reject(name, (path, mp, pp, os_name, paging))
    program = Path(pp).read_bytes()
    labels = dict(zip(SYMBOLS, struct.unpack_from('<35I', program, program.index(b'CPTSADDR')+8)))
    basis = address-labels['proof']
    for name, physical in (('busy-descriptor', basis+labels['gdt']+69), ('saved-task-eip', basis+labels['bad']+0x20),
                           ('handler-stack-guard', basis+labels['handlersp']-388)):
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
    changed['status']['debugOutput'] = changed['status']['debugOutput'].replace('TASK-GUEST-END', 'FAKE-END')
    path = directory/'dos-return.json'
    path.write_text(json.dumps(changed), encoding='utf-8')
    reject('dos-return', (path, mp, pp, os_name, paging))
    if paging:
        pt = struct.unpack_from('<I', memory, address+92)[0]
        for name, physical in (('pdb-address', address+88), ('pte-frame', pt+18), ('pte-rights', pt+4)):
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
