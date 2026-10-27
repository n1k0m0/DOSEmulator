# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Run actual DOS media cuts on fresh private journals and invoke independent PIO/FAT oracles."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid

REPO = Path.cwd()


def safe(path):
    """Exclude personal play media before opening configured inputs or generated reports."""
    path = Path(path).resolve()
    if '/artifacts/doom2-play' in path.as_posix().lower():
        raise ValueError('Personal play media are excluded.')
    return path


def sha(path):
    """Stream one explicitly named artifact without opening other installations."""
    with safe(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def read(path):
    """Read structured private metadata using the runner's UTF-8 convention."""
    return json.loads(safe(path).read_text(encoding='utf-8-sig'))


def run(args, output):
    """Retain exact process output and reject a failed command before counting an accepted start."""
    with safe(output).open('wb') as stream:
        result = subprocess.run([str(v) for v in args], stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError('Process failed; inspect ' + str(output))


def main(args):
    """Execute six cold phases per guest/manager against independent writable media branches."""
    build = safe(args.build)
    runner = build / 'Release/net10.0/DOSEmulator.Headless.dll'
    systems = [('msdos', safe(args.disk), safe(args.overlay)),
               ('freedos', safe(REPO / 'artifacts/freedos-m1/FD14LITE-minimal.img'), None),
               ('emm386-noems', safe(args.configured_disk), safe(args.noems_overlay)),
               ('emm386-ram', safe(args.configured_disk), safe(args.ram_overlay))]
    originals = {str(p): sha(p) for _, disk, cow in systems for p in [disk, cow] if p is not None}
    assemblies = [{'Path': str(p), 'Hash': sha(p)} for p in sorted(runner.parent.glob('*.dll'))]
    root = REPO / 'artifacts/media-checkpoint-m3' / ('acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    (root / 'tested-assemblies.json').write_text(json.dumps(assemblies, indent=2), encoding='utf-8')
    nasm = REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe'
    program, floppy = root / 'HD4TEST.COM', root / 'own-probe.img'
    run([nasm, '-Werror', '-f', 'bin', REPO / 'tests/guests/harddisks-test.asm', '-o', program], root / 'assembly.log')
    run([sys.executable, REPO / 'tests/prepare-harddisk-media.py', root, floppy], root / 'floppy.json')
    for mib in [2, 8, 1536]:
        run(['dotnet', runner, '--create-disk', root / f'{mib}.img', '--size', mib, '--unit', 'MiB'], root / f'{mib}.created.json')
    rows, configs, negatives = [], [], 0
    for system, basis, source_cow in systems:
        system_cow = root / (system + '.cow')
        if source_cow is not None:
            with source_cow.open('rb') as source, system_cow.open('xb') as dest:
                while chunk := source.read(1024 * 1024):
                    dest.write(chunk)
        original_pc = root / (system + '.dospc.json')
        launch = ['dotnet', runner, '--disk', basis, '--overlay', system_cow, '--floppy-a', floppy,
                  '--save-machine', original_pc]
        for slot, mib in [(1, 2), (2, 8), (3, 1536)]:
            launch.extend([f'--disk{slot}', root / f'{mib}.img', f'--overlay{slot}', root / f'{system}-data{slot}.cow'])
        run(launch, root / f'{system}.saved.json')
        checkpoint = root / (system + '.dosemedia')
        branch_a, branch_b = root / (system + '-a.dosebranch'), root / (system + '-b.dosebranch')
        pcs = {'seed': original_pc, 'capture': original_pc, 'origin-change': original_pc,
               'branch-read': branch_a / 'branch.dospc.json', 'branch-change': branch_a / 'branch.dospc.json',
               'second-read': branch_b / 'branch.dospc.json'}
        capture_hashes = {}
        for phase in ['seed', 'capture', 'origin-change', 'branch-read', 'branch-change', 'second-read']:
            if phase in ['branch-read', 'second-read']:
                destination = branch_a if phase == 'branch-read' else branch_b
                run(['dotnet', runner, '--branch-media', checkpoint, '--output', destination], root / f'{system}-{phase}.branch.json')
            stem = root / f'{system}-{phase}'
            prompt, plan = 'C:\\>', []
            if system != 'freedos':
                plan.append(dict(waitForScreen=prompt, text='kezb us\r\n'))
                prompt = 'C:\\>keyb us'
            if phase in ['seed', 'origin-change', 'branch-change']:
                word = {'seed': 'BEFORE', 'origin-change': 'AFTER', 'branch-change': 'BRANCH'}[phase]
                commands = 'echo DOSE-' + system.upper() + '-' + word + '>c:\\snap.txt\r\ntype c:\\snap.txt\r\n'
            else:
                commands = ('type c:\\snap.txt\r\n' if phase != 'capture' else '') + 'a:hd4test' + (' /r' if phase != 'capture' else '') + '\r\necho HD4-DOS-RETURN\r\n'
            commands += 'echo MEDIA-DOS-RETURN\r\n'
            plan.append(dict(waitForScreen=prompt, text=commands))
            scenario = stem.with_suffix('.scenario.json')
            scenario.write_text(json.dumps(plan, indent=2), encoding='utf-8')
            stop = '\r\nMEDIA-DOS-RETURN\r\n' + ('C:\\>' if system == 'freedos' else '\r\nC:\\>')
            report = stem.with_suffix('.json')
            command = ['dotnet', runner, '--machine', pcs[phase], '--scenario', scenario, '--steps', 300000000,
                       '--stop-text', stop, '--report', report]
            if phase == 'capture':
                command.extend(['--capture-media', checkpoint])
            if phase in ['capture', 'branch-read', 'second-read']:
                command.extend(['--memory-dump', stem.with_suffix('.ram.bin'), '--ide-io-trace', stem.with_suffix('.io.jsonl')])
            run(command, stem.with_suffix('.stdout.json'))
            data = read(report)
            if data['status']['fault'] is not None or not data['scenario']['complete'] or 'MEDIA-DOS-RETURN' not in data['text']:
                raise AssertionError('Actual DOS phase did not complete: ' + str(report))
            row = dict(system=system, phase=phase, report=str(report), machineFile=str(pcs[phase]))
            if phase in ['capture', 'branch-read', 'second-read']:
                oracle = ['--report', report, '--memory', stem.with_suffix('.ram.bin'), '--program', program,
                          '--io', stem.with_suffix('.io.jsonl'), '--negative-controls']
                if phase != 'capture':
                    oracle.append('--persist')
                if system.startswith('emm386'):
                    oracle.append('--manager')
                verified = stem.with_suffix('.verified.json')
                run([sys.executable, REPO / 'tests/verify-harddisks.py', *oracle], verified)
                proof = read(verified)
                if not proof['passed']:
                    raise AssertionError('Independent hard-disk guest evidence failed.')
                negatives += proof['negativeControlsRejected']
                row['hardDiskOracle'] = str(verified)
            rows.append(row)
            if phase == 'capture':
                definition = read(original_pc)
                capture_hashes = {f"ide{v['slot']}.cow": sha(original_pc.parent / v['overlayPath']) for v in definition['hardDisks']}
        configs.append(dict(system=system, checkpoint=str(checkpoint), originPC=str(original_pc),
                            firstBranchPC=str(pcs['branch-read']), secondBranchPC=str(pcs['second-read']),
                            captureJournalHashes=capture_hashes))
    for path, identity in originals.items():
        if sha(path) != identity:
            raise AssertionError('Protected source changed: ' + path)
    for item in assemblies:
        if sha(item['Path']) != item['Hash']:
            raise AssertionError('Executed DLL changed during guest matrix.')
    summary = dict(passed=True, directory=str(root), headlessColdBoots=len(rows), cases=rows, systems=configs,
                   negativeHardDiskOraclesRejected=negatives, originalsUnchanged=True, testedAssembliesUnchanged=True,
                   nativeInputSent=False, fullStateSnapshot=False)
    summary_path = root / 'guest-summary.json'
    summary_path.write_text(json.dumps(summary, indent=2), encoding='utf-8')
    result_path = root / 'media.verified.json'
    run([sys.executable, REPO / 'tests/verify-media-checkpoint.py', '--summary', summary_path], result_path)
    summary['verification'] = read(result_path)
    summary['negativeControlsRejected'] = negatives + summary['verification']['additionalNegativeControlsRejected']
    (root / 'acceptance-summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--build', required=True)
    parser.add_argument('--disk', required=True)
    parser.add_argument('--overlay', required=True)
    parser.add_argument('--configured-disk', required=True)
    parser.add_argument('--noems-overlay', required=True)
    parser.add_argument('--ram-overlay', required=True)
    main(parser.parse_args())
