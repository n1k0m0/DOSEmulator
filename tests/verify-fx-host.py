# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent actual 82437FX PCI/PAM byte, 100-MHz clock, DOS/VCPI and full-container oracles."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('p54c_console_oracle', Path(__file__).with_name('verify-p54c-pc.py'))
console = importlib.util.module_from_spec(spec)
spec.loader.exec_module(console)
media = console.media
MASK = (1 << 64) - 1
FREQUENCY = 100_000_000
RAM_BYTES = 32 * 1048576


def check(value, reason):
    """Reject a missing actual guest effect or independently inconsistent artifact."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named private JSON document without directory traversal."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Hash exactly one named non-personal file."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def decode(value):
    """Decode one strictly validated byte-array observation."""
    return base64.b64decode(value, validate=True)


def metadata(program):
    """Read the own assembler's unique fixed label map, not emulator implementation data."""
    check(program.count(b'FXMETA01') == 1, 'unique own assembler metadata')
    return struct.unpack_from('<12H', program, program.index(b'FXMETA01') + 8)


def host(state):
    """Select exactly one registered 82437FX state component by its explicit reviewed type."""
    found = [block['fields'] for block in state['components'].values() if block['type'].endswith('Intel82437Fx')]
    check(len(found) == 1, 'one actual host bridge component')
    return found[0]


def ticks(point, anchor):
    """Reconstruct integer virtual tick phase from the persisted four-clock scheduler remainder."""
    clock = anchor['scheduler']
    cpu = anchor['components']['cpu']['fields']
    delta = cpu['Instructions'] - point['instructions']
    check(delta >= 0 and clock['cachedFrequency'] == FREQUENCY, 'actual fixed-frequency anchor')
    remainder = (clock['remainder'] - delta * 4 * 1_000_000_000) % FREQUENCY
    value = ((point['nanoseconds'] * FREQUENCY + remainder) // 1_000_000_000) & MASK
    check(point['ticks'] == value and point['counter'] == (value - point['epoch']) & MASK, 'exact integer clock/counter phase')
    return value


def ownership(ram, program, table, base, cpu):
    """Prove actual DOS MCB ownership, private disjoint allocations, stack/table extents and client mappings."""
    check(base & 15 == 0 and 16 <= base < 0xA0000 and ram[base:base + 2] == b'\xcd\x20', 'actual PSP signature/alignment')
    psp = base >> 4
    header = ram[base - 16:base]
    check(header[0] in [ord('M'), ord('Z')] and struct.unpack_from('<H', header, 1)[0] == psp, 'actual own COM MCB owner/type')
    paragraphs = struct.unpack_from('<H', header, 3)[0]
    check(paragraphs == (table[6] + 15) // 16, 'AH4A retains exactly complete COM/tables/stacks')
    end = base + paragraphs * 16
    check(base + table[6] <= end <= 0xA0000, 'complete own COM extent')
    data = base + table[0]
    buffer = struct.unpack_from('<I', ram, data + 16)[0]
    check(buffer & 15 == 0 and 16 <= buffer and buffer + 65536 + 16 <= 0xA0000, 'actual conventional AH48 extent')
    buffer_header = ram[buffer - 16:buffer]
    check(buffer_header[0] in [ord('M'), ord('Z')] and struct.unpack_from('<HH', buffer_header, 1) == (psp, 0x1000), 'exact 64-KiB AH48 MCB with own PSP owner')
    check(end <= buffer - 16 or buffer + 65536 <= base - 16, 'actual COM and readback allocations disjoint')
    successor = ram[buffer + 65536:buffer + 65536 + 16]
    check(successor[0] in [ord('M'), ord('Z')] and struct.unpack_from('<H', successor, 1)[0] == 0, 'intact free DOS successor MCB guards full buffer end')
    check(0 < struct.unpack_from('<H', successor, 3)[0] <= (0xA0000 - buffer - 65536 - 16) // 16, 'actual successor free-chain extent')
    stack_top = program.index(b'FXMETA01') + 0x100
    check(0x100 <= stack_top - 2048 < stack_top <= table[9] - 4096 and table[9] + 12288 + 4095 == table[6], 'complete static real/PM stack and page-table reservations')
    check(base + table[9] - 4096 <= cpu['_registers'][4] < base + table[9], 'actual private PM ESP inside own DOS allocation')
    mode = struct.unpack_from('<I', ram, data + 8)[0]
    if mode:
        pd = struct.unpack_from('<I', ram, base + table[8])[0]
        check(cpu['Cr3'] == pd and base <= pd and pd + 12288 <= end and pd & 4095 == 0, 'actual own shared CR3/table storage')
        # Walk actual private tables; neither a host mapper nor monitor page edits can substitute for them.
        pages = set(range(base & ~4095, (end + 4095) & ~4095, 4096))
        pages.update(range(buffer & ~4095, (buffer + 65536 + 4095) & ~4095, 4096))
        for address in pages:
            pde = struct.unpack_from('<I', ram, pd + (address >> 22) * 4)[0]
            check(pde & 1 and not pde & 0x80 and (pde & 0xFFFFF000) <= len(ram) - 4096, 'actual classic private PDE')
            pte = struct.unpack_from('<I', ram, (pde & 0xFFFFF000) + ((address >> 12) & 1023) * 4)[0]
            check(pte & 3 == 3 and pte & 0xFFFFF000 == address, 'actual identity backing for each owned conventional page')
        pde0, pde1 = struct.unpack_from('<II', ram, pd)
        check(not any(ram[pd + 8:pd + 4096]), 'private page directory has no unintended PDE2..1023 mappings')
        check(pde0 & 0xFFFFF000 == pd + 4096 and pde1 & 0xFFFFF000 == pd + 8192 and pde1 & 3 == 3, 'actual separate owned PDE/PT for PAM aliases')
        supplied = base + table[11]
        check(base <= supplied and supplied + 4096 <= end, 'original server PTE backup belongs to DOS allocation')
        for index in range(1024):
            original = struct.unpack_from('<I', ram, supplied + index * 4)[0]
            current = struct.unpack_from('<I', ram, pd + 4096 + index * 4)[0]
            check(original & ~0x60 == current & ~0x60 and current & original == original, 'all original DE01 PTEs preserved except newly set actual A/D bits')
        for index in range(64):
            pte = struct.unpack_from('<I', ram, pd + 8192 + index * 4)[0]
            check(pte & 0xFFFFF000 == 0xC0000 + index * 4096 and pte & 3 == 3, 'actual separate virtual PAM alias has exact physical backing')
        check(not any(ram[pd + 8192 + 256:pd + 12288]), 'private alias table maps no unrequested physical pages')
    return dict(pspSegment=psp, comParagraphs=paragraphs, comEnd=end, bufferBase=buffer,
                bufferParagraphs=0x1000, bufferEnd=buffer + 65536, successorMcbHex=bytes(successor).hex().upper())


def expected_config():
    """Construct the named A2 primary reset bytes plus the own firmware's actual 32-MiB DRB initialization."""
    data = bytearray(256)
    for address, value in {0: 0x86, 1: 0x80, 2: 0x2D, 3: 0x12, 4: 6, 7: 2, 8: 2, 11: 6, 0x52: 2, 0x57: 1, 0x72: 2}.items():
        data[address] = value
    data[0x60:0x65] = bytes([8] * 5)
    return data


def config_transfers():
    """Generate every expected actual CF8/lane transaction from the documented own guest sequence."""
    data = expected_config()
    rows = []
    for address in list(range(0, 0x40, 4)) + [0x60, 0x64, 0x58, 0x5C]:
        rows.extend([(0xCF8, 0x80000000 | address, 4, True), (0xCFC, int.from_bytes(data[address:address + 4], 'little'), 4, False)])
    rows.extend([(0xCF8, 0x80000800, 4, True), (0xCFC, 0xFFFFFFFF, 4, False),
                 (0xCF8, 0x80000004, 4, True), (0xCFC, 0x22000006, 4, False),
                 (0xCFE, 0x2000, 2, True), (0xCFC, 0x02000006, 4, False)])
    for number in range(13):
        address = 0x59 if number == 0 else 0x5A + (number - 1) // 2
        shift = 4 if number == 0 or number % 2 == 0 else 0
        for value in [0, 2, 1, 2, 1, 3, 7, 2, 0]:
            rows.extend([(0xCF8, 0x80000000 | (address & ~3), 4, True),
                         (0xCFC + (address & 3), value << shift, 1, True),
                         (0xCFC + (address & 3), value << shift, 1, False)])
    return rows


def certify_port(row, expected, anchor):
    """Check one actual bus publication, including exact width, value, direction and virtual clock."""
    check((row['address'], row['value'], row['width'], row['write']) == expected, 'actual exact PCI transaction/lane value')
    check(row['at']['cr0'] & 1 and not row['at']['v86'], 'actual private protected client owns config I/O')
    ticks(row['at'], anchor)


def certify_bank(row, number, phase, firmware, low_guard, high_guard, anchor):
    """Check every guest-read byte, hidden backing byte and adjacent guard without invoking VM memory logic."""
    address, count = (0xF0000, 65536) if number == 0 else (0xC0000 + (number - 1) * 16384, 16384)
    check((row['number'], row['phase'], row['address'], row['length']) == (number, phase, address, count), 'actual complete bank/phase extent')
    shift = 4 if number == 0 or number % 2 == 0 else 0
    attributes = [0, 2, 1, 2, 1, 1, 3, 7, 0][phase]
    check(row['config'] == attributes << shift, 'actual PAM nibble/lane readback')
    external = firmware if number == 0 else bytes([255]) * count
    seeds = {3: 0x31, 4: 0x31, 5: 0x31, 6: 0x79, 7: 0xC7}
    pattern = bytes(((i * 13 + number * 17 + seeds[phase]) & 255) for i in range(256)) * (count // 256) if phase in seeds else None
    expected_read = external if phase in [0, 1, 2, 3, 8] else pattern
    expected_hidden = bytes(count) if phase in [0, 8] else external if phase in [1, 2] else pattern
    check(decode(row['readback']) == expected_read, 'every actual external/shadow readback byte')
    check(decode(row['hidden']) == expected_hidden, 'every independently observed hidden DRAM byte')
    check(decode(row['lowGuard']) == (low_guard if address == 0xC0000 else bytes(16)), 'low physical RAM guard')
    check(decode(row['highGuard']) == (high_guard if address + count == 0x100000 else bytes(16)), 'high physical RAM guard')
    check(0 < row['buffer'] <= 0xA0000 - 65536 and not row['at']['v86'] and row['at']['cr0'] & 1, 'own conventional DOS buffer/private client')
    registers = row['at']['registers']
    check(registers[1] == 0 and registers[6] == address + count + (0x340000 if row['at']['cr0'] & 0x80000000 else 0) and registers[7] == row['buffer'] + count, 'actual REP completed exact source/destination extent')
    ticks(row['at'], anchor)
    return count


def common(work, result, state, manifest, hashes):
    """Certify complete strict board construction, exact schema, physical decode and container identities."""
    check(result['passed'] and result['returnedToDos'] and result['exitA'] and result['exitB'] and result['comparisons'] > 1 and result['futureSteps'] > 10000,
          'actual complete DOS continuation and return')
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and manifest['ramBytes'] == RAM_BYTES, 'full 32-MiB format1')
    check(manifest['ramSha256'] == hashes['ram'] == result['capturedRamSha256'], 'entire captured RAM hash')
    check(manifest['stateSha256'] == hashes['state'] == result['capturedStateSha256'], 'entire captured graph hash')
    check(manifest['mediaSha256'] == hashes['media'] and manifest['stateBytes'] == hashes['stateBytes'], 'sealed media and exact graph size')
    check(result['finalStateSha256'] == hashes['finalState'] and result['finalRamSha256'] == hashes['finalRam'], 'complete final identities')
    check(result['firmwareSha256'] == hashes['firmware'] and result['programSha256'] == hashes['program'], 'exact own firmware/program identities')
    check(result['dma'] == 512 and result['pcmFrames'] > 480 and len(result['pcmSha256']) == 64, 'actual complete PCM/DMA future')
    check(state['scheduler']['now'] == manifest['virtualNanoseconds'], 'shared common virtual boundary')
    cpu = state['components']['cpu']['fields']
    check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == FREQUENCY and cpu['Cr4'] & ~12 == 0, 'strict constructor-fixed P54C/100-MHz subset')
    check(cpu['Cr0'] & 1 and not cpu['Eflags'] & 0x20000, 'actual own protected client at cut')
    check(state['components']['memory']['fields']['DecodedRamBytes'] == RAM_BYTES, 'actual DRB CPU memory decoder')
    config = decode(host(state)['_config'])
    expected = expected_config()
    expected[0x59] = 0x20
    check(config == expected, 'all actual bridge bytes at WE-only cut')
    check(state['components']['soundBlaster16']['fields']['RemainingSamples'] > 0 if 'soundBlaster16' in state['components'] else any(
        block['fields'].get('RemainingSamples', 0) > 0 for block in state['components'].values() if block['type'].endswith('SoundBlaster16')), 'actual active PCM block at cut')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    for block in state['components'].values():
        check(block['type'] in schema and block['version'] == 1, 'registered component type/version')
        fields = {field['name'] for field in schema[block['type']]['fields'] if field['mode'] not in ['children', 'excluded']}
        check(set(block['fields']) == fields, 'every reviewed saved/invariant field exactly once')
    return cpu


def certify_timestamps(rows, cut, final, table):
    """Check both real RDTSC effects, preserved GPRs/flags and exact integer virtual source."""
    check(len(rows) == 2 and [row['offset'] for row in rows] == [table[3], table[4]], 'two actual own timestamp instructions')
    for index, row in enumerate(rows):
        before, after = row['before'], row['after']
        anchor = cut if index == 0 else final
        ticks(before, anchor)
        ticks(after, anchor)
        check(before['epoch'] == after['epoch'] == 0, 'unchanged reset-anchored virtual epoch')
        check((after['registers'][2] << 32 | after['registers'][0]) == before['counter'], 'actual RDTSC value committed to full EDX:EAX')
        check(before['flags'] == after['flags'] and all(before['registers'][i] == after['registers'][i] for i in [1, 3, 4, 5, 6, 7]), 'actual RDTSC preserved registers/flags')
    check(rows[1]['before']['counter'] > rows[0]['before']['counter'], 'actual future counter advances')


def certify_files(work, data, mode):
    """Independently replay sealed DOSCOW records and walk original DOS FAT for real open/write/close effects."""
    root = work / 'evidence'
    saved = root / 'cut.dossav/media.dosemedia'
    record = read(saved / 'media.json')['machine']['hardDisks'][0]
    check(media.Disk(saved / record['imagePath'], saved / record['overlayPath']).file(b'FXHOST  DAT') == b'FXPROOF1', 'actual open own DOS file/header at cut')
    raw = b'SNAPRAW1' + struct.pack('<QH', 0xC90FDAA22168C235, 0x4000) + struct.pack('<QH', 0xFEDCBA9876543210, 0x4001) + struct.pack('<HH', 0x037F, 0)
    for name in ['final-a.dosemedia', 'final-b.dosemedia']:
        directory = root / name
        record = read(directory / 'media.json')['machine']['hardDisks'][0]
        disk = media.Disk(directory / record['imagePath'], directory / record['overlayPath'])
        check(disk.file(b'SNAP    DAT') == raw, 'actual DOS raw 80-bit x87 file after return')
        actual = disk.file(b'FXHOST  DAT')
        check(actual[:160] == data[:160] and actual[160:176] == data[160:176] and actual[180:] == data[180:], 'actual DOS file preserves all stable pre-cut proof bytes')
        check(struct.unpack_from('<I', actual, 176)[0] == 1 and struct.unpack_from('<I', actual, 232)[0] == 1 and actual[-8:] == b'FXEND001', 'actual real/VCPI return and DOS file completed')
        check(struct.unpack_from('<I', actual, 8)[0] == mode and struct.unpack_from('<I', actual, 236)[0] == 0, 'no guest fallback/fault')
    check(read(root / 'final-a.dosemedia/media.json')['media'] == read(root / 'final-b.dosemedia/media.json')['media'], 'all final journal generations equal')
    return raw.hex().upper()


def verify(work):
    """Certify actual full-PC files and reject independent bank/config/clock/identity corruptions."""
    work = media.safe(work)
    root = work / 'evidence'
    result = read(root / 'result.json')
    cut = read(root / 'cut.dossav/state.json')
    final = read(root / 'final-state.json')
    manifest = read(root / 'cut.dossav/snapshot.json')
    ledger = read(root / 'ledger.json')
    program = media.safe(read(work / 'input.json')['probe']).read_bytes()
    firmware = (root / 'firmware.bin').read_bytes()
    table = metadata(program)
    ram = (root / 'cut.dossav/ram.bin').read_bytes()
    final_ram = (root / 'final-ram.bin').read_bytes()
    check(len(ram) == len(final_ram) == RAM_BYTES and len(firmware) == 65536, 'actual entire RAM/ROM lengths')
    hashes = dict(ram=sha(root / 'cut.dossav/ram.bin'), state=sha(root / 'cut.dossav/state.json'),
                  stateBytes=(root / 'cut.dossav/state.json').stat().st_size, media=sha(root / 'cut.dossav/media.dosemedia/media.json'),
                  finalState=sha(root / 'final-state.json'), finalRam=sha(root / 'final-ram.bin'), firmware=sha(root / 'firmware.bin'),
                  program=hashlib.sha256(program).hexdigest().upper())
    cpu = common(work, result, cut, manifest, hashes)
    base = result['programBase']
    check(base == ledger['programBase'] and base & 15 == 0 and 0 < base < 0xA0000, 'actual own DOS program base')
    check(ram[base + 0x100:base + table[10]] == program[:table[10] - 0x100], 'every unchanged own executable byte')
    owned = ownership(ram, program, table, base, cpu)
    data = ram[base + table[0]:base + table[1]]
    check(len(data) == 248 and data[:8] == b'FXPROOF1' and data[-8:] == b'FXEND001', 'actual own proof extent/guards')
    mode = struct.unpack_from('<I', data, 8)[0]
    check(struct.unpack_from('<I', data, 220)[0] == (0x340000 if mode else 0), 'explicit separate physical PAM linear alias bias')
    check(mode in [0, 1] and struct.unpack_from('<I', data, 12)[0] == base, 'actual SMSW mode and own code base')
    check(struct.unpack_from('<I', data, 40)[0] == 1 and data[44:56] == b'GenuineIntel' and struct.unpack_from('<II', data, 56) == (0x524, 0x135), 'actual CPUID subset values')
    initial = expected_config()
    check(data[64:128] == initial[:64] and data[128:136] == initial[0x60:0x68] and data[136:144] == initial[0x58:0x60], 'actual initial PCI/BDF/A2/DRB/PAM bytes')
    check(struct.unpack_from('<I', data, 212)[0] == 0x22000006 and struct.unpack_from('<I', data, 216)[0] == 0x02000006, 'actual master-abort and status-only W1C')
    check(struct.unpack_from('<I', data, 160)[0] & 0x80000001 == (0x80000001 if mode else 1), 'actual original VCPI paging or own real-mode PE client')
    check(struct.unpack_from('<II', data, 172) == (1, 0), 'actual one protected entry and no early fabricated return')
    if not mode:
        check(struct.unpack_from('<I', data, 224)[0] == 31744, 'actual 32-MiB firmware AH88 inventory')
    profile = read(root / 'cut.dossav/media.dosemedia/media.json')['machine']['hardware']
    check(profile['id'] == 'dose-430fx-host-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == FREQUENCY and profile['ramMiB'] == 32, 'strict full-PC profile identity')
    low_guard, high_guard = decode(result['lowBoundaryGuard']), decode(result['highBoundaryGuard'])
    check(len(low_guard) == len(high_guard) == 16, 'explicit actual RAM guard extents')
    check(ledger['beforeCutBanks'] == 3 and ledger['beforeCutTimestamps'] == 1 and len(ledger['banks']) == 117, 'actual WE-only cut phase')
    check(program[table[2] - 0x100] == 0x90, 'actual static one-byte NOP at REP-completion observation label')
    # InstructionStarting publishes after consuming the first opcode byte; EIP is therefore label+1 here.
    check(all(row['buffer'] == owned['bufferBase'] and row['at']['registers'][5] == base and row['at']['eip'] == table[2] + 1 and
              base + table[9] - 4096 <= row['at']['registers'][4] < base + table[9] for row in ledger['banks']), 'all actual readbacks use own DOS buffer/stack and static REP completion label')
    byte_count = sum(certify_bank(row, index // 9, index % 9, firmware, low_guard, high_guard, cut if index < 3 else final)
                     for index, row in enumerate(ledger['banks']))
    expected_ports = config_transfers()
    check(len(ledger['ports']) == len(expected_ports), 'all actual PCI transactions, no hidden access')
    for index, (row, expected) in enumerate(zip(ledger['ports'], expected_ports)):
        certify_port(row, expected, cut if index < ledger['beforeCutPorts'] else final)
    check(ledger['futureBanks'] == ledger['restoredFutureBanks'] and ledger['futurePorts'] == ledger['restoredFuturePorts'] and
          ledger['futureTimestamps'] == ledger['restoredFutureTimestamps'], 'every future actual publication restored')
    certify_timestamps(ledger['timestamps'], cut, final, table)
    check(struct.unpack_from('<Q', data, 144)[0] == ledger['timestamps'][0]['before']['counter'], 'actual guest timestamp bytes')
    # DOS uses this same reserved proof; mutable phase/return/future fields are checked separately below.
    final_record = read(root / 'final-a.dosemedia/media.json')['machine']['hardDisks'][0]
    final_data = media.Disk(root / 'final-a.dosemedia' / final_record['imagePath'], root / 'final-a.dosemedia' / final_record['overlayPath']).file(b'FXHOST  DAT')
    expected_data = bytearray(data)
    expected_data[20:40] = final_data[20:40]
    expected_data[152:160] = final_data[152:160]
    struct.pack_into('<I', expected_data, 176, 1)
    struct.pack_into('<I', expected_data, 232, 1)
    check(struct.unpack_from('<III', final_data, 20) == (13, 8, 0xEC000), 'actual completed last bank/phase')
    check(struct.unpack_from('<I', final_data, 32)[0] == 16384 and struct.unpack_from('<I', final_data, 36)[0] == 0, 'actual final bank size and restored PAM')
    check(struct.unpack_from('<Q', final_data, 152)[0] == ledger['timestamps'][1]['before']['counter'], 'actual later DOS timestamp bytes')
    raw_file = certify_files(work, bytes(expected_data), mode)
    check(final_ram[0xC0000:0x100000] == bytes(0x40000), 'all hidden physical shadow bytes restored')
    check(decode(host(final)['_config']) == initial, 'all original actual bridge configuration restored')
    raw_text = console.certify_text(final, final_ram, result['finalScreen'])
    negatives = 0
    changed_ram = bytearray(ram)
    for address in [base, base - 16, base - 15, base - 13, owned['bufferBase'] - 16, owned['bufferBase'] - 15, owned['bufferBase'] - 13, owned['bufferEnd']]:
        changed_ram[address] ^= 1
        try:
            ownership(changed_ram, program, table, base, cpu)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted independent DOS ownership/guard accepted')
        changed_ram[address] ^= 1
    # Each bank's first, middle and last observed byte and backing/guard identity must matter independently.
    for index, row in enumerate(ledger['banks']):
        anchor = cut if index < 3 else final
        for field in ['readback', 'hidden', 'lowGuard', 'highGuard']:
            positions = [0, row['length'] // 2, row['length'] - 1] if field in ['readback', 'hidden'] else [0, 15]
            for position in positions:
                changed = dict(row)
                payload = bytearray(decode(row[field]))
                payload[position] ^= 1
                changed[field] = base64.b64encode(payload).decode('ascii')
                try:
                    certify_bank(changed, index // 9, index % 9, firmware, low_guard, high_guard, anchor)
                except (AssertionError, ValueError):
                    negatives += 1
                else:
                    raise AssertionError('corrupted independent bank byte/guard accepted')
    for index, row in enumerate(ledger['ports']):
        changed = dict(row)
        changed['value'] ^= 1
        try:
            certify_port(changed, expected_ports[index], cut if index < ledger['beforeCutPorts'] else final)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted independent exact PCI transaction accepted')
    for index, row in enumerate(ledger['timestamps']):
        for field in ['counter', 'epoch', 'ticks', 'nanoseconds']:
            changed = copy.deepcopy(ledger['timestamps'])
            changed[index]['before'][field] += 1000 if field == 'nanoseconds' else 1
            try:
                certify_timestamps(changed, cut, final, table)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted actual timestamp accepted')
    for field in ['passed', 'returnedToDos', 'exitA', 'exitB', 'comparisons', 'futureSteps', 'capturedStateSha256', 'capturedRamSha256', 'finalStateSha256', 'finalRamSha256', 'firmwareSha256', 'programSha256', 'dma', 'pcmFrames']:
        changed = dict(result)
        changed[field] = False if isinstance(changed[field], bool) else 0 if isinstance(changed[field], int) else 'bad'
        try:
            common(work, changed, cut, manifest, hashes)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted complete identity/result accepted')
    return dict(passed=True, negativeOracles=negatives, banks=13, bankReadbacks=117, actualReadBytes=byte_count,
                pciTransactions=len(expected_ports), reconstructedTimestamps=2, guestVcpi=bool(mode), ownership=owned, rawFileHex=raw_file, rawText=raw_text)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    print(json.dumps(verify(parser.parse_args().directory), indent=2))