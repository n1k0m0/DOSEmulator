# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Run actual 82437FX physical PAM and full 32-MiB DOS continuations on fresh private COWs."""
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
    """Hash exactly one protected source and reject personal play paths."""
    path = Path(path).resolve()
    assert 'doom2-play' not in str(path).lower(), 'Excluded personal play path.'
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(arguments, log):
    """Preserve the exact subprocess exit status and its private output."""
    with Path(log).open('w', encoding='utf-8') as output:
        result = subprocess.run([str(x) for x in arguments], cwd=REPO, stdout=output, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}); see {log}')


def main(args):
    """Construct strict 100-MHz/32-MiB host-subset PCs and certify complete actual guest effects."""
    systems = [('msdos', args.disk, args.overlay), ('freedos', REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img', None)]
    if args.configured_disk:
        assert args.noems_overlay and args.ram_overlay
        systems += [('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        systems = [s for s in systems if s[0] == args.only]
    assert systems
    sources = {Path(p).resolve() for _, disk, overlay in systems for p in [disk, overlay] if p}
    before = {str(p): digest(p) for p in sources}
    root = REPO / 'artifacts/430fx-m4' / ('acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    probe, floppy = root / 'FXHOST.COM', root / 'probe.img'
    assembler = REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe'
    run([assembler, '-Werror', '-f', 'bin', REPO / 'tests/guests/fx-host-probe.asm', '-o', probe], root / 'assemble.log')
    run(['python', REPO / 'tests/prepare-font-media.py', probe, floppy, 'FXHOST.COM'], root / 'media.log')
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(p): digest(p) for p in build.glob('DOSEmulator*.dll')}
    assert len(assemblies) >= 7
    spec = importlib.util.spec_from_file_location('fx_host_oracle', REPO / 'tests/verify-fx-host.py')
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
            hardware = dict(id='dose-430fx-host-dev-v1', cpu='p54c-b5-subset-development', cpuHz=100_000_000,
                            name='DOSE 82437FX A2 host subset DOS fixture', ramMiB=32, floppyDriveA='1440k',
                            videoCard='et4000ax-isa-1m-development', soundCard='sb16-ct1740-dsp405-development',
                            mouse='ps2-two-button', gameport='ibm-game-control-development', serialPort='pc16550d-com1-development',
                            parallelPort='ibm-printer-adapter-development', midiInterface='ct1740-mpu401-uart-development')
            machine = dict(schemaVersion=1, id=uuid.uuid4().hex, name='Own full 82437FX physical PAM validation', hardware=hardware,
                           hardDisks=[dict(slot=0, imagePath=str(Path(disk).resolve()), overlayPath=str(cow))],
                           floppies=[dict(slot=0, imagePath=str(floppy))], builtInTestDisk=False)
            machine_path = work / 'source.dospc.json'
            machine_path.write_bytes(json.dumps(machine, indent=2).encode('utf-8'))
            config = work / 'input.json'
            config.write_bytes(json.dumps(dict(machine=str(machine_path), output=str(work / 'evidence'), germanKeyboard=name != 'freedos',
                                               probe=str(probe)), indent=2).encode('utf-8'))
            run(['dotnet', build / 'DOSEmulator.Tests.dll', '--fx-host-guest', config], work / 'run.log')
            result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
            proof = oracle.verify(work)
            assert proof['guestVcpi'] == name.startswith('emm386'), 'Actual guest mode differs from selected original DOS/EMM386 fixture.'
            rows.append(dict(system=name, repeat=repeat + 1, directory=str(work), result=result, independentOracle=proof))
            print('PASS', name, repeat + 1, 'comparisons', result['comparisons'], flush=True)
    assert all(digest(p) == h for p, h in before.items()), 'Protected media changed.'
    assert all(digest(p) == h for p, h in assemblies.items()), 'Tested assembly changed.'
    report = dict(passed=True, coldStarts=len(rows), restoredContinuations=len(rows), rows=rows,
                  sources=before, assemblies=assemblies, probeSha256=digest(probe), floppySha256=digest(floppy),
                  limitations=['Own 82437FX A2 host-controller subset with existing ISA ATA/FDC; PIIX/full 430FX/Windows95 remain open.',
                               'No cache timing, SMM, PCI masters, monitor-table edits, native input or host peers.'])
    (root / 'acceptance.json').write_bytes(json.dumps(report, indent=2).encode('utf-8'))
    (REPO / 'artifacts/tests/m4-fx-host-guest-path.txt').write_bytes(str(root).encode('utf-8'))
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