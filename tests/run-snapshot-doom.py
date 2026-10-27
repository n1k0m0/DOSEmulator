# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Repeat actual DOS4GW/Doom snapshots on fresh journals, with independent WAD pixels and media guards."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]


def checked(value, parent=ROOT):
    """Resolve one explicit input and exclude personal play media before opening anything."""
    path = (parent / value).resolve()
    if any('doom2-play' in part.lower() for part in path.parts):
        raise ValueError('Personal game journals are excluded')
    return path


def sha(path):
    """Hash only an explicitly selected input or newly owned artifact."""
    with checked(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def module(name, filename):
    """Load a known independent repository oracle without activating code from snapshot files."""
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tests' / filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def frames(directory, hud):
    """Independently require every actual image's WAD pixels and a changing rendered viewport."""
    records, views = [], set()
    for path in sorted(directory.glob('*.ppm')):
        magic, size, maximum, rgb = path.read_bytes().split(b'\n', 3)
        assert (magic, size, maximum) == (b'P6', b'320 200', b'255') and len(rgb) == 192000
        assert all(rgb[((y+168)*320+x)*3:((y+168)*320+x)*3+3] == color for (x,y), color in hud.items())
        views.add(hashlib.sha256(rgb[:320*168*3]).hexdigest())
        records.append(dict(filename=path.name, sha256=sha(path), staticHudPixels=len(hud)))
    assert len(records) == 9 and len(views) >= 2
    return records


def main(args):
    """Clone an explicit known bootable video fixture, continue both cards and preserve every source."""
    source = checked(args.machine)
    definition = json.loads(source.read_text(encoding='utf-8-sig'))
    assert not definition.get('builtInTestDisk', False)
    build = checked(args.build) / 'Release/net10.0'
    game = checked(args.game)
    hud, _ = module('doom_snapshot_wad', 'verify-doom-extender.py').hud_reference(game)
    protected = {source, game/'DOOM2.EXE', game/'DOOM2.WAD'}
    for kind in ('hardDisks', 'floppies'):
        for medium in definition.get(kind, []):
            for field in ('imagePath', 'overlayPath'):
                if medium.get(field):
                    protected.add(checked(medium[field], source.parent))
    before = {str(path): sha(path) for path in protected}
    assemblies = {str(path): sha(path) for path in build.glob('DOSEmulator*.dll')}
    output = ROOT / 'artifacts/snapshots-m3' / ('doom-acceptance-'+uuid.uuid4().hex)
    output.mkdir(parents=True)
    rows = []
    for label, card, sound in [('vga-sound', 'dose-vga-development', True), ('et-nosound', 'et4000ax-isa-1m-development', False)]:
        work = output / label
        work.mkdir()
        pc = copy.deepcopy(definition)
        pc['hardware']['videoCard'] = card
        pc['hardware']['soundCard'] = 'sb16-ct1740-dsp405-development' if sound else 'none'
        pc['hardware'].pop('isEt4000', None)
        pc['hardware'].pop('isSb16', None)
        original_journals = {}
        for kind in ('hardDisks', 'floppies'):
            for medium in pc.get(kind, []):
                medium['imagePath'] = str(checked(medium['imagePath'], source.parent))
                journal = work / f'{kind}-{medium["slot"]}.cow'
                if medium.get('overlayPath'):
                    old = checked(medium['overlayPath'], source.parent)
                    with journal.open('xb') as target, old.open('rb') as data:
                        shutil.copyfileobj(data, target)
                    original_journals[str(journal)] = sha(journal)
                medium['overlayPath'] = str(journal)
        machine = work/'source.dospc.json'
        machine.write_text(json.dumps(pc, indent=2), encoding='utf-8')
        evidence = work/'evidence'
        config = dict(machine=str(machine), output=str(evidence),
                      command='cd \\games\\doom2\rdoom2 -config '+('sound.cfg' if sound else 'nosound.cfg -nosound')+' -timedemo fastdemo\r',
                      hud=[dict(index=(y+168)*320+x, argb=0xff000000|color[0]<<16|color[1]<<8|color[2]) for (x,y), color in hud.items()])
        (work/'input.json').write_text(json.dumps(config), encoding='utf-8')
        with (work/'run.log').open('x', encoding='utf-8') as log:
            result = subprocess.run(['dotnet', str(build/'DOSEmulator.Tests.dll'), '--snapshot-doom', str(work/'input.json')], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=False)
        assert result.returncode == 0, str(work/'run.log')
        result = json.loads((evidence/'result.json').read_text(encoding='utf-8-sig'))
        assert result['passed'] and result['activeDmaAtCut'] == sound and result['comparisons'] >= 15
        assert result['pcmFrames'] > 0
        images = frames(evidence, hud)
        # Reconstruct each corruption from an actual local image; no copyrighted golden image is embedded.
        rejected = 0
        for record in images:
            data = bytearray((evidence/record['filename']).read_bytes())
            prefix = data.index(b'\n255\n')+5
            x,y = next(iter(hud))
            data[prefix+((y+168)*320+x)*3] ^= 1
            rgb = data[prefix:]
            assert not all(rgb[((py+168)*320+px)*3:((py+168)*320+px)*3+3] == color for (px,py),color in hud.items())
            rejected += 1
        assert all(sha(path) == value for path,value in original_journals.items()), 'Guest journal changed'
        rows.append(dict(name=label, directory=str(work), result=result, frames=images, negativeOracles=rejected))
        print('PASS', label, 'actual snapshot comparisons', result['comparisons'], 'WAD frames', len(images), flush=True)
    assert all(sha(path) == value for path,value in before.items()), 'Protected input changed'
    assert all(sha(path) == value for path,value in assemblies.items()), 'Tested build changed'
    report = dict(passed=True, coldStarts=2, restoredContinuations=2, rows=rows, protectedInputs=before, assemblies=assemblies,
                  nativeInput=False, generalGameAcceptance=False)
    (output/'acceptance.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('build', 'machine', 'game'):
        parser.add_argument('--'+name, required=True, type=Path)
    main(parser.parse_args())
