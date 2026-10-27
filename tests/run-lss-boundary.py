# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce LSS/PIT/full-state continuations with original DOS managers on fresh private journals."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import uuid

REPO = Path(__file__).resolve().parents[1]


def digest(path):
    """Hash one explicitly named artifact without accessing personal play journals."""
    path = Path(path).resolve()
    assert 'doom2-play' not in str(path).lower()
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(arguments, log):
    """Retain the actual compiler/guest exit status and its complete output in a new file."""
    with Path(log).open('x', encoding='utf-8') as output:
        process = subprocess.run([str(a) for a in arguments], stdout=output, stderr=subprocess.STDOUT, cwd=REPO, check=False)
    if process.returncode:
        raise RuntimeError(f'Failed ({process.returncode}): {log}')


def main(args):
    """Run two repetitions per original DOS configuration and compare actual future state, timing and proof digests."""
    systems = [('msdos', args.disk, args.overlay), ('freedos', REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img', None),
               ('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    sources = {Path(p).resolve() for _, disk, cow in systems for p in (disk, cow) if p}
    before = {str(p): digest(p) for p in sources}
    root = REPO / 'artifacts/cpu-x87-contracts-m3' / ('acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(p): digest(p) for p in build.glob('DOSEmulator*.dll')}
    probe, floppy = root / 'lss.com', root / 'lss.img'
    run([REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe', '-Werror', '-f', 'bin', REPO / 'tests/guests/lss-boundary-test.asm', '-o', probe], root / 'assembly.log')
    run(['python', REPO / 'tests/prepare-font-media.py', probe, floppy, 'LSSBOUND.COM'], root / 'media.log')
    spec = importlib.util.spec_from_file_location('lss_oracle', REPO / 'tests/verify-lss-boundary.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    rows = []
    for system, disk, overlay in systems:
        previous, machine_id = None, uuid.uuid4().hex
        for repeat in range(1, 3):
            work = root / f'{system}-{repeat}'
            work.mkdir()
            cow = work / 'original.cow'
            if overlay:
                shutil.copyfile(overlay, cow)
            definition = dict(schemaVersion=1, id=machine_id, name='Own LSS PIT boundary caller',
                              hardware=dict(name='DOSE LSS boundary hardware', ramMiB=8, floppyDriveA='1440k'),
                              hardDisks=[dict(slot=0, imagePath=str(Path(disk).resolve()), overlayPath=str(cow))],
                              floppies=[dict(slot=0, imagePath=str(floppy))], builtInTestDisk=False)
            pc = work / 'source.dospc.json'
            pc.write_text(json.dumps(definition, indent=2), encoding='utf-8')
            config = work / 'input.json'
            config.write_text(json.dumps(dict(machine=str(pc), output=str(work / 'evidence'), germanKeyboard=system != 'freedos',
                                             proofOffset=probe.read_bytes().index(b'LSSPR001'))), encoding='utf-8')
            run(['dotnet', build / 'DOSEmulator.Tests.dll', '--lss-boundary-guest', config], work / 'run.log')
            verified = oracle.verify(work, probe, system.startswith('emm386'))
            (work / 'verified.json').write_text(json.dumps(verified, indent=2), encoding='utf-8')
            result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
            deterministic = (result['finalStateSha256'], result['finalRamSha256'], result['manifest']['virtualNanoseconds'], verified['proofSha256'])
            if previous is not None:
                assert deterministic == previous, 'Repeated actual future differs.'
            previous = deterministic
            rows.append(dict(system=system, repeat=repeat, directory=str(work), result=result, independentOracle=verified))
            print('PASS', system, repeat, 'PIT IRQs', verified['physicalIrqs'], 'full-state comparisons', result['comparisons'], flush=True)
    assert all(digest(p) == value for p, value in before.items())
    assert all(digest(p) == value for p, value in assemblies.items())
    report = dict(passed=True, coldDosStarts=len(rows), restoredContinuations=len(rows), rows=rows, sources=before, assemblies=assemblies,
                  program=str(probe), floppy=str(floppy), programSha256=digest(probe), floppySha256=digest(floppy))
    (root / 'acceptance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (REPO / 'artifacts/tests/m3-lss-boundary-guest-path.txt').write_text(str(root), encoding='utf-8')
    print(root, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True)
    parser.add_argument('--disk', required=True, type=Path)
    parser.add_argument('--overlay', required=True, type=Path)
    parser.add_argument('--configured-disk', required=True, type=Path)
    parser.add_argument('--noems-overlay', required=True, type=Path)
    parser.add_argument('--ram-overlay', required=True, type=Path)
    main(parser.parse_args())
