# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent DOS/EMM386 PIIX IRQ pin, PIC/EOI/IRET frame, raw file and full-container oracles."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('piix_console_oracle', REPO / 'tests/verify-p54c-pc.py')
console = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(console)
media = console.media
RAM_BYTES = 32 * 1048576
FREQUENCY = 100_000_000
MASK = (1 << 64) - 1
IRQS = [10, 10, 10, 10, 10, 11, 10, 11]
PHASES = [1, 1, 2, 2, 4, 5, 6, 6]
RETURN_LABELS = [11, 11, 12, 12, 13, 14, 15, 16]
PIN_CHANGES = [('pirq', 0, True), ('pirq', 1, True), ('pirq', 0, False), ('pirq', 1, False),
               ('pirq', 0, True), ('pirq', 0, False), ('isa', 10, True), ('isa', 10, False),
               ('pirq', 2, True), ('pirq', 2, False), ('pirq', 3, True), ('pirq', 3, False)]
PIRQ_VALUES = [1, 3, 2, 0, 1, 0, 0, 0, 4, 0, 8, 0]


def check(value, reason):
    """Reject an incorrect actual effect or inconsistent source, frame, clock or container."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named own evidence file, never personal overlay directories."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def digest(path):
    """Hash only one explicitly selected own evidence file."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def decode(data):
    """Decode a strictly bounded artifact byte array without CLR or emulator imports."""
    return base64.b64decode(data, validate=True)


def metadata(program):
    """Read the own assembler's unique static labels, independent of emulator implementation."""
    check(program.count(b'PIXMETA1') == 1, 'unique own IRQ metadata')
    table = struct.unpack_from('<19H', program, program.index(b'PIXMETA1') + 8)
    check(table[3] == len(program) + 0x100 and table[1] - table[0] == 200, 'complete own proof/program extent')
    check(program[table[4] - 0x100:table[4] - 0xfe] == bytes([0xE6, 0xE9]), 'actual own ready OUT bytes')
    check(program[table[7] - 0x100] == program[table[10] - 0x100] == 0xCF, 'actual static own IRET opcodes')
    check(all(program[table[index] - 0x100:table[index] - 0xfe] == bytes([0x66, 0x60]) for index in [5, 8]), 'actual 66h PUSHAD handler entry bytes')
    return table


def component(state, suffix):
    """Select one explicitly registered hardware type rather than depending on a guessed instance name."""
    blocks = [block['fields'] for block in state['components'].values() if block['type'].endswith(suffix)]
    check(len(blocks) == 1, 'one actual ' + suffix + ' state component')
    return blocks[0]


def clock(point, state):
    """Reconstruct the complete persisted 100-MHz virtual phase; no host clock or tick helper."""
    scheduler = state['scheduler']
    cpu = state['components']['cpu']['fields']
    check(scheduler['cachedFrequency'] == FREQUENCY and scheduler['remainder'] == 0, 'persisted exact fixed-frequency remainder')
    check(point['instructions'] <= cpu['Instructions'] and point['nanoseconds'] <= scheduler['now'], 'point precedes exact full-state anchor')
    ticks = (point['nanoseconds'] * FREQUENCY // 1_000_000_000) & MASK
    check(point['ticks'] == ticks and point['epoch'] == 0 and point['counter'] == ticks, 'independent modular integer TSC phase')
    check(point['elcr'] & ~0xDEF8 == 0 and point['pirqs'] & ~15 == 0 and not point['isa'] & 4, 'allowed actual normalized pin/ELCR observations')
    return ticks


def ownership(ram, program, table, base, cpu, a20):
    """Prove complete DOS PSP/MCB ownership and every actual conventional code/frame/stack mapping."""
    check(16 <= base < 0xA0000 and base & 15 == 0 and ram[base:base + 2] == b'\xCD\x20', 'actual own conventional PSP')
    psp = base >> 4
    header = ram[base - 16:base]
    paragraphs = (table[3] + 15) // 16
    check(header[0] in [ord('M'), ord('Z')] and struct.unpack_from('<HH', header, 1) == (psp, paragraphs), 'actual AH4A complete own MCB owner/paragraph count')
    end = base + paragraphs * 16
    check(base + table[3] <= end <= 0xA0000, 'complete own program/data/stack inside conventional allocation')
    check(program.index(b'PIXMETA1') + 0x100 == table[18] and table[18] - 2048 > table[17] + 32, 'separate complete static stack and proof/FPU storage')
    check(ram[base + 0x100:base + table[2]] == program[:table[2] - 0x100], 'all unchanged actual own instruction bytes')
    for linear in range(base & ~4095, (end + 4095) & ~4095, 4096):
        actual = console.linear(ram, cpu, linear, 4096, a20)
        check(actual == ram[linear:linear + 4096], 'all actual DOS-owned conventional pages retain original identity backing')
    check(cpu['_segments'][1] == cpu['_segments'][2] == psp and cpu['_segmentCaches'][1]['base'] == cpu['_segmentCaches'][2]['base'] == base, 'actual own DOS CS/SS and bases')
    check(cpu['_registers'][4] & 0xFFFF == table[18] - 40, 'actual private small frame plus PUSHAD/DS cut stack')
    check(cpu['Eip'] == table[6], 'exact first IRQ10 committed-count cut before NOP/EOI')
    frame = console.linear(ram, cpu, base + table[18] - 6, 6, a20)
    handle = struct.unpack_from('<H', ram, base + table[2] + 2)[0]
    jft_count, jft_offset, jft_segment = struct.unpack_from('<3H', ram, base + 0x32)
    jft = (jft_segment << 4) + jft_offset
    check(5 <= handle < jft_count <= 255 and 0 <= jft <= 0xA0000 - jft_count, 'actual own reopened DOS handle/JFT extent')
    entry = console.linear(ram, cpu, jft + handle, 1, a20)[0]
    check(3 <= entry < 0x80, 'actual live ordinary own file handle in original DOS JFT at cut')
    return dict(pspSegment=psp, comParagraphs=paragraphs, comEnd=end, stackTop=table[18], cutFrameHex=frame.hex().upper(),
                openFileHandle=handle, jftBase=jft, sftIndex=entry)


def common(result, cut, final, manifest, hashes, profile):
    """Validate the full build-bound graph, installed RAM, strict model/profile and raw PCM/DMA result."""
    check(result['passed'] is True and result['returnedToDos'] is True and result['exitA'] is True and result['exitB'] is True and
          result['comparisons'] > 1 and result['futureSteps'] > 10000, 'actual complete DOS/PC continuation')
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and manifest['ramBytes'] == RAM_BYTES, 'full 32-MiB state format1')
    for field, saved, actual in [('capturedStateSha256', 'stateSha256', 'state'), ('capturedRamSha256', 'ramSha256', 'ram')]:
        check(result[field] == manifest[saved] == hashes[actual], 'actual complete captured identity ' + field)
    check(manifest['stateBytes'] == hashes['stateBytes'] and manifest['mediaSha256'] == hashes['media'], 'exact complete graph size/media seal')
    check(result['finalStateSha256'] == hashes['finalState'] and result['finalRamSha256'] == hashes['finalRam'] and result['programSha256'] == hashes['program'], 'complete final state/RAM/program identities')
    check(result['dma'] == 512 and result['pcmFrames'] > 480 and len(result['pcmSha256']) == 64 and int(result['pcmSha256'], 16) != 0, 'actual complete PCM/DMA future')
    check(cut['scheduler']['now'] == manifest['virtualNanoseconds'], 'one full-machine shared owner boundary')
    check(profile['id'] == 'dose-430fx-piix-irq-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == FREQUENCY and profile['ramMiB'] == 32, 'strict integrated PIIX IRQ board profile')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    for state in [cut, final]:
        cpu = state['components']['cpu']['fields']
        check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == FREQUENCY and cpu['Cr4'] & ~12 == 0 and cpu['_tscEpochTicks'] == 0, 'fixed actual P54C subset/model/frequency')
        check(state['components']['memory']['fields']['DecodedRamBytes'] == RAM_BYTES, 'actual CPU physical RAM decoder')
        for block in state['components'].values():
            check(block['type'] in schema and block['version'] == 1, 'explicit registered component/version')
            fields = {field['name'] for field in schema[block['type']]['fields'] if field['mode'] not in ['children', 'excluded']}
            check(set(block['fields']) == fields, 'every reviewed raw field exactly once')
    sb16 = component(cut, 'SoundBlaster16')
    check(sb16['RemainingSamples'] > 0 and component(final, 'SoundBlaster16')['TransferredSamples'] == 512, 'actual active PCM cut and real DMA completion')
    events = cut['scheduler']['events']
    check(any(e['owner'] == 'pit' and e['time'] > cut['scheduler']['now'] for e in events) and any(e['owner'] == 'sb16' for e in events), 'actual future PIT/PCM events saved')


def certify_proof(data, mode, table, base, cut=False):
    """Check every fixed proof field and actual complete IRQ/return-frame record from the DOS file."""
    check(len(data) == 200 and data[:8] == b'PIIXIRQ1' and data[-8:] == b'IRQEND01', 'exact real proof file extent/guards')
    check(struct.unpack_from('<H', data, 8)[0] == mode and struct.unpack_from('<II', data, 56) == (0x524, 0x135), 'actual allowed execution mode and CPUID subset')
    check(data[22:24] == bytes([0xF8, 0xDE]) and struct.unpack_from('<III', data, 24) == (0x80808080, 0x122E8086, 0x06010002) and data[38] == 0x80, 'actual ELCR masks, BDF/A1 function ID/class/header/routes')
    check(data[20] & ~0xF8 == 0 and data[21] & ~0xDE == 0, 'actual original allowed ELCR values')
    expected = (1, 1, 0, 1) if cut else (7, 6, 2, 8)
    check(struct.unpack_from('<4H', data, 10) == expected, 'actual phase and IRQ10/IRQ11/frame counts')
    check(struct.unpack_from('<HH', data, 52) == ((0, 0) if cut else (1, 1)), 'actual post-return restore/file completion')
    count = 1 if cut else 8
    for index in range(count):
        irq, phase, ordinal, ip, cs, flags, sp, ss = struct.unpack_from('<8H', data, 64 + index * 16)
        check((irq, phase, ordinal, ip, cs, sp, ss) == (IRQS[index], PHASES[index], index + 1, table[RETURN_LABELS[index]], base >> 4, table[18] - 6, base >> 4), 'actual independently specified IRQ frame address/order')
        check(flags & 0x200 and not flags & 0x100, 'actual completed HLT resume has IF and no TF')
    check(not any(data[64 + count * 16:192]), 'no unrecorded/ninth guest IRQ frame')
    if not cut:
        tick_start, tick_end = struct.unpack_from('<HH', data, 48)
        check(2 <= (tick_end - tick_start) & 65535 < 100, 'two genuine BIOS/PIT ticks while idle active routes suppress ISA10')
    return count


def certify_pin(row, index, cut, final):
    """Prove each controlled virtual pin was published only between CPU steps and altered no clock/state token."""
    check((row['kind'], row['index'], row['high']) == PIN_CHANGES[index], 'actual exact owner pin publication order')
    before, after = row['before'], row['after']
    anchor = cut if index < 3 else final
    clock(before, anchor)
    clock(after, anchor)
    for field in before:
        if field not in ['pirqs', 'isa']:
            check(before[field] == after[field], 'pin mutation cannot advance CPU/clock or alter flags/registers')
    check(after['pirqs'] == PIRQ_VALUES[index], 'actual independent active-low PIRQ normalization/shared providers')
    if row['kind'] == 'pirq':
        bit = 1 << row['index']
        expected = before['pirqs'] | bit if row['high'] else before['pirqs'] & ~bit
        check(after['pirqs'] == expected and before['isa'] == after['isa'], 'exact one PIRQ source changed')
    else:
        bit = 1 << row['index']
        expected = before['isa'] | bit if row['high'] else before['isa'] & ~bit
        check(after['isa'] == expected and before['pirqs'] == after['pirqs'], 'exact one ISA source changed')
    return 2


def certify_frame(rows, index, proof, table, base, mode, cut, final, ports):
    """Bind one actual own entry/commit/EOI/IRET to its exact real stack bytes and preserved GPRs."""
    irq = IRQS[index]
    labels = table[5:8] if irq == 10 else table[8:11]
    kinds = ['entry' + str(irq), 'commit' + str(irq), 'iret' + str(irq)]
    check(len(rows) == 3 and [row['kind'] for row in rows] == kinds and [row['offset'] for row in rows] == list(labels), 'three actual static handler boundaries per frame')
    record = struct.unpack_from('<8H', proof, 64 + index * 16)
    frame = struct.pack('<3H', *record[3:6])
    entry, commit, leave = rows
    for row in rows:
        point = row['before']
        clock(point, cut if index == 0 and row is entry else final)
        check(point['bases'][1] == point['bases'][2] == base and point['segments'][1] == point['segments'][2] == base >> 4, 'actual own conventional handler CS/SS')
        # InstructionStarting follows prefix decoding: own entry is 66h PUSHAD; NOP/IRET have no prefix.
        fetched = 2 if row['kind'].startswith('entry') else 1
        check(point['eip'] == row['offset'] + fetched and point['v86'] == bool(mode) and point['cr0'] & 1 == mode, 'actual fetched instruction boundary/execution mode')
        check(not point['flags'] & 0x200, 'hardware/reflected handler runs with IF cleared')
    check(decode(entry['frame']) == decode(leave['frame']) == frame and not decode(commit['frame']), 'complete actual small IRQ frame preserved to IRET')
    check(entry['before']['registers'][4] & 65535 == leave['before']['registers'][4] & 65535 == table[18] - 6 and
          commit['before']['registers'][4] & 65535 == table[18] - 40, 'actual exact PUSHAD/DS frame stack effects')
    after = leave['after']
    check(after is not None, 'actual completed IRET observation')
    clock(after, final)
    check(after['registers'] == entry['before']['registers'][:4] + [((entry['before']['registers'][4] & 0xFFFF0000) | table[18])] + entry['before']['registers'][5:], 'actual IRET restores all GPRs and original pre-frame SP')
    check(after['segments'] == entry['before']['segments'] and after['bases'] == entry['before']['bases'], 'actual DS/all segments preserved through handler')
    check(after['eip'] == record[3] and after['flags'] & 0xFFFF == record[5] and after['v86'] == bool(mode), 'actual saved IP/FLAGS consumed by real IRET')
    # The guest issues one slave and one master nonspecific EOI while its own handler still holds IF clear.
    first, last = commit['before']['instructions'], leave['before']['instructions']
    eois = [p for p in ports if first <= p['at']['instructions'] < last and p['write'] and p['address'] in [0xA0, 0x20] and p['value'] == 0x20]
    check([(p['address'], p['width']) for p in eois] == [(0xA0, 1), (0x20, 1)], 'one actual ordered slave/master EOI pair before guest IRET')
    return 4


def pci_transactions():
    """Specify every own PCI config operation independently from the finite guest protocol."""
    expected = []
    for offset, value in [(0, 0x122E8086), (8, 0x06010002), (12, 0x00800000), (0x60, 0x80808080)]:
        expected += [(0xCF8, 0x80003800 | offset, 4, True), (0xCFC, value, 4, False)]
    for routes in [0x80800A0A, 0x80808080, 0x800B8080, 0x0A808080, 0x0B808080, 0x80808080]:
        expected += [(0xCF8, 0x80003860, 4, True), (0xCFC, routes, 4, True), (0xCFC, routes, 4, False)]
    return expected


def certify_port(row, expected, final):
    """Check exact actual port bytes/widths/directions plus their independent virtual time."""
    check((row['address'], row['value'], row['width'], row['write']) == expected, 'actual complete exact PCI/ELCR transfer')
    clock(row['at'], final)


def certify_text(final, ram, reported):
    """Decode actual VGA/ET4000 character planes, CRTC and DOS BDA, independent of reported console text."""
    video = final['components']['video']
    fields = video['fields']
    check(video['type'] == 'DOSEmulator.Hardware.VgaAdapter' and isinstance(fields['IsEt4000'], bool), 'explicit actual VGA constructor variant')
    expected_size = 262144 if fields['IsEt4000'] else 65536
    characters, crtc, gc = decode(fields['_planes'][0]), decode(fields['_crtc']), decode(fields['_gc'])
    check(len(characters) == expected_size and not gc[6] & 1, 'actual adapter character plane extent/text mode')
    extended = len(characters) > 65536
    columns = crtc[1] + 1
    lines = crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + (((crtc[0x35] & 4) << 8) if extended else 0) + 1
    rows = (lines // (2 if crtc[9] & 0x80 else 1)) // ((crtc[9] & 31) + 1)
    check((columns, rows) == (80, 25) and ram[0x449] & 0x7F == 3 and struct.unpack_from('<H', ram, 0x44A)[0] == columns and ram[0x484] + 1 == rows, 'actual 80x25 CRTC and original DOS BDA agree')
    start = (crtc[12] << 8) | crtc[13] | (((crtc[0x33] & 3) << 16) if extended else 0)
    stride = crtc[0x13] * 2
    check(stride >= columns, 'actual text row stride')
    cells = bytes(characters[(start + row * stride + column) % len(characters)] for row in range(rows) for column in range(columns))
    text = '\n'.join(''.join(chr(c) if 32 <= c < 127 else ' ' for c in cells[row * columns:(row + 1) * columns]).rstrip() for row in range(rows)).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip(), 'actual raw text cells equal reported completed DOS console')
    check('SNAPSHOT-DONE' in text and 'PIIX-IRQ-DONE' in text and text.endswith('A:\\>'), 'actual own completed output and following DOS prompt')
    return dict(columns=columns, rows=rows, cellsSha256=hashlib.sha256(cells).hexdigest().upper())


def disk_file(directory, filename):
    """Replay actual sealed DOSCOW records and original FAT without invoking the emulator."""
    definition = read(directory / 'media.json')
    disk = definition['machine']['hardDisks'][0]
    return media.Disk(directory / disk['imagePath'], directory / disk['overlayPath']).file(filename)


def verify(directory, program=None):
    """Certify actual complete original/restore artifacts and meaningful independent corruption rejections."""
    directory = media.safe(directory)
    root = directory if (directory / 'result.json').is_file() else directory / 'evidence'
    work = root.parent
    config = read(work / 'input.json')
    program_path = media.safe(program or config['probe'])
    program = program_path.read_bytes()
    table = metadata(program)
    result, ledger = read(root / 'result.json'), read(root / 'ledger.json')
    cut, final = read(root / 'cut.dossav/state.json'), read(root / 'final-state.json')
    manifest = read(root / 'cut.dossav/snapshot.json')
    ram, final_ram = (root / 'cut.dossav/ram.bin').read_bytes(), (root / 'final-ram.bin').read_bytes()
    check(len(ram) == len(final_ram) == RAM_BYTES, 'all actual installed physical RAM bytes')
    hashes = dict(state=digest(root / 'cut.dossav/state.json'), ram=digest(root / 'cut.dossav/ram.bin'), stateBytes=(root / 'cut.dossav/state.json').stat().st_size,
                  media=digest(root / 'cut.dossav/media.dosemedia/media.json'), finalState=digest(root / 'final-state.json'), finalRam=digest(root / 'final-ram.bin'),
                  program=hashlib.sha256(program).hexdigest().upper())
    saved_media = read(root / 'cut.dossav/media.dosemedia/media.json')
    common(result, cut, final, manifest, hashes, saved_media['machine']['hardware'])
    system = config['system']
    check(system in ['msdos', 'freedos', 'emm386-noems', 'emm386-ram'] and result['system'] == system, 'explicit own selected DOS/manager fixture')
    mode = int(system.startswith('emm386'))
    cpu = cut['components']['cpu']['fields']
    check(bool(cpu['Eflags'] & 0x20000) == bool(mode) and cpu['Cr0'] & 1 == mode, 'actual original DOS/EMM386 monitor execution mode')
    base = result['programBase']
    check(base == ledger['programBase'], 'same actual loaded own program base')
    owned = ownership(ram, program, table, base, cpu, cut['components']['memory']['fields']['A20Enabled'])
    data = ram[base + table[0]:base + table[1]]
    certify_proof(data, mode, table, base, cut=True)
    check(disk_file(root / 'cut.dossav/media.dosemedia', b'PIRQIRQ DAT') == b'PIIXIRQ1', 'actual committed own DOS header and open file handle at cut')
    proof = disk_file(root / 'final-a.dosemedia', b'PIRQIRQ DAT')
    check(proof == disk_file(root / 'final-b.dosemedia', b'PIRQIRQ DAT'), 'actual whole proof DOS file identical after restore')
    certify_proof(proof, mode, table, base)
    stable = bytearray(data)
    for start, end in [(10, 18), (48, 56), (64, 192)]:
        stable[start:end] = proof[start:end]
    check(bytes(stable) == proof, 'every remaining actual pre-cut proof byte retained')
    expected_raw = b'SNAPRAW1' + struct.pack('<QH', 0xC90FDAA22168C235, 0x4000) + struct.pack('<QH', 0xFEDCBA9876543210, 0x4001) + struct.pack('<HH', 0x037F, 0)
    check(disk_file(root / 'final-a.dosemedia', b'PIRQFPU DAT') == disk_file(root / 'final-b.dosemedia', b'PIRQFPU DAT') == expected_raw, 'actual full raw 80-bit operands/control/status DOS file')
    check(read(root / 'final-a.dosemedia/media.json')['media'] == read(root / 'final-b.dosemedia/media.json')['media'], 'all sealed final media generations identical')
    bridge = component(cut, 'Intel82371FbIrq')
    restored_bridge = component(final, 'Intel82371FbIrq')
    initial_config = bytearray(256)
    for offset, value in {0: 0x86, 1: 0x80, 2: 0x2E, 3: 0x12, 4: 7, 7: 2, 8: 2, 10: 1, 11: 6, 14: 0x80, 0x4E: 3, 0x6A: 4, 0x70: 0x80, 0x71: 0x80}.items():
        initial_config[offset] = value
    initial_config[0x60:0x64] = bytes([0x80] * 4)
    check(decode(restored_bridge['_config']) == initial_config and restored_bridge['_pirqAssertions'] == 0 and not restored_bridge['_isaLines'] & 0x400, 'actual original routes/config and external test pins restored')
    initial_config[0x60:0x64] = bytes([10, 10, 0x80, 0x80])
    check(decode(bridge['_config']) == initial_config and bridge['_pirqAssertions'] == 2 and not bridge['_isaLines'] & 0x400, 'actual shared B provider remains asserted in full cut')
    check(bridge['_elcr'] == ((data[21] | 12) << 8 | data[20]) and restored_bridge['_elcr'] == (data[21] << 8 | data[20]), 'actual level selection saved and restored')
    master, slave = cut['components']['pic/_master']['fields'], cut['components']['pic/_slave']['fields']
    check(master['Isr'] & 4 and slave['Isr'] & 4 and slave['Lines'] & 4 and slave['LevelMask'] & 4, 'actual full PIC cascade/IRQ10 in service while B stays high')
    phases = ledger['phases']
    check([r['number'] for r in phases] == list(range(1, 8)), 'all seven actual own phase OUTs observed once')
    for row in phases:
        check(row['at']['bases'][1] == base and row['at']['eip'] == table[4] + 2 and row['at']['v86'] == bool(mode), 'own phase bound to actual completed OUT')
        clock(row['at'], final)
    check(ledger['beforeCutPins'] == 3 and ledger['beforeCutBoundaries'] == 1 and len(ledger['pins']) == 12, 'first actual committed shared-frame cut')
    points = sum(certify_pin(row, index, cut, final) for index, row in enumerate(ledger['pins']))
    check(ledger['pins'][2]['after']['pirqs'] == 2 and ledger['pins'][3]['before']['pirqs'] == 2, 'deasserting A preserves still-held B until its own second handler')
    check(ledger['pins'][4]['after']['pirqs'] == ledger['pins'][5]['before']['pirqs'] == 1, 'held A generates two distinct actual level acknowledgements')
    check(ledger['pins'][6]['after']['pitPulses'] + 2 <= ledger['pins'][7]['before']['pitPulses'], 'two actual PIT pulses while ISA10 stays asserted across masked routing')
    acks = [a for a in ledger['acks'] if a['irq'] in [10, 11]]
    check([a['irq'] for a in acks] == IRQS and all(not a['spurious'] for a in acks), 'exact eight physical real IRQ acknowledgements')
    for index, ack in enumerate(acks):
        clock(ack['at'], cut if index == 0 else final)
        check(ack['vector'] == slave['Vector'] + ack['irq'] - 8, 'actual slave PIC vector, not inferred from handler number')
        check(ack['at']['elcr'] & (1 << ack['irq']), 'actual acknowledged source configured level')
    suppressed_start = ledger['pins'][6]['after']['instructions']
    suppressed_end = phases[3]['at']['instructions']
    check(not any(suppressed_start <= a['at']['instructions'] < suppressed_end for a in acks), 'idle active PIRQ route truly suppresses high ISA pin until disabled')
    check(any(a['irq'] == 0 and suppressed_start <= a['at']['instructions'] < suppressed_end for a in ledger['acks']), 'real physical PIT acknowledgement during ISA suppression')
    boundaries = ledger['boundaries']
    check(len(boundaries) == 24, 'actual entry/commit/IRET boundaries for each of eight handlers')
    for index in range(8):
        points += certify_frame(boundaries[index * 3:index * 3 + 3], index, proof, table, base, mode, cut, final, ledger['ports'])
        check(acks[index]['at']['instructions'] <= boundaries[index * 3]['before']['instructions'], 'physical PIC acknowledgement precedes actual own handler')
    pci = [p for p in ledger['ports'] if 0xCF8 <= p['address'] <= 0xCFF]
    expected_pci = pci_transactions()
    check(len(pci) == len(expected_pci), 'all actual finite guest PCI transactions')
    for row, expected in zip(pci, expected_pci):
        certify_port(row, expected, final)
    elcr = [p for p in ledger['ports'] if p['address'] in [0x4D0, 0x4D1]]
    expected_elcr = [(0x4D0, data[20], 1, False), (0x4D0, 255, 1, True), (0x4D0, 0xF8, 1, False),
                     (0x4D1, data[21], 1, False), (0x4D1, 255, 1, True), (0x4D1, 0xDE, 1, False),
                     (0x4D1, data[21] | 12, 1, True), (0x4D0, data[20], 1, True), (0x4D0, data[20], 1, True), (0x4D1, data[21], 1, True)]
    check(len(elcr) == len(expected_elcr), 'all actual ELCR transfers')
    for row, expected in zip(elcr, expected_elcr):
        certify_port(row, expected, final)
    for family in ['Ports', 'Acks', 'Boundaries', 'Pins', 'Phases']:
        check(ledger['future' + family] == ledger['restored' + family], 'every actual future ' + family + ' publication identical')
    raw_text = certify_text(final, final_ram, result['finalScreen'])
    check('PIIX-IRQ-DONE' in result['finalScreen'] and 'PIIX-IRQ-FAILED' not in result['finalScreen'], 'actual own success and following raw DOS prompt')
    negatives = 0
    # Every mutation targets an independent explicit contract, never a recomputation using device code.
    for field in ['passed', 'returnedToDos', 'exitA', 'exitB', 'comparisons', 'futureSteps', 'capturedStateSha256', 'capturedRamSha256', 'finalStateSha256', 'finalRamSha256', 'programSha256', 'dma', 'pcmFrames']:
        changed = dict(result)
        changed[field] = False if isinstance(changed[field], bool) else 0 if isinstance(changed[field], int) else 'bad'
        try:
            common(changed, cut, final, manifest, hashes, saved_media['machine']['hardware'])
        except (AssertionError, ValueError):
            negatives += 1
        else:
            raise AssertionError('corrupted actual full-container result accepted')
    for address in [base, base - 16, base - 15, base - 13, base + 0x100]:
        changed = bytearray(ram)
        changed[address] ^= 1
        try:
            ownership(changed, program, table, base, cpu, cut['components']['memory']['fields']['A20Enabled'])
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted actual DOS ownership/code accepted')
    for offset in list(range(8)) + [8, 10, 12, 14, 16, 22, 23, 24, 28, 32, 38, 52, 54, 56, 60] + list(range(192, 200)):
        changed = bytearray(proof)
        changed[offset] ^= 1
        try:
            certify_proof(changed, mode, table, base)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted actual fixed proof byte accepted')
    for address, value in [(owned['jftBase'] + owned['openFileHandle'], 255), (base + table[2] + 2, 255)]:
        changed = bytearray(ram)
        changed[address] = value
        try:
            ownership(changed, program, table, base, cpu, cut['components']['memory']['fields']['A20Enabled'])
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted actual open DOS JFT/handle accepted')
    for index in range(8):
        for offset in [0, 2, 4, 6, 8, 12, 14]:
            changed = bytearray(proof)
            changed[64 + index * 16 + offset] ^= 1
            try:
                certify_proof(changed, mode, table, base)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted actual IRQ/return frame accepted')
    for index, row in enumerate(ledger['pins']):
        for field in ['pirqs', 'isa', 'nanoseconds', 'counter', 'ticks', 'epoch']:
            changed = copy.deepcopy(row)
            changed['after'][field] ^= 1
            try:
                certify_pin(changed, index, cut, final)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted independent pin/clock accepted')
    for row, expected in zip(pci + elcr, expected_pci + expected_elcr):
        changed = copy.deepcopy(row)
        changed['value'] ^= 1
        try:
            certify_port(changed, expected, final)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted exact PCI/ELCR publication accepted')
    for index in range(8):
        for where in [0, 2]:
            changed = copy.deepcopy(boundaries[index * 3:index * 3 + 3])
            value = bytearray(decode(changed[where]['frame']))
            value[0] ^= 1
            changed[where]['frame'] = base64.b64encode(value).decode('ascii')
            try:
                certify_frame(changed, index, proof, table, base, mode, cut, final, ledger['ports'])
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted actual entry/IRET frame accepted')
    return dict(passed=True, negativeOracles=negatives, frameCount=8, irq10=6, irq11=2, pins=12, pciTransactions=len(pci), elcrTransactions=len(elcr),
                reconstructedPoints=points, comparisons=result['comparisons'], pcmFrames=result['pcmFrames'], programBase=base, system=system, guestV86=bool(mode),
                ownership=owned, rawFileHex=expected_raw.hex().upper(), proofFileSha256=hashlib.sha256(proof).hexdigest().upper(), rawText=raw_text)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--program', type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.program), indent=2))