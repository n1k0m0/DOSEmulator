# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce complete-state continuations on fresh private DOS media; never enumerate personal play journals."""
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
    """Hash one explicitly named protected input, never a recursive media inventory."""
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(arguments, log):
    """Run one real compiler or guest process and retain its original output and exit status."""
    with Path(log).open('w', encoding='utf-8') as output:
        result = subprocess.run([str(x) for x in arguments], cwd=REPO, stdout=output, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}); see {log}')


def main(args):
    """Build an own COM/floppy, seal the original running PC, then compare resumed CPU, RAM, devices, time, PCM and journals."""
    systems = [('msdos', args.disk, args.overlay), ('freedos', REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img', None)]
    if args.configured_disk:
        assert args.noems_overlay and args.ram_overlay
        systems += [('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        systems = [s for s in systems if s[0] == args.only]
    sources = {Path(p).resolve() for _, disk, overlay in systems for p in [disk, overlay] if p}
    assert all('doom2-play' not in str(p).lower() for p in sources), 'Personal play COWs are excluded.'
    before = {str(p): digest(p) for p in sources}
    root = REPO / 'artifacts/snapshots-m3' / ('acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    probe, floppy = root / 'SNAPTEST.COM', root / 'probe.img'
    run([REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe', '-Werror', '-f', 'bin', REPO / 'tests/guests/snapshot-test.asm', '-o', probe], root / 'assemble.log')
    run(['python', REPO / 'tests/prepare-font-media.py', probe, floppy, 'SNAPTEST.COM'], root / 'media.log')
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(p): digest(p) for p in build.glob('DOSEmulator*.dll')}
    spec = importlib.util.spec_from_file_location('snapshot_oracle', REPO / 'tests/verify-snapshot-continuation.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    rows = []
    for name, disk, overlay in systems:
        for repeat in range(args.repeats):
            work = root / f'{name}-{repeat + 1}'
            work.mkdir()
            cow = work / 'original.cow'
            if overlay:
                shutil.copyfile(overlay, cow)
            hardware = dict(name='DOSE snapshot DOS fixture', ramMiB=8, floppyDriveA='1440k', videoCard='et4000ax-isa-1m-development',
                            soundCard='sb16-ct1740-dsp405-development', mouse='ps2-two-button',
                            gameport='ibm-game-control-development', serialPort='pc16550d-com1-development',
                            parallelPort='ibm-printer-adapter-development', midiInterface='ct1740-mpu401-uart-development')
            machine = dict(schemaVersion=1, id=uuid.uuid4().hex, name='Own DOS snapshot acceptance', hardware=hardware,
                           hardDisks=[dict(slot=0, imagePath=str(Path(disk).resolve()), overlayPath=str(cow))],
                           floppies=[dict(slot=0, imagePath=str(floppy))], builtInTestDisk=False)
            machine_path = work / 'source.dospc.json'
            machine_path.write_text(json.dumps(machine, indent=2), encoding='utf-8')
            config = work / 'input.json'
            config.write_text(json.dumps(dict(machine=str(machine_path), output=str(work / 'evidence'), germanKeyboard=name != 'freedos')), encoding='utf-8')
            run(['dotnet', build / 'DOSEmulator.Tests.dll', '--snapshot-guest', config], work / 'run.log')
            result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
            assert result['passed'] and result['comparisons'] > 1 and result['exitA'] and result['exitB']
            assert result['dma'] == 512 and result['pcmFrames'] > 480
            proof = oracle.verify(work)
            rows.append(dict(system=name, repeat=repeat + 1, directory=str(work), result=result, independentOracle=proof))
            print('PASS', name, repeat + 1, 'comparisons', result['comparisons'], flush=True)
    assert all(digest(p) == h for p, h in before.items()), 'A protected source changed.'
    assert all(digest(p) == h for p, h in assemblies.items()), 'A tested assembly changed.'
    report = dict(passed=True, coldStarts=len(rows), restoredContinuations=len(rows), rows=rows, sources=before, assemblies=assemblies,
                  probeSha256=digest(probe), floppySha256=digest(floppy))
    (root / 'acceptance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (REPO / 'artifacts/tests/m3-snapshots-guest-path.txt').write_text(str(root), encoding='utf-8')
    print(root, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True)
    parser.add_argument('--disk', required=True, type=Path)
    parser.add_argument('--overlay', required=True, type=Path)
    parser.add_argument('--configured-disk', type=Path)
    parser.add_argument('--noems-overlay', type=Path)
    parser.add_argument('--ram-overlay', type=Path)
    parser.add_argument('--repeats', type=int, choices=[1, 2], default=2)
    parser.add_argument('--only', choices=['msdos', 'freedos', 'emm386-noems', 'emm386-ram'])
    main(parser.parse_args())
