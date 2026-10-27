# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Build own read-only reset media and repeat real STI/PIT/ENTER/full-PC continuations on both CPU platforms."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import uuid

REPO = Path(__file__).resolve().parents[1]


def sha(path):
    """Hash exactly one own file without touching personal game journals."""
    path = Path(path).resolve()
    assert 'doom2-play' not in str(path).lower()
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def run(command, log):
    """Retain exact process output and require actual successful execution."""
    with Path(log).open('x', encoding='utf-8') as output:
        result = subprocess.run([str(value) for value in command], cwd=REPO, stdout=output, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}): {log}')


def main(args):
    """Prepare each own medium and certify full restored futures, independently of protected OS media."""
    root = REPO / 'artifacts/audit2-fixes' / ('cpu-acceptance-' + uuid.uuid4().hex)
    root.mkdir(parents=True)
    assembler = REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe'
    payload, loader, image = root / 'kernel.bin', root / 'boot.bin', root / 'own-cpu.img'
    run([assembler, '-Werror', '-f', 'bin', REPO / 'tests/guests/audit2-cpu-test.asm', '-o', payload], root / 'kernel-assemble.log')
    sectors = (payload.stat().st_size + 511) // 512
    assert 1 <= sectors <= 62
    run([assembler, '-Werror', '-f', 'bin', '-DPAYLOAD_SECTORS=' + str(sectors), REPO / 'tests/guests/audit2-boot.asm', '-o', loader], root / 'boot-assemble.log')
    raw = bytearray(2 * 1048576)
    raw[:512] = loader.read_bytes()
    raw[512:512 + payload.stat().st_size] = payload.read_bytes()
    with image.open('xb') as output:
        output.write(raw)
    profiles = ['virtual486-dev-v1', 'dose-430fx-host-dev-v1']
    if args.only:
        profiles = [args.only]
    build = Path(args.build).resolve() / 'Release/net10.0'
    assemblies = {str(path): sha(path) for path in build.glob('DOSEmulator*.dll')}
    assert len(assemblies) == 7
    spec = importlib.util.spec_from_file_location('audit2_cpu_oracle', REPO / 'tests/verify-audit2-cpu.py')
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    rows = []
    for name in profiles:
        for repeat in range(args.repeats):
            work = root / (name + '-' + str(repeat + 1))
            work.mkdir()
            config = dict(profile=str(REPO / 'profiles' / (name + '.json')), image=str(image), payload=str(payload), output=str(work / 'evidence'))
            path = work / 'input.json'
            path.write_bytes(json.dumps(config, indent=2).encode('utf-8'))
            run(['dotnet', build / 'DOSEmulator.Tests.dll', '--audit2-cpu-guest', path], work / 'run.log')
            result = json.loads((work / 'evidence/result.json').read_text(encoding='utf-8-sig'))
            proof = oracle.verify(work / 'evidence')
            rows.append(dict(profile=name, repeat=repeat + 1, directory=str(work), result=result, independentOracle=proof))
            print('PASS', name, repeat + 1, 'full comparisons', result['comparisons'], flush=True)
    assert all(sha(path) == value for path, value in assemblies.items())
    assert sha(image) == hashlib.sha256(raw).hexdigest().upper()
    report = dict(passed=True, ownResetStarts=len(rows), fullContinuations=len(rows), rows=rows, assemblies=assemblies,
                  imageSha256=sha(image), payloadSha256=sha(payload), notDosAcceptance=True)
    (root / 'acceptance.json').write_bytes(json.dumps(report, indent=2).encode('utf-8'))
    (REPO / 'artifacts/tests/audit2-cpu-guest-path.txt').write_bytes(str(root).encode('utf-8'))
    print(root, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True)
    parser.add_argument('--only', choices=['virtual486-dev-v1', 'dose-430fx-host-dev-v1'])
    parser.add_argument('--repeats', type=int, choices=[1, 2], default=2)
    main(parser.parse_args())
