# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent LSS/PIT/frame/FPU/DOS/snapshot oracle. No VM implementation is imported."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]


def check(value, why):
    """Reject an incorrect raw result, source, instruction boundary or actual guest return."""
    if not value:
        raise AssertionError(why)


def load(path):
    """Read exactly one explicitly named non-personal evidence file."""
    check('doom2-play' not in str(path).lower(), 'personal play journals excluded')
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Stream one explicitly named private payload into an independent SHA-256 identity."""
    check('doom2-play' not in str(path).lower(), 'personal play journals excluded')
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def certify(work, program, result, ram, events, ports, cut, manifest, manager):
    """Certify atomic LSS, real PIT acceptance, distinct SS controls, raw FPU restore and actual whole-state continuation."""
    root, saved = work / 'evidence', work / 'evidence/pending.dossav'
    check(result['passed'] and result['comparisons'] >= 10 and result['steps'] > 1000000, 'real guest continuation')
    check('LSS-FAILED' not in result['debug'] and result['debug'].rfind('A:\\>') > result['debug'].rfind('LSS-DONE') >= 0, 'real DOS prompt after TSR')
    check(manifest == result['manifest'] and manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1, 'actual snapshot format')
    check(manifest['ramBytes'] == len(ram) == 8388608 and manifest['ramSha256'] == sha(saved / 'ram.bin'), 'full captured RAM')
    check(manifest['stateBytes'] == (saved / 'state.json').stat().st_size and manifest['stateSha256'] == sha(saved / 'state.json') == result['capturedStateSha256'], 'full captured state')
    check(manifest['mediaSha256'] == sha(saved / 'media.dosemedia/media.json'), 'sealed media boundary')
    check(result['finalStateSha256'] == sha(root / 'final-state.json') and result['finalRamSha256'] == sha(root / 'final-ram.bin'), 'actual final payloads')
    schema = load(REPO / 'docs/state-schema-v1.json')['nodes']
    for block in cut['components'].values():
        check(block['type'] in schema and block['version'] == 1, 'known component type')
        check(set(block['fields']) == {f['name'] for f in schema[block['type']]['fields'] if f['mode'] not in ('children', 'excluded')}, 'every explicit state field')
    cpu = cut['components']['cpu']['fields']
    pic = cut['components']['pic/_master']['fields']
    check(cut['scheduler']['now'] == manifest['virtualNanoseconds'] and cpu['_interruptShadow'] == 0 and cpu['Eflags'] & 0x200, 'LSS cut permits immediate IRQ')
    check(pic['Irr'] & 1 and not pic['Mask'] & 1 and not pic['Isr'] & 1, 'actual PIT pending before acknowledge')
    check(load(root / 'final-a.dosemedia/media.json')['media'] == load(root / 'final-b.dosemedia/media.json')['media'], 'identical continued media')
    address = result['proofAddress']
    check(ram[address:address + 8] == b'LSSPR001', 'actual proof location')
    segment = struct.unpack_from('<H', ram, address + 8)[0]
    offset = program.index(b'LSSPR001')
    check(address == segment * 16 + 256 + offset and ram[segment * 16 + 256:address] == program[:offset], 'unchanged own executed code')
    check(struct.unpack_from('<3H', ram, address + 22) == (1, 5, 5), 'own pass/case/handler counts')
    check(struct.unpack_from('<H', ram, address + 424)[0] == 0xA55A, 'FPU buffer guard')
    check(ram[address + 208:address + 316] == ram[address + 316:address + 424], 'all 108 original FPU bytes restored')
    expected = [program.index(bytes.fromhex('FB0FB226')) + 6 + 256,
                program.index(bytes.fromhex('FB660FB226')) + 7 + 256,
                program.index(bytes.fromhex('FB0FB206')) + 6 + 256,
                program.index(bytes.fromhex('FB8ED0BC')) + 6 + 256,
                program.index(bytes.fromhex('FB17BC')) + 5 + 256]
    irq = [e for e in events if e['kind'] == 'irq']
    lss = [e for e in events if e['kind'] == 'lss']
    check(len(irq) == 5 and len(lss) == 3, 'actual physical IRQ and LSS counts')
    check(irq[0]['time'] == manifest['virtualNanoseconds'] and cpu['Eip'] == expected[0], 'first IRQ immediately after captured LSS')
    for i in range(5):
        position = address + 48 + i * 32
        exp, ip, cs, flags, ss, guard, esp, count = struct.unpack_from('<6HIH', ram, position)
        target = len(program) | (0x12340000 if i == 1 else 0)
        check((exp, ip, cs, ss, esp, count, guard) == (expected[i], expected[i], segment, segment + 16, target, 1, 0xBEEF), 'independent IP/CS/new-SS/full-ESP/handler frame')
        check(flags & 0x200 and not flags & 0x100 and struct.unpack_from('<I', ram, position + 28)[0] == 0xFACECAFE, 'actual frame flags/guard')
        event = irq[i]
        check(event['line'] == 0 and event['canAccept'] and not event['spurious'], 'actual accepted PIT source')
        # EMM386 can route physical interrupts through a monitor. Its guest frame above is checked separately.
        if not manager:
            check((event['ip'], event['cs'], event['ss'], event['esp']) == (ip, cs, ss, esp), 'physical IRQ before following guest instruction')
        last = [p for p in ports if p['time'] <= event['time'] and p['write'] and p['port'] == 0x21][-1]
        check(last['value'] == 0xFE, 'guest explicitly unmasks only IRQ0')
        pending = [p for p in ports if not p['write'] and p['port'] == 0x20 and p['time'] < last['time']][-1]
        check(pending['value'] & 1, 'actual pending IRR read before STI/LSS')
        bases, icw = 8, False
        for row in ports:
            if row['time'] > event['time']:
                break
            if row['write'] and row['port'] == 0x20 and row['value'] & 0x10:
                icw = True
            elif row['write'] and row['port'] == 0x21 and icw:
                bases, icw = row['value'] & 0xF8, False
        check(event['vector'] == bases, 'actual ICW2 vector mapping')
    check(all(54000000 < b['time'] - a['time'] < 56000000 for a, b in zip(irq, irq[1:])), 'real periodic PIT deadlines')
    begin, end = irq[0]['time'], irq[-1]['time']
    eois = [p for p in ports if begin <= p['time'] < end + 1000000 and p['write'] and p['port'] == 0x20 and p['value'] == 0x20]
    check(len(eois) >= 5, 'actual master EOIs')
    old_ip, old_cs = struct.unpack_from('<HH', ram, address + 16)
    check(struct.unpack_from('<HH', ram, 8 * 4) == (old_ip, old_cs), 'restored DOS vector')
    for port, value in [(0x21, ram[address + 20]), (0xA1, ram[address + 21])]:
        check([p['value'] for p in ports if p['write'] and p['port'] == port][-1] == value, 'restored PIC mask')
    mcb = (segment - 1) * 16
    check(ram[mcb] in (0x4D, 0x5A) and struct.unpack_from('<H', ram, mcb + 1)[0] == segment and struct.unpack_from('<H', ram, mcb + 3)[0] == (len(program) + 0x10F) // 16, 'own limited resident MCB')
    if manager:
        check(result['virtual8086Instructions'] > 1000 and cpu['Cr3'] != 0 and cpu['Eflags'] & 0x20000, 'real original EMM386 paging/V86')
    return dict(cases=5, physicalIrqs=5, lssBoundaries=3, fullFpuBytes=108, continuedComparisons=result['comparisons'], proofSha256=hashlib.sha256(ram[address:address + 426]).hexdigest().upper())


def verify(work, program, manager=False):
    """Certify raw evidence and require independent mutations of bytes, frames, sources and clocks to fail."""
    work = Path(work); root = work / 'evidence'; program = Path(program).read_bytes()
    result, events, ports = load(root / 'result.json'), load(root / 'events.json'), load(root / 'ports.json')
    ram = (root / 'final-ram.bin').read_bytes(); cut = load(root / 'pending.dossav/state.json'); manifest = load(root / 'pending.dossav/snapshot.json')
    evidence = certify(work, program, result, ram, events, ports, cut, manifest, manager)
    mutations = []
    for key in ['passed', 'comparisons', 'steps', 'proofAddress', 'finalStateSha256', 'finalRamSha256', 'debug']:
        changed = dict(result); changed[key] = False if isinstance(changed[key], bool) else 0 if isinstance(changed[key], int) else 'bad'
        mutations.append((changed, ram, events, ports, cut, manifest))
    for offset in [0, 22, 24, 26, 48, 50, 52, 56, 60, 64, 208, 424]:
        changed = bytearray(ram); changed[result['proofAddress'] + offset] ^= 1
        mutations.append((result, changed, events, ports, cut, manifest))
    for field in ['_interruptShadow', 'Eflags']:
        changed = copy.deepcopy(cut); changed['components']['cpu']['fields'][field] = 1 if field == '_interruptShadow' else 2
        mutations.append((result, ram, events, ports, changed, manifest))
    for field in ['Irr', 'Mask']:
        changed = copy.deepcopy(cut); changed['components']['pic/_master']['fields'][field] = 0 if field == 'Irr' else 255
        mutations.append((result, ram, events, ports, changed, manifest))
    for field in ['spurious', 'vector', 'canAccept']:
        changed = copy.deepcopy(events); event = next(e for e in changed if e['kind'] == 'irq'); event[field] = not event[field] if isinstance(event[field], bool) else event[field] + 1
        mutations.append((result, ram, changed, ports, cut, manifest))
    rejected = 0
    for row in mutations:
        try:
            certify(work, program, *row, manager)
        except (AssertionError, KeyError, IndexError):
            rejected += 1
    check(rejected == len(mutations), 'all corrupted observations rejected')
    return dict(passed=True, negativeOracles=rejected, **evidence)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('program', type=Path)
    parser.add_argument('--manager', action='store_true')
    args = parser.parse_args()
    print(json.dumps(verify(args.directory, args.program, args.manager), indent=2))
