# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Check real CMD path resolution after moving an own fixture; never start an emulator."""

import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts/glide-bridge'))
SPEC = importlib.util.spec_from_file_location('glide_setup', ROOT / 'scripts/glide-bridge/setup.py')
SETUP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SETUP)


def check_launcher(root):
    """Inspect a generated CMD from an unrelated CWD, checking every resolved local target."""
    branch = root / 'private branch'
    frontend = root / 'host files/frontend.exe'
    host = root / 'host files/provider.dll'
    machine = branch / 'branch.dospc.json'
    data = branch / 'frontend-data'
    for path in (frontend, host, machine):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('OWN INERT FIXTURE', encoding='ascii')
    definition = {'id': 'OWN', 'hardDisks': [{'imagePath': str(frontend), 'overlayPath': 'ide0.cow'}],
                  'opticalDiscs': [{'imagePath': str(host)}], 'floppies': [{'imagePath': str(host), 'overlayPath': None}]}
    before = json.dumps(definition)
    portable = SETUP.portable_machine_paths(definition, branch)
    assert json.dumps(definition) == before, 'Relocation must not mutate its input.'
    assert not Path(portable['hardDisks'][0]['imagePath']).is_absolute()
    machine.write_text(json.dumps(portable), encoding='ascii')
    launcher = branch / 'Start-Glide.cmd'
    launcher.write_text(SETUP.launcher_text(frontend, machine, host, data), encoding='ascii')
    return launcher, (frontend, machine, host, data)


def inspect(launcher, expected, cwd, succeeds=True):
    """Execute only the dry inspection path and compare its resolved file locations."""
    result = subprocess.run(['cmd', '/d', '/c', str(launcher), '--inspect'], cwd=cwd,
                            capture_output=True, text=True, check=False)
    assert (result.returncode == 0) == succeeds, (result.returncode, result.stdout, result.stderr)
    if succeeds:
        lines = result.stdout.splitlines()
        assert len(lines) == 4, result.stdout
        actual = [Path(line.split(': ', 1)[1].strip('"')).resolve() for line in lines]
        assert actual == [path.resolve() for path in expected], (actual, expected)
        definition = json.loads(expected[1].read_text())
        assert (launcher.parent / definition['hardDisks'][0]['imagePath']).resolve() == expected[0].resolve()
        assert definition['hardDisks'][0]['overlayPath'] == 'ide0.cow'
        assert (launcher.parent / definition['opticalDiscs'][0]['imagePath']).resolve() == expected[2].resolve()
        assert definition['floppies'][0]['overlayPath'] is None


def check_occupied_link(work):
    """Ensure compatibility setup rejects occupied or nested locations without changing files."""
    occupied = work / 'occupied'
    occupied.mkdir()
    sentinel = occupied / 'keep.txt'
    sentinel.write_text('KEEP')
    for previous in (occupied, ROOT, ROOT / 'nested', ROOT.parent):
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'scripts/repair-workspace-links.ps1'),
                                 '-PreviousRoot', str(previous)], capture_output=True, check=False)
        assert result.returncode != 0, previous
        assert sentinel.read_text() == 'KEEP'
    absent = work / 'inspect-only'
    result = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'scripts/repair-workspace-links.ps1'),
                             '-PreviousRoot', str(absent), '-Inspect'], capture_output=True, text=True, check=True)
    assert not json.loads(result.stdout)['compatible'] and not absent.exists()


def main():
    """Prove movement, missing-resource rejection and batch-character validation on own files."""
    if os.name != 'nt':
        raise RuntimeError('This test executes Windows CMD inspection and requires Windows.')
    with tempfile.TemporaryDirectory(prefix='dose-relocation-') as temporary:
        work = Path(temporary)
        check_occupied_link(work)
        old = work / 'old project'
        launcher, expected = check_launcher(old)
        inspect(launcher, expected, work)
        moved = work / 'moved project'
        assert old.resolve().parent == moved.resolve().parent == work.resolve() and not moved.exists()
        shutil.move(str(old), moved)  # Only this freshly created temporary fixture is moved.
        new_expected = tuple(moved / path.relative_to(old) for path in expected)
        new_launcher = moved / launcher.relative_to(old)
        inspect(new_launcher, new_expected, work)
        new_expected[2].unlink()
        inspect(new_launcher, new_expected, work, succeeds=False)
        for character in ('%', '!', '"', '\n', '\r'):
            try:
                SETUP.launcher_text(Path('bad' + character + 'frontend.exe'), *new_expected[1:])
            except ValueError:
                pass
            else:
                raise AssertionError('Unsafe CMD path accepted: ' + repr(character))
    print('PASS: actual CMD and all media-reference paths before/after own fixture move; unrelated CWD, missing resource, five unsafe path controls, non-mutating conversion, occupied/nested link rejection and read-only inspection; no emulator/window/media.')


if __name__ == '__main__':
    main()
