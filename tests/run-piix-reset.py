# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce the limited PIIX CF9 INIT/reset DOS matrix on fresh private bases/COWs and certify full-PC continuation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
SYSTEMS = ('msdos', 'freedos', 'emm386-noems', 'emm386-ram')
VM_MODULES = ('DOSEmulator.Runtime', 'DOSEmulator.Cpu.X86', 'DOSEmulator.Core',
              'DOSEmulator.Hardware', 'DOSEmulator.Contracts')
REQUIRED_DLLS = tuple(name + '.dll' for name in VM_MODULES) + ('DOSEmulator.Tests.dll', 'DOSEmulator.Headless.dll')


def check(condition, reason):
    """Reject a failed artifact, guard or actual guest effect even when Python assertions are disabled."""
    if not condition:
        raise AssertionError(reason)


def safe(path, strict=False):
    """Resolve only a named path and reject the excluded personal play tree before any content is opened."""
    path = Path(path)
    check('doom2-play' not in str(path.absolute()).casefold(), 'Personal play paths are excluded.')
    result = path.resolve(strict=strict)
    check('doom2-play' not in str(result).casefold(), 'Resolved personal play paths are excluded.')
    return result


def digest(path):
    """Hash one explicitly named regular file without enumerating unrelated media or protected-input manifests."""
    path = safe(path, True)
    check(path.is_file(), f'Expected a named file: {path}')
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def read_json(path):
    """Read one explicitly named observation document."""
    return json.loads(safe(path, True).read_text(encoding='utf-8-sig'))


def write_json_new(path, value):
    """Publish one new private JSON file without replacing an earlier result."""
    with safe(path).open('xb') as stream:
        stream.write((json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))


def copy_new(source, destination, expected):
    """Copy one selected immutable source to a new private file and verify the bytes actually copied."""
    source, destination = safe(source, True), safe(destination)
    with source.open('rb') as incoming, destination.open('xb') as outgoing:
        shutil.copyfileobj(incoming, outgoing, length=1024 * 1024)
    check(digest(destination) == expected, f'Private copy differs from selected source: {source}')
    return destination


def run(arguments, log, timeout):
    """Retain the original subprocess status and private log; never pass interpolated commands through a shell."""
    log = safe(log)
    environment = dict(os.environ, PYTHONUTF8='1', PYTHONIOENCODING='utf-8', PYTHONDONTWRITEBYTECODE='1')
    with log.open('x', encoding='utf-8') as output:
        result = subprocess.run([str(value) for value in arguments], cwd=REPO, stdout=output,
                                stderr=subprocess.STDOUT, env=environment, timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(f'Process failed ({result.returncode}); see {log}')
    return log


def dll_inventory(build):
    """Inventory only DLLs beneath the explicitly selected Release build, including the GUI copy when present."""
    release = safe(build / 'Release', True)
    check(release.is_dir(), 'The build root must contain Release.')
    result = {}
    for path in sorted(release.rglob('*.dll')):
        resolved = safe(path, True)
        check(resolved.is_relative_to(release), 'A build DLL resolves outside the named Release directory.')
        result[str(resolved)] = digest(resolved)
    check(result, 'The selected Release build contains no DLLs.')
    return result


def guard_errors(hashes, label):
    """Recheck only the named original or private immutable files and retain every guard failure."""
    errors = []
    for path, expected in hashes.items():
        try:
            if digest(path) != expected:
                errors.append(f'{label} bytes changed: {path}')
        except (OSError, AssertionError) as error:
            errors.append(f'{label} unavailable: {path}: {error}')
    return errors


def guards(build, assemblies, immutable):
    """Require the exact DLL inventory and all named private immutable inputs before accepting a row."""
    errors = guard_errors(immutable, 'Private immutable input')
    try:
        if dll_inventory(build) != assemblies:
            errors.append('The selected complete Release DLL inventory or bytes changed.')
    except (OSError, AssertionError) as error:
        errors.append(f'Release DLL guard failed: {error}')
    check(not errors, '\n'.join(errors))


def hardware():
    """Use the constructor-fixed new IRQ board and the existing controlled DOS/PCM/x87 fixture devices."""
    return dict(id='dose-430fx-piix-reset-dev-v1', cpu='p54c-b5-subset-development', cpuHz=100_000_000,
                name='DOSE 82371FB A1 reset subset DOS fixture', ramMiB=32, floppyDriveA='1440k', floppyDriveB='1440k',
                videoCard='dose-vga-development', soundCard='sb16-ct1740-dsp405-development',
                mouse='ps2-two-button', gameport='ibm-game-control-development', serialPort='pc16550d-com1-development',
                parallelPort='ibm-printer-adapter-development', midiInterface='ct1740-mpu401-uart-development')


def certify_row(work, system, program, module_hashes, oracle):
    """Require two actual reset vectors, three full imports, original DOS/KBC returns and exact VM identities."""
    evidence = safe(work / 'evidence', True)
    result = read_json(evidence / 'result.json')
    proof = read_json(run([sys.executable, oracle, evidence, '--program', program], work / 'oracle.json', 300))
    check(result.get('passed') is True and result.get('returnedToDos') is True and result.get('postResetEcho') is True and
          result.get('exits') == [True] * 4, 'All four actual original DOS returns and later KBC/echo missing.')
    check(result.get('system') == system and proof.get('system') == system and proof.get('guestVcpi') is system.startswith('emm386'),
          'Actual original DOS/manager execution mode differs.')
    check(proof.get('passed') is True and proof.get('resetVectors') == 2 and proof.get('cuts') == proof.get('restoredContinuations') == 3 and
          proof.get('negativeOracles', 0) > 0 and proof.get('dmaCommands') == 3 and proof.get('cancelledWriteSectors') == 1,
          'Actual reset/INIT/domain/DMA/media oracles missing.')
    check(result.get('comparisons', 0) > 5 and proof.get('comparisons') == result['comparisons'] and result.get('futureSteps', 0) > 0,
          'Whole-PC continuation comparisons missing.')
    check(result.get('programSha256') == digest(program) and len(result.get('audioSegments', [])) == 2 and
          len(result.get('audioFuture', {}).get('sha256', '')) == 64, 'Own executable/future PCM identity missing.')
    check(len(result.get('captures', [])) == 3, 'All three exact full captures missing.')
    media_payloads, media_ids = [], []
    for name, capture in zip(['soft', 'hard', 'held'], result['captures']):
        saved = evidence / (name + '-cut.dossav')
        manifest = read_json(saved / 'snapshot.json')
        check(capture['name'] == name and capture['manifest'] == manifest and manifest.get('runtime') == module_hashes and
              manifest.get('ramBytes') == 32 * 1048576, 'Executed exact VM build/full installed RAM differs.')
        check(capture.get('stateSha256') == digest(saved / 'state.json') and capture.get('ramSha256') == digest(saved / 'ram.bin'),
              'Actual full capture hashes differ.')
        media_file = saved / 'media.dosemedia/media.json'
        metadata = read_json(media_file)
        check(manifest.get('mediaSha256') == digest(media_file) and metadata.get('kind') == 'DOSE-MEDIA-1' and
              metadata.get('virtualNanoseconds') == manifest['virtualNanoseconds'],
              'Actual complete sealed media metadata/hash/shared cut differs.')
        media_id = metadata['id']
        check(str(uuid.UUID(media_id)).replace('-', '').lower() == media_id.replace('-', '').lower(),
              'Actual nested media container ID is not a UUID.')
        media_ids.append(media_id)
        canonical_media = dict(metadata)
        del canonical_media['id']
        media_payloads.append(canonical_media)
    check(len({capture['manifest']['id'] for capture in result['captures']}) == 3 and
          all(str(uuid.UUID(capture['manifest']['id'])).replace('-', '').lower() ==
              capture['manifest']['id'].replace('-', '').lower() for capture in result['captures']), 'Three actual unique container IDs missing.')
    check(len(set(media_ids)) == 3 and len(set(media_ids + [capture['manifest']['id'] for capture in result['captures']])) == 6,
          'Actual nested/outer container IDs must each identify distinct containers.')
    check(result.get('finalStateSha256') == digest(evidence / 'final-state.json') and result.get('finalRamSha256') == digest(evidence / 'final-ram.bin'),
          'Actual whole final graph/RAM differs.')
    stable = {key: result[key] for key in ('captures', 'finalStateSha256', 'finalRamSha256', 'programSha256',
              'audioSegments', 'audioFuture', 'programBase', 'comparisons', 'futureSteps', 'beforeEcho', 'finalScreen')}
    # All actual manifests, nested metadata and seals were checked above. Exactly six generated IDs are volatile.
    stable['captures'] = json.loads(json.dumps(stable['captures']))
    for capture, canonical_media in zip(stable['captures'], media_payloads):
        del capture['manifest']['id']
        # The actual seal includes the nested random ID. Preserve every other media field in the repetition value.
        capture['manifest']['mediaSha256'] = dict(randomIdFieldOmitted='id', canonicalMetadata=canonical_media)
    stable['ledgerSha256'] = digest(evidence / 'ledger.json')
    stable['independentOracle'] = proof
    encoded = json.dumps(stable, sort_keys=True, separators=(',', ':')).encode('utf-8')
    return result, proof, stable, hashlib.sha256(encoded).hexdigest().upper()


def main(args):
    """Prepare only selected sources, execute the requested matrix, and publish acceptance after independent row and guard checks."""
    build = safe(args.build, True)
    executable = build / 'Release/net10.0/DOSEmulator.Tests.dll'
    headless = executable.parent
    for name in REQUIRED_DLLS:
        check((headless / name).is_file(), f'Required tested assembly missing: {headless / name}')
    root = safe(args.output if args.output else REPO / 'artifacts/piix-reset-m4' / ('acceptance-' + uuid.uuid4().hex))
    check(not root.exists(), 'The output directory must be new; existing evidence is never replaced.')
    systems = [('msdos', args.disk, args.overlay), ('freedos', args.freedos_disk, None),
               ('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        systems = [system for system in systems if system[0] == args.only]
    check(systems, 'No selected DOS systems.')
    selected = {}
    for _, disk, overlay in systems:
        for path in (disk, overlay):
            if path is not None:
                resolved = safe(path, True)
                selected[str(resolved)] = digest(resolved)
    generator = safe(REPO / 'scripts/create-piix-reset-media.py', True)
    oracle = safe(REPO / 'scripts/verify-piix-reset.py', True)
    tools = {str(path): digest(path) for path in (generator, oracle,
             safe(REPO / 'tests/guests/piix-reset.asm', True), safe(__file__, True),
             safe(REPO / 'scripts/test-piix-reset.ps1', True))}
    assemblies = dll_inventory(build)
    module_hashes = {name: digest(headless / (name + '.dll')) for name in VM_MODULES}
    root.mkdir(parents=True, exist_ok=False)
    sources = root / 'sources'
    sources.mkdir()
    immutable, copies, rows, repeat_checks = {}, {}, [], []
    try:
        for index, (path, expected) in enumerate(selected.items(), 1):
            destination = sources / f'source-{index:02d}{Path(path).suffix or ".img"}'
            copies[path] = copy_new(path, destination, expected)
            immutable[str(destination)] = expected
        media_dir = root / 'media'
        generated = read_json(run([sys.executable, generator, '--output', media_dir], root / 'media.json', 120))
        program, floppy = safe(generated['program'], True), safe(generated['media'], True)
        check(program.is_relative_to(media_dir) and floppy.is_relative_to(media_dir), 'Generator output escaped its fresh private media directory.')
        check(generated.get('programSha256') == digest(program) and generated.get('mediaSha256') == digest(floppy),
              'Generator identities differ from the actual new COM/FAT12 bytes.')
        immutable[str(program)], immutable[str(floppy)] = digest(program), digest(floppy)
        scratch = safe(generated['scratch']['path'], True)
        auxiliaries = [safe(value['path'], True) for value in generated['auxiliaries']]
        second_floppy = safe(generated['secondFloppy'], True)
        check(len(auxiliaries) == 2 and all(p.is_relative_to(media_dir) for p in [scratch, *auxiliaries, second_floppy]),
              'Own generated six-position fixture escaped its private output.')
        for item in [generated['scratch'], *generated['auxiliaries']]:
            check(digest(item['path']) == item['sha256'], 'Generated own disk identity differs.')
        check(digest(second_floppy) == generated['secondFloppySha256'], 'Generated own floppy identity differs.')
        for item in [scratch, *auxiliaries, second_floppy]:
            immutable[str(item)] = digest(item)
        fingerprints = {}
        for system, disk, overlay in systems:
            private_base = copies[str(safe(disk, True))]
            template = copies[str(safe(overlay, True))] if overlay is not None else None
            for repeat in range(1, args.repeats + 1):
                guards(build, assemblies, immutable)
                check(not guard_errors(tools, 'Reproduction tool'), 'A named reproduction tool changed during the matrix.')
                work = root / f'{system}-{repeat}'
                work.mkdir()
                cow = work / 'original.cow'
                if template is not None:
                    copy_new(template, cow, immutable[str(template)])
                check(not cow.exists() or template is not None, 'An unexpected original overlay already exists.')
                # A stable own definition identity makes the entire captured/final hardware record comparable across repeats.
                machine_id = uuid.uuid5(uuid.NAMESPACE_URL, 'DOSE/PIIX-RESET/own-validation/' + system).hex
                definition = dict(schemaVersion=1, id=machine_id, name='Own full PIIX CF9 reset validation ' + system,
                                  hardware=hardware(), hardDisks=[dict(slot=0, imagePath=str(private_base), overlayPath=str(cow)),
                                  dict(slot=1, imagePath=str(scratch), overlayPath=str(work / 'scratch.cow')),
                                  dict(slot=2, imagePath=str(auxiliaries[0])), dict(slot=3, imagePath=str(auxiliaries[1]))],
                                  floppies=[dict(slot=0, imagePath=str(floppy)), dict(slot=1, imagePath=str(second_floppy))], builtInTestDisk=False)
                machine_path = work / 'source.dospc.json'
                write_json_new(machine_path, definition)
                input_path = work / 'input.json'
                write_json_new(input_path, dict(machine=str(machine_path), output=str(work / 'evidence'),
                                               germanKeyboard=system != 'freedos', probe=str(program), system=system))
                immutable[str(machine_path)], immutable[str(input_path)] = digest(machine_path), digest(input_path)
                run(['dotnet', executable, '--piix-reset-guest', input_path], work / 'run.log', 1800)
                result, proof, stable, fingerprint = certify_row(work, system, program, module_hashes, oracle)
                guards(build, assemblies, immutable)
                check(not guard_errors(tools, 'Reproduction tool'), 'A named reproduction tool changed before row acceptance.')
                if system in fingerprints:
                    prior = fingerprints[system]
                    check(prior['values'] == stable, f'Actual whole-state/RAM/IRQ/file/PCM repetition differs for {system}.')
                    repeat_checks.append(dict(system=system, firstRepeat=prior['repeat'], repeated=repeat,
                                              fingerprintSha256=fingerprint, passed=True))
                else:
                    fingerprints[system] = dict(values=stable, repeat=repeat)
                row = dict(accepted=True, system=system, repeat=repeat, directory=str(work), result=result,
                           independentOracle=proof, repetitionSha256=fingerprint)
                rows.append(row)
                write_json_new(work / 'accepted-row.json', row)
                print('PASS', system, repeat, 'resets', proof['resetVectors'], 'comparisons', result['comparisons'], flush=True)
        errors = guard_errors(selected, 'Selected original source') + guard_errors(tools, 'Reproduction tool')
        check(not errors, '\n'.join(errors))
        guards(build, assemblies, immutable)
        check(len(rows) == len(systems) * args.repeats and
              (args.repeats == 1 or len(repeat_checks) == len(systems)), 'Requested actual matrix/repetition scope is incomplete.')
        report = dict(passed=True, coldStarts=len(rows), restoredContinuations=3 * len(rows), selectedSystems=[item[0] for item in systems],
                      repeats=args.repeats, fullEightRunMatrix=not args.only and args.repeats == 2, rows=rows,
                      repeatedDeterminism=repeat_checks, sources=selected, privateInputs=immutable, assemblies=assemblies,
                      runtimeModules=module_hashes, tools=tools, probeSha256=digest(program), floppySha256=digest(floppy),
                      guestResetVectors=sum(row['independentOracle']['resetVectors'] for row in rows),
                      fullComparisons=sum(row['result']['comparisons'] for row in rows),
                      negativeOracles=sum(row['independentOracle']['negativeOracles'] for row in rows),
                      limitations=['Own original-82371FB CF9 subset and explicit early DOSE firmware warm rendezvous.',
                                   'Three complete actual restores per cold start; original resident DOS/EMM386 retained.',
                                   'Nominal 1-ms PCI/ISA and 2-ms CPU holds; no electrical bus/reset calibration.',
                                   'Actual INIT preserves x87/TSC; hard reset cancels the private pending ATA write before commit.',
                                   'ATAPI, complete chipset/Windows95 and native input remain open.',
                                   'No host endpoints, recordings, original-media writes or personal play overlays.'])
        write_json_new(root / 'acceptance.json', report)
        print(root, flush=True)
        return root
    except Exception as error:
        errors = guard_errors(selected, 'Selected original source') + guard_errors(tools, 'Reproduction tool')
        try:
            guards(build, assemblies, immutable)
        except (OSError, AssertionError) as guard_error:
            errors.append(str(guard_error))
        write_json_new(root / 'failure.json', dict(passed=False, error=repr(error), completedRows=rows,
                                                  guardFailures=errors, sources=selected, assemblies=assemblies, tools=tools))
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', required=True, type=Path, help='Build root containing Release/net10.0.')
    parser.add_argument('--disk', required=True, type=Path, help='Explicit installed MS-DOS base image.')
    parser.add_argument('--overlay', required=True, type=Path, help='Explicit installed MS-DOS source COW.')
    parser.add_argument('--freedos-disk', required=True, type=Path, help='Explicit original FreeDOS boot base image.')
    parser.add_argument('--configured-disk', required=True, type=Path, help='Explicit original EMM386 fixture base image.')
    parser.add_argument('--noems-overlay', required=True, type=Path, help='Explicit original EMM386 NOEMS source COW.')
    parser.add_argument('--ram-overlay', required=True, type=Path, help='Explicit original EMM386 RAM source COW.')
    parser.add_argument('--output', type=Path, help='Optional fresh private output directory; existing targets are rejected.')
    parser.add_argument('--repeats', type=int, choices=(1, 2), default=2)
    parser.add_argument('--only', choices=SYSTEMS)
    main(parser.parse_args())
