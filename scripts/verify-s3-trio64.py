# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent actual Trio64 PCI/shared-bank/LFB/DAC/scanout and full-PC original-DOS continuation proof."""
import argparse
import base64
import copy
import functools
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('s3_independent_dos_helpers', REPO / 'scripts/verify-piix-reset.py')
legacy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(legacy)
media, diskio = legacy.media, legacy.legacy
RAM_BYTES, VRAM_BYTES, HZ = 32 * 1048576, 2 * 1048576, 100_000_000
GRAPHICS = [(640, 480, 640, 0), (640, 480, 672, 0x50000), (800, 600, 832, 0xD0000)]
KEYS = ['ports', 'acks', 'readbacks', 'frames', 'services', 'boundaries', 'pcmTransfers', 'pcmSamples']


def check(value, reason):
    """Reject a missing actual device, mode, ownership, timing, pixel or persistence effect."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read only one explicitly named own textual observation."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def digest(path):
    """Hash one explicitly named private artifact without scanning any media tree."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def decode(value):
    """Decode strict raw state bytes independently from emulator code."""
    return base64.b64decode(value, validate=True)


def metadata(program):
    """Bind actual unchanged own labels, complete extents, REP targets and observation NOPs."""
    check(program.count(b'S3META01') == 1, 'unique own Trio64 metadata')
    at = program.index(b'S3META01')
    check(struct.unpack_from('<HH', program, at + 8) == (1, 32), 'own metadata version/count')
    table = struct.unpack_from('<32H', program, at + 12)
    check(table[3] == len(program) + 256 and table[1] - table[0] == 256 and table[2] < table[0] < table[3], 'complete own COM/data/proof extents')
    for index in [4, 5, 6]:
        check(program[table[index] - 256] == 0x90, 'actual immutable completed-read/render/cut NOP')
    for index in [28, 29]:
        check(program[table[index] - 256:table[index] - 254] == b'\x0F\x31', 'actual two timestamp opcodes')
    for index in [9, 11, 12, 13, 14]:
        check(program[table[index] - 256:table[index] - 254] == bytes([0xCD, 0x67 if index == 9 else 0x4B]), 'actual original resident service INT')
    return table


@functools.lru_cache(maxsize=64)
def pattern(bank, second):
    """Specify every intended VRAM byte from independent bank, row and within-row identities."""
    return bytes(((column * (5 if second else 1)) + row * (7 if second else 3) +
                  bank * (29 if second else 17) + (0x5B if second else 0)) & 255
                 for row in range(256) for column in range(256))


def vram(video):
    """Reconstruct the physical two-MiB byte order from four independent512KiB planes."""
    planes = [decode(value) for value in video['_planes']]
    check(len(planes) == 4 and all(len(p) == 524288 for p in planes), 'actual fixed four-plane2MiB storage')
    result = bytearray(VRAM_BYTES)
    for index, plane in enumerate(planes):
        result[index::4] = plane
    return bytes(result)


def clock(point, final):
    """Independently reconstruct100MHz scheduler phase, without the host clock or VM code."""
    check(point['epoch'] == 0 and point['nanoseconds'] <= final['scheduler']['now'] and
          point['ticks'] == point['counter'] == point['nanoseconds'] // 10, 'complete integer100MHz source and unchanged reset epoch')


def ownership(ram, state, program, table, base):
    """Prove entire actual MCB/PSP/JFT/buffer ownership and all unchanged original DE01 PTEs plus two high aliases."""
    cpu = state['components']['cpu']['fields']
    proof = ram[base + table[0]:base + table[1]]
    check(16 < base and base & 15 == 0 and ram[base:base + 2] == b'\xCD\x20', 'actual own DOS PSP')
    paragraphs = (table[3] + 15) // 16
    check(ram[base - 16] in [77, 90] and struct.unpack_from('<HH', ram, base - 15) == (base >> 4, paragraphs), 'complete own program/data/stack/table MCB')
    check(ram[base + 256:base + table[2]] == program[:table[2] - 256], 'every unchanged executing own byte')
    mode = struct.unpack_from('<I', proof, 8)[0]
    allocation = struct.unpack_from('<H', proof, 172)[0] << 4
    buffer = struct.unpack_from('<I', proof, 16)[0]
    check(mode in [0, 1] and struct.unpack_from('<I', proof, 12)[0] == base, 'actual own program base and original DOS mode')
    check(ram[allocation - 16] in [77, 90] and struct.unpack_from('<HH', ram, allocation - 15) == (base >> 4, 0x4002) and
          struct.unpack_from('<HH', proof, 174) == (base >> 4, 0x4002), 'complete independent DOS data MCB and guest observed owner')
    check(base + paragraphs * 16 <= allocation - 16 or allocation + 0x40020 <= base - 16, 'disjoint full own allocations')
    check(buffer & 0x1FFFF == 0 and allocation + 16 <= buffer and buffer + 131088 <= allocation + 0x40020 <= 0xA0000, 'entire guarded128KiB buffer belongs to own MCB')
    check(ram[buffer - 16:buffer] == ram[buffer + 131072:buffer + 131088] == bytes([0xD6]) * 16 and
          ram[buffer + 65536:buffer + 131072] == bytes([0xA5]) * 65536, 'both physical guards and entire unused buffer half')
    check(ram[buffer:buffer + 65536] == pattern(31, True), 'actual last whole guest bank readback remains in owned RAM')
    check(struct.unpack_from('<IIHHI', ram, base + table[15]) == (131072, 0, buffer >> 4, 0, buffer) and
          proof[184:200] == ram[base + table[15]:base + table[15] + 16], 'actual complete same VDS DDS')
    check(struct.unpack_from('<HHH', proof, 178) == (mode, 0, mode), 'actual original VDS lock/translation count at cut')
    handle = struct.unpack_from('<H', ram, base + table[16])[0]
    count, offset, segment = struct.unpack_from('<3H', ram, base + 0x32)
    jft = (segment << 4) + offset
    check(5 <= handle < count <= 255 and jft + count <= 0xA0000 and 3 <= ram[jft + handle] < 0x80, 'actual ordinary live DOS file/JFT handle')
    check(cpu['_segments'][1] == 8 and cpu['_segments'][2] == 16 and cpu['_segmentCaches'][1]['base'] == base and
          cpu['_segmentCaches'][2]['base'] == 0 and cpu['_registers'][4] == base + table[25] and cpu['_registers'][5] == base and
          cpu['Eip'] == table[6] + 1 and not cpu['Eflags'] & 0x20000 and cpu['Cr0'] & 0x80000001 == (0x80000001 if mode else 1),
          'actual private protected client at completed cut NOP with own flat stack')
    aliases = (0x400000, 0x800000) if mode else (0xE0000000, 0xE0800000)
    check(struct.unpack_from('<I', proof, 220)[0] == aliases[0] and struct.unpack_from('<I', proof, 232)[0] == aliases[1], 'explicit actual alias policy')
    if mode:
        pd = struct.unpack_from('<I', ram, base + table[22])[0]
        check(pd & 4095 == 0 and base <= pd and pd + 16384 <= base + table[3] and cpu['Cr3'] == pd, 'all four private PD/PT pages owned and active')
        for index, target in [(0, pd + 4096), (1, pd + 8192), (2, pd + 12288)]:
            pde = struct.unpack_from('<I', ram, pd + index * 4)[0]
            check(pde & ~0x60 == target | 7, 'exact present/user/writable private PDE target with only A/D additions')
        check(not any(ram[pd + 12:pd + 4096]), 'no unintended PDE3..1023 mapping')
        for index in range(1024):
            before = struct.unpack_from('<I', ram, base + table[23] + index * 4)[0]
            after = struct.unpack_from('<I', ram, pd + 4096 + index * 4)[0]
            check(before & ~0x60 == after & ~0x60 and after & before == before, 'all1024 original DE01 PTEs retained except added A/D')
        pages = set(range(base & ~4095, (base + paragraphs * 16 + 4095) & ~4095, 4096)) | set(range(buffer - 4096, buffer + 135168, 4096)) | set(range(0xA0000, 0xB0000, 4096))
        for page in pages:
            pte = struct.unpack_from('<I', ram, pd + 4096 + (page >> 12) * 4)[0]
            check(pte & 7 == 7 and pte & 0xFFFFF000 == page, 'complete conventional own/video identity mapping and user/writable rights')
        for start, physical in [(pd + 8192, 0xE0000000), (pd + 12288, 0xE0800000)]:
            for index in range(512):
                pte = struct.unpack_from('<I', ram, start + index * 4)[0]
                check(pte & ~0x60 == (physical + index * 4096) | 7, 'exact high LFB physical page/user/writable alias with only A/D additions')
            check(not any(ram[start + 2048:start + 4096]), 'unmapped half of each high alias table remains zero')
    return dict(mode=mode, base=base, buffer=buffer, allocation=allocation, aliases=aliases, paragraphs=paragraphs)


def critical_cut(state, proof, table):
    """Bind actual PCI/wake/bank/LAW, open DAC/ATC, pending live PCM/PIT and unchanged timestamp source."""
    video = state['components']['video']['fields']
    crtc, seq, gc, attr = (decode(video[key]) for key in ['_crtc', '_seq', '_gc', '_attr'])
    check(video['IsTrio64'] is True and video['IsEt4000'] is False and video['_trioSetup'] == 8 and video['_trioPos'] == 1 and
          video['_trioAdvanced'] == 1 and video['_misc'] == 0x67, 'actual original wake/POS and Enhanced mode, not plain VGA')
    check((crtc[0x38], crtc[0x39], crtc[0x40], crtc[0x31], crtc[0x3A], crtc[0x58], crtc[0x59], crtc[0x5A], crtc[0x6A]) ==
          (0x48, 0xA0, 0x31, 9, 0x10, 0x12, 0xE0, 0, 17), 'actual unlocked S3 bank17 and2MiB LAW at canonical BAR')
    check(seq[2] == 15 and seq[4] == 14 and gc[0] == gc[1] == gc[3] == 0 and gc[5] == 0x40 and gc[8] == 255, 'actual plain VGA chain4 pass-through controls')
    check(video['_attributeData'] is True and video['_attrIndex'] == 0x10 and video['_displayEnabled'] is True and
          video['_writeIndex'] == 0x37 and video['_writeComponent'] == 1, 'actual open ATC data and partial DAC triplet at capture')
    check(crtc[1] == 79 and crtc[7] == 2 and crtc[9] == 0 and crtc[0x12] == 0xDF and crtc[0x13] == 84 and
          crtc[0x69] == 1 and (crtc[12] << 8 | crtc[13]) == 0x4000, 'actual upcoming shifted640x480 stride672 mode')
    expected = bytearray((value for i in range(256) for value in (i & 63, i >> 2, i * 7 & 63)))
    expected[0x37 * 3] = 63
    check(decode(video['_dac']) == expected, 'every DAC byte before pending two-component completion')
    check(vram(video) == b''.join(pattern(bank, True) for bank in range(32)), 'entire actual two-MiB storage independently equals the LFB-written pattern')
    config = decode(legacy.fields(state, '.S3Trio64')['_config'])
    expected_config = bytearray(256)
    for index, value in {0:0x33,1:0x53,2:0x11,3:0x88,4:3,7:2,0x0B:3,0x32:0x0C,0x3D:1}.items():
        expected_config[index] = value
    check(config == expected_config, 'all256 actual fixed/writable PCI bytes, with one mirrored BAR source')
    sb = legacy.fields(state, '.SoundBlaster16')
    events = [e for e in state['scheduler']['events'] if e['id'] not in state['scheduler']['cancelled']]
    check(0 < sb['RemainingSamples'] <= 512 and sb['_activeRate'] == 8000 and not sb['Paused'] and not sb['_word'] and not sb['_stereo'] and
          not sb['_recording'] and sb['_activePeriod'] == 0 and sb['CompletedBlocks'] == sb['Dma16Grants'] == 0 and
          sb['_sampleDeadline'] <= state['scheduler']['now'] + 125000 and
          sb['TransferredSamples'] == sb['Dma8Grants'] == 512 - sb['RemainingSamples'] and
          sb['_sampleEvent'] > 0 and any(e['id'] == sb['_sampleEvent'] and e['owner'] == 'sb16' and e['operation'] == 'event2' and
          e['payload'] == [] and e['time'] == sb['_sampleDeadline'] > state['scheduler']['now'] for e in events), 'actual live PCM engine and exact pending sample callback')
    pit = legacy.fields(state, '.Pit8254')
    check(pit['_running'] and any(e['id'] == pit['_event'] and e['owner'] == 'pit' and e['operation'] == 'event0' and e['time'] > state['scheduler']['now'] for e in events), 'actual running PIT and exact future event')
    check(struct.unpack_from('<III', proof, 20) == (6, 31, 0) and struct.unpack_from('<I', proof, 32)[0] == 0 and
          struct.unpack_from('<II', proof, 36) == (0x524, 0x135) and struct.unpack_from('<II', proof, 68) == (0, 64), 'actual entire readback stages and pre-cut bookkeeping')
    check(struct.unpack_from('<4I', proof, 80) == (0x88115333, 0x03000000, 0xE0000000, 0xFF800000) and
          proof[96:102] == bytes([3, 0, 0x88, 0x11, 0, 0xE1]), 'actual PCI/CR identification and resource probe')
    check(proof[120:122] == bytes([0xB1, 0xC2]) and struct.unpack_from('<I', proof, 200)[0] == 6 and
          struct.unpack_from('<3I', proof, 208) == (1, VRAM_BYTES, VRAM_BYTES) and struct.unpack_from('<I', proof, 224)[0] == 1,
          'real old/new bank priority, six decode checks and both whole-VRAM self-comparisons')


def readbacks(rows, owned, table, base, final):
    """Require all actual copied bytes, correct original/private CPU context and static completion sites."""
    check(len(rows) == 64, 'all64 complete readbacks')
    for index, row in enumerate(rows):
        phase, bank = (2 if index < 32 else 4), index % 32
        check(row['phase'] == phase and row['bank'] == bank and decode(row['bytes']) == pattern(bank, phase == 4), 'every actual byte from each of32banks in both directions')
        point = row['at']
        expected_source_end = owned['aliases'][0] + (bank + 1) * 65536 if phase == 2 else 0xB0000
        check(point['phase'] == phase and point['eip'] == table[4] + 1 and point['segments'][1] == 8 and point['bases'][1] == base and
              point['segments'][0] == point['segments'][3] == 16 and point['bases'][0] == point['bases'][3] == 0 and
              point['registers'][6] == expected_source_end and point['registers'][7] == owned['buffer'] + 65536 and
              point['registers'][4] == base + table[25] - 4, 'actual flat private CPU REP source/destination and completed verification stack')
        clock(point, final)


def scanout(row, ordinal, final):
    """Independently compute every intended pixel from original S3 address units/stride and the guest's own DAC ramp."""
    width, height, pitch, start = GRAPHICS[ordinal]
    registers = row['registers']
    crtc, seq, gc, attr, dac = (decode(registers[key]) for key in ['_crtc', '_seq', '_gc', '_attr', '_dac'])
    check(row['ordinal'] == ordinal and row['width'] == width and row['height'] == height and row['frameId'] == ordinal + 1, 'actual three distinct scanouts and renderer calls')
    check(registers['_trioAdvanced'] == 1 and registers['_trioSetup'] == 8 and registers['_trioPos'] == 1 and registers['_misc'] == 0x67 and
          registers['_displayEnabled'] is True and not seq[1] & 32 and not seq[0x18] & 32, 'actual active S3 enhanced scanout gates')
    actual_width = (crtc[1] + ((crtc[0x5D] & 2) << 7) + 1) * 8
    actual_height = (crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + ((crtc[0x5E] & 2) << 9) + 1) // ((crtc[9] & 31) + 1) // (2 if crtc[9] & 128 else 1)
    offset = crtc[0x13] | ((crtc[0x51] & 0x30) << 4 if crtc[0x51] & 0x30 else (crtc[0x43] & 4) << 6)
    high = (crtc[0x69] & 15) << 16 if crtc[0x69] & 15 else (crtc[0x31] & 0x30) << 12 | (crtc[0x51] & 3) << 18
    display_start = ((crtc[12] << 8) | crtc[13] | high) * 4
    check((actual_width, actual_height, offset * 8, display_start) == (width, height, pitch, start) and
          crtc[0x31] & 8 and crtc[0x3A] & 16 and crtc[0x50] & 0x30 == 0 and crtc[0x67] & 0xF0 == 0 and not crtc[0x42] & 0x20,
          'independent original S3 visible dimensions, DWORD start and pitch')
    palette = bytearray(v for i in range(256) for v in (i & 63, i >> 2, i * 7 & 63))
    if ordinal:
        palette[0x37 * 3:0x37 * 3 + 3] = bytes([63, 13, 29])
    check(dac == palette and registers['_pelMask'] == 255 and (ordinal == 0 or attr[0x10] == 1 and registers['_attributeData'] is False and
          registers['_writeIndex'] == 0x38 and registers['_writeComponent'] == 0), 'actual DAC triplet and ATC phases were consumed after restore')
    colors = [struct.pack('<I', 0xFF000000 | ((r << 2 | r >> 4) << 16) | ((g << 2 | g >> 4) << 8) | (b << 2 | b >> 4))
              for r, g, b in zip(palette[0::3], palette[1::3], palette[2::3])]
    all_pixels = bytearray()
    for y in range(height):
        for x in range(width):
            address = start + y * pitch + x
            value = ((address & 255) * 5 + ((address >> 8) & 255) * 7 + (address >> 16) * 29 + 0x5B) & 255
            all_pixels.extend(colors[value])
    check(row['pixelsSha256'] == hashlib.sha256(all_pixels).hexdigest().upper(), 'independent entire actual renderer surface, every pixel and six-bit DAC component')
    clock(row['at'], final)
    return width * height


def port_contract(ports, base, final):
    """Bind real complete PCI cycles, wakeup, all shared-bank addresses, DAC bytes and gated writes."""
    own = [p for p in ports if p['at']['bases'][1] == base and p['at']['segments'][1] == 8 and not p['at']['v86']]
    address, writes, reads = 0, [], []
    for point in own:
        p, value, width, write = point['address'], point['value'], point['width'], point['write']
        if p == 0xCF8 and write:
            check(width == 4, 'actual complete PCI address cycle')
            address = value
        if p == 0xCFC:
            check(address & 0xFFFFFF00 == 0x80001800, 'actual fixed PCI00:03.0')
            (writes if write else reads).append((address & 255, value, width))
    expected_writes = [(4, 1, 2), (0x10, 0xFFFFFFFF, 4), (0x10, 0xE0000000, 4), (4, 3, 2),
                       (0x10, 0xE0800000, 4), (0x10, 0xE0000000, 4), (4, 1, 2), (4, 3, 2),
                       (4, 1, 2), (4, 3, 2), (4, 2, 2), (4, 3, 2)]
    check(writes == expected_writes and reads == [(0, 0x88115333, 4), (8, 0x03000000, 4), (0x10, 0xE0000000, 4),
          (4, 0x02000003, 4), (0x10, 0xFF800000, 4)], 'exact real PCI identification/BAR probe/relocation/independent IOSE-MSE cycles')
    wake = [(p['address'], p['value'], p['width']) for p in own if p['write'] and p['address'] in [0x46E8, 0x102]]
    check(wake == [(0x46E8, 0x10, 1), (0x102, 1, 1), (0x46E8, 8, 1)], 'actual original PCI video wake/POS sequence')
    crtc_writes = [(p['value'] & 255, p['value'] >> 8 & 255) for p in own if p['write'] and p['address'] == 0x3D4 and p['width'] == 2]
    banks = [v for index, v in crtc_writes if index == 0x6A]
    check(banks == [0, 17, 1] + list(range(32)) + list(range(32)) + [0, 17, 0], 'every actual common64KiB bank and old/new override case')
    check([v for index, v in crtc_writes if index == 0x58] == [0, 0x12, 0, 0x12, 0, 0x12, 0], 'actual legacy/LFB window enable sequence')
    check((0x38, 0x48) in crtc_writes and (0x39, 0xA0) in crtc_writes and (0x40, 0x31) in crtc_writes, 'actual original S3 register unlock/ADV gate')
    dac = [(p['address'], p['value'], p['width']) for p in own if p['write'] and p['address'] in [0x3C8, 0x3C9]]
    expected_dac = [(0x3C8, 0, 1)] + [(0x3C9, v, 1) for i in range(256) for v in (i & 63, i >> 2, i * 7 & 63)] + [
        (0x3C8, 0x37, 1), (0x3C9, 63, 1), (0x3C9, 13, 1), (0x3C9, 29, 1)]
    check(dac == expected_dac, 'all actual programmed six-bit DAC components and interrupted triplet')
    attr = [(p['value'], p['width'], p['at']['phase']) for p in own if p['write'] and p['address'] == 0x3C0]
    check(attr == [(0x30, 1, 5), (1, 1, 6)], 'actual open then completed ATC data phase without a new index reset')
    check(any(p['address'] == 0x3CC and not p['write'] and p['value'] == 255 for p in own) and
          any(p['address'] == 8 and not p['write'] and p['value'] & 2 for p in own), 'actual gated I/O read and real DMA1 terminal-count observation')
    for point in ports:
        clock(point['at'], final)


def pcm_contract(ledger, cut, final, program, base):
    """Check all512 actual owned raw DMA1 samples, their exact deadlines and completion before freeing the own program."""
    transfers, samples = ledger['pcmTransfers'], ledger['pcmSamples']
    check(len(transfers) == 1 and len(samples) == 512, 'one actual real stream and every512 DMA publications')
    t = transfers[0]
    check({k:t[k] for k in ['rate','word','stereo','signed','recording','autoInit','fifoRequested','samples','dmaChannel','periodNanoseconds']} ==
          dict(rate=8000,word=False,stereo=False,signed=False,recording=False,autoInit=False,fifoRequested=False,samples=512,dmaChannel=1,periodNanoseconds=0),
          'actual concrete mono8/8000Hz512-component stream')
    for index, row in enumerate(samples, 1):
        check(row == dict(time=t['time'] + index * 125000,word=False,recording=False,right=False,raw=0xC0,value=16384,dmaResult='Transferred'),
              'every independently timed real raw byte/DAC component')
    sb = legacy.fields(final, '.SoundBlaster16')
    check(sb['RemainingSamples'] == 0 and sb['TransferredSamples'] == sb['Dma8Grants'] == 512 and
          sb['CompletedBlocks'] == 1 and sb['Dma16Grants'] == 0 and sb['_sampleEvent'] == 0, 'actual entire block completed, no continuing DMA on freed DOS memory')
    cut_sb = legacy.fields(cut, '.SoundBlaster16')
    check(cut_sb['TransferredSamples'] == cut_sb['Dma8Grants'] == 512 - cut_sb['RemainingSamples'] ==
          sum(row['time'] <= cut['scheduler']['now'] for row in samples) == ledger['cut']['pcmSamples'],
          'actual saved partial PCM phase equals every preceding native publication and physical grant')
    channel = cut['components']['dma/_channels/1']['fields']
    sample = base + 256 + program.index(bytes([0xC0]) * 512)
    check(program.count(bytes([0xC0]) * 512) == 1 and (channel['Page'] << 16 | channel['BaseAddress']) == sample and
          channel['BaseCount'] == 511 and channel['Mode'] == 0x49 and not channel['Masked'] and
          channel['Count'] == 511 - cut_sb['TransferredSamples'] and channel['Address'] == (sample + cut_sb['TransferredSamples']) & 65535,
          'actual owned physical DMA sample block and channel cursor')


def audio_contract(result, cut, final, ledger):
    """Bind positive actual published PCM duration/identity to48kHz virtual time, without claiming a new mixer reconstruction."""
    start, end = legacy.fields(cut, '.PcAudioMixer'), legacy.fields(final, '.PcAudioMixer')
    check(start['GeneratedFrames'] == cut['scheduler']['now'] * 48000 // 1000000000 and
          end['GeneratedFrames'] == final['scheduler']['now'] * 48000 // 1000000000 and
          start['_count'] % 2 == 0 and end['_count'] == 0, 'actual integer48kHz integration and final flush phase')
    expected = end['GeneratedFrames'] - start['GeneratedFrames'] + start['_count'] // 2
    audio, restored = result['audioFuture'], result['restoredAudioFuture']
    check(audio == restored and audio['frames'] == expected > 480 and isinstance(audio['sha256'],str) and len(audio['sha256']) == 64 and
          all(c in '0123456789ABCDEF' for c in audio['sha256']) and audio['sha256'] != hashlib.sha256(b'').hexdigest().upper(),
          'both actual positive whole future PCM publications, their exact duration and nonempty64hex digests')
    check(ledger['pcmSamples'][-1]['time'] <= final['scheduler']['now'] and ledger['pcmSamples'][0]['time'] > ledger['pcmTransfers'][0]['time'],
          'all512 independently checked native samples occurred within the real executed future')
    return expected


def text_contract(final, ram, reported):
    """Independently decode original post-graphics DOS text/attributes/fonts/cursor from real Trio64 VRAM and CRTC."""
    video = final['components']['video']['fields']
    planes = [decode(v) for v in video['_planes']]
    crtc, seq, gc, attr = (decode(video[k]) for k in ['_crtc','_seq','_gc','_attr'])
    check(video['IsTrio64'] is True and all(len(p) == 524288 for p in planes) and video['_trioAdvanced'] == 0 and
          crtc[0x31] == crtc[0x69] == crtc[0x6A] == 0 and video['_displayEnabled'] and not seq[1] & 32 and not gc[6] & 1,
          'actual standard text organization and completed S3 enhanced/bank/LAW exit')
    columns = crtc[1] + 1
    height = (crtc[9] & 31) + 1
    rows = (crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + 1) // height
    check((columns,rows,height) == (80,25,16) and ram[0x449] == 3 and struct.unpack_from('<H',ram,0x44A)[0] == 80 and
          ram[0x484] == 24 and ram[0x485] == 16, 'actual BIOS80x25 text state, not an old graphics-mode BDA')
    start, stride = crtc[12] << 8 | crtc[13], crtc[0x13] * 2
    check(stride == 80, 'actual original text stride')
    chars = bytes(planes[0][(start + y * stride + x) & 0x7FFFF] for y in range(rows) for x in range(columns))
    attrs = bytes(planes[1][(start + y * stride + x) & 0x7FFFF] for y in range(rows) for x in range(columns))
    lines = [''.join(chr(v) if 32 <= v < 127 else ' ' for v in chars[y*80:(y+1)*80]).rstrip() for y in range(25)]
    text = '\n'.join(lines).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip() and 'SNAPSHOT-DONE' in text and 'S3-TRIO64-DONE' in text and
          text.endswith('A:\\>'), 'actual independent raw-VRAM original DOS text')
    echo_rows = [y for y,line in enumerate(lines) if line == 'TRIOCONTINUED']
    prompt = max(y for y,line in enumerate(lines) if line)
    check(len(echo_rows) == 1 and echo_rows[0] < prompt and lines[prompt] == 'A:\\>' and
          all(not line for line in lines[echo_rows[0]+1:prompt]), 'new actually executed ECHO and following prompt')
    for index in list(range(echo_rows[0]*80,echo_rows[0]*80+len('TRIOCONTINUED'))) + list(range(prompt*80,prompt*80+4)):
        a = attrs[index]
        fg,bg = a & 15, a >> 4 & (7 if attr[0x10]&8 else 15)
        check(not a & 128 and attr[fg & attr[0x12] & 15] != attr[bg & attr[0x12] & 15] and
              any(planes[2][chars[index]*32:chars[index]*32+16]), 'actual visible retained font and text attributes')
    check((crtc[14] << 8 | crtc[15]) == start + prompt * 80 + 4 and struct.unpack_from('<H',ram,0x450)[0] == prompt*256+4,
          'all four prompt cells and complete original BIOS/CRTC cursor update')
    return dict(columns=80,rows=25,echoRow=echo_rows[0],promptRow=prompt,cellsSha256=hashlib.sha256(b''.join(bytes([c,a]) for c,a in zip(chars,attrs))).hexdigest().upper())


def files(root, saved, owned, table, ledger):
    """Replay real sealed COW/FAT and compare exact two actual DOS files, the open header and untouched private scratch."""
    expected_raw = b'S3RAW001' + struct.pack('<QH',0xC90FDAA22168C235,0x4000) + struct.pack('<QH',0xFEDCBA9876543210,0x4001) + struct.pack('<HH',0x37F,0)
    points = [b for b in ledger['boundaries'] if b['kind'] in ['tsc-before','tsc-after']]
    check([b['kind'] for b in points] == ['tsc-before','tsc-after'], 'two actual RDTSC observations')
    expected = bytearray(saved)
    struct.pack_into('<I',expected,20,9)
    struct.pack_into('<I',expected,28,1)
    expected[44:68] = expected_raw[8:]
    struct.pack_into('<I',expected,68,2)
    struct.pack_into('<Q',expected,112,points[1]['at']['counter'])
    struct.pack_into('<H',expected,180,owned['mode'])
    struct.pack_into('<I',expected,228,1)
    check(struct.unpack_from('<Q',expected,104)[0] == points[0]['at']['counter'], 'actual first stored full RDTSC value')
    for index, row in enumerate(points):
        point = row['at']
        check(point['eip'] == table[28 + index] + 1 and point['segments'][1] == 8 and point['bases'][1] == owned['base'] and
              not point['v86'] and point['cr0'] & 0x80000001 == (0x80000001 if owned['mode'] else 1),
              'actual immutable own0F31 instruction site and private CS:EIP/PE/PG context')
    generations, proofs = [], []
    for name in ['final-a.dosemedia','final-b.dosemedia']:
        directory = root / name
        generations.append(read(directory/'media.json')['media'])
        drive = diskio.disk(directory)
        proof = diskio.fat_file(drive,b'TRIO    DAT')
        raw = diskio.fat_file(drive,b'S3RAW   DAT')
        check(proof == expected and raw == expected_raw, 'every byte of actual full own DOS proof and raw80-bit file')
        proofs.append(proof)
        for slot,seed in [(1,0x31),(2,0x91),(3,0xD1)]:
            scratch = diskio.disk(directory,slot)
            check(scratch.sectors == 4096 and all(scratch.sector(n) == legacy.initial_bytes(n,1,seed) for n in range(4096)), 'entire untouched own private IDE scratch/auxiliary disk')
    check(generations[0] == generations[1], 'all six final media generations identical')
    check(diskio.fat_file(diskio.disk(root/'cut.dossav/media.dosemedia'),b'TRIO    DAT') == b'S3PROOF1', 'actual flushed original open DOS file header at capture')
    return proofs[0],expected_raw,bytes(expected)


def verify(root, program_path):
    """Certify actual build-bound full containers, all memory/pixels/effects and reject independent corruptions."""
    root = media.safe(root)
    result,ledger,cut,final = (read(root/name) for name in ['result.json','ledger.json','cut.dossav/state.json','final-state.json'])
    manifest = read(root/'cut.dossav/snapshot.json')
    program = media.safe(program_path).read_bytes()
    table = metadata(program)
    ram,final_ram = (root/'cut.dossav/ram.bin').read_bytes(),(root/'final-ram.bin').read_bytes()
    check(result['passed'] is True and result['returnedToDos'] is True and result['postGraphicsEcho'] is True and result['exits'] == [True,True] and
          result['comparisons'] > 1 and result['futureSteps'] > 0, 'two actual full PCs and original DOS continuations')
    check('TRIOCONTINUED' not in result['beforeEcho'] and 'S3-TRIO64-DONE' in result['beforeEcho'] and
          '\nTRIOCONTINUED' in result['finalScreen'], 'fresh actually executed shell output, not stale visible text')
    check(len(ram) == len(final_ram) == RAM_BYTES and manifest == result['manifest'] and manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and
          manifest['ramBytes'] == RAM_BYTES and manifest['ramSha256'] == result['capturedRamSha256'] == digest(root/'cut.dossav/ram.bin') and
          manifest['stateSha256'] == result['capturedStateSha256'] == digest(root/'cut.dossav/state.json') and
          manifest['mediaSha256'] == digest(root/'cut.dossav/media.dosemedia/media.json') and manifest['virtualNanoseconds'] == cut['scheduler']['now'] and
          manifest['stateBytes'] == (root/'cut.dossav/state.json').stat().st_size, 'every actual complete raw state/RAM/media seal and shared time')
    check(result['finalStateSha256'] == digest(root/'final-state.json') and result['finalRamSha256'] == digest(root/'final-ram.bin') and
          result['programSha256'] == hashlib.sha256(program).hexdigest().upper(), 'whole final graph/RAM and immutable own executable')
    profile = read(root/'cut.dossav/media.dosemedia/media.json')['machine']['hardware']
    check(profile['id'] == 'dose-430fx-piix-s3-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == HZ and
          profile['ramMiB'] == 32 and profile['videoCard'] == 's3-trio64-86c764-pci-2m-development', 'constructor-fixed actual board/CPU/clock/video/RAM profile')
    schema = read(REPO/'docs/state-schema-v1.json')['nodes']
    for state in [cut,final]:
        cpu = state['components']['cpu']['fields']
        check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == HZ and cpu['_tscEpochTicks'] == 0 and state['scheduler']['cachedFrequency'] == HZ and
              state['scheduler']['remainder'] == 0 and state['components']['memory']['fields']['DecodedRamBytes'] == RAM_BYTES, 'actual full CPU/RAM decode and fixed integer clock')
        for block in state['components'].values():
            check(block['type'] in schema and block['version'] == 1 and set(block['fields']) ==
                  {f['name'] for f in schema[block['type']]['fields'] if f['mode'] not in ['children','excluded']}, 'every reviewed component/version/saved/invariant field exactly once')
    base = result['programBase']
    check(base == ledger['programBase'], 'same actual original own load base')
    owned = ownership(ram,cut,program,table,base)
    check(bool(owned['mode']) == result['system'].startswith('emm386'), 'actual original manager/client mode')
    saved = ram[base+table[0]:base+table[1]]
    check(saved[:8] == b'S3PROOF1' and saved[-8:] == b'S3END001', 'actual complete own proof guards')
    critical_cut(cut,saved,table)
    readbacks(ledger['readbacks'],owned,table,base,final)
    check(len(ledger['frames']) == 3, 'three actual render configurations')
    pixels = sum(scanout(row,index,final) for index,row in enumerate(ledger['frames']))
    port_contract(ledger['ports'],base,final)
    # Reuse the reviewed original-server service contract with an explicit label mapping, never VM code.
    service_table = list(table)
    service_table[6],service_table[16],service_table[25] = table[8],table[15],table[24]
    legacy.original_services(ledger,[cut],service_table,base,owned['mode'],program)
    pcm_contract(ledger,cut,final,program,base)
    published_frames = audio_contract(result,cut,final,ledger)
    text = text_contract(final,final_ram,result['finalScreen'])
    proof,raw,expected_proof = files(root,saved,owned,table,ledger)
    for key in KEYS:
        check(ledger[key][ledger['cut'][key]:] == ledger['future'][key], 'every actual future '+key+' restored exactly')
    echo = result['echoAt']
    check(any(p['address'] == 0x60 and not p['write'] and p['value'] == 0x1C and p['at']['nanoseconds'] >= echo['nanoseconds'] for p in ledger['ports'][echo['ports']:]) and
          any(a['irq'] == 1 and not a['spurious'] for a in ledger['acks'][echo['acks']:]), 'actual newly consumed Enter and physical IRQ1 after graphics/DOS return')
    for point in ledger['boundaries']:
        clock(point['at'],final)
    negative = 0
    def reject(function,reason):
        """Require an independent contract to reject one deliberately discarded in-memory corruption."""
        nonlocal negative
        try:
            function()
        except (AssertionError,ValueError,KeyError,IndexError,struct.error):
            negative += 1
            return
        raise AssertionError('Accepted corrupt oracle: '+reason)
    for index in range(256):
        changed = bytearray(proof)
        changed[index] ^= 1
        reject(lambda data=changed: check(data == expected_proof,'whole actual DOS result differs'),'every DOS proof byte')
    for index in range(64):
        changed = list(ledger['readbacks'])
        row = dict(changed[index])
        data = bytearray(decode(row['bytes']))
        data[[0,32768,65535][index%3]] ^= 1
        row['bytes'] = base64.b64encode(data).decode()
        changed[index] = row
        reject(lambda rows=changed: readbacks(rows,owned,table,base,final),'actual full bank/LFB byte')
    for index in range(3):
        for member in ['width','height','pixelsSha256']:
            changed = copy.deepcopy(ledger['frames'][index])
            changed[member] = ('0'*64 if member == 'pixelsSha256' else changed[member]+1)
            reject(lambda row=changed,i=index: scanout(row,i,final),'actual whole scanout '+member)
        for register in [0x13,0x0C,0x69,1]:
            changed = copy.deepcopy(ledger['frames'][index])
            crtc = bytearray(decode(changed['registers']['_crtc']))
            crtc[register] ^= 1
            changed['registers']['_crtc'] = base64.b64encode(crtc).decode()
            reject(lambda row=changed,i=index: scanout(row,i,final),'actual S3 pitch/start/width register')
    for key,value in [('_writeComponent',0),('_writeIndex',0x38),('_attributeData',False),('_attrIndex',1),('_trioAdvanced',0)]:
        changed = copy.deepcopy(cut)
        changed['components']['video']['fields'][key] = value
        reject(lambda state=changed: critical_cut(state,saved,table),'actual open S3 state '+key)
    for index in [0,65535,65536,1048576,VRAM_BYTES-1]:
        changed = copy.deepcopy(cut)
        video = changed['components']['video']['fields']
        plane,offset = index & 3,index>>2
        data = bytearray(decode(video['_planes'][plane]))
        data[offset] ^= 1
        video['_planes'][plane] = base64.b64encode(data).decode()
        reject(lambda state=changed: critical_cut(state,saved,table),'actual physical S3 byte')
    for port in [0x46E8,0x102,0x3C8,0x3C9,0xCFC]:
        changed = [row for row in ledger['ports'] if row['address'] != port]
        reject(lambda rows=changed: port_contract(rows,base,final),'missing actual hardware cycle')
    if owned['mode']:
        pd = struct.unpack_from('<I',ram,base+table[22])[0]
        for at in [pd+4,pd+8,pd+12,pd+4096,pd+8192,pd+12288,pd+8192+2048]:
            changed = bytearray(ram)
            changed[at] ^= 4
            reject(lambda data=changed: ownership(data,cut,program,table,base),'actual original or own high alias PTE/PDE')
        for index,row in enumerate(ledger['services']):
            for member in ['ax','v86','es','di']:
                changed = dict(ledger)
                changed['services'] = copy.deepcopy(ledger['services'])
                point = changed['services'][index]['before']
                if member == 'ax':
                    point['registers'][0] ^= 1
                elif member == 'v86':
                    point['v86'] = not point['v86']
                elif member == 'es' and row['kind'] in ['vds-lock','vds-unlock']:
                    point['segments'][0] ^= 1
                elif member == 'di' and row['kind'] in ['vds-lock','vds-unlock']:
                    point['registers'][7] ^= 1
                else:
                    continue
                reject(lambda rows=changed: legacy.original_services(rows,[cut],service_table,base,owned['mode'],program),'actual original resident service input')
    for index in [0,255,511]:
        changed = dict(ledger)
        changed['pcmSamples'] = copy.deepcopy(ledger['pcmSamples'])
        changed['pcmSamples'][index]['raw'] ^= 1
        reject(lambda rows=changed: pcm_contract(rows,cut,final,program,base),'actual native accepted PCM byte')
    for member, value in [('frames',0),('frames',result['audioFuture']['frames']-1),('sha256',''),('sha256','0'*64)]:
        changed = dict(result)
        changed['audioFuture'] = dict(result['audioFuture'])
        changed['audioFuture'][member] = value
        reject(lambda data=changed: audio_contract(data,cut,final,ledger),'actual future PCM duration/hash')
    for index,row in enumerate(ledger['boundaries']):
        if row['kind'] not in ['tsc-before','tsc-after']:
            continue
        changed = dict(ledger)
        changed['boundaries'] = copy.deepcopy(ledger['boundaries'])
        changed['boundaries'][index]['at']['eip'] ^= 1
        reject(lambda rows=changed: files(root,saved,owned,table,rows),'actual exact RDTSC site')
    changed_ram = bytearray(final_ram)
    changed_ram[0x450] ^= 1
    reject(lambda: text_contract(final,changed_ram,result['finalScreen']),'actual complete original cursor')
    for key in KEYS:
        changed = dict(ledger['future'])
        changed[key] = []
        if ledger[key][ledger['cut'][key]:]:
            reject(lambda k=key,rows=changed: check(ledger[k][ledger['cut'][k]:] == rows[k],'future differs'),'actual future '+key)
    return dict(passed=True,system=result['system'],guestVcpi=bool(owned['mode']),cuts=1,restoredContinuations=1,readbacks=64,frames=3,
                verifiedVramBytes=4*1048576,pixels=pixels,publishedPcmFrames=published_frames,comparisons=result['comparisons'],negativeOracles=negative,
                rawFileHex=raw.hex().upper(),proofFileSha256=hashlib.sha256(proof).hexdigest().upper(),text=text,
                limitations=['Own86C764/2MiB/PCI target and named raw VGA/8bpp shared-bank/LAW subset.',
                             'Actual original VDS/VCPI service and all original DE01 PTEs retained.',
                             'Pixel surfaces hashed only in memory; no screenshot/image/native GL acceptance.',
                             'Native512DAC/DMA samples independently specified; whole published PCM bytes compared by actual peer digests, no new whole-mixer reconstruction.',
                             'No Graphics Engine/Streams/hardware cursor/original S3 driver/full VBE/Windows95 acceptance.'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence',type=Path)
    parser.add_argument('--program',required=True,type=Path)
    arguments=parser.parse_args()
    print(json.dumps(verify(arguments.evidence,arguments.program),indent=2))
