# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce actual P54C DOS full snapshots and separately scoped original-EMM386 faults on private copies."""
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
    """Hash one explicitly named source; personal play journals are never accepted."""
    path = Path(path).resolve()
    assert 'doom2-play' not in str(path).lower(), 'Personal play COWs are excluded.'
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(arguments, log):
    """Execute one compiler/guest process and preserve its original status and bounded diagnostic output."""
    with Path(log).open('w', encoding='utf-8') as output:
        result = subprocess.run([str(x) for x in arguments], cwd=REPO, stdout=output, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}); see {log}')


def main(args):
    """Prepare private media, run original/restored PCs, and certify independent CPU/clock/file/frame results."""
    systems = [('msdos', args.disk, args.overlay), ('freedos', REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img', None)]
    if args.configured_disk:
        assert args.noems_overlay and args.ram_overlay
        systems += [('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        systems = [s for s in systems if s[0] == args.only]
    if args.faults_only:
        systems = [s for s in systems if s[0].startswith('emm386')]
    assert systems, 'No selected DOS systems.'
    sources = {Path(p).resolve() for _, disk, overlay in systems for p in [disk, overlay] if p}
    before = {str(p): digest(p) for p in sources}
    root = REPO / 'artifacts/p54c-pc-m4' / ('acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    probe, floppy = root / 'P54CPC.COM', root / 'probe.img'
    assembler = REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe'
    run([assembler, '-Werror', '-f', 'bin', REPO / 'tests/guests/p54c-pc-probe.asm', '-o', probe], root / 'assemble.log')
    run(['python', REPO / 'tests/prepare-font-media.py', probe, floppy, 'P54CPC.COM'], root / 'media.log')
    fault_probes = {}
    if not args.skip_faults:
        for number, instruction in enumerate(['rdtsc', 'rdmsr', 'wrmsr']):
            program, image = root / (instruction + '.com'), root / (instruction + '.img')
            run([assembler, '-Werror', '-DPROBE=' + str(number), '-f', 'bin', REPO / 'tests/guests/p54c-pc-fault.asm', '-o', program], root / (instruction + '-assemble.log'))
            run(['python', REPO / 'tests/prepare-font-media.py', program, image, 'P54CFLT.COM'], root / (instruction + '-media.log'))
            fault_probes[instruction] = (program, image)
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(p): digest(p) for p in build.glob('DOSEmulator*.dll')}
    assert len(assemblies) >= 7, 'Full tested runtime required.'
    spec = importlib.util.spec_from_file_location('p54c_pc_oracle', REPO / 'tests/verify-p54c-pc.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    rows, faults = [], []
    for name, disk, overlay in systems:
        modes = ([] if args.faults_only else [(None, probe, floppy)])
        if name.startswith('emm386') and not args.skip_faults:
            modes += [(instruction, program, image) for instruction, (program, image) in fault_probes.items()]
        for instruction, program, image in modes:
            for repeat in range(args.repeats):
                work = root / f'{name}-{instruction or "positive"}-{repeat + 1}'
                work.mkdir()
                cow = work / 'original.cow'
                if overlay:
                    shutil.copyfile(overlay, cow)
                hardware = dict(id='dose-p54c-at-dev-v1', cpu='p54c-b5-subset-development', cpuHz=90_000_000,
                                name='DOSE AT P54C B5 subset DOS fixture', ramMiB=8, floppyDriveA='1440k', videoCard='et4000ax-isa-1m-development',
                                soundCard='sb16-ct1740-dsp405-development', mouse='ps2-two-button', gameport='ibm-game-control-development',
                                serialPort='pc16550d-com1-development', parallelPort='ibm-printer-adapter-development', midiInterface='ct1740-mpu401-uart-development')
                machine = dict(schemaVersion=1, id=uuid.uuid4().hex, name='Own full P54C DOS validation', hardware=hardware,
                               hardDisks=[dict(slot=0, imagePath=str(Path(disk).resolve()), overlayPath=str(cow))],
                               floppies=[dict(slot=0, imagePath=str(image))], builtInTestDisk=False)
                machine_path = work / 'source.dospc.json'
                machine_path.write_bytes(json.dumps(machine, indent=2).encode('utf-8'))
                config = work / 'input.json'
                config.write_bytes(json.dumps(dict(machine=str(machine_path), output=str(work / 'evidence'), germanKeyboard=name != 'freedos',
                                                   probe=str(program), faultInstruction=instruction), indent=2).encode('utf-8'))
                run(['dotnet', build / 'DOSEmulator.Tests.dll', '--p54c-pc-guest', config], work / 'run.log')
                result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
                proof = oracle.verify(work)
                item = dict(system=name, repeat=repeat + 1, instruction=instruction, directory=str(work), result=result, independentOracle=proof)
                (faults if instruction else rows).append(item)
                print('PASS', name, instruction or 'DOS-return', repeat + 1, 'comparisons', result['comparisons'], flush=True)
    assert all(digest(p) == h for p, h in before.items()), 'A protected source changed.'
    assert all(digest(p) == h for p, h in assemblies.items()), 'A tested assembly changed.'
    report = dict(passed=True, coldStarts=len(rows), restoredContinuations=len(rows), monitorFaultStarts=len(faults), monitorFaultContinuations=len(faults),
                  rows=rows, faultRows=faults, sources=before, assemblies=assemblies, probeSha256=digest(probe), floppySha256=digest(floppy),
                  faultProbeHashes={instruction: dict(program=digest(program), floppy=digest(image)) for instruction, (program, image) in fault_probes.items()},
                  limitations=['DOSE AT validation profile, no historical 430FX/Win95 acceptance.',
                               'Monitor fault cuts execute eight original handler instructions; no completed DOS return is claimed for these negatives.'])
    (root / 'acceptance.json').write_bytes(json.dumps(report, indent=2).encode('utf-8'))
    (REPO / 'artifacts/tests/m4-p54c-pc-guest-path.txt').write_bytes(str(root).encode('utf-8'))
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
    parser.add_argument('--skip-faults', action='store_true')
    parser.add_argument('--faults-only', action='store_true')
    options = parser.parse_args()
    assert not (options.skip_faults and options.faults_only), 'Contradictory scope.'
    main(options)
