# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently certify actual STI/PIT frames, ENTER fault/restart, raw FILD and full own-PC snapshots."""
import argparse
import base64
import copy
import hashlib
import json
from pathlib import Path
import struct


def safe(path):
    """Resolve one explicitly named private artifact while rejecting personal game paths."""
    path = Path(path).resolve()
    if 'doom2-play' in str(path).lower():
        raise ValueError('Excluded personal path')
    return path


def read(path):
    """Read strictly one own JSON evidence file."""
    return json.loads(safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Hash only an explicitly named non-personal source or artifact."""
    with safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def check(value, reason):
    """Reject a false independently expected guest or container effect."""
    if not value:
        raise AssertionError(reason)


def integer(value):
    """Construct the exact 80-bit integer encoding using Python's independent unbounded integers."""
    if not value:
        return bytes(10)
    magnitude = abs(value)
    bits = magnitude.bit_length()
    return struct.pack('<QH', magnitude << (64 - bits), 16383 + bits - 1 | (0x8000 if value < 0 else 0))


def proof(data, labels):
    """Certify real ENTER frame/restart and every raw integer result, without emulator arithmetic."""
    check(len(data) == 160 and data[:8] == b'AU2CPU01' and data[-8:] == b'AU2END01', 'own proof boundaries')
    words = struct.unpack_from('<14I', data, 8)
    phase, start, end, faults, error, ip, cs, flags, sp, pushed, final_sp, bp, leave_sp, leave_bp = words
    check(phase == 4 and end > start, 'completed own phase and real BIOS IRQ ticks')
    check(faults == 1 and error == 0 and ip == labels[4] and cs == 8 and flags & ~0x10000 == 2 and sp == 0xF0,
          'actual stack-fault frame and original restart EIP')
    check((pushed, final_sp, bp, leave_sp, leave_bp) == (0x4242, 0x7C, 0xFC, 0x100, 0x4242), 'actual retry/LEAVE commit')
    values = [-(1 << 63), (1 << 63) - 1, -(1 << 31), (1 << 31) - 1, -(1 << 15), (1 << 15) - 1]
    check(data[64:124] == b''.join(integer(value) for value in values), 'six actual FILD/FSTP raw80 records')
    check(struct.unpack_from('<HH', data, 124) == (0x037F, 0), 'actual final FPU control/status')
    return end - start


def interrupt(row, labels):
    """Check a physical IRQ0 acknowledgement's real frame, priority bit and future timer deadline."""
    check(row['savedCs'] == 0x800 and labels[2] + 2 <= row['savedIp'] <= labels[3], 'actual timer return address in STI stream')
    check(row['savedFlags'] & 0x202 == 0x202 and row['isr'] == 1 and row['nextDeadline'] > row['time'], 'physical PIC service/flags/deadline')
    check(0x6F00 <= row['stack'] <= 0x6FFA and row['stack'] % 2 == 0, 'actual own real interrupt stack')


def normalized(cpu):
    """Require the actual normalized real segment selectors and descriptor-flag nibbles."""
    check(cpu['Halted'] and cpu['_segments'] == [0x800] * 6 and
          all(segment['base'] == 0x8000 and segment['limit'] == 65535 and segment['flags'] == 0 for segment in cpu['_segmentCaches']),
          'actual normalized real-mode segment caches at final HLT')


def verify(directory):
    """Check complete artifacts and independently reject altered proof/frame/container identities."""
    root = safe(directory)
    config = read(root.parent / 'input.json')
    result = read(root / 'result.json')
    manifest = read(root / 'cut.dossav/snapshot.json')
    state = read(root / 'cut.dossav/state.json')
    final = read(root / 'final-state.json')
    ledger = read(root / 'ledger.json')
    payload = safe(config['payload']).read_bytes()
    meta = payload.index(b'AU2META1')
    labels = struct.unpack_from('<16H', payload, meta + 8)
    ram = (root / 'cut.dossav/ram.bin').read_bytes()
    end = (root / 'final-ram.bin').read_bytes()
    check(result['passed'] and result['notDosAcceptance'] and result['comparisons'] > 10 and result['futureSteps'] > 12000, 'actual full own-PC future')
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1 and len(ram) == len(end) == manifest['ramBytes'], 'whole installed RAM')
    check(sha(root / 'cut.dossav/ram.bin') == manifest['ramSha256'] and sha(root / 'cut.dossav/state.json') == manifest['stateSha256'] and
          (root / 'cut.dossav/state.json').stat().st_size == manifest['stateBytes'] and sha(root / 'cut.dossav/media.dosemedia/media.json') == manifest['mediaSha256'], 'full protected cut identities')
    check(sha(root / 'final-state.json') == result['stateSha256'] and sha(root / 'final-ram.bin') == result['ramSha256'] and
          sha(config['image']) == result['imageSha256'] and sha(config['payload']) == result['payloadSha256'], 'actual final/source identities')
    cpu = state['components']['cpu']['fields']
    check(cpu['Eip'] == labels[2] + 4096 and cpu['_segments'][1] == 0x800 and cpu['_interruptShadow'] == 0 and cpu['Eflags'] & 0x200,
          'actual IF-already-enabled STI cut without artificial shadow')
    check(state['scheduler']['now'] == manifest['virtualNanoseconds'] and final['scheduler']['now'] == result['finalTime'], 'exact common scheduler boundaries')
    check(result['profile']['cpuHz'] == (100000000 if cpu['Model'] else 33000000) and len(ram) == (32 if cpu['Model'] else 8) * 1048576,
          'constructor-fixed actual CPU/clock/RAM tuple')
    data = end[0x8000 + labels[0]:0x8000 + labels[1]]
    check(data == base64.b64decode(result['proof'], validate=True), 'actual proof RAM equals observation')
    delta = proof(data, labels)
    check(delta == len(ledger['irqs']) == len(ledger['returns']) == len(ledger['eois']) == result['interruptEntries'] == result['interruptReturns'] == result['actualEois'],
          'all physical handler entries/EOIs/IRET returns and independent BDA ticks')
    check(any(row['savedIp'] < labels[3] for row in ledger['irqs']), 'interrupt accepted inside actual consecutive STI')
    for row, returned, eoi in zip(ledger['irqs'], ledger['returns'], ledger['eois']):
        interrupt(row, labels)
        check((returned['ip'], returned['cs']) == (row['savedIp'], row['savedCs']) and row['time'] < eoi['time'] < returned['time'] and
              eoi['value'] == 0x20 and eoi['width'] == 1 and eoi['isr'] & 1 == 0, 'actual EOI then IRET to saved address')
    check(ledger['futureIrqs'] == ledger['restoredIrqs'] and ledger['futureReturns'] == ledger['restoredReturns'] and ledger['futureEois'] == ledger['restoredEois'],
          'every actual future IRQ/EOI/return restored')
    expected = bytearray(payload)
    struct.pack_into('<H', expected, labels[4] + 1, 0x80)
    expected[labels[0]:labels[1]] = data
    expected[labels[10]:labels[10] + 6] = struct.pack('<HI', 65535, 0)
    expected[labels[11]:labels[11] + 6] = struct.pack('<HI', 1023, 0)
    expected[labels[12]:labels[12] + 4] = struct.pack('<I', 0x60000010)
    gates = bytearray(struct.pack('<HHHH', labels[15], 8, 0x8E00, 0) * 256)
    struct.pack_into('<HHHH', gates, 12 * 8, labels[14], 8, 0x8E00, 0)
    expected[labels[13]:labels[13] + 2048] = gates
    check(end[0x8000:0x8000 + len(payload)] == expected, 'all own static guest bytes and exact permitted self-modification')
    cut_expected = bytearray(payload)
    cut_expected[labels[0]:labels[1]] = ram[0x8000 + labels[0]:0x8000 + labels[1]]
    check(ram[0x8000:0x8000 + len(payload)] == cut_expected, 'unchanged executable bytes at full cut')
    check('AUDIT2-CPU-DONE' in result['finalDebug'] and 'FAILED' not in result['finalDebug'] and not final['components']['cpu']['fields']['Cr0'] & 1,
          'own final real CLI/HLT after actual protected return')
    end_cpu = final['components']['cpu']['fields']
    normalized(end_cpu)
    negatives = 0
    for at in [0, 8, 20, 24, 28, 32, 36, 40, 44, 48, 52, 56, 60, 124, 126, 159] + list(range(64, 124, 5)):
        changed = bytearray(data)
        changed[at] ^= 1
        try:
            proof(changed, labels)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('altered proof accepted')
    for row in ledger['irqs']:
        for key in ('savedIp', 'savedCs', 'savedFlags', 'isr', 'nextDeadline'):
            changed = dict(row)
            changed[key] = 0
            try:
                interrupt(changed, labels)
            except AssertionError:
                negatives += 1
            else:
                raise AssertionError('altered IRQ effect accepted')
    for index in range(6):
        changed = copy.deepcopy(end_cpu)
        changed['_segmentCaches'][index]['flags'] |= 4
        try:
            normalized(changed)
        except AssertionError:
            negatives += 1
        else:
            raise AssertionError('altered real D/B cache accepted')
    return dict(passed=True, negativeOracles=negatives, irqEntries=len(ledger['irqs']), eois=len(ledger['eois']), returns=len(ledger['returns']),
                fullComparisons=result['comparisons'], enterFaults=1, actualFildRecords=6, notDosAcceptance=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    print(json.dumps(verify(parser.parse_args().directory), indent=2))
