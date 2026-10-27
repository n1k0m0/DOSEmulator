# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Sustained real DMA/FM/MPU DOS callers and full-state continuation on explicitly private media."""
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
    """Hash one explicitly named artifact, excluding personal play journals before opening it."""
    path = Path(path).resolve()
    assert 'doom2-play' not in str(path).lower(), 'Personal play journals are excluded.'
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(arguments, log):
    """Retain the actual process exit status and complete compiler/guest output in a new file."""
    with Path(log).open('x', encoding='utf-8') as output:
        result = subprocess.run([str(x) for x in arguments], cwd=REPO, stdout=output, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}); see {log}')


def main(args):
    """Run independent master/slave IRQ resource profiles under original DOS managers, with exact repeated futures."""
    systems = [('msdos', args.disk, args.overlay), ('freedos', REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img', None)]
    if args.configured_disk:
        assert args.noems_overlay and args.ram_overlay
        systems += [('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    else:
        assert not args.noems_overlay and not args.ram_overlay
    if args.only:
        systems = [row for row in systems if row[0] == args.only]
    sources = {Path(p).resolve() for _, disk, cow in systems for p in [disk, cow] if p}
    before = {str(p): digest(p) for p in sources}
    root = Path(args.output).resolve() if args.output else REPO / 'artifacts/audio-combined-m3' / ('acceptance-' + uuid.uuid4().hex)
    assert root.is_relative_to(REPO / 'artifacts') and 'doom2-play' not in str(root).lower()
    root.mkdir(parents=True, exist_ok=False)
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(p): digest(p) for p in build.glob('DOSEmulator*.dll')}
    spec = importlib.util.spec_from_file_location('combined_oracle', REPO / 'tests/verify-audio-combined.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    variants = [dict(name='a220-i5-d1-h5-p330', card=0x220, irq=5, dma8=1, dma16=5, mpu=0x330),
                dict(name='a280-i9-d0-h7-p300', card=0x280, irq=9, dma8=0, dma16=7, mpu=0x300)]
    rows, programs = [], []
    for variant in variants:
        probe, floppy = root / (variant['name'] + '.com'), root / (variant['name'] + '.img')
        parameters = ['-D' + key.upper() + '=' + str(variant[key]) for key in ['irq', 'dma8', 'dma16', 'mpu']]
        parameters.append('-DPORT=' + str(variant['card']))
        run([REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe', '-Werror', '-f', 'bin', REPO / 'tests/guests/audio-combined-test.asm', '-o', probe, *parameters], root / (variant['name'] + '.assembly.log'))
        run(['python', REPO / 'tests/prepare-font-media.py', probe, floppy, 'COMBINED.COM'], root / (variant['name'] + '.media.log'))
        programs.append(dict(program=str(probe), floppy=str(floppy), programSha256=digest(probe), floppySha256=digest(floppy)))
        for name, disk, overlay in systems:
            previous, machine_id = None, uuid.uuid4().hex
            for repeat in range(args.repeats):
                work = root / f'{name}-{variant["name"]}-{repeat + 1}'
                work.mkdir()
                cow = work / 'original.cow'
                if overlay:
                    shutil.copyfile(overlay, cow)
                hardware = dict(name='DOSE sustained audio fixture', ramMiB=8, floppyDriveA='1440k',
                                soundCard='sb16-ct1740-dsp405-development', midiInterface='ct1740-mpu401-uart-development',
                                soundBlasterBasePort=variant['card'], soundBlasterIrq=variant['irq'],
                                soundBlasterDmaChannel=variant['dma8'], soundBlasterDma16Channel=variant['dma16'], midiBasePort=variant['mpu'])
                definition = dict(schemaVersion=1, id=machine_id, name='Own sustained audio snapshot caller', hardware=hardware,
                                  hardDisks=[dict(slot=0, imagePath=str(Path(disk).resolve()), overlayPath=str(cow))],
                                  floppies=[dict(slot=0, imagePath=str(floppy))], builtInTestDisk=False)
                pc = work / 'source.dospc.json'
                pc.write_text(json.dumps(definition, indent=2), encoding='utf-8')
                config = dict(machine=str(pc), output=str(work / 'evidence'), germanKeyboard=name != 'freedos',
                              proofOffset=probe.read_bytes().index(b'COMBPRO1'), card=variant['card'], irq=variant['irq'], mpu=variant['mpu'])
                (work / 'input.json').write_text(json.dumps(config), encoding='utf-8')
                run(['dotnet', build / 'DOSEmulator.Tests.dll', '--combined-audio-guest', work / 'input.json'], work / 'run.log')
                proof = oracle.verify(work, probe, manager=name.startswith('emm386'))
                (work / 'verified.json').write_text(json.dumps(proof, indent=2), encoding='utf-8')
                result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
                deterministic = {key: result[key] for key in ['longStart', 'finalStateSha256', 'finalRamSha256', 'futureAudioSha256', 'futureSignalsSha256', 'pcmFrames', 'nativeFrames']}
                if previous is not None:
                    assert previous == deterministic, 'Repeated actual guest future differs.'
                previous = deterministic
                rows.append(dict(system=name, variant=variant['name'], repeat=repeat + 1, directory=str(work), result=result, independentOracle=proof))
                print('PASS', name, variant['name'], repeat + 1, 'full-state comparisons', result['comparisons'], flush=True)
    assert all(digest(p) == h for p, h in before.items()), 'A protected source changed.'
    assert all(digest(p) == h for p, h in assemblies.items()), 'A tested assembly changed.'
    report = dict(passed=True, coldStarts=len(rows), restoredContinuations=len(rows), rows=rows, sources=before, assemblies=assemblies, programs=programs)
    (root / 'acceptance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (REPO / 'artifacts/tests/m3-audio-combined-guest-path.txt').write_text(str(root), encoding='utf-8')
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
    parser.add_argument('--output', type=Path)
    main(parser.parse_args())
