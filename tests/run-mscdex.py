# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce real original-MSCDEX filesystem access through the own DOS CD driver on fresh private DOS copies."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys
import uuid

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]


def module(name, path):
    """Load one explicitly named existing reproduction helper without creating bytecode files."""
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


common = module('mscdex_matrix_common', REPO / 'tests/run-atapi.py')
generator = module('mscdex_matrix_generator', REPO / 'scripts/create-mscdex-test.py')
fat = module('mscdex_matrix_fat', REPO / 'tests/verify-msdos-install.py')


def certify_row(work, system, generated, driver, mscdex, modules):
    """Require independently observed DOS files, real PIO and a sealed full-PC continuation before accepting a row."""
    evidence = work / 'evidence'
    result = common.read_json(evidence / 'result.json')
    proof = common.read_json(common.run([sys.executable, REPO / 'scripts/verify-mscdex-test.py', evidence,
                                         '--program', generated['program'], '--iso', generated['iso'], '--driver', driver,
                                         '--mscdex', mscdex], work / 'oracle.json', 600))
    common.check(result.get('passed') is True and result.get('returnedToDos') is True and result.get('postMscdexEcho') is True and
                 result.get('exits') == [True, True, True], 'All three actual original-DOS returns and fresh echo are required.')
    common.check(result.get('system') == proof.get('system') == system and proof.get('passed') is True and
                 proof.get('restoredContinuations') == 2 and proof.get('negativeOracles', 0) > 0,
                 'Independent DOS/MSCDEX/PIO/continuation proof is incomplete.')
    common.check(result.get('comparisons', 0) > 2 and result.get('futureSteps', 0) > 10000, 'Whole-PC future comparisons missing.')
    for key, path in [('program', generated['program']), ('iso', generated['iso']), ('driver', driver), ('mscdex', mscdex)]:
        common.check(result[key + 'Sha256'] == common.digest(path), 'Actual guest/driver/original MSCDEX identity differs: ' + key)
    canonical, ids = {}, []
    for name in ('packet', 'data'):
        saved = evidence / (name + '-cut.dossav')
        manifest, media = common.read_json(saved / 'snapshot.json'), common.read_json(saved / 'media.dosemedia/media.json')
        common.check(manifest == result[name + 'Manifest'] and manifest['runtime'] == modules and manifest['ramBytes'] == 32 * 1048576 and
                     manifest['mediaSha256'] == common.digest(saved / 'media.dosemedia/media.json') and
                     manifest['virtualNanoseconds'] == media['virtualNanoseconds'], 'Full capture/VM/sealed-media identity differs.')
        for suffix, path in [('State', saved / 'state.json'), ('Ram', saved / 'ram.bin')]:
            common.check(result[name + suffix + 'Sha256'] == common.digest(path), 'Whole raw capture bytes differ.')
        ids += [manifest['id'], media['id']]
        stable_media, stable_manifest = dict(media), dict(manifest)
        del stable_media['id']
        del stable_manifest['id']
        stable_manifest['mediaSha256'] = dict(onlyVerifiedUuidOmitted='id', canonicalMetadata=stable_media)
        canonical[name] = stable_manifest
    common.check(len({uuid.UUID(value) for value in ids}) == 4, 'Four actual distinct capture UUIDs missing.')
    for name in ('finalState', 'finalRam'):
        common.check(result[name + 'Sha256'] == common.digest(evidence / ('final-state.json' if name == 'finalState' else 'final-ram.bin')),
                     'Whole final bytes differ.')
    stable = {key: value for key, value in result.items() if key not in ('hostSeconds', 'packetManifest', 'dataManifest')}
    stable.update(manifests=canonical, ledgerSha256=common.digest(evidence / 'ledger.json'), independentOracle=proof)
    fingerprint = hashlib.sha256(json.dumps(stable, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest().upper()
    return result, proof, stable, fingerprint


def main(args):
    """Copy only six named own inputs, install the own SYS privately and execute the requested real DOS/MSCDEX matrix."""
    build = common.safe(args.build, True)
    root = common.safe(args.output if args.output else REPO / 'artifacts/mscdex-m4' / ('acceptance-' + uuid.uuid4().hex))
    common.check(not root.exists(), 'A wholly new output directory is required.')
    executable = build / 'Release/net10.0/DOSEmulator.Tests.dll'
    common.check(executable.is_file(), 'Selected test build missing.')
    driver = build / 'Release/drivers/DOSECD.SYS'
    common.check(driver.is_file(), 'The selected product build has no own DOS CD driver.')
    selected_systems = [('msdos', args.disk, args.overlay), ('freedos', args.freedos_disk, None),
                        ('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        selected_systems = [row for row in selected_systems if row[0] == args.only]
    selected = {str(common.safe(path, True)): common.digest(path) for _, disk, overlay in selected_systems
                for path in (disk, overlay) if path is not None}
    source_tools = [Path(__file__), REPO / 'scripts/create-mscdex-test.py', REPO / 'scripts/verify-mscdex-test.py',
                    REPO / 'scripts/test-mscdex.ps1', REPO / 'tests/guests/mscdex-test.asm', REPO / 'firmware/dose-cdrom.asm',
                    REPO / 'scripts/build-cdrom-driver.py', REPO / 'tests/run-atapi.py', REPO / 'scripts/create-atapi-test.py',
                    REPO / 'scripts/verify-atapi-test.py', REPO / 'scripts/verify-piix-ide.py',
                    REPO / 'tests/prepare-font-media.py', REPO / 'tests/verify-msdos-install.py',
                    REPO / 'tests/verify-p54c-pc.py', REPO / 'tests/verify-snapshot-continuation.py',
                    REPO / 'tests/verify-media-checkpoint.py', REPO / 'docs/state-schema-v1.json',
                    REPO / 'tests/verify-font-frame.py', REPO / 'tests/verify-cp437-frame.py',
                    REPO / 'firmware/fonts/cp437-8x8.txt']
    tools = {str(path): common.digest(path) for path in source_tools}
    assemblies = common.dll_inventory(build)
    modules = {name: common.digest(executable.parent / (name + '.dll')) for name in common.VM_MODULES}
    root.mkdir(parents=True)
    (root / 'sources').mkdir()
    immutable, copies, rows, repeats, fingerprints = {str(driver): common.digest(driver)}, {}, [], [], {}
    try:
        for index, (name, expected) in enumerate(selected.items(), 1):
            destination = root / 'sources' / ('source-%02d' % index + Path(name).suffix)
            copies[name] = common.copy_new(name, destination, expected)
            immutable[str(destination)] = expected
        generated = generator.generate(root / 'media')
        common.write_json_new(root / 'generated.json', generated)
        for name in ('program', 'floppy', 'iso'):
            immutable[generated[name]] = common.digest(generated[name])
        source_base, source_cow = common.safe(args.disk, True), common.safe(args.overlay, True)
        # An explicit --only freedos row still obtains the original MSCDEX from the named, private MS-DOS fixture.
        for path in (source_base, source_cow):
            if str(path) not in copies:
                expected = common.digest(path)
                selected[str(path)] = expected
                destination = root / 'sources' / ('mscdex-source' + path.suffix)
                copies[str(path)] = common.copy_new(path, destination, expected)
                immutable[str(destination)] = expected
        dos_disk, _ = fat.materialize(copies[str(source_base)], copies[str(source_cow)])
        start = struct.unpack_from('<I', dos_disk, 454)[0] * 512
        source_files, _ = fat.Fat(dos_disk, start).files()
        original_mscdex = source_files['DOS/MSCDEX.EXE']
        common.check(hashlib.sha256(original_mscdex).hexdigest().upper() == '9B292B062CD33644A7706670E92BA7E68D39D7655CBCBFF293F7D626E721441A',
                     'Original selected MSCDEX identity differs.')
        mscdex = root / 'sources/MSCDEX.EXE'
        generator.write_new(mscdex, original_mscdex)
        immutable[str(mscdex)] = common.digest(mscdex)
        for system, disk, overlay in selected_systems:
            private_base = root / 'sources' / (system + '-with-cd.img')
            preparation = generator.prepare_dos(copies[str(common.safe(disk, True))],
                copies[str(common.safe(overlay, True))] if overlay else None, driver, mscdex, system, private_base)
            common.write_json_new(root / 'sources' / (system + '-installation.json'), preparation)
            immutable[str(private_base)] = common.digest(private_base)
            for repeat in range(1, args.repeats + 1):
                common.guards(build, assemblies, immutable)
                row_work = root / (system + '-' + str(repeat))
                row_work.mkdir()
                machine = dict(schemaVersion=1, id=uuid.uuid5(uuid.NAMESPACE_URL, 'DOSE/MSCDEX/own-validation/' + system).hex,
                    name='Own DOS CD-driver and original MSCDEX ' + system, hardware=common.hardware(),
                    hardDisks=[dict(slot=0, imagePath=str(private_base), overlayPath=str(row_work / 'original.cow'))],
                    opticalDiscs=[dict(slot=2, imagePath=generated['iso'])],
                    floppies=[dict(slot=0, imagePath=generated['floppy'])], builtInTestDisk=False)
                machine['hardware']['name'] = 'DOSE own DOS CD driver / MSCDEX filesystem fixture'
                machine_path, input_path = row_work / 'source.dospc.json', row_work / 'input.json'
                common.write_json_new(machine_path, machine)
                common.write_json_new(input_path, dict(machine=str(machine_path), output=str(row_work / 'evidence'),
                    germanKeyboard=system != 'freedos', probe=generated['program'], system=system, iso=generated['iso'],
                    driver=str(driver), mscdex=str(mscdex), firstCutTimeoutSeconds=240))
                common.run(['dotnet', executable, '--mscdex-guest', input_path], row_work / 'run.log', 2100)
                result, proof, stable, fingerprint = certify_row(row_work, system, generated, driver, mscdex, modules)
                common.guards(build, assemblies, immutable)
                common.check(not common.guard_errors(tools, 'Named reproduction source'), 'Named source changed during a run.')
                if system in fingerprints:
                    common.check(stable == fingerprints[system], 'Actual repeated CPU/RAM/VRAM/ledger/files/clock/capture differs for ' + system)
                    repeats.append(dict(system=system, passed=True, fingerprintSha256=fingerprint))
                else:
                    fingerprints[system] = stable
                row = dict(passed=True, system=system, repeat=repeat, directory=str(row_work), result=result,
                           independentOracle=proof, repetitionSha256=fingerprint)
                rows.append(row)
                common.write_json_new(row_work / 'accepted-row.json', row)
                print('PASS', system, repeat, 'full comparisons', result['comparisons'], 'negative oracles', proof['negativeOracles'], flush=True)
        common.check(not common.guard_errors(selected, 'Named original own fixture') and not common.guard_errors(tools, 'Named source'),
                     'Original fixture or source changed before acceptance.')
        common.guards(build, assemblies, immutable)
        report = dict(passed=True, coldStarts=len(rows), restoredContinuations=2 * len(rows), systems=[row[0] for row in selected_systems],
            repeats=args.repeats, fullEightRunMatrix=not args.only and args.repeats == 2, rows=rows, repeatedDeterminism=repeats,
            sourceInputs=selected, privateInputs=immutable, tools=tools, assemblies=assemblies, runtimeModules=modules,
            driverSha256=common.digest(driver), originalMscdexSha256=common.digest(mscdex), generated=generated,
            fullComparisons=sum(row['result']['comparisons'] for row in rows),
            negativeOracles=sum(row['independentOracle']['negativeOracles'] for row in rows),
            limitations=['Own fixed secondary-master PIO cooked/HSG SYS and the named original MSCDEX.',
                         'Full 32-MiB PC continuation inside an actual SYS read with an open original DOS CD handle.',
                         'No CD audio/raw sectors/ATAPI DMA/Windows95/native-input/host endpoint acceptance.'])
        common.write_json_new(root / 'acceptance.json', report)
        print(root, flush=True)
    except Exception as error:
        common.write_json_new(root / 'failure.json', dict(passed=False, error=repr(error), completedRows=rows,
            guardFailures=common.guard_errors(selected, 'Named original') + common.guard_errors(tools, 'Named source') +
                          common.guard_errors(immutable, 'Named private immutable input')))
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('build', 'disk', 'overlay', 'freedos-disk', 'configured-disk', 'noems-overlay', 'ram-overlay'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--repeats', type=int, choices=(1, 2), default=2)
    parser.add_argument('--only', choices=common.SYSTEMS)
    main(parser.parse_args())
