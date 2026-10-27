# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent full-PC P54C DOS/clock/CPU/file and original-monitor-frame oracles; no VM imports."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('legacy_snapshot_oracle', Path(__file__).with_name('verify-snapshot-continuation.py'))
legacy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(legacy)
media = legacy.media
MASK = (1 << 64) - 1
FREQUENCY = 90_000_000


def check(value, reason):
    """Reject incorrect actual artifacts, flags, instruction values or error frames."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named private proof document without traversing media directories."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Hash exactly one explicitly named protected file."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def metadata(program, fault=False):
    """Read only the assembler's static label table from the unchanged own test program."""
    tag, count = (b'P54FAULT', 2) if fault else (b'P54META1', 14)
    at = program.index(tag)
    check(program.count(tag) == 1, 'one own label table')
    return struct.unpack_from('<' + 'H' * count, program, at + 8)


def common(work, result, state, manifest):
    """Certify real full-container identities and the explicit CPU/profile/clock boundary."""
    root = work / 'evidence'
    saved = root / 'cut.dossav'
    check(result['passed'] and result['comparisons'] > 1, 'actual full continuation')
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1, 'full PC format')
    check(manifest['ramBytes'] == 8 * 1048576 and manifest['ramSha256'] == sha(saved / 'ram.bin'), 'full raw RAM hash')
    check(manifest['stateBytes'] == (saved / 'state.json').stat().st_size and manifest['stateSha256'] == sha(saved / 'state.json'), 'full state identity')
    check(manifest['mediaSha256'] == sha(saved / 'media.dosemedia/media.json'), 'shared media identity')
    check(result['capturedStateSha256'] == manifest['stateSha256'] and result['capturedRamSha256'] == manifest['ramSha256'], 'actual captured identities')
    check(state['scheduler']['now'] == manifest['virtualNanoseconds'], 'full shared clock')
    cpu = state['components']['cpu']['fields']
    check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == FREQUENCY, 'constructor-fixed P54C model/frequency')
    check(cpu['Cr4'] & ~12 == 0 and cpu['Dr7'] & 0xDC00 == 0x400, 'implemented P54C control subset')
    profile = read(saved / 'media.dosemedia/media.json')['machine']['hardware']
    check(profile['id'] == 'dose-p54c-at-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == FREQUENCY, 'matching full-PC profile')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    for block in state['components'].values():
        check(block['type'] in schema and block['version'] == 1, 'known component format')
        expected = {v['name'] for v in schema[block['type']]['fields'] if v['mode'] not in ['children', 'excluded']}
        check(set(block['fields']) == expected, 'exact reviewed component fields')
    return cpu


def ticks(point, state):
    """Reconstruct exact tick phase using the real four-cycle instruction ledger and persisted scheduler remainder."""
    clock = state['scheduler']
    cpu = state['components']['cpu']['fields']
    check(clock['cachedFrequency'] == FREQUENCY, 'fixed own clock conversion')
    delta = cpu['Instructions'] - point['instructions']
    check(delta >= 0, 'point precedes persisted clock anchor')
    remainder = (clock['remainder'] - delta * 4 * 1_000_000_000) % FREQUENCY
    return ((point['nanoseconds'] * FREQUENCY + remainder) // 1_000_000_000) & MASK


def certify_ledger(entries, cut, final, ledger):
    """Check every actually executed own CPUID/CX8/MSR/TSC operation from unchanged bytes and integer virtual time."""
    expected_epoch = 0
    v86 = entries[0]['before']['v86']
    check([r['kind'] for r in entries] == ['cpuid0', 'cpuid1', 'cx8match', 'cx8miss'] + ([] if v86 else ['wrmsr', 'rdmsr', 'rdtsc']), 'actual permitted instruction sequence')
    points = 0
    for row in entries + ledger['future']:
        before, after, kind = row['before'], row['after'], row['kind']
        check(after is not None, 'completed actual instruction')
        anchor = final if kind == 'futureTsc' else cut
        for point in [before, after]:
            actual_ticks = ticks(point, anchor)
            check(point['ticks'] == actual_ticks and point['counter'] == (actual_ticks - point['epoch']) & MASK, 'integer TSC phase')
            check(point['cr0'] & 1 == (1 if v86 else 0) and point['v86'] == v86, 'actual own DOS execution mode')
            points += 1
        check(before['epoch'] == expected_epoch, 'actual preceding TSC epoch')
        a, b = before['registers'], after['registers']
        preserved = list(range(8))
        if kind == 'cpuid0':
            check([b[0], b[3], b[2], b[1]] == [1, 0x756E6547, 0x49656E69, 0x6C65746E], 'original CPUID vendor/leaf')
            preserved = [4, 5, 6, 7]
        elif kind == 'cpuid1':
            check([b[0], b[1], b[2], b[3]] == [0x524, 0, 0x135, 0], 'exact advertised subset')
            preserved = [4, 5, 6, 7]
        elif kind == 'cx8match':
            check(before['operand'] == 0xFEDCBA9876543210 and after['operand'] == 0x0123456789ABCDEF and after['flags'] & 0x40, 'actual full 64-bit exchange')
        elif kind == 'cx8miss':
            check(before['operand'] == after['operand'] == 0x0123456789ABCDEF and (b[2] << 32 | b[0]) == after['operand'] and not after['flags'] & 0x40, 'actual failing 64-bit comparison')
            preserved = [1, 3, 4, 5, 6, 7]
        elif kind == 'wrmsr':
            check(not v86 and a[1] == 0x10 and (a[2] << 32 | a[0]) == 0x12345678FFFFFFE0, 'actual full privileged TSC write')
            expected_epoch = (before['ticks'] - (a[2] << 32 | a[0])) & MASK
        elif kind in ['rdmsr', 'rdtsc', 'futureTsc']:
            check(not v86 and (b[2] << 32 | b[0]) == before['counter'], 'actual full 64-bit timestamp read')
            if kind == 'rdmsr':
                check(a[1] == 0x10, 'full ECX selects implemented TSC MSR')
            preserved = [1, 3, 4, 5, 6, 7]
        else:
            raise AssertionError('unknown actual own instruction')
        check(after['epoch'] == expected_epoch, 'actual committed modular epoch')
        check(all(a[i] == b[i] for i in preserved), 'unmodified actual GPRs')
        mask = ~0x40 if kind.startswith('cx8') else -1
        check(before['flags'] & mask == after['flags'] & mask, 'actual flags preserved outside CX8 ZF')
    check(ledger['future'] == ledger['restoredFuture'] and [r['kind'] for r in ledger['future']] == ([] if v86 else ['futureTsc']), 'actual future CPU publications equal')
    check(cut['components']['cpu']['fields']['_tscEpochTicks'] == expected_epoch == final['components']['cpu']['fields']['_tscEpochTicks'], 'full saved/restored TSC epoch')
    return points, v86


def certify_text(final, ram, reported):
    """Decode the actual 80x25 text cells from raw VGA planes/CRTC and independently cross-check the DOS BDA."""
    video = final['components']['video']['fields']
    crtc = base64.b64decode(video['_crtc'], validate=True)
    gc = base64.b64decode(video['_gc'], validate=True)
    characters = base64.b64decode(video['_planes'][0], validate=True)
    check(not gc[6] & 1 and len(characters) == 262144, 'actual ET4000 text memory')
    columns = crtc[1] + 1
    lines = crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + ((crtc[0x35] & 4) << 8) + 1
    rows = (lines // (2 if crtc[9] & 0x80 else 1)) // ((crtc[9] & 31) + 1)
    check((columns, rows) == (80, 25), 'actual own 80x25 console mode')
    check(ram[0x449] & 0x7F == 3 and struct.unpack_from('<H', ram, 0x44A)[0] == columns and ram[0x484] + 1 == rows, 'actual DOS/BIOS text BDA agrees')
    start = (crtc[12] << 8) | crtc[13] | ((crtc[0x33] & 3) << 16)
    stride = crtc[0x13] * 2
    check(stride >= columns, 'actual text row stride')
    cells = bytes(characters[(start + row * stride + column) & 0x3FFFF] for row in range(rows) for column in range(columns))
    text = '\n'.join(''.join(chr(c) if 32 <= c < 127 else ' ' for c in cells[row * columns:(row + 1) * columns]).rstrip() for row in range(rows)).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip(), 'reported text equals independently decoded raw VGA cells')
    check('SNAPSHOT-DONE' in text and text.endswith('A:\\>'), 'actual raw VGA post-program DOS prompt')
    return dict(columns=columns, rows=rows, cellsSha256=hashlib.sha256(cells).hexdigest().upper())


def certify_positive(work, result, state, manifest, ledger):
    """Certify actual DOS files, raw x87/PCM, CPU instructions and full snapshot continuation."""
    cpu = common(work, result, state, manifest)
    legacy.certify(work, result, state, manifest)
    check(result['mode'] == 'positive' and result['returnedToDos'] and result['finalScreen'].rstrip().endswith('A:\\>'), 'actual following DOS prompt')
    root = work / 'evidence'
    program = media.safe(read(work / 'input.json')['probe']).read_bytes()
    check(result['programSha256'] == hashlib.sha256(program).hexdigest().upper(), 'own exact program identity')
    table = metadata(program)
    ram = (root / 'cut.dossav/ram.bin').read_bytes()
    base = result['programBase']
    check(base == ledger['programBase'] and base & 15 == 0 and 0 < base < 0xA0000, 'actual own DOS program segment')
    check(ram[base + 0x100:base + table[0]] == program[:table[0] - 0x100], 'actual immutable own instructions')
    data = ram[base + table[0]:base + table[1]]
    check(len(data) == 100 and data[:8] == b'P54CPC01' and data[-8:] == b'P54CEND1', 'CPU proof extent/guards')
    final = read(root / 'final-state.json')
    text = certify_text(final, (root / 'final-ram.bin').read_bytes(), result['finalScreen'])
    points, v86 = certify_ledger(ledger['beforeCut'], state, final, ledger)
    check(struct.unpack_from('<H', data, 8)[0] == int(v86), 'actual SMSW mode')
    before, toggled, restored = struct.unpack_from('<III', data, 10)
    check(toggled == before ^ 0x200000 and restored == before, 'actual ID flag toggle/restore')
    check(struct.unpack_from('<I', data, 22)[0] == 1 and data[26:38] == b'GenuineIntel', 'guest CPUID vendor data')
    check(struct.unpack_from('<II', data, 38) == (0x524, 0x135), 'guest signature/features data')
    check(struct.unpack_from('<QQ', data, 46) == (0x0123456789ABCDEF, 0x0123456789ABCDEF) and data[62:64] == bytes([1, 0]), 'guest CX8 result bytes')
    check(struct.unpack_from('<I', data, 64)[0] == 0, 'original implemented CR4 reset value')
    expected = bytearray(data)
    if v86:
        check(data[68:92] == bytes(24), 'monitor privileges are not bypassed')
    else:
        check(cpu['Cr4'] == 12 and cpu['_tscEpochTicks'] != 0, 'actual privileged control/epoch survives full cut')
        msr = next(r for r in ledger['beforeCut'] if r['kind'] == 'rdmsr')
        tsc = next(r for r in ledger['beforeCut'] if r['kind'] == 'rdtsc')
        future = ledger['future'][0]
        check(struct.unpack_from('<QQ', data, 68) == (msr['before']['counter'], tsc['before']['counter']), 'guest read bytes match independently reconstructed timestamps')
        check(future['before']['counter'] > tsc['before']['counter'] and future['before']['counter'] >> 32 != 0, 'actual future high counter')
        struct.pack_into('<Q', expected, 84, future['before']['counter'])
        check(final['components']['cpu']['fields']['Cr4'] == 0, 'guest restores its real-mode CR4 selection')
    for name in ['final-a.dosemedia', 'final-b.dosemedia']:
        directory = root / name
        disk = read(directory / 'media.json')['machine']['hardDisks'][0]
        actual = media.Disk(directory / disk['imagePath'], directory / disk['overlayPath']).file(b'P54C    DAT')
        check(actual == bytes(expected), 'actual DOS file preserves CPU/clock proof after full continuation')
    check(result['actualInstructions'] == len(ledger['beforeCut']) + len(ledger['future']), 'actual CPU instruction tally')
    return dict(instructionCount=result['actualInstructions'], reconstructedPoints=points, guestV86=v86, proofBytes=len(data), savedCr4=cpu['Cr4'], rawText=text)


def physical(ram, address, a20=True):
    """Read unchanged physical RAM through the explicit recorded A20 line, never a VM helper."""
    if not a20:
        address &= ~(1 << 20)
    check(0 <= address < len(ram), 'physical frame byte bounds')
    return ram[address]


def linear(ram, cpu, address, count, a20=True):
    """Independently walk classic two-level x86 paging for original EMM386's stack and gate bytes."""
    def pdword(physical_address):
        """Read one little-endian page-table word using the recorded physical A20 line."""
        return sum(physical(ram, physical_address + n, a20) << (n * 8) for n in range(4))
    result = bytearray()
    for position in range(address, address + count):
        mapped = position
        if cpu['Cr0'] & 0x80000000:
            pde = pdword((cpu['Cr3'] & 0xFFFFF000) + (position >> 22) * 4)
            check(pde & 1 and not pde & 0x80, 'present classic monitor PDE')
            pte = pdword((pde & 0xFFFFF000) + ((position >> 12) & 1023) * 4)
            check(pte & 1, 'present monitor PTE')
            mapped = (pte & 0xFFFFF000) | (position & 4095)
        result.append(physical(ram, mapped, a20))
    return bytes(result)


def certify_fault(work, result, state, manifest, before, point):
    """Certify the actual original EMM386 #GP frame and complete monitor cut, without claiming DOS completion."""
    cpu = common(work, result, state, manifest)
    check(result['mode'] == 'monitorFault' and result['returnedToDos'] is False and result['comparisons'] == 8, 'bounded original-monitor scope')
    check(result['instruction'] in ['rdtsc', 'rdmsr', 'wrmsr'], 'known negative instruction')
    original = before['fields']
    check(original['Eflags'] & 0x20000 and original['Cr0'] & 0x80000001 == 0x80000001, 'actual original V86 paging state')
    check(not cpu['Eflags'] & 0x20000 and cpu['_cpl'] == 0 and cpu['Virtual8086Exits'] == original['Virtual8086Exits'] + 1, 'actual original-monitor entry')
    check(cpu['Cr3'] == original['Cr3'] and cpu['Cr2'] == original['Cr2'], 'fault preserves original monitor address space')
    check(cpu['_tscEpochTicks'] == original['_tscEpochTicks'] == point['epoch'], 'privilege fault preserves counter epoch')
    check(cpu['Cr4'] == original['Cr4'] == point['cr4'], 'privilege fault preserves CR4')
    check(point['ticks'] == ticks(point, state) and point['counter'] == (point['ticks'] - point['epoch']) & MASK, 'fault point exact virtual counter')
    for i, expected in [(0, 0xA55A0FF0), (1, 0x10), (2, 0x12345678), (3, 0xABCDDCBA)]:
        check(original['_registers'][i] == cpu['_registers'][i] == expected, 'actual rejected instruction preserves guest operands')
    for i in [5, 6, 7]:
        check(original['_registers'][i] == cpu['_registers'][i], 'fault preserves remaining GPRs')
    ram = (work / 'evidence/cut.dossav/ram.bin').read_bytes()
    a20 = state['components']['memory']['fields']['A20Enabled']
    program = media.safe(read(work / 'input.json')['probe']).read_bytes()
    check(result['programSha256'] == hashlib.sha256(program).hexdigest().upper(), 'unchanged own fault program')
    offset, immutable_end = metadata(program, True)
    check(result['faultOffset'] == offset and result['programBase'] == original['_segments'][1] << 4, 'actual fault program address')
    check(linear(ram, cpu, result['programBase'] + 0x100, immutable_end - 0x100, a20) == program[:immutable_end - 0x100], 'actual unchanged fault instruction/data bytes')
    opcode = {'rdtsc': bytes.fromhex('0f31'), 'rdmsr': bytes.fromhex('0f32'), 'wrmsr': bytes.fromhex('0f30')}[result['instruction']]
    check(program[offset - 0x100:offset - 0x100 + 2] == opcode, 'actual prohibited opcode')
    stack = cpu['_segmentCaches'][2]
    pointer = cpu['_registers'][4] & (0xFFFFFFFF if stack['flags'] & 4 else 0xFFFF)
    frame = struct.unpack('<10I', linear(ram, cpu, stack['base'] + pointer, 40, a20))
    segments = original['_segments']
    expected = (0, offset, segments[1], original['Eflags'] | 0x10000, original['_registers'][4], segments[2], segments[0], segments[3], segments[4], segments[5])
    check(frame == expected, 'complete independently decoded original #GP(0) V86 frame')
    idtr = original['Idtr']
    low, high = struct.unpack('<II', linear(ram, cpu, idtr['base'] + 13 * 8, 8, a20))
    check(((high >> 8) & 15) in [14, 15] and high & 0x8000, 'original present GP gate')
    check(cpu['Eip'] == (low & 0xFFFF) | (high & 0xFFFF0000) and cpu['_segments'][1] == (low >> 16) & ~3, 'actual original gate target before first handler instruction')
    tss = original['_trCache']
    esp0, ss0 = struct.unpack('<II', linear(ram, cpu, tss['base'] + 4, 8, a20))
    check(pointer == (esp0 - 40) & 0xFFFFFFFF and cpu['_segments'][2] == ss0 & 0xFFFF, 'actual original TSS stack selected')
    check(not cpu['TripleFaulted'] and cpu['Instructions'] == original['Instructions'] + 1, 'one actual faulting instruction, no synthetic entry')
    return dict(frameWords=list(frame), instruction=result['instruction'], tsd=bool(point['cr4'] & 4), monitorInstructions=8)


def verify(work):
    """Reject independent CPU/clock/frame/report mutations in addition to certifying the real artifacts."""
    work = media.safe(work)
    result = read(work / 'evidence/result.json')
    state = read(work / 'evidence/cut.dossav/state.json')
    manifest = read(work / 'evidence/cut.dossav/snapshot.json')
    mutations = []
    text_negative = 0
    if result['mode'] == 'positive':
        ledger = read(work / 'evidence/ledger.json')
        proof = certify_positive(work, result, state, manifest, ledger)
        baseline = legacy.verify(work)
        final = read(work / 'evidence/final-state.json')
        final_ram = (work / 'evidence/final-ram.bin').read_bytes()
        text_mutations = []
        for field, index, value in [('_crtc', 1, 78), ('_crtc', 9, 31), ('_crtc', 13, None), ('_gc', 6, 1)]:
            changed = copy.deepcopy(final)
            data = bytearray(base64.b64decode(changed['components']['video']['fields'][field]))
            data[index] = data[index] ^ 1 if value is None else value
            changed['components']['video']['fields'][field] = base64.b64encode(data).decode('ascii')
            text_mutations.append((changed, final_ram))
        changed = copy.deepcopy(final)
        changed['components']['video']['fields']['_planes'][0] = base64.b64encode(bytes(262144)).decode('ascii')
        text_mutations.append((changed, final_ram))
        for address in [0x449, 0x44A, 0x484]:
            changed_ram = bytearray(final_ram)
            changed_ram[address] = 0
            text_mutations.append((final, changed_ram))
        for changed, changed_ram in text_mutations:
            try:
                certify_text(changed, changed_ram, result['finalScreen'])
            except (AssertionError, KeyError, ValueError):
                text_negative += 1
        check(text_negative == len(text_mutations), 'all independent raw-console corruptions rejected')
        for key in ['passed', 'returnedToDos', 'actualInstructions', 'programSha256', 'programBase', 'finalScreen']:
            changed = copy.deepcopy(result)
            changed[key] = False if isinstance(changed[key], bool) else 0 if isinstance(changed[key], int) else 'bad'
            mutations.append((changed, state, manifest, ledger))
        for index, row in enumerate(ledger['beforeCut'] + ledger['future']):
            where = 'beforeCut' if index < len(ledger['beforeCut']) else 'future'
            position = index if where == 'beforeCut' else index - len(ledger['beforeCut'])
            for key in ['epoch', 'ticks', 'counter', 'nanoseconds']:
                changed = copy.deepcopy(ledger)
                changed[where][position]['before'][key] += 1000 if key == 'nanoseconds' else 1
                mutations.append((result, state, manifest, changed))
        certify = lambda r, s, m, last: certify_positive(work, r, s, m, last)
    else:
        before = read(work / 'evidence/before-cpu.json')
        point = read(work / 'evidence/before-point.json')
        proof = certify_fault(work, result, state, manifest, before, point)
        baseline = dict(negativeOracles=0)
        for key in ['passed', 'returnedToDos', 'comparisons', 'faultOffset', 'programBase', 'programSha256', 'instruction']:
            changed = copy.deepcopy(result)
            changed[key] = not changed[key] if isinstance(changed[key], bool) else 0 if isinstance(changed[key], int) else 'bad'
            mutations.append((changed, state, manifest, point))
        for key in ['ticks', 'counter', 'epoch', 'nanoseconds', 'cr4']:
            changed = copy.deepcopy(point)
            changed[key] += 1000 if key == 'nanoseconds' else 1
            mutations.append((result, state, manifest, changed))
        for key in ['Eip', '_cpl', 'Virtual8086Exits', '_tscEpochTicks', 'Cr4']:
            changed = copy.deepcopy(state)
            changed['components']['cpu']['fields'][key] += 1
            mutations.append((result, changed, manifest, point))
        certify = lambda r, s, m, last: certify_fault(work, r, s, m, before, last)
    for key in ['Model', 'TimestampFrequency']:
        changed = copy.deepcopy(state)
        changed['components']['cpu']['fields'][key] = 0
        mutations.append((result, changed, manifest, ledger if result['mode'] == 'positive' else point))
    rejected = 0
    for r, s, m, last in mutations:
        try:
            certify(r, s, m, last)
        except (AssertionError, KeyError, ValueError):
            rejected += 1
    check(rejected == len(mutations), 'all independent P54C evidence corruptions rejected')
    return dict(passed=True, negativeOracles=rejected + baseline['negativeOracles'] + text_negative, p54cNegativeOracles=rejected + text_negative, **proof)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    print(json.dumps(verify(parser.parse_args().directory), indent=2))
