# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent snapshot/hash/field-set and actual DOSCOW/FAT raw-x87 proof oracle; no VM implementation imports."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('media_oracle', Path(__file__).with_name('verify-media-checkpoint.py'))
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)


def check(value, reason):
    """Reject an incorrect raw artifact, omitted state field, clock or actual guest result."""
    if not value:
        raise AssertionError(reason)


def read(path):
    """Read a specifically named non-personal JSON artifact."""
    return json.loads(media.safe(path).read_text(encoding='utf-8-sig'))


def sha(path):
    """Stream exactly one named artifact, reusing only the independent oracle's path protection."""
    with media.safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def certify(work, result, state, manifest):
    """Certify capture and actual future against schema, sealed media and mathematically fixed raw extended operands."""
    root = work / 'evidence'
    saved = root / 'cut.dossav'
    check(manifest['kind'] == 'DOSE-PC-STATE-1' and manifest['version'] == 1, 'full PC format')
    check(manifest['ramBytes'] == 8 * 1024 * 1024 and manifest['ramSha256'] == sha(saved / 'ram.bin'), 'raw capture RAM')
    check(manifest['stateBytes'] == (saved / 'state.json').stat().st_size and manifest['stateSha256'] == sha(saved / 'state.json'), 'capture state bytes')
    check(manifest['mediaSha256'] == sha(saved / 'media.dosemedia/media.json'), 'sealed topology identity')
    check(result['passed'] and result['comparisons'] > 1 and result['futureSteps'] > 10000 and result['exitA'] and result['exitB'], 'actual original/resumed execution')
    check(result['capturedStateSha256'] == sha(saved / 'state.json') and result['capturedRamSha256'] == manifest['ramSha256'], 'captured oracle identities')
    check(result['finalStateSha256'] == sha(root / 'final-state.json') and result['finalRamSha256'] == sha(root / 'final-ram.bin'), 'actual final payloads')
    check(result['pcmFrames'] > 480 and result['dma'] == 512 and len(result['pcmSha256']) == 64, 'real DMA and PCM continuation')
    check(state['scheduler']['now'] == manifest['virtualNanoseconds'], 'shared CPU/device/media clock')
    check(read(saved / 'media.dosemedia/media.json')['virtualNanoseconds'] == manifest['virtualNanoseconds'], 'sealed media boundary clock')
    schema = read(REPO / 'docs/state-schema-v1.json')['nodes']
    for block in state['components'].values():
        check(block['type'] in schema and block['version'] == 1, 'known component format')
        expected = {v['name'] for v in schema[block['type']]['fields'] if v['mode'] not in ['children', 'excluded']}
        check(set(block['fields']) == expected, 'every reviewed raw field exactly once')
    check(state['components']['floppy']['fields']['ReadSectorCount'] > 0, 'program loaded through actual floppy path')
    check(state['components']['cpu']['fields']['X87Instructions'] >= 3, 'actual x87 instructions before cut')
    events = {(r['owner'], r['operation']) for r in read(REPO / 'docs/state-events-v1.json')['events']}
    ids = [r['id'] for r in state['scheduler']['events']]
    check(len(ids) == len(set(ids)), 'stable unique event tokens')
    for event in state['scheduler']['events']:
        check(event['time'] >= state['scheduler']['now'] and 0 < event['id'] <= state['scheduler']['sequence'], 'pending event boundary')
        check((state['components'][event['owner']]['type'], event['operation']) in events, 'known stateful callback')
    # The OS and guest are not trusted as a file oracle: independently replay journals and walk FAT chains.
    expected = b'SNAPRAW1' + struct.pack('<QH', 0xC90FDAA22168C235, 0x4000) + struct.pack('<QH', 0xFEDCBA9876543210, 0x4001) + struct.pack('<HH', 0x037F, 0)
    for name in ['final-a.dosemedia', 'final-b.dosemedia']:
        directory = root / name
        metadata = read(directory / 'media.json')
        disk = metadata['machine']['hardDisks'][0]
        basis = media.safe(directory / disk['imagePath'])
        journal = media.safe(directory / disk['overlayPath'])
        check(media.Disk(basis, journal).file(b'SNAP    DAT') == expected, 'actual DOS file preserves exact 80-bit values after resume')
    check(read(root / 'final-a.dosemedia/media.json')['media'] == read(root / 'final-b.dosemedia/media.json')['media'], 'both future journal generations exactly equal')
    return dict(componentCount=len(state['components']), pendingEvents=len(ids), rawFileHex=expected.hex().upper())


def verify(work):
    """Certify one actual original-versus-restored DOS execution and reject independent report/state/clock corruptions."""
    work = media.safe(work)
    result = read(work / 'evidence/result.json')
    state = read(work / 'evidence/cut.dossav/state.json')
    manifest = read(work / 'evidence/cut.dossav/snapshot.json')
    proof = certify(work, result, state, manifest)
    mutations = []
    for key in ['passed', 'comparisons', 'futureSteps', 'exitA', 'exitB', 'capturedStateSha256', 'capturedRamSha256', 'finalStateSha256', 'finalRamSha256', 'pcmFrames', 'dma', 'pcmSha256']:
        changed = copy.deepcopy(result)
        changed[key] = False if isinstance(changed[key], bool) else 0 if isinstance(changed[key], (int, float)) else 'bad'
        mutations.append((changed, state, manifest))
    for key in ['kind', 'version', 'ramBytes', 'ramSha256', 'stateBytes', 'stateSha256', 'mediaSha256', 'virtualNanoseconds']:
        changed = copy.deepcopy(manifest)
        changed[key] = 0 if isinstance(changed[key], int) else 'bad'
        mutations.append((result, state, changed))
    for node, field in [('cpu', 'Cr0'), ('cpu', '_fpuRegisters'), ('memory', 'A20Enabled'), ('pic/_master', 'Irr'), ('video', '_planes')]:
        changed = copy.deepcopy(state)
        fields = changed['components'][node]['fields']
        if field not in fields:
            field = next(iter(fields))
        del fields[field]
        mutations.append((result, changed, manifest))
    rejected = 0
    for r, s, m in mutations:
        try:
            certify(work, r, s, m)
        except (AssertionError, KeyError):
            rejected += 1
    check(rejected == len(mutations), 'all corrupted evidence rejected')
    return dict(passed=True, negativeOracles=rejected, **proof)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    arguments = parser.parse_args()
    print(json.dumps(verify(arguments.directory), indent=2))
