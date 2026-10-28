# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent actual CF9/INIT/reset, physical DMA cancellation, DOS ownership and full-PC continuation proof."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('piix_reset_independent_media', REPO / 'scripts/verify-piix-ide.py')
legacy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(legacy)
media, console = legacy.media, legacy.console
RAM_BYTES, HZ, MASK = 32 * 1048576, 100_000_000, (1 << 64) - 1
NAMES = ['soft', 'hard', 'held']
FPU_FIELDS = ['FpuControlWord', 'FpuStatusWord', '_fpuRegisters', '_fpuEmpty', '_fpuIp', '_fpuData',
              '_fpuRealIp', '_fpuRealData', '_fpuCs', '_fpuDs', '_fpuOpcode', '_publishedFpuError']


def check(condition, reason):
    """Reject a missing original guest, architecture, domain, data or identity effect."""
    if not condition:
        raise AssertionError(reason)


def read(path):
    """Read one explicitly named private textual observation."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def digest(path):
    """Hash one explicitly named private evidence file without discovering unrelated media."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def decode(value):
    """Decode strict raw byte observations, independently of the VM serializer."""
    return base64.b64decode(value, validate=True)


def fields(state, suffix):
    """Locate exactly one named hardware type, without importing or activating emulator classes."""
    values = [node['fields'] for node in state['components'].values() if node['type'].endswith(suffix)]
    check(len(values) == 1, 'unique registered ' + suffix)
    return values[0]


def clock(point, last):
    """Reconstruct 100-MHz phase and modular written/reset epochs from integer virtual time."""
    check(0 <= point['nanoseconds'] <= last['scheduler']['now'], 'observed point precedes final virtual clock')
    ticks = point['nanoseconds'] // 10
    check(point['ticks'] == ticks and point['counter'] == (ticks - point['epoch']) & MASK, 'independent complete integer TSC')
    return ticks


def metadata(program):
    """Bind all assembler labels, extents and exact request/RDTSC/INT instruction bytes."""
    check(program.count(b'RESETM01') == 1, 'unique own reset metadata')
    at = program.index(b'RESETM01')
    check(struct.unpack_from('<HH', program, at + 8) == (1, 32), 'reset metadata version/count')
    table = struct.unpack_from('<32H', program, at + 12)
    check(table[3] == len(program) + 256 and table[1] - table[0] == 256 and table[2] < table[0] < table[3], 'complete immutable own code/proof extents')
    for index, opcode in [(7, b'\xEE'), (8, b'\xEF'), (9, b'\x66\xEF')]:
        check(program[table[index] - 256:table[index] - 256 + len(opcode)] == opcode, 'actual byte/dword/word CF9 OUT')
    for index in [28, 29, 30]:
        check(program[table[index] - 256:table[index] - 254] == b'\x0F\x31', 'actual unchanged RDTSC')
    for index in [12, 14, 15, 31]:
        check(program[table[index] - 256:table[index] - 254] == bytes([0xCD, 0x67 if index == 12 else 0x4B]), 'actual original VCPI/VDS INT')
    return table


def initial_bytes(lba, count, seed=0x31):
    """Specify every scratch byte from its independent sector and within-sector identities."""
    return bytes((sector * 13 + position * 17 + (position >> 8) * 7 + seed) & 255
                 for sector in range(lba, lba + count) for position in range(512))


def write_bytes():
    """Specify the own attempted write that the physical hard reset must cancel before commit."""
    return bytes((index * 29 + 0x5B) & 255 for index in range(1024))


def ownership(ram, state, program, table, base, mode, name):
    """Prove complete DOS MCB/PSP/JFT ownership, private tables/stack and original DE01 PTE preservation."""
    cpu = state['components']['cpu']['fields']
    check(base > 16 and base & 15 == 0 and base + table[3] < 0xA0000 and ram[base:base + 2] == b'\xCD\x20', 'actual own PSP')
    paragraphs = (table[3] + 15) // 16
    mcb = ram[base - 16:base]
    check(mcb[0] in [77, 90] and struct.unpack_from('<HH', mcb, 1) == (base >> 4, paragraphs), 'full actual own code/data/stack MCB')
    check(ram[base + 256:base + table[2]] == program[:table[2] - 256], 'every unchanged executable byte')
    proof = ram[base + table[0]:base + table[1]]
    check(proof[:8] == b'RSTPROF1' and proof[-8:] == b'RSTEND01' and struct.unpack_from('<I', proof, 12)[0] == base, 'own fixed proof/base')
    allocation = struct.unpack_from('<H', proof, 172)[0] << 4
    buffer = struct.unpack_from('<I', proof, 16)[0]
    block = ram[allocation - 16:allocation]
    check(block[0] in [77, 90] and struct.unpack_from('<HH', block, 1) == (base >> 4, 0x4002), 'complete separate DOS DMA MCB')
    check(struct.unpack_from('<HH', proof, 174) == (base >> 4, 0x4002), 'guest observed actual DMA ownership')
    check(base + paragraphs * 16 <= allocation - 16 or allocation + 0x40020 <= base - 16, 'disjoint complete DOS allocations')
    check(buffer & 0x1FFFF == 0 and allocation + 16 <= buffer and buffer + 131088 <= allocation + 0x40020 <= 0xA0000, 'owned aligned DMA region plus both guards')
    check(ram[buffer - 16:buffer] == ram[buffer + 131072:buffer + 131088] == bytes([0xD6]) * 16, 'both exact physical boundary guards')
    check(struct.unpack_from('<IIHHI', proof, 184) == (131072, 0, buffer >> 4, 0, buffer), 'actual complete identity VDS DDS')
    check(struct.unpack_from('<I', proof, 8)[0] == mode and struct.unpack_from('<HHH', proof, 178) == (mode, 0, mode), 'actual original VDS lock/translation disable before cut')
    handle = struct.unpack_from('<H', ram, base + table[17])[0]
    count, offset, segment = struct.unpack_from('<3H', ram, base + 0x32)
    jft = (segment << 4) + offset
    check(5 <= handle < count <= 255 and jft + count <= 0xA0000 and 3 <= ram[jft + handle] < 0x80, 'actual ordinary live DOS JFT file handle')
    check(struct.unpack_from('<I', ram, 0x467)[0] == ((base >> 4) << 16 | table[4]) and struct.unpack_from('<H', ram, 0x472)[0] == 0x1234, 'actual explicitly armed own firmware cookies')
    if name != 'held':
        check(cpu['_segments'][1] == 8 and cpu['_segments'][2] == 16 and cpu['_segmentCaches'][1]['base'] == base and cpu['_segmentCaches'][2]['base'] == 0,
              'actual own protected client CS/flat SS, never monitor')
        check(cpu['Cr0'] & 0x80000001 == (0x80000001 if mode else 1) and not cpu['Eflags'] & 0x20000 and
              cpu['_registers'][4] == base + table[26] and cpu['_registers'][5] == base, 'actual own 32-bit private stack/base')
        check(cpu['Eip'] == table[7 if name == 'soft' else 9] + (1 if name == 'soft' else 2), 'cut after completed actual triggering OUT')
    if mode:
        pd = struct.unpack_from('<I', ram, base + table[23])[0]
        check(base <= pd and pd + 8192 <= base + table[3] and pd & 4095 == 0, 'entire private PD/PT in own DOS block')
        pde = struct.unpack_from('<I', ram, pd)[0]
        check(pde & 0xFFFFF000 == pd + 4096 and pde & 7 == 7 and not any(ram[pd + 4:pd + 4096]), 'single original first-4MiB client PDE and no extra mappings')
        for index in range(1024):
            original = struct.unpack_from('<I', ram, base + table[24] + index * 4)[0]
            actual = struct.unpack_from('<I', ram, pd + 4096 + index * 4)[0]
            check(original & ~0x60 == actual & ~0x60 and actual & original == original, 'every DE01 server PTE retained apart from actual A/D additions')
        pages = set(range(base & ~4095, (base + paragraphs * 16 + 4095) & ~4095, 4096)) | set(range(buffer - 4096, buffer + 135168, 4096))
        for page in pages:
            pte = struct.unpack_from('<I', ram, pd + 4096 + (page >> 12) * 4)[0]
            check(pte & 7 == 7 and pte & 0xFFFFF000 == page, 'actual complete own user/writable identity mapping')
        if name != 'held':
            check(cpu['Cr3'] == pd, 'actual private original-server client CR3')
    return dict(pspSegment=base >> 4, comParagraphs=paragraphs, allocation=allocation, buffer=buffer, prdt=buffer + 0x1F000, fileHandle=handle, jftBase=jft)


def reset_cpu(cpu, fetched=False, cr0=0x60000010):
    """Specify the historical integer RESET/INIT image, allowing only the observed first fetched opcode."""
    check(cpu['_registers'] == [0, 0, 0x524, 0, 0, 0, 0, 0] and cpu['_segments'] == [0, 0xF000, 0, 0, 0, 0], 'actual reset signature/GPR/selector image')
    check(cpu['Eip'] == (0xFFF1 if fetched else 0xFFF0) and cpu['Eflags'] == 2 and cpu['Cr0'] == cr0 and
          cpu['Cr2'] == cpu['Cr3'] == cpu['Cr4'] == 0 and cpu['Dr6'] == 0xFFFF0FF0 and cpu['Dr7'] == 0x400, 'actual reset IP/flags/control/debug image')
    check(cpu['Gdtr'] == cpu['Idtr'] == dict(limit=0xFFFF, base=0) and cpu['Tr'] == cpu['Ldtr'] == 0 and
          not cpu['Halted'] and not cpu['TripleFaulted'] and not cpu['_repValid'], 'actual descriptor/restart image')
    for index, cache in enumerate(cpu['_segmentCaches']):
        check(cache['base'] == (0xFFFF0000 if index == 1 else 0) and cache['limit'] == 65535 and cache['flags'] == 0 and cache['access'] == 0x93 and cache['usable'] is True, 'actual complete hidden reset segment image')


def reset_fpu(cpu):
    """Specify CPURST positive-zero raw registers, CW0040, SW0 and valid-zero tags5555; no FNINIT substitute."""
    check(cpu['FpuControlWord'] == 0x40 and cpu['FpuStatusWord'] == 0 and cpu['_fpuEmpty'] == 0 and not cpu['_publishedFpuError'], 'actual hard-reset x87 status/control/tags')
    check(len(cpu['_fpuRegisters']) == 8 and all(r['significand'] == r['signExponent'] == 0 for r in cpu['_fpuRegisters']), 'all eight raw positive-zero registers')
    check(all(cpu[k] == 0 for k in ['_fpuIp', '_fpuData', '_fpuRealIp', '_fpuRealData', '_fpuCs', '_fpuDs', '_fpuOpcode']), 'all hard-reset x87 pointer/opcode fields')


def domain(state, name, ram, owned, table, base):
    """Specify each real request/hold domain, physical pending write and every cancelled/live relevant deadline."""
    now, cpu = state['scheduler']['now'], state['components']['cpu']['fields']
    reset = fields(state, '.PiixResetController')
    events = [e for e in state['scheduler']['events'] if e['id'] not in state['scheduler']['cancelled']]
    channels = [n['fields'] for k, n in state['components'].items() if n['type'].endswith('Intel82371FbIde.Channel') and k.endswith('/0')]
    check(len(channels) == 1, 'one actual primary BM channel')
    c, ata = channels[0], state['components']['ata0/_drives/1']['fields']
    check(reset['SoftEdges'] == 1 and reset['HardEdges'] == (0 if name == 'soft' else 1), 'only actual two distinct reset edges')
    if name != 'held':
        phase, read = (1 if name == 'soft' else 2), name == 'soft'
        check(reset['_pending'] == (1 if read else 2) and reset['_phase'] == 0 and reset['_control'] == (4 if read else 6), 'pending request after OUT before owner consumption')
        check(reset['_pciEvent'] == reset['_cpuEvent'] == 0 and not reset['_cpuReleaseReady'], 'pending request has not asserted a physical hold')
        check(c['Command'] == (9 if read else 1) and c['Status'] == 0x61 and c['Prdt'] == owned['prdt'] and c['Loaded'] and c['Eot'], 'actual live busmaster single EOT PRD')
        transferred = 32 if read else 512
        check(c['RegionAddress'] == owned['buffer'] and c['RegionLength'] == 1024 and c['RegionOffset'] == c['BytesTransferred'] == transferred and
              c['DescriptorAddress'] == owned['prdt'], 'actual partial PRD/sector cursor')
        check(ata['_dmaDirection'] == phase and ata['_offset'] == transferred and ata['_remaining'] == 2 and ata['_lba'] == (8 if read else 32) and
              ata['_dmaSelected'] and ata['WrittenSectorCount'] == 0, 'actual selected private scratch DMA cursor with no media commit')
        check(decode(ata['_data']) == (initial_bytes(8, 1) if read else write_bytes()[:512]), 'all real staged ATA bytes')
        check(ram[owned['prdt']:owned['prdt'] + 8] == struct.pack('<II', owned['buffer'], 0x80000400), 'all actual physical PRD bits')
        window = bytearray([0xA5]) * 131072
        window[0x1F000:0x1F008] = struct.pack('<II', owned['buffer'], 0x80000400)
        window[:32 if read else 1024] = initial_bytes(8, 1)[:32] if read else write_bytes()
        check(ram[owned['buffer']:owned['buffer'] + 131072] == window, 'every complete DMA-window byte before request')
        if read:
            check(any(e['operation'] == 'primary-burst' and e['time'] > now for e in events), 'real future next READ grant')
        else:
            check(ata['Status'] == 0x80 and any(e['id'] == ata['_event'] and e['owner'] == 'ata0/_drives/1' and
                  e['operation'] == 'dma-write-commit' and now < e['time'] <= now + 5000 for e in events), 'real full staged first-sector future commit')
        sb = fields(state, '.SoundBlaster16')
        check(0 < sb['RemainingSamples'] <= 512 and not sb['_resetHigh'] and sb['_ready'] and not sb['_busy'] and not sb['Paused'] and
              not sb['_word'] and not sb['_stereo'] and not sb['_recording'] and sb['_activeRate'] == 8000 and sb['_activePeriod'] == 0 and
              sb['_sampleEvent'] > 0 and sb['_commandEvent'] == sb['_resetEvent'] == 0, 'real active unpaused mono8/8000Hz PCM engine before both requests')
        samples = [e for e in events if e['id'] == sb['_sampleEvent']]
        check(len(samples) == 1 and samples[0]['owner'] == 'sb16' and samples[0]['operation'] == 'event2' and samples[0]['payload'] == [] and
              samples[0]['time'] == sb['_sampleDeadline'] > now and samples[0]['time'] <= now + 125000, 'exact live DSP sample token/deadline')
        check(sb['Dma8Grants'] == sb['TransferredSamples'] == 512 - sb['RemainingSamples'] and sb['Dma16Grants'] == sb['CompletedBlocks'] == 0, 'all actual accepted PCM DMA grants and committed samples')
        pit = fields(state, '.Pit8254')
        check(pit['_running'] and pit['_event'] > 0 and any(e['owner'] == 'pit' and e['id'] == pit['_event'] and
              e['operation'] == 'event0' and e['payload'] == [] and e['time'] > now for e in events), 'exact real live PIT event token before both requests')
    else:
        reset_cpu(cpu)
        reset_fpu(cpu)
        check(reset['_pending'] == 0 and reset['_phase'] == 1 and reset['_control'] == 0 and not reset['_cpuReleaseReady'], 'actual physical all-domain hold')
        check(now == reset['_assertedAt'] + 500000 and reset['_pciReleaseAt'] == reset['_assertedAt'] + 1000000 and
              reset['_cpuReleaseAt'] == reset['_assertedAt'] + 2000000, 'independent exact nominal hold deadlines')
        live = [e for e in events if e['owner'] == 'piix-reset']
        check([(e['id'], e['time'], e['operation'], e['payload']) for e in live] ==
              [(reset['_pciEvent'], reset['_pciReleaseAt'], 'release-pci-isa', []),
               (reset['_cpuEvent'], reset['_cpuReleaseAt'], 'release-cpu-ready', [])], 'both exact physical release callbacks preserved')
        check(not any((e['owner'] == 'piix-ide' and e['operation'] in ['primary-burst', 'secondary-burst']) or
                      (e['owner'].startswith(('ata0/', 'ata1/')) and e['operation'] in ['dma-read-ready', 'dma-write-ready', 'dma-write-commit']) or
                      (e['owner'] == 'pit' and e['operation'] == 'event0') or
                      (e['owner'] == 'sb16' and e['operation'] in ['event0', 'event1', 'event2']) for e in events),
              'no live old ATA/BM/PIT/DSP command/sample/ready callback during all-domain hold')
        sb = fields(state, '.SoundBlaster16')
        check(sb['RemainingSamples'] == 0 and sb['_resetHigh'] and not sb['Paused'] and sb['_sampleEvent'] == sb['_commandEvent'] == sb['_resetEvent'] == 0 and
              sb['DacLeft'] == sb['DacRight'] == 0 and not sb['_ready'], 'actual hard-held DSP stop and zero DAC, without an old sample token')
        for node in state['components'].values():
            if node['type'].endswith('Dma8237.Channel'):
                c_dma = node['fields']
                check(c_dma['Masked'] is True and not c_dma['Requested'] and not c_dma['TerminalCount'] and
                      all(c_dma[k] == 0 for k in ['Address', 'BaseAddress', 'Count', 'BaseCount', 'Page', 'Mode']), 'all real hard-reset ISA DMA channels masked/cleared')
        check(c['Command'] == c['Status'] == c['Prdt'] == c['DescriptorAddress'] == c['RegionAddress'] == c['BytesTransferred'] == 0 and not c['Loaded'],
              'hard reset clears BM configuration/cursor without media commit')
        check(ata['_dmaDirection'] == ata['_offset'] == ata['_remaining'] == ata['_event'] == 0 and ata['_resetActive'] and ata['_control'] == 4 and
              ata['WrittenSectorCount'] == 0, 'actual ATA pin held and old write cancelled')
        host, ide, bridge = fields(state, '.Intel82437Fx'), fields(state, '.Intel82371FbIde'), fields(state, '.Intel82371FbIrq')
        host_config, ide_config, bridge_config = map(lambda x: decode(x['_config']), [host, ide, bridge])
        check(host_config[0x60:0x65] == bytes([2]) * 5 and not any(host_config[0x59:0x60]), 'physical host reset DRB8MiB/PAM')
        check(ide_config[4] == 2 and struct.unpack_from('<I', ide_config, 0x20)[0] == 1 and not any(ide_config[0x40:0x44]), 'actual Fn1 IOSE/BME/IDETIM/BMIBA reset')
        check(bridge_config[0x60:0x64] == bytes([0x80]) * 4 and bridge_config[0x70:0x72] == bytes([0x80]) * 2 and bridge['_elcr'] == 0, 'actual bridge routes/ELCR reset')
        check(state['components']['memory']['fields']['DecodedRamBytes'] == 8 * 1048576, 'hard host decoder resets8MiB while all32MiB storage persists')
        pit = fields(state, '.Pit8254')
        check(pit['_resetOutputLow'] is True and not pit['_running'] and pit['_event'] == 0, 'actual PIT output domain held/stopped')
        for node in state['components'].values():
            if node['type'].endswith('Pic8259.Chip'):
                check(node['fields']['Mask'] == 255 and node['fields']['Irr'] == node['fields']['Isr'] == node['fields']['LevelMask'] == 0, 'actual PIC service/mask/level reset')


def proof_contract(data, saved, mode, table, base, ledger, final):
    """Bind all own result fields to independently specified values and actual timestamp instruction observations."""
    check(len(data) == 256 and data[:8] == b'RSTPROF1' and data[-8:] == b'RSTEND01', 'actual complete RESET.DAT extent/guards')
    check(struct.unpack_from('<5I', data, 8) == (mode, base, struct.unpack_from('<I', saved, 16)[0], 3, 2), 'actual mode/base/window/phases/warm entries')
    check(struct.unpack_from('<4I', data, 28) == (1, 0, 0x524, 0x135), 'actual completed DOS proof/no guest fault/exact CPUID subset')
    raw = struct.pack('<QH', 0xC90FDAA22168C235, 0x4000) + struct.pack('<QH', 0xFEDCBA9876543210, 0x4001)
    check(data[44:64] == raw and struct.unpack_from('<HH', data, 64) == (0x37F, 0), 'raw extended x87 values/CW/SW preserved across actual INIT')
    check(data[76:104] == struct.pack('<6HIHHIHH', 0x40, 0xFFFF, 0, 0xFFFF, 0x5555, 0xFFFF, 0, 0, 0, 0, 0, 0xFFFF),
          'every actual post-RESET 32-bit FNSTENV control/status/tag/pointer/reserved byte')
    check(data[128:178] == saved[128:178] and data[184:200] == saved[184:200], 'all original flags/system/BIOS/RTC/KBC/DOS allocation/DDS values retained')
    check(struct.unpack_from('<HHH', data, 178) == (mode, mode, mode), 'actual original VDS lock/unlock/translation lifecycle')
    check(struct.unpack_from('<HH', data, 200) == (1, 1) and struct.unpack_from('<4I', data, 204) == (1, 1, 1, 1), 'both consumed cookies, same-high rejection, explicit recovery and cancelled write')
    check(struct.unpack_from('<II', data, 224) == (3, 1), 'one initial and two warm protected entries, one actual original DOS return')
    check(not any(data[68:76]) and not any(data[220:224]) and not any(data[232:248]), 'untouched proof guards')
    boundaries = ledger['boundaries']
    timestamps = [b for b in boundaries if b['kind'].startswith('tsc-')]
    check([b['kind'] for b in timestamps] == ['tsc-before', 'tsc-soft', 'tsc-hard'], 'three actual own timestamp instructions')
    for index, row in enumerate(timestamps):
        check(struct.unpack_from('<Q', data, 104 + index * 8)[0] == row['at']['counter'], 'actual full EDX:EAX RDTSC stored bytes')
        check(row['at']['eip'] == table[28 + index] + 1, 'actual static RDTSC boundary after first0F opcode byte')
        clock(row['at'], final)



def original_services(ledger, states, table, base, mode, program):
    """Bind actual original VDS/VCPI calls, carry results, real/V86 context and own client/server returns."""
    entries = [row for row in ledger['boundaries'] if row['kind'] == 'dos-entry']
    check(len(entries) == 1, 'one actual original DOS entry before any own mode switch')
    first = entries[0]['at']
    check(first['segments'][1] == base >> 4 and first['bases'][1] == base and first['eip'] == 0x101 and
          first['v86'] == bool(mode) and first['cr0'] & 0x80000001 == (0x80000001 if mode else 0), 'actual original real DOS or original EMM386 V86/PE/PG entry')
    expected = ['vds-lock', 'vds-disable', 'vcpi-presence', 'vcpi-interface', 'vcpi-enter', 'vcpi-return', 'vds-enable', 'vds-unlock'] if mode else []
    check([row['kind'] for row in ledger['services']] == expected, 'exact original VDS/VCPI lifecycle, no silent fallback')
    functions = {'vds-lock': 0x8103, 'vds-disable': 0x810B, 'vds-enable': 0x810C, 'vds-unlock': 0x8104,
                 'vcpi-presence': 0xDE00, 'vcpi-interface': 0xDE01, 'vcpi-enter': 0xDE0C, 'vcpi-return': 0xDE0C}
    for row in ledger['services']:
        kind, before, after, offset = row['kind'], row['before'], row['after'], row['offset']
        check(after is not None and before['registers'][0] & 65535 == functions[kind] and before['bases'][1] == base and
              before['eip'] == offset + 1, 'actual original service AX and immutable own call site')
        opcode = program[offset - 256:offset - 254]
        if kind != 'vcpi-return':
            check(opcode == bytes([0xCD, 0x67 if kind.startswith('vcpi') else 0x4B]) and
                  before['v86'] and before['cr0'] & 0x80000001 == 0x80000001 and before['segments'][1] == base >> 4,
                  'actual original monitored V86 INT call, not host entry or monitor replacement')
        else:
            check(opcode[0] == 0xFF and not before['v86'] and before['cr0'] & 0x80000001 == 0x80000001 and
                  before['segments'][1] == 8 and before['segments'][3] == 0x28, 'actual private client DE0C far-call through original server-data selector')
        if kind in ['vds-lock', 'vds-unlock']:
            for point in [before, after]:
                check(point['segments'][0] == base >> 4 and point['bases'][0] == base and
                      point['registers'][7] & 65535 == table[16] and point['bases'][0] + (point['registers'][7] & 65535) == base + table[16],
                      'actual original VDS ES selector/hidden base and DI bind the same entire owned DDS on entry/return')
        if kind == 'vcpi-enter':
            switch = before['registers'][6] - base
            check(256 <= switch <= table[3] - 22, 'actual DE0C switch frame is inside the complete own executable')
            frame = program[switch - 256:switch - 234]
            target = struct.unpack_from('<I', frame, 16)[0]
            check(256 <= target < table[2] and frame == struct.pack('<IIIHHIH', 0, 0, 0, 0, 0x18, target, 8) and
                  program.count(frame) == 1 and program[target - 256:target - 252] == bytes([0x66, 0xB8, 0x10, 0]) and
                  after['eip'] == target + 2,
                  'actual DE0C ESI binds unique immutable switch frame; entry consumed only66h prefix andB8h opcode')
            check(not after['v86'] and after['cr0'] & 0x80000001 == 0x80000001 and after['segments'][1] == 8 and
                  after['bases'][1] == base, 'actual original server transferred into own paging client')
        elif kind == 'vcpi-return':
            check(after['v86'] and after['cr0'] & 0x80000001 == 0x80000001 and after['segments'][1] == after['segments'][2] == base >> 4 and
                  after['eip'] == table[6] + 1 and after['registers'][4] & 65535 == table[25], 'actual original server returned own V86 CS:IP/SS:SP')
        else:
            check(after['v86'] and after['cr0'] & 0x80000001 == 0x80000001 and after['segments'][1] == base >> 4 and
                  after['bases'][1] == base and after['eip'] == offset + 3, 'actual own original V86 service return')
            if kind.startswith('vds'):
                check(not after['flags'] & 1, 'actual original VDS carry success')
            if kind.startswith('vcpi'):
                check(after['registers'][0] & 0xFF00 == 0, 'actual original VCPI AH success')
            if kind == 'vds-lock':
                check(before['registers'][2] & 65535 == 0x2C and before['registers'][7] & 65535 == table[16] and
                      before['segments'][0] == base >> 4, 'actual entire contiguous no-bounce VDS lock DDS request')
            if kind in ['vds-disable', 'vds-enable']:
                check(before['registers'][3] & 65535 == 1 and before['registers'][2] & 65535 == 0, 'actual VDS channel1 translation toggle BX/DX')
            if kind == 'vds-unlock':
                check(before['registers'][2] & 65535 == 0 and before['registers'][7] & 65535 == table[16] and
                      before['segments'][0] == base >> 4, 'actual original matching VDS unlock DDS')
    if mode:
        interface = next(row for row in ledger['services'] if row['kind'] == 'vcpi-interface')
        check(interface['after']['registers'][3] != 0, 'actual original DE01 server entry returned in EBX')
        enter = next(row for row in ledger['services'] if row['kind'] == 'vcpi-enter')
        leave = next(row for row in ledger['services'] if row['kind'] == 'vcpi-return')
        check(enter['after']['cr3'] == states[0]['components']['cpu']['fields']['Cr3'] == leave['before']['cr3'] and
              leave['after']['cr3'] == first['cr3'], 'original monitor and private client address spaces remain distinct and restored')


def pcm_lifecycle(ledger, states, final, program, base):
    """Specify real mono PCM grants/publications, exact device tokens and their cancellation by hard assertion."""
    transfers, samples = ledger['pcmTransfers'], ledger['pcmSamples']
    check(len(transfers) == 1, 'only one actual PCM start; no hidden restart after hard reset')
    transfer = transfers[0]
    check({k: transfer[k] for k in ['rate', 'word', 'stereo', 'signed', 'recording', 'autoInit', 'fifoRequested', 'samples', 'dmaChannel', 'periodNanoseconds']} ==
          dict(rate=8000, word=False, stereo=False, signed=False, recording=False, autoInit=False, fifoRequested=False,
               samples=512, dmaChannel=1, periodNanoseconds=0), 'actual independent mono8/8000Hz/512 transfer contract')
    hard = fields(states[1], '.SoundBlaster16')
    held = fields(states[2], '.SoundBlaster16')
    end = fields(final, '.SoundBlaster16')
    expected_count = hard['TransferredSamples']
    check(expected_count > 0 and len(samples) == expected_count and held['TransferredSamples'] == end['TransferredSamples'] == expected_count and
          held['Dma8Grants'] == end['Dma8Grants'] == expected_count and held['Dma16Grants'] == end['Dma16Grants'] == 0 and
          end['RemainingSamples'] == end['_sampleEvent'] == end['_commandEvent'] == 0, 'old DSP/DMA grants stop at actual hard assertion and never restart')
    for index, row in enumerate(samples, 1):
        check(row == dict(time=transfer['time'] + index * 125000, word=False, recording=False, right=False, raw=0xC0,
                          value=16384, dmaResult='Transferred'), 'every actual accepted raw DMA byte/DAC publication and integer sample deadline')
    reset = fields(states[2], '.PiixResetController')
    check(samples[-1]['time'] <= reset['_assertedAt'], 'no old sample publication after physical hard assertion')
    sample_at = program.index(bytes([0xC0]) * 512) + 256 + base
    check(program.count(bytes([0xC0]) * 512) == 1, 'unique immutable own sample block')
    for state in states[:2]:
        sb = fields(state, '.SoundBlaster16')
        channel = state['components']['dma/_channels/1']['fields']
        count = sb['TransferredSamples']
        check((channel['Page'] << 16 | channel['BaseAddress']) == sample_at and channel['BaseCount'] == 511 and channel['Mode'] == 0x49 and
              not channel['Masked'] and channel['Address'] == (sample_at + count) & 65535 and channel['Count'] == 511 - count,
              'actual physical ISA DMA1 sample base/count/mode/cursor')
    old_tokens = {states[1]['components']['pit']['fields']['_event'], hard['_sampleEvent'], hard['_commandEvent'],
                  states[1]['components']['ata0/_drives/1']['fields']['_event']}
    old_tokens.discard(0)
    for state in [states[2], final]:
        live = [e for e in state['scheduler']['events'] if e['id'] not in state['scheduler']['cancelled']]
        check(not any(e['id'] in old_tokens for e in live), 'no live old PIT/DSP command/sample/ATA commit token after hard assertion')
    for row in ledger['changes']:
        if row['nanoseconds'] < reset['_assertedAt'] or row['resets'] < 2:
            continue
        pcm = row['pcm']
        check(pcm['transferred'] == pcm['dma8'] == expected_count and pcm['dma16'] == pcm['remaining'] == pcm['left'] == pcm['right'] == pcm['blocks'] == 0 and
              pcm['active'] is False, 'no old PCM DMA/DAC state commit through peripheral and CPU release')

def execution(ledger, states, points, table, base, final):
    """Prove actual request widths, same-high suppression, integer INIT preservation and physical RESET release."""
    ports = ledger['ports']
    own_cf9 = [p for p in ports if p['address'] == 0xCF9 and p['at']['phase'] > 0]
    check([(p['write'], p['width'], p['value']) for p in own_cf9] ==
          [(True, 1, 4), (False, 1, 0), (True, 4, 0xABCDEF04), (True, 1, 0), (True, 2, 6)],
          'actual byte INIT, readback, held-high DWORD suppression, rearm and word hard request')
    check(any(p['write'] and p['address'] == 0xCF8 and p['width'] == 4 and p['value'] == 0x80000400 and p['at']['resets'] == 0 for p in ports),
          'actual undivided CF8 dword does not alias CF9 lane')
    vector = [b for b in ledger['boundaries'] if b['kind'] == 'reset-vector']
    check(len(vector) == 2 and [b['at']['resetSource'] for b in vector] == ['cf9-init', 'cf9-hard'] and
          [b['at']['resets'] for b in vector] == [1, 2], 'exact actual two reset vectors and owner sources')
    soft_cpu, held_cpu = states[0]['components']['cpu']['fields'], states[2]['components']['cpu']['fields']
    for index, row in enumerate(vector):
        cpu = row['cpu']['fields']
        reset_cpu(cpu, True, (soft_cpu['Cr0'] & 0x60000000) | 0x10 if index == 0 else 0x60000010)
        check(row['at']['warmFlag'] == 0x1234 and row['at']['reset']['phase'] == 0 and not row['at']['reset']['cpuHeld'], 'actual fetch occurs after owner reset/release with cookie still armed')
        check(row['at']['nanoseconds'] == row['at']['resetTime'] + (0 if index == 0 else 2000000), 'no opcode while held and exact first owner fetch deadline')
        if index == 0:
            check(all(cpu[key] == soft_cpu[key] for key in FPU_FIELDS) and row['at']['epoch'] == points[0]['epoch'], 'INIT preserves every raw x87 field and full written epoch')
        else:
            reset_fpu(cpu)
            check(row['at']['epoch'] == row['at']['ticks'] and row['at']['counter'] == 0 and
                  all(cpu[key] == held_cpu[key] for key in FPU_FIELDS), 'RESET release reanchors TSC without repeating x87 initialization')
    warm = [b for b in ledger['boundaries'] if b['kind'] == 'warm-stub']
    check(len(warm) == 2 and [b['at']['resets'] for b in warm] == [1, 2], 'actual ROM dispatched to own warm stub twice')
    for row in warm:
        cpu = row['cpu']['fields']
        check(cpu['Cr0'] & 1 == 0 and cpu['_segments'][1] == base >> 4 and cpu['_segments'][2] == 0 and cpu['_registers'][4] == 0x7C00,
              'real early firmware stub conditions before own private stack setup')
        check(not row['at']['flags'] & 0x600 and struct.unpack_from('<I', decode(row['proof']), 24)[0] == row['at']['resets'] - 1,
              'actual CLI/CLD firmware and as-yet-unincremented own warm entry')
    write = next(b for b in ledger['boundaries'] if b['kind'] == 'wrmsr')
    written = 0x12345678FFFFFFE0
    epoch = (write['at']['ticks'] - written) & MASK
    check(points[0]['epoch'] == points[1]['epoch'] == epoch and points[0]['counter'] > written and points[1]['counter'] > points[0]['counter'],
          'complete modular written epoch retained through INIT and first pending hard cut')
    check(points[2]['epoch'] == points[2]['reset']['assertedAt'] // 10 and points[2]['counter'] == 50000, 'held RESET TSC counts from assertion until release reanchor')
    commands = [p for p in ports if p['write'] and p['address'] == 0x1F7 and p['value'] in [0xC8, 0xCA] and p['at']['phase'] > 0]
    check([(p['value'], p['width'], p['at']['phase']) for p in commands] == [(0xC8, 1, 1), (0xCA, 1, 2), (0xC8, 1, 2)], 'three actual scratch DMA commands, including post-reset readback')
    starts = [p for p in ports if p['write'] and p['address'] == 0xC000 and p['value'] & 1 and p['at']['phase'] > 0]
    check([p['value'] for p in starts] == [9, 1, 9], 'three actual BM directions/start operations')
    changes = ledger['changes']
    for command, maximum in [(commands[0], 1024), (commands[1], 512), (commands[2], 512)]:
        begin, phase = command['at']['nanoseconds'], command['at']['phase']
        end = commands[commands.index(command) + 1]['at']['nanoseconds'] if command is not commands[-1] else final['scheduler']['now']
        expected = {}
        transferred, deadline = 0, begin + 5000 + 7680
        while transferred < maximum:
            transferred += 32
            expected[transferred] = deadline
            deadline += 7680 + (5000 if transferred % 512 == 0 else 0)
        actual = {}
        for row in changes:
            n = row['dma']['bytesTransferred']
            if begin < row['nanoseconds'] < end and row['phase'] == phase and 0 < n <= maximum and n not in actual:
                actual[n] = row['nanoseconds']
        check(actual == expected, 'all actual nominal 32-byte DMA grant deadlines before/after resets')
    for row in [p['at'] for p in ports] + [b['at'] for b in ledger['boundaries']] + changes:
        clock(row, final)
    for name, future in zip(NAMES, ledger['futures']):
        cut = ledger['cuts'][name]
        for key in ['ports', 'acks', 'boundaries', 'services', 'changes', 'pcmTransfers', 'pcmSamples']:
            check(ledger[key][cut[key]:] == future[key], 'every future actual ' + key + ' publication from ' + name + ' restored')


def final_media(root, saved_proof):
    """Replay all sealed COWs and walk real DOS FAT files; the cancelled scratch write must never reach media."""
    proofs, raw_values, generations = [], [], []
    for index in range(4):
        directory = root / ('final-' + str(index) + '.dosemedia')
        manifest = read(directory / 'media.json')
        generations.append(manifest['media'])
        drive = legacy.disk(directory)
        proofs.append(legacy.fat_file(drive, b'RESET   DAT'))
        raw_values.append(legacy.fat_file(drive, b'RSTDATA DAT'))
        scratch = legacy.disk(directory, 1)
        check(scratch.sectors == 4096 and all(scratch.sector(n) == initial_bytes(n, 1) for n in range(4096)), 'all entire private scratch bytes preserved; pending write never committed')
        for slot, seed in [(2, 0x91), (3, 0xD1)]:
            disk = legacy.disk(directory, slot)
            check(disk.sectors == 4096 and all(disk.sector(n) == initial_bytes(n, 1, seed) for n in range(4096)), 'complete untouched private auxiliary IDE position')
        check(raw_values[-1] == initial_bytes(32, 1), 'actual post-hard-reset DMA DOS file contains original cancelled-write sector')
    check(all(value == proofs[0] for value in proofs) and all(value == raw_values[0] for value in raw_values) and
          all(value == generations[0] for value in generations), 'all four actual DOS files and six media generations equal')
    for name in NAMES:
        directory = root / (name + '-cut.dossav') / 'media.dosemedia'
        check(legacy.fat_file(legacy.disk(directory), b'RESET   DAT') == b'RSTPROF1', 'actual flushed header and still-open original DOS file at ' + name)
    return proofs[0], raw_values[0]



def standard_text(final, ram, reported):
    """Decode own standard-VGA80x25 cells/attributes from four64KiB planes and cross-check actual BIOS state."""
    video = final['components']['video']['fields']
    planes = [decode(value) for value in video['_planes']]
    crtc, gc, seq, attr = (decode(video[key]) for key in ['_crtc', '_gc', '_seq', '_attr'])
    check(video['IsEt4000'] is False and len(planes) == 4 and all(len(value) == 65536 for value in planes),
          'actual standard-VGA four64KiB planes, never ET4000 geometry')
    check(video['_displayEnabled'] and seq[0] & 3 == 3 and not seq[1] & 0x20 and gc[6] & 0x0D == 0x0C and
          seq[4] & 2 and seq[3] == 0 and gc[5] & 0x10, 'actual enabled standard-VGA text odd/even B800 decoder')
    columns = crtc[1] + 1
    lines = crtc[0x12] + ((crtc[7] & 2) << 7) + ((crtc[7] & 0x40) << 3) + 1
    height = (crtc[9] & 31) + 1
    rows = (lines // (2 if crtc[9] & 128 else 1)) // height
    check((columns, rows, height) == (80, 25, 16) and ram[0x449] & 127 == 3 and
          struct.unpack_from('<H', ram, 0x44A)[0] == columns and ram[0x484] + 1 == rows and ram[0x485] == height,
          'actual standard-VGA80x25/16-line console agrees with original DOS BDA')
    start, stride = (crtc[12] << 8) | crtc[13], crtc[0x13] * 2
    check(stride == 80, 'actual standard console row stride')
    indices = [(start + row * stride + column) & 65535 for row in range(rows) for column in range(columns)]
    characters = bytes(planes[0][index] for index in indices)
    attributes = bytes(planes[1][index] for index in indices)
    cells = b''.join(bytes((char, attribute)) for char, attribute in zip(characters, attributes))
    lines = [''.join(chr(value) if 32 <= value < 127 else ' ' for value in characters[row * columns:(row + 1) * columns]).rstrip()
             for row in range(rows)]
    text = '\n'.join(lines).rstrip()
    check(text == '\n'.join(reported.splitlines()).rstrip() and 'SNAPSHOT-DONE' in text and 'PIIX-RESET-DONE' in text and
          text.endswith('A:\\>'), 'reported original DOS text equals all actual standard-VGA cells')
    echo_rows = [row for row, line in enumerate(lines) if line == 'RESETCONTINUED']
    prompt_row = max(row for row, line in enumerate(lines) if line)
    check(len(echo_rows) == 1 and echo_rows[0] < prompt_row and all(line == '' for line in lines[echo_rows[0] + 1:prompt_row]) and lines[prompt_row] == 'A:\\>',
          'actual new ECHO output row followed by original command prompt')
    visible = list(range(echo_rows[0] * columns, echo_rows[0] * columns + len('RESETCONTINUED'))) + list(range(prompt_row * columns, prompt_row * columns + 4))
    for index in visible:
        attribute = attributes[index]
        foreground, background = attribute & 15, (attribute >> 4) & (7 if attr[0x10] & 8 else 15)
        check(not attribute & 0x80 and attr[foreground & attr[0x12] & 15] != attr[background & attr[0x12] & 15],
              'actual ECHO/prompt glyph attributes remain visible and nonblinking')
        glyph = planes[2][characters[index] * 32:characters[index] * 32 + height]
        check(any(glyph) and any(value != 255 for value in glyph), 'actual retained ECHO/prompt font has visible foreground and background pixels')
    cursor = (crtc[14] << 8) | crtc[15]
    expected_cursor = start + prompt_row * stride + 4
    check(cursor == expected_cursor and struct.unpack_from('<H', ram, 0x450)[0] == prompt_row * 256 + 4,
          'actual new original DOS cursor agrees between CRTC and BIOS')
    return dict(columns=columns, rows=rows, cellsSha256=hashlib.sha256(cells).hexdigest().upper(),
                attributesSha256=hashlib.sha256(attributes).hexdigest().upper(), echoRow=echo_rows[0], promptRow=prompt_row)


def verify(root, program_path):
    """Certify exact full containers, own guest/domain/clock effects and independently rejected corruptions."""
    root = media.safe(root)
    result, final, ledger = read(root / 'result.json'), read(root / 'final-state.json'), read(root / 'ledger.json')
    states = [read(root / (name + '-cut.dossav/state.json')) for name in NAMES]
    manifests = [read(root / (name + '-cut.dossav/snapshot.json')) for name in NAMES]
    points = [read(root / (name + '-cut-point.json')) for name in NAMES]
    rams = [(root / (name + '-cut.dossav/ram.bin')).read_bytes() for name in NAMES]
    program = media.safe(program_path).read_bytes()
    table = metadata(program)
    check(result['passed'] is True and result['returnedToDos'] is True and result['postResetEcho'] is True and result['exits'] == [True] * 4 and
          result['comparisons'] > 5 and result['futureSteps'] > 0, 'four actual original DOS returns and later real KBC/ECHO execution')
    check('PIIX-RESET-DONE' in result['beforeEcho'] and 'RESETCONTINUED' not in result['beforeEcho'] and '\nRESETCONTINUED' in result['finalScreen'] and
          result['finalScreen'].rstrip().endswith('A:\\>') and result['initialPost'] == result['finalPost'] > 0,
          'actual original DOS prompts and own warm dispatch without repeated POST')
    check(result['programSha256'] == hashlib.sha256(program).hexdigest().upper() and result['finalStateSha256'] == digest(root / 'final-state.json') and
          result['finalRamSha256'] == digest(root / 'final-ram.bin'), 'entire immutable own program and final raw graph/RAM hashes')
    check(len((root / 'final-ram.bin').read_bytes()) == RAM_BYTES and all(len(ram) == RAM_BYTES for ram in rams), 'every full installed32MiB RAM image')
    base, schema = result['programBase'], read(REPO / 'docs/state-schema-v1.json')['nodes']
    check(base == ledger['programBase'], 'one actual own original DOS load base')
    mode = struct.unpack_from('<I', rams[0], base + table[0] + 8)[0]
    check(mode in [0, 1] and (mode == 1) == result['system'].startswith('emm386'), 'actual original manager/client mode')
    captures = result['captures']
    for index, (name, state, manifest) in enumerate(zip(NAMES, states, manifests)):
        save = root / (name + '-cut.dossav')
        check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and manifest['ramBytes'] == RAM_BYTES and
              manifest['virtualNanoseconds'] == state['scheduler']['now'] == points[index]['nanoseconds'], 'full shared-clock captured container')
        check(manifest == captures[index]['manifest'] and manifest['stateSha256'] == captures[index]['stateSha256'] == digest(save / 'state.json') and
              manifest['ramSha256'] == captures[index]['ramSha256'] == digest(save / 'ram.bin') and manifest['mediaSha256'] == digest(save / 'media.dosemedia/media.json') and
              manifest['stateBytes'] == (save / 'state.json').stat().st_size, 'all full graph/RAM/media sealed identities')
        profile = read(save / 'media.dosemedia/media.json')['machine']['hardware']
        check(profile['id'] == 'dose-430fx-piix-reset-dev-v1' and profile['cpu'] == 'p54c-b5-subset-development' and profile['cpuHz'] == HZ and profile['ramMiB'] == 32,
              'constructor-fixed actual reset profile/model/clock/RAM')
        cpu = state['components']['cpu']['fields']
        check(cpu['Model'] == 1 and cpu['TimestampFrequency'] == HZ and state['scheduler']['cachedFrequency'] == HZ and state['scheduler']['remainder'] == 0, 'fixed CPU/frequency and full integer virtual phase')
        for block in state['components'].values():
            check(block['type'] in schema and block['version'] == 1 and set(block['fields']) ==
                  {f['name'] for f in schema[block['type']]['fields'] if f['mode'] not in ['children', 'excluded']}, 'exact reviewed every field/type/version')
        owned = ownership(rams[index], state, program, table, base, mode, name)
        domain(state, name, rams[index], owned, table, base)
        clock(points[index], final)
    saved_proof = rams[0][base + table[0]:base + table[1]]
    proof, raw = final_media(root, saved_proof)
    proof_contract(proof, saved_proof, mode, table, base, ledger, final)
    execution(ledger, states, points, table, base, final)
    original_services(ledger, states, table, base, mode, program)
    pcm_lifecycle(ledger, states, final, program, base)
    text_ram = (root / 'final-ram.bin').read_bytes()
    text_proof = standard_text(final, text_ram, result['finalScreen'])
    echo = result['echoAt']
    check(any(p['address'] == 0x60 and not p['write'] and p['width'] == 1 and p['value'] == 0x1C and
              p['at']['nanoseconds'] >= echo['nanoseconds'] for p in ledger['ports'][echo['ports']:]) and
          any(a['irq'] == 1 and not a['spurious'] and a['at']['nanoseconds'] >= echo['nanoseconds'] for a in ledger['acks'][echo['acks']:]),
          'actual newly admitted ECHO Enter was consumed at real port60 after physical IRQ1')
    check(final['components']['memory']['fields']['DecodedRamBytes'] == RAM_BYTES and decode(fields(final, '.Intel82437Fx')['_config'])[0x60:0x65] == bytes([8]) * 5,
          'actual guest PCI DRB restoration before original DOS return')
    negative = 0
    # These are discarded in-memory observations, never modified containers or edited media.
    def reject(function, description):
        """Require an independent contract to reject a concrete one-field/one-byte corruption."""
        nonlocal negative
        try:
            function()
        except (AssertionError, KeyError, IndexError, ValueError, struct.error):
            negative += 1
            return
        raise AssertionError('Accepted corrupt oracle: ' + description)
    for at in range(256):
        bad = bytearray(proof)
        bad[at] ^= 1
        reject(lambda b=bytes(bad): proof_contract(b, saved_proof, mode, table, base, ledger, final), 'own DOS result byte' + str(at))
    for index, name in enumerate(NAMES):
        for member in ['SoftEdges', 'HardEdges', '_pending', '_phase', '_control', '_cpuReleaseReady']:
            bad = copy.deepcopy(states[index])
            reset = fields(bad, '.PiixResetController')
            reset[member] = not reset[member] if isinstance(reset[member], bool) else reset[member] + 1
            reject(lambda s=bad, i=index, n=name: domain(s, n, rams[i], ownership(rams[i], s, program, table, base, mode, n), table, base), name + '/' + member)
        for at in [base - 15, base - 13, base + 256, struct.unpack_from('<H', saved_proof, 172)[0] * 16 - 15,
                   struct.unpack_from('<I', saved_proof, 16)[0] - 1, struct.unpack_from('<I', saved_proof, 16)[0] + 131072]:
            bad = bytearray(rams[index])
            bad[at] ^= 1
            reject(lambda r=bad, i=index, n=name: ownership(r, states[i], program, table, base, mode, n), name + '/ownership/guard')
    for member in ['ticks', 'counter', 'epoch', 'nanoseconds']:
        bad = copy.deepcopy(points[0])
        bad[member] += 1 if member != 'nanoseconds' else 10
        reject(lambda p=bad: clock(p, final), 'integer timestamp ' + member)
    for field in ['_registers', 'Eip', 'Eflags', 'Cr0', 'Cr3', 'Dr7']:
        bad = copy.deepcopy(states[2]['components']['cpu']['fields'])
        if isinstance(bad[field], list):
            bad[field][0] ^= 1
        else:
            bad[field] ^= 1
        reject(lambda c=bad: reset_cpu(c), 'held raw CPU ' + field)
    for field in FPU_FIELDS:
        bad = copy.deepcopy(states[2]['components']['cpu']['fields'])
        if field == '_fpuRegisters':
            bad[field][0]['significand'] = 1
        elif isinstance(bad[field], bool):
            bad[field] = not bad[field]
        else:
            bad[field] += 1
        reject(lambda c=bad: reset_fpu(c), 'held raw x87 ' + field)
    for index, row in enumerate(ledger['services']):
        for side, member in [('before', 'registers'), ('before', 'v86'), ('after', 'v86'), ('after', 'eip')]:
            bad = dict(ledger)
            bad['services'] = copy.deepcopy(ledger['services'])
            point = bad['services'][index][side]
            if member == 'registers':
                point[member][0] ^= 1
            elif isinstance(point[member], bool):
                point[member] = not point[member]
            else:
                point[member] ^= 1
            reject(lambda l=bad: original_services(l, states, table, base, mode, program), 'original resident service ' + row['kind'] + '/' + side + '/' + member)
    for index, row in enumerate(ledger['services']):
        if row['kind'] not in ['vds-lock', 'vds-unlock']:
            continue
        for side in ['before', 'after']:
            for member in ['es-selector', 'es-base', 'di']:
                bad = dict(ledger)
                bad['services'] = copy.deepcopy(ledger['services'])
                point = bad['services'][index][side]
                if member == 'es-selector':
                    point['segments'][0] ^= 1
                elif member == 'es-base':
                    point['bases'][0] ^= 16
                else:
                    point['registers'][7] ^= 1
                reject(lambda l=bad: original_services(l, states, table, base, mode, program), 'actual original VDS ' + row['kind'] + '/' + side + '/' + member)
    for name, state, ram in zip(NAMES, states, rams):
        owner = ownership(ram, state, program, table, base, mode, name)
        for member in ['_sampleEvent', 'RemainingSamples', 'Dma8Grants']:
            bad = copy.deepcopy(state)
            fields(bad, '.SoundBlaster16')[member] += 1
            if name == 'held' and member == 'Dma8Grants':
                changed_states = [states[0], states[1], bad]
                reject(lambda st=changed_states: pcm_lifecycle(ledger, st, final, program, base), name + '/actual PCM ' + member)
            else:
                reject(lambda st=bad, n=name, r=ram, o=owner: domain(st, n, r, o, table, base), name + '/actual PCM ' + member)
    for index in range(len(ledger['pcmSamples'])):
        bad = dict(ledger)
        bad['pcmSamples'] = copy.deepcopy(ledger['pcmSamples'])
        bad['pcmSamples'][index]['raw'] ^= 1
        reject(lambda l=bad: pcm_lifecycle(l, states, final, program, base), 'actual accepted PCM sample raw byte')
    for index in [0, -1]:
        bad = dict(ledger)
        bad['pcmSamples'] = copy.deepcopy(ledger['pcmSamples'])
        bad['pcmSamples'][index]['time'] += 1
        reject(lambda l=bad: pcm_lifecycle(l, states, final, program, base), 'actual native PCM sample deadline')
    for index in [0, 1, 2]:
        bad = copy.deepcopy(states[2])
        event = dict(id=9999990 + index, time=bad['scheduler']['now'] + 1,
                     owner=['ata0/_drives/1', 'pit', 'sb16'][index],
                     operation=['dma-write-ready', 'event0', 'event2'][index], payload=[])
        bad['scheduler']['events'].append(event)
        reject(lambda st=bad: domain(st, 'held', rams[2], ownership(rams[2], st, program, table, base, mode, 'held'), table, base), 'old hard-cancelled callback')
    for plane, cell, value in [(0, text_proof['echoRow'] * 80, 1), (1, text_proof['echoRow'] * 80, 0)]:
        bad = copy.deepcopy(final)
        content = bytearray(decode(bad['components']['video']['fields']['_planes'][plane]))
        content[cell] = (content[cell] ^ value) if plane == 0 else value
        bad['components']['video']['fields']['_planes'][plane] = base64.b64encode(content).decode()
        reject(lambda st=bad: standard_text(st, text_ram, result['finalScreen']), 'actual standard-VGA ECHO character/attribute')
    for member, index, mask in [('_gc', 6, 1), ('_seq', 0, 3), ('_crtc', 1, 1)]:
        bad = copy.deepcopy(final)
        content = bytearray(decode(bad['components']['video']['fields'][member]))
        content[index] ^= mask
        bad['components']['video']['fields'][member] = base64.b64encode(content).decode()
        reject(lambda st=bad: standard_text(st, text_ram, result['finalScreen']), 'actual standard-VGA decoder/geometry')
    bad_ram = bytearray(text_ram)
    bad_ram[0x449] ^= 1
    reject(lambda: standard_text(final, bad_ram, result['finalScreen']), 'actual original DOS console BDA')
    return dict(passed=True, system=result['system'], guestVcpi=bool(mode), cuts=3, restoredContinuations=3,
                resetVectors=2, softInitEdges=1, hardResetEdges=1, dmaCommands=3, cancelledWriteSectors=1,
                comparisons=result['comparisons'], negativeOracles=negative, text=text_proof, rawFileHex=raw.hex().upper(),
                proofFileSha256=hashlib.sha256(proof).hexdigest().upper(),
                limitations=['Own early DOSE warm-cookie firmware dispatch; no universal IBM BIOS contract.',
                             'Original resident DOS/EMM386 retained; private client descriptors/tables only.',
                             'Own nominal PCI/ISA1ms and CPU2ms holds; no electrical reset timing calibration.',
                             'Only new private scratch primary-slave DMA; no general Windows95/ATAPI/platform acceptance.'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence', type=Path)
    parser.add_argument('--program', required=True, type=Path)
    arguments = parser.parse_args()
    print(json.dumps(verify(arguments.evidence, arguments.program), indent=2))
