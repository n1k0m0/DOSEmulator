# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent PIIX DMA/PRD/IRQ/data/clock and two-cut full-PC continuation oracles."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('piix_ide_console', REPO / 'tests/verify-p54c-pc.py')
console = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(console)
media = console.media
RAM_BYTES = 32 * 1048576
FREQUENCY = 100_000_000
TOTALS = [1024, 1024, 1024, 65536, 65536, 512]
LBAS = [8, 32, 32, 128, 512, 8]
COMMANDS = [0xC8, 0xCA, 0xC8, 0xC8, 0xCA, 0xC8]


def check(condition, why):
    """Reject inconsistent actual architecture, data, media, timing or identity."""
    if not condition:
        raise AssertionError(why)


def read(path):
    """Read only a named own JSON artifact."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def digest(path):
    """Hash one explicitly selected own evidence file."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def decode(value):
    """Decode strict artifact bytes without importing emulator code."""
    return base64.b64decode(value, validate=True)


def metadata(program):
    """Bind the own assembler labels to complete extents and actual opcodes."""
    check(program.count(b'IDEMETA1') == 1, 'unique own DMA metadata')
    table = struct.unpack_from('<21H', program, program.index(b'IDEMETA1') + 8)
    check(table[3] == len(program) + 256 and table[1] - table[0] == 328, 'complete own executable/proof extents')
    for index in [4, 5]:
        check(program[table[index] - 256:table[index] - 254] == bytes([0xE6, 0xE9]), 'actual own marker OUT')
    check(program[table[6] - 256:table[6] - 254] == bytes([0x66, 0x60]) and program[table[8] - 256] == 0xCF, 'actual PUSHAD/IRET opcodes')
    return table


def clock(point, anchor):
    """Reconstruct complete 100-MHz scheduler phase independently of the VM and host clock."""
    scheduler, cpu = anchor['scheduler'], anchor['components']['cpu']['fields']
    check(scheduler['cachedFrequency'] == FREQUENCY and scheduler['remainder'] == 0, 'persisted fixed-frequency phase')
    ticks = point['nanoseconds'] * FREQUENCY // 1_000_000_000
    check(point['nanoseconds'] <= scheduler['now'] and point['instructions'] <= cpu['Instructions'], 'point precedes full-state anchor')
    check(point['epoch'] == 0 and point['ticks'] == point['counter'] == ticks, 'independent complete integer TSC')
    return ticks


def ownership(ram, cpu, table, program, base, mode):
    """Prove actual MCB/PSP/JFT ownership, exact identity PTEs, complete private stack and DMA guards."""
    check(base & 15 == 0 and 16 <= base < 0xA0000 and ram[base:base + 2] == b'\xCD\x20', 'actual own PSP')
    paragraphs = (table[3] + 15) // 16
    mcb = ram[base - 16:base]
    check(mcb[0] in [77, 90] and struct.unpack_from('<HH', mcb, 1) == (base >> 4, paragraphs), 'complete own COM MCB owner/size')
    check(ram[base + 256:base + table[2]] == program[:table[2] - 256], 'all unchanged executable bytes')
    data = ram[base + table[0]:base + table[1]]
    allocation = struct.unpack_from('<H', data, 16)[0] << 4
    buffer = struct.unpack_from('<I', data, 20)[0]
    header = ram[allocation - 16:allocation]
    check(header[0] in [77, 90] and struct.unpack_from('<HH', header, 1) == (base >> 4, 0x4002), 'separate complete DMA MCB')
    check(struct.unpack_from('<HH', data, 56) == (base >> 4, 0x4002), 'guest observed actual DMA MCB')
    check(base + paragraphs * 16 <= allocation - 16 or allocation + 0x40020 <= base - 16, 'disjoint complete COM/DMA allocations')
    check(buffer & 0x1FFFF == 0 and allocation + 16 <= buffer and buffer + 131088 <= allocation + 0x40020 <= 0xA0000, 'aligned complete DMA/PRD window owned')
    check(ram[buffer - 16:buffer] == ram[buffer + 131072:buffer + 131088] == bytes([0xD6]) * 16, 'both private physical DMA boundary guards')
    check(struct.unpack_from('<H', data, 18)[0] << 4 == buffer, 'actual segment/physical window identity')
    check(struct.unpack_from('<IIHHI', data, 96) == (131072, 0, buffer >> 4, 0, buffer), 'actual entire contiguous unlocked-or-VDS DDS without bounce/remap')
    pages = set(range(base & ~4095, (base + paragraphs * 16 + 4095) & ~4095, 4096)) | set(range(buffer - 4096, buffer + 135168, 4096))
    for page in pages:
        if cpu['Cr0'] & 0x80000000:
            pde = struct.unpack_from('<I', ram, (cpu['Cr3'] & 0xFFFFF000) + (page >> 22) * 4)[0]
            check(pde & 1 and not pde & 0x80, 'original classic PDE')
            pte = struct.unpack_from('<I', ram, (pde & 0xFFFFF000) + ((page >> 12) & 1023) * 4)[0]
            check(pte & 7 == 7 and pte & 0xFFFFF000 == page, 'exact original user/writable physical identity PTE')
        check(console.linear(ram, cpu, page, 4096, True) == ram[page:page + 4096], 'every actual original mapped own byte')
    check(cpu['_segments'][1] == cpu['_segments'][2] == base >> 4 and cpu['_segmentCaches'][1]['base'] == cpu['_segmentCaches'][2]['base'] == base, 'actual waiting CS/SS')
    waiting = table[9]
    check(program[waiting - 258:waiting - 252] == bytes([0xFB, 0x90, 0xFA, 0xA1]) + struct.pack('<H', table[0] + 10),
          'actual own STI/NOP/CLI polling boundary bytes')
    check(cpu['_registers'][4] & 65535 == table[11] - 2 and cpu['Eip'] in [waiting - 2, waiting - 1, waiting, waiting + 1, waiting + 4, waiting + 8] and
          not cpu['Halted'], 'actual own private polling client/CALL cut stack')
    check(bool(cpu['Eflags'] & 0x20000) == bool(mode) and cpu['Cr0'] & 1 == mode, 'actual original real/V86 DMA polling client')
    handle = struct.unpack_from('<H', ram, base + table[16])[0]
    count, offset, segment = struct.unpack_from('<3H', ram, base + 0x32)
    jft = (segment << 4) + offset
    check(5 <= handle < count <= 255 and jft <= 0xA0000 - count and 3 <= console.linear(ram, cpu, jft + handle, 1, True)[0] < 0x80, 'live ordinary own DOS file handle/JFT')
    check(struct.unpack_from('<I', ram, base + table[14])[0] == buffer + 0x1F000, 'actual PRDT in owned final 4-KiB page')
    return dict(pspSegment=base >> 4, comParagraphs=paragraphs, allocation=allocation, buffer=buffer, prdt=buffer + 0x1F000, fileHandle=handle, jftBase=jft)


def initial_bytes(lba, count, seed=0x31):
    """Specify every scratch byte independently from its sector and byte identities."""
    return bytes((sector * 13 + p * 17 + (p >> 8) * 7 + seed) & 255 for sector in range(lba, lba + count) for p in range(512))


def write_bytes():
    """Specify the complete own logical scatter write pattern."""
    return bytes((i * 29 + (i >> 8) * 11 + 0x5B) & 255 for i in range(1024))


def prds(phase, buffer):
    """Specify real physical PRDs and explicit 64-KiB zero-count meaning."""
    if phase <= 3:
        return [(buffer + 0x100, 510, False), (buffer + 0x9000, 514, True)]
    return [(buffer if phase <= 5 else buffer + 0x10100, 65536 if phase <= 5 else 512, True)]


def table_bytes(phase, buffer):
    """Specify raw descriptor words, including unchanged second entry after single-entry cases."""
    result = bytearray(struct.pack('<4I', buffer + 0x100, 510, buffer + 0x9000, 0x80000202))
    if phase >= 4:
        struct.pack_into('<II', result, 0, buffer if phase <= 5 else buffer + 0x10100, 0x80000000 if phase <= 5 else 0x80000200)
    return bytes(result)


def windows(buffer):
    """Reconstruct every complete actual physical window, including untouched data and PRD bytes."""
    value, result = bytearray([0xA5]) * 131072, []
    for phase in range(1, 8):
        if phase <= 6:
            value[0x1F000:0x1F010] = table_bytes(phase, buffer)
        if phase == 2:
            data = write_bytes()
            value[0x100:0x2FE], value[0x9000:0x9202] = data[:510], data[510:]
        if phase == 3:
            value[0x100:0x2FE], value[0x9000:0x9202] = bytes([0xA5]) * 510, bytes([0xA5]) * 514
        result.append((False, phase, bytes(value)))
        if phase in [1, 3]:
            data = initial_bytes(8, 2) if phase == 1 else write_bytes()
            value[0x100:0x2FE], value[0x9000:0x9202] = data[:510], data[510:]
        if phase == 4:
            value[:65536] = initial_bytes(128, 128)
        if phase == 6:
            value[0x10100:0x10300] = initial_bytes(8, 1)
        if phase <= 6:
            result.append((True, phase, bytes(value)))
    return result


def certify_marker(row, expected, table, base, final):
    """Check every actual DMA data/PRD/untouched byte and the real executed marker/clock."""
    complete, phase, window = expected
    check((row['complete'], row['phase']) == (complete, phase) and decode(row['window']) == window, 'actual complete DMA phase/data/PRD/guard bytes')
    p = row['at']
    check(p['phase'] == phase and p['bases'][1] == base and p['eip'] == table[5 if complete else 4] + 2, 'actual own ready/done OUT')
    clock(p, final)


def certify_proof(data, mode, table, base, completed=6):
    """Specify stable proof values and all six actual frames with separate ATA/BM ACK ordering."""
    check(len(data) == 328 and data[:8] == b'PIIXIDE1' and data[-8:] == b'IDEEND01', 'complete real DOS proof')
    check(struct.unpack_from('<H', data, 8)[0] == mode and struct.unpack_from('<II', data, 48) == (0x524, 0x135), 'actual mode/CPUID subset')
    check(data[44:47] == bytes([15, 255, 255]) and struct.unpack_from('<III', data, 32) == (0xC001, 0x12308086, 0x01018002), 'actual BAR/function/class/MIRQ/gates')
    check(struct.unpack_from('<H', data, 26)[0] == 3 and struct.unpack_from('<HH', data, 28) == (0x8000, 0x8000), 'actual firmware IOSE/DE with BME initially off')
    check(struct.unpack_from('<HH', data, 60) == (mode, mode if completed == 6 else 0), 'actual original VDS lock/unlock counts')
    check(data[82] == 0x50 and struct.unpack_from('<H', data, 66)[0] == 0, 'actual MDMA0 selection/no fallback')
    if completed == 6:
        check(struct.unpack_from('<HH', data, 10) == (7, 6) and struct.unpack_from('<H', data, 64)[0] == 1 and data[47] == 0x60, 'all six DMA effects/nIEN/actual DOS return')
    for index in range(completed):
        row = data[128 + index * 32:160 + index * 32]
        irq, phase, ordinal, ip, cs, flags, sp, ss = struct.unpack_from('<8H', row)
        check((irq, phase, ordinal, ip, cs, sp, ss) == (14, index + 1, index + 1, table[9], base >> 4, table[11] - 8, base >> 4), 'actual full DMA IRQ frame')
        check(flags & 0x200 and not flags & 0x100, 'actual HLT return IF/no TF')
        check(row[16:20] == bytes([0x64, 0x50, 0x64 if phase & 1 else 0x60, 0x60]), 'separate ATA status/BM W1C latch order')
        check(row[20] == (0 if phase & 1 else 0x50) and not any(row[21:]), 'nondestructive alternate status/record tail')
    check(not any(data[128 + completed * 32:320]), 'no unrecorded/seventh DMA IRQ')


def common(result, states, manifests, hashes, profile):
    """Require full build-bound snapshots, exact schema, model, installed RAM and actual future PCM."""
    check(result['passed'] is True and result['returnedToDos'] is True and result['exits'] == [True] * 3 and result['comparisons'] > 2 and result['futureSteps'] > 10000, 'two real complete restores and DOS returns')
    check(result['dma'] == 512 and result['audioFuture']['frames'] > 480 and len(result['audioFuture']['sha256']) == 64, 'actual complete future PCM DMA')
    check(profile['id'] == 'dose-430fx-piix-ide-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == FREQUENCY and profile['ramMiB'] == 32, 'strict IDE board/model/clock')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    for name, state, manifest in zip(['read', 'write'], states, manifests):
        check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and manifest['ramBytes'] == RAM_BYTES, 'full 32-MiB format1')
        check(manifest['stateSha256'] == result[name + 'StateSha256'] == hashes[name + 'State'] and manifest['ramSha256'] == result[name + 'RamSha256'] == hashes[name + 'Ram'], 'entire actual captured identity')
        check(manifest['stateBytes'] == hashes[name + 'StateBytes'] and manifest['mediaSha256'] == hashes[name + 'Media'] and state['scheduler']['now'] == manifest['virtualNanoseconds'], 'whole graph size/media seal/shared cut')
        cpu = state['components']['cpu']['fields']
        check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == FREQUENCY and cpu['_tscEpochTicks'] == 0 and cpu['Cr4'] & ~12 == 0, 'fixed actual CPU/clock/control subset')
        check(state['components']['memory']['fields']['DecodedRamBytes'] == RAM_BYTES, 'whole physical RAM decoder')
        for block in state['components'].values():
            check(block['type'] in schema and block['version'] == 1, 'registered raw type/version')
            fields = {f['name'] for f in schema[block['type']]['fields'] if f['mode'] not in ['children', 'excluded']}
            check(set(block['fields']) == fields, 'every saved/invariant field exactly once')
        sb = next(v['fields'] for v in state['components'].values() if v['type'].endswith('SoundBlaster16'))
        check(sb['RemainingSamples'] > 0 and any(e['owner'] == 'pit' and e['time'] > state['scheduler']['now'] for e in state['scheduler']['events']), 'actual active PCM/future PIT at both cuts')
    check(result['finalStateSha256'] == hashes['finalState'] and result['finalRamSha256'] == hashes['finalRam'] and result['programSha256'] == hashes['program'], 'entire actual final/program identity')


def disk(directory, slot=0):
    """Independently replay exactly the selected sealed original DOSCOW disk."""
    record = next(d for d in read(directory / 'media.json')['machine']['hardDisks'] if d['slot'] == slot)
    return media.Disk(directory / record['imagePath'], directory / record['overlayPath'] if record.get('overlayPath') else None)


def fat_file(drive, filename):
    """Walk original FAT12/16 for one own bounded complete 64-KiB file, without a VM file helper."""
    boot, start = drive.sector(0), 0
    if struct.unpack_from('<H', boot, 11)[0] != 512:
        starts = [struct.unpack_from('<I', boot, 446 + i * 16 + 8)[0] for i in range(4) if boot[450 + i * 16]]
        check(starts, 'actual DOS partition')
        start = starts[0]
        boot = drive.sector(start)
    bps, spc, reserved, fats, roots = struct.unpack_from('<HBHBH', boot, 11)
    total = struct.unpack_from('<H', boot, 19)[0] or struct.unpack_from('<I', boot, 32)[0]
    fat_sectors = struct.unpack_from('<H', boot, 22)[0]
    root_sectors = (roots * 32 + 511) // 512
    data_start = reserved + fats * fat_sectors + root_sectors
    clusters = (total - data_start) // spc
    check(bps == 512 and spc > 0 and spc & (spc - 1) == 0 and 0 < clusters < 65525 and start + total <= drive.sectors, 'actual FAT12/16 geometry')
    directory = b''.join(drive.sector(start + reserved + fats * fat_sectors + i) for i in range(root_sectors))
    entry = next((directory[i:i + 32] for i in range(0, len(directory), 32) if directory[i] not in [0, 0xE5] and directory[i:i + 11] == filename and not directory[i + 11] & 0x18), None)
    check(entry is not None, 'actual whole own DOS file')
    cluster, size = struct.unpack_from('<HI', entry, 26)
    check(size <= 65536, 'own bounded complete readback file')
    fat = b''.join(drive.sector(start + reserved + i) for i in range(fat_sectors))
    seen, result = set(), bytearray()
    while len(result) < size:
        check(2 <= cluster < clusters + 2 and cluster not in seen, 'finite actual FAT chain')
        seen.add(cluster)
        result.extend(b''.join(drive.sector(start + data_start + (cluster - 2) * spc + i) for i in range(spc)))
        if clusters < 4085:
            value = struct.unpack_from('<H', fat, cluster + cluster // 2)[0]
            cluster = value >> 4 if cluster & 1 else value & 0xFFF
        else:
            cluster = struct.unpack_from('<H', fat, cluster * 2)[0]
    return bytes(result[:size])


def cut_contract(state, ram, table, base, owned, phase):
    """Check actual partial PRD/sector contents and the persisted future grant/commit event."""
    channels = [(k, v['fields']) for k, v in state['components'].items() if v['type'].endswith('Intel82371FbIde.Channel')]
    check(len(channels) == 2, 'both explicit BM channels saved')
    key, c = next((k, v) for k, v in channels if k.endswith('/0'))
    source, buffer = state['components']['ata0/_drives/1']['fields'], owned['buffer']
    check(c['Command'] == (9 if phase == 1 else 1) and c['Status'] == 0x61 and c['Prdt'] == owned['prdt'] and c['Loaded'], 'actual active BM/PRD cut')
    check(c['BytesTransferred'] == (32 if phase == 1 else 512) and c['DescriptorAddress'] == owned['prdt'] + (0 if phase == 1 else 8) and
          c['RegionAddress'] == buffer + (0x100 if phase == 1 else 0x9000) and c['RegionLength'] == (510 if phase == 1 else 514) and
          c['RegionOffset'] == (32 if phase == 1 else 2) and c['Eot'] == (phase == 2), 'actual independently specified partial PRD cursor')
    check(source['_dmaDirection'] == phase and source['_offset'] == (32 if phase == 1 else 512) and source['_remaining'] == 2 and source['_lba'] == (8 if phase == 1 else 32), 'actual latched scratch ATA sector cursor')
    check(source['_dmaSelected'] and source['WrittenSectorCount'] == 0, 'actual selected MDMA0/no premature sector write')
    check(decode(source['_data']) == (initial_bytes(8, 1) if phase == 1 else write_bytes()[:512]), 'every real staged ATA sector byte')
    data = ram[base + table[0]:base + table[1]]
    check(struct.unpack_from('<HH', data, 10) == (phase, phase - 1) and struct.unpack_from('<H', data, 64)[0] == 0, 'actual incomplete phase/count at cut')
    certify_proof(data, int(bool(state['components']['cpu']['fields']['Eflags'] & 0x20000)), table, base, phase - 1)
    check(ram[owned['prdt']:owned['prdt'] + 16] == table_bytes(phase, buffer), 'every actual physical PRDT byte')
    window = bytearray(windows(buffer)[0 if phase == 1 else 2][2])
    if phase == 1:
        window[0x100:0x120] = initial_bytes(8, 1)[:32]
    check(ram[buffer:buffer + 131072] == window, 'entire actual partial-read/write-source window')
    events = state['scheduler']['events']
    if phase == 1:
        check(any(e['owner'] == key.split('/')[0] and e['operation'] == 'primary-burst' and e['time'] > state['scheduler']['now'] for e in events), 'real next BM grant persisted')
    else:
        check(source['Status'] == 0x80 and any(e['id'] == source['_event'] and e['owner'] == 'ata0/_drives/1' and e['operation'] == 'dma-write-commit' and e['time'] == state['scheduler']['now'] + 5000 for e in events), 'real future uncommitted full-sector write token/deadline')


def certify_programming(ports, buffer, final):
    """Bind exact real DMA task commands, physical PRDT writes and correctly directed START operations."""
    commands = [p for p in ports if p['write'] and p['address'] == 0x1F7 and p['value'] in [0xC8, 0xC9, 0xCA, 0xCB] and p['at']['phase'] > 0]
    check([(p['value'], p['width'], p['at']['phase']) for p in commands] == [(v, 1, i + 1) for i, v in enumerate(COMMANDS)], 'exact six real ATA DMA commands')
    pointers = [p for p in ports if p['write'] and p['address'] == 0xC004 and p['at']['phase'] > 0]
    check([(p['value'], p['width'], p['at']['phase']) for p in pointers] == [(buffer + 0x1F000, 4, i) for i in range(1, 7)], 'exact six complete physical PRDT register writes')
    starts = [p for p in ports if p['write'] and p['address'] == 0xC000 and p['value'] & 1 and p['at']['phase'] > 0]
    check([(p['value'], p['width'], p['at']['phase']) for p in starts] == [(9 if command == 0xC8 else 1, 1, i + 1) for i, command in enumerate(COMMANDS)], 'six actual BM direction/START writes')
    for row in commands + pointers + starts:
        clock(row['at'], final)


def certify_grants(points, ports, final, buffer):
    """Reconstruct all real nominal grants from independent PRD/sector boundaries and command deadlines."""
    total = 0
    for phase in range(1, 7):
        start = next(p for p in ports if p['write'] and p['address'] == 0x1F7 and p['value'] == COMMANDS[phase - 1] and p['at']['phase'] == phase)
        expected, transferred, desc, offset = [], 0, 0, 0
        deadline = start['at']['nanoseconds'] + 5000 + 7680
        descriptors = prds(phase, buffer)
        while transferred < TOTALS[phase - 1]:
            count = min(32, descriptors[desc][1] - offset, 512 - transferred % 512)
            transferred, offset = transferred + count, offset + count
            expected.append((transferred, deadline))
            if offset == descriptors[desc][1] and not descriptors[desc][2]:
                desc, offset = desc + 1, 0
            if transferred < TOTALS[phase - 1]:
                deadline += 7680 + (5000 if transferred % 512 == 0 else 0)
        actual, seen = [], 0
        for p in points:
            if p['phase'] != phase:
                continue
            c = p['dma']
            clock(p, final)
            if c['bytesTransferred'] > seen:
                actual.append((c['bytesTransferred'], p['nanoseconds']))
                seen = c['bytesTransferred']
                live = c['deviceIndex'] == 1 and c['direction'] == (1 if COMMANDS[phase - 1] == 0xC8 else 2)
                terminal = (c['bytesTransferred'] == TOTALS[phase - 1] and c['deviceIndex'] == -1 and c['direction'] == 0 and
                            c['sectorsRemaining'] == c['sectorOffset'] == 0 and not c['ataBusy'] and not c['dmaReady'] and
                            not c['writeCommitPending'] and not c['active'])
                check(not c['status'] & 2 and (live or terminal), 'DMA remains on scratch until exact terminal command release')
        check(actual == expected, 'all actual grant byte counts and exact own nominal deadlines')
        total += len(expected)
    return total


def certify_frame(rows, index, proof, table, base, mode, ports, final):
    """Bind a real IRQ14 frame, GPR/segment preservation, EOI order and completed real IRET."""
    check([r['kind'] for r in rows] == ['entry', 'commit', 'iret'], 'actual handler boundary triple')
    record = struct.unpack_from('<8H', proof, 128 + index * 32)
    expected = struct.pack('<3H', *record[3:6])
    first, commit, leave = rows
    check(decode(first['frame']) == decode(leave['frame']) == expected and not decode(commit['frame']), 'complete actual IRQ frame preserved')
    for row, label in zip(rows, table[6:9]):
        p = row['before']
        clock(p, final)
        check(row['offset'] == label and p['eip'] == label + (2 if row['kind'] == 'entry' else 1) and p['phase'] == index + 1 and p['v86'] == bool(mode), 'actual static handler instruction/mode')
        check(p['bases'][1] == p['bases'][2] == base and not p['flags'] & 0x200, 'actual own handler IF cleared')
    check(first['before']['registers'][4] & 65535 == table[11] - 8 and commit['before']['registers'][4] & 65535 == table[11] - 42, 'actual PUSHAD/DS stack extent')
    after = leave['after']
    check(after is not None and after['eip'] == table[9] and after['flags'] & 65535 == record[5] and after['v86'] == bool(mode), 'actual original real/V86 IRET')
    expected_registers = list(first['before']['registers'])
    expected_registers[4] = (expected_registers[4] & 0xFFFF0000) | (table[11] - 2)
    check(after['registers'] == expected_registers and after['segments'] == first['before']['segments'] and after['bases'] == first['before']['bases'], 'all actual GPR/segment/SP values restored')
    a, b = commit['before']['instructions'], leave['before']['instructions']
    eois = [p for p in ports if a <= p['at']['instructions'] < b and p['write'] and p['address'] in [0x20, 0xA0] and p['value'] == 0x20]
    check([(p['address'], p['width']) for p in eois] == [(0xA0, 1), (0x20, 1)], 'actual ordered slave/master EOIs')
    clock(after, final)


def text_contract(final, ram, reported):
    """Decode real DOSE VGA planes/CRTC/BDA independently of the reported DOS text."""
    v = final['components']['video']['fields']
    plane, crtc, gc = decode(v['_planes'][0]), decode(v['_crtc']), decode(v['_gc'])
    check(not v['IsEt4000'] and len(plane) == 65536 and not gc[6] & 1, 'actual DOSE VGA text plane')
    cols = crtc[1] + 1
    lines = crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + 1
    rows = (lines // (2 if crtc[9] & 0x80 else 1)) // ((crtc[9] & 31) + 1)
    check((cols, rows) == (80, 25) and ram[0x449] == 3 and struct.unpack_from('<H', ram, 0x44A)[0] == 80 and ram[0x484] == 24, 'actual CRTC and DOS BDA agree')
    start, stride = (crtc[12] << 8) | crtc[13], crtc[0x13] * 2
    check(stride >= cols, 'actual text stride')
    cells = bytes(plane[(start + r * stride + c) & 65535] for r in range(rows) for c in range(cols))
    text = '\n'.join(''.join(chr(v) if 32 <= v < 127 else ' ' for v in cells[r * cols:(r + 1) * cols]).rstrip() for r in range(rows)).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip() and 'PIIX-IDE-DONE' in text and 'PIIX-IDE-FAILED' not in text and text.endswith('A:\\>'), 'actual raw following DOS prompt')
    return hashlib.sha256(cells).hexdigest().upper()


def verify(directory, program=None):
    """Certify both real restores, actual data/files/IRQs and independent evidence corruption rejections."""
    directory = media.safe(directory)
    root = directory if (directory / 'result.json').exists() else directory / 'evidence'
    config = read(root.parent / 'input.json')
    program = media.safe(program or config['probe']).read_bytes()
    table, result, ledger = metadata(program), read(root / 'result.json'), read(root / 'ledger.json')
    states = [read(root / (name + '-cut.dossav/state.json')) for name in ['read', 'write']]
    manifests = [read(root / (name + '-cut.dossav/snapshot.json')) for name in ['read', 'write']]
    rams = [(root / (name + '-cut.dossav/ram.bin')).read_bytes() for name in ['read', 'write']]
    final, final_ram = read(root / 'final-state.json'), (root / 'final-ram.bin').read_bytes()
    check(all(len(r) == RAM_BYTES for r in rams + [final_ram]), 'all physical RAM bytes at both cuts/final')
    hashes = dict(finalState=digest(root / 'final-state.json'), finalRam=digest(root / 'final-ram.bin'), program=hashlib.sha256(program).hexdigest().upper())
    for name in ['read', 'write']:
        saved = root / (name + '-cut.dossav')
        hashes.update({name + 'State': digest(saved / 'state.json'), name + 'Ram': digest(saved / 'ram.bin'), name + 'StateBytes': (saved / 'state.json').stat().st_size, name + 'Media': digest(saved / 'media.dosemedia/media.json')})
    definitions = [read(root / (name + '-cut.dossav/media.dosemedia/media.json')) for name in ['read', 'write']]
    common(result, states, manifests, hashes, definitions[0]['machine']['hardware'])
    system = config['system']
    check(system in ['msdos', 'freedos', 'emm386-noems', 'emm386-ram'] and result['system'] == system, 'explicit original DOS/manager source')
    mode, base = int(system.startswith('emm386')), result['programBase']
    check(base == ledger['programBase'], 'same actual own COM base')
    owned = []
    for phase, state, ram, definition in zip([1, 2], states, rams, definitions):
        check(len(definition['machine']['hardDisks']) == 4 and len(definition['machine']['floppies']) == 2, 'all four IDE/two floppy positions saved')
        own = ownership(ram, state['components']['cpu']['fields'], table, program, base, mode)
        owned.append(own)
        cut_contract(state, ram, table, base, own, phase)
        directory = root / (('read' if phase == 1 else 'write') + '-cut.dossav/media.dosemedia')
        check(disk(directory).file(b'PIIXIDE DAT') == b'PIIXIDE1', 'real committed DOS header/live handle at both cuts')
        scratch = disk(directory, 1)
        check(scratch.records == 0 and not scratch.changed, 'no early scratch commit before full-sector deadline')
    check(owned[0] == owned[1], 'same actual DOS allocations/JFT/PRDT at both cuts')
    files, proof = {}, None
    expected_raw = b'SNAPRAW1' + struct.pack('<QH', 0xC90FDAA22168C235, 0x4000) + struct.pack('<QH', 0xFEDCBA9876543210, 0x4001) + struct.pack('<HH', 0x037F, 0)
    expected_disk = bytearray(initial_bytes(0, 4096))
    expected_disk[32 * 512:34 * 512] = write_bytes()
    expected_disk[512 * 512:640 * 512] = initial_bytes(128, 128)
    media_states = []
    for index in range(3):
        directory = root / f'final-{index}.dosemedia'
        drive = disk(directory)
        current = drive.file(b'PIIXIDE DAT')
        certify_proof(current, mode, table, base)
        check(drive.file(b'IDE-SCATDAT') == initial_bytes(8, 2) and fat_file(drive, b'IDE-READDAT') == initial_bytes(128, 128) and drive.file(b'IDE-FPU DAT') == expected_raw, 'actual complete scatter/64-KiB/raw-x87 DOS files')
        proof = current if proof is None else proof
        check(current == proof, 'all three actual full proof files equal')
        scratch = disk(directory, 1)
        actual = bytearray(scratch.basis.read_bytes())
        for lba, data in scratch.changed.items():
            actual[lba * 512:(lba + 1) * 512] = data
        check(actual == expected_disk and scratch.records == 130 and set(scratch.changed) == {32, 33, *range(512, 640)}, 'every scratch byte/only 130 true DMA sector commits')
        media_states.append(read(directory / 'media.json')['media'])
        files[str(index)] = hashlib.sha256(current).hexdigest().upper()
    check(media_states[0] == media_states[1] == media_states[2], 'all final media generations identical')
    expected_windows = windows(owned[0]['buffer'])
    check(len(ledger['markers']) == len(expected_windows), 'all finite actual DMA markers')
    for row, expected in zip(ledger['markers'], expected_windows):
        certify_marker(row, expected, table, base, final)
    certify_programming(ledger['ports'], owned[0]['buffer'], final)
    grants = certify_grants(ledger['dmaStates'], ledger['ports'], final, owned[0]['buffer'])
    frames = [r for r in ledger['boundaries'] if r['before']['phase'] > 0]
    check(len(frames) == 18, 'six actual DMA handler entry/commit/IRET triples')
    acks = [a for a in ledger['acks'] if a['irq'] == 14 and a['at']['phase'] > 0]
    check(len(acks) == 6 and all(not a['spurious'] and a['vector'] == 0x76 for a in acks), 'six true physical IRQ14 acknowledgements')
    for index in range(6):
        certify_frame(frames[index * 3:index * 3 + 3], index, proof, table, base, mode, ledger['ports'], final)
        clock(acks[index]['at'], final)
        check(acks[index]['at']['instructions'] <= frames[index * 3]['before']['instructions'], 'real PIC ACK precedes reflected own handler')
    for field, cut_field in [('ports', 'ports'), ('acks', 'acks'), ('markers', 'markers'), ('boundaries', 'frames'), ('dmaStates', 'dma'), ('services', 'services')]:
        check(ledger[field][ledger['readCut'][cut_field]:] == ledger['readFuture'][field], 'all first restored publications ' + field)
        check(ledger[field][ledger['writeCut'][cut_field]:] == ledger['writeFuture'][field], 'all second restored publications ' + field)
    services = ledger['services']
    check([s['kind'] for s in services] == (['lock', 'unlock'] if mode else []), 'balanced actual original VDS services')
    for service in services:
        check(service['after'] is not None and not service['after']['flags'] & 1, 'real VDS successful carry return')
        before = service['before']
        check(before['segments'][0] == base >> 4 and before['registers'][7] & 65535 == table[13], 'actual VDS ES:DI belongs to complete own DDS')
        check(before['registers'][0] & 65535 == (0x8103 if service['kind'] == 'lock' else 0x8104) and before['registers'][2] & 65535 == (0x2C if service['kind'] == 'lock' else 0), 'actual resident INT4B function/flags')
    raw_text = text_contract(final, final_ram, result['finalScreen'])
    negatives = 0
    for field in ['passed', 'returnedToDos', 'exits', 'comparisons', 'futureSteps', 'dma', 'readStateSha256', 'readRamSha256', 'writeStateSha256', 'writeRamSha256', 'finalStateSha256', 'finalRamSha256', 'programSha256']:
        changed = dict(result)
        changed[field] = False if isinstance(changed[field], bool) else [] if isinstance(changed[field], list) else 0 if isinstance(changed[field], int) else 'bad'
        try:
            common(changed, states, manifests, hashes, definitions[0]['machine']['hardware'])
        except (AssertionError, ValueError):
            negatives += 1
        else:
            raise AssertionError('corrupted complete result accepted')
    for index, row in enumerate(ledger['markers']):
        for position in [0, 255, 256, 509, 510, 0x9000, 65535, 0x10100, 0x1F000, 0x1F007, 0x1F00F, 131071]:
            changed = copy.deepcopy(row)
            data = bytearray(decode(row['window']))
            data[position] ^= 1
            changed['window'] = base64.b64encode(data).decode('ascii')
            try:
                certify_marker(changed, expected_windows[index], table, base, final)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted actual data/PRD/guard accepted')
    for offset in [0, 8, 10, 12, 26, 28, 30, 32, 36, 40, 44, 45, 46, 47, 48, 52, 60, 62, 64, 66, 82, 320, 327]:
        changed = bytearray(proof)
        changed[offset] ^= 1
        try:
            certify_proof(changed, mode, table, base)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted real fixed proof accepted')
    for index in range(6):
        for offset in [0, 2, 4, 6, 8, 12, 14, 16, 17, 18, 19, 20]:
            changed = bytearray(proof)
            changed[128 + index * 32 + offset] ^= 1
            try:
                certify_proof(changed, mode, table, base)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('corrupted real frame/ACK accepted')
    for index, row in enumerate(ledger['ports']):
        if not row['write'] or row['at']['phase'] not in range(1, 7) or row['address'] not in [0x1F7, 0xC004]:
            continue
        changed = copy.deepcopy(ledger['ports'])
        changed[index]['value'] ^= 1
        try:
            certify_programming(changed, owned[0]['buffer'], final)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted real ATA/PRDT programming accepted')
    for field in ['nanoseconds', 'counter', 'epoch', 'ticks']:
        changed = copy.deepcopy(ledger['markers'][0])
        changed['at'][field] += 100 if field == 'nanoseconds' else 1
        try:
            certify_marker(changed, expected_windows[0], table, base, final)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('corrupted independent clock phase accepted')
    return dict(passed=True, negativeOracles=negatives, frameCount=6, cuts=2, restoredContinuations=2, actualDmaGrants=grants, dmaBytes=sum(TOTALS), scratchWrittenSectors=130,
                comparisons=result['comparisons'], guestV86=bool(mode), system=system, programBase=base, ownership=owned[0], proofFileSha256=files['0'],
                rawFileHex=expected_raw.hex().upper(), fullReadSha256=hashlib.sha256(initial_bytes(128, 128)).hexdigest().upper(), rawTextSha256=raw_text)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--program', type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.program), indent=2))
