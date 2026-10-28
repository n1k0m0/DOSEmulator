# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Reproduce actual PCI BIOS/BIOS32 calls and full continuation on fresh private original-DOS/MSCDEX copies."""
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
    """Import one explicitly selected reproduction helper without writing caches."""
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


common = module('pci_matrix_common', REPO / 'tests/run-atapi.py')
generator = module('pci_matrix_generator', REPO / 'scripts/create-pci-bios-test.py')
fat = module('pci_matrix_fat', REPO / 'tests/verify-msdos-install.py')


def certify_row(work, system, generated, expected, modules):
    """Bind independent firmware/guest/resource/DOS proof to all raw captures and all future state bytes."""
    evidence = work / 'evidence'
    result = common.read_json(evidence / 'result.json')
    proof = common.read_json(common.run([sys.executable, REPO / 'scripts/verify-pci-bios-test.py', evidence,
        '--program', generated['program'], '--iso', generated['iso'], '--firmware', generated['firmware'],
        '--expected-firmware', expected['path']], work / 'oracle.json', 600))
    common.check(result.get('passed') is True and result.get('returnedToDos') is True and result.get('postPciBiosEcho') is True and
                 result.get('exits') == [True, True, True], 'Three actual original-DOS returns and a new ECHO are required.')
    common.check(result.get('system') == proof.get('system') == system and proof.get('passed') is True and
                 proof.get('restoredContinuations') == 2 and proof.get('negativeOracles', 0) > 0,
                 'Independent firmware/resource/continuation proof is incomplete.')
    common.check(result.get('comparisons', 0) > 2 and result.get('futureSteps', 0) > 10000, 'Actual whole-PC future missing.')
    common.check(result['programSha256'] == common.digest(generated['program']) and result['isoSha256'] == common.digest(generated['iso']) and
                 result['firmwareSha256'] == expected['sha256'] and result['rawFirmwareSha256'] == common.digest(generated['firmware']),
                 'The actual guest or complete selected firmware identity differs.')
    canonical, identifiers = {}, []
    for name in ('bios16', 'bios32'):
        saved = evidence / (name + '-cut.dossav')
        manifest, media = common.read_json(saved / 'snapshot.json'), common.read_json(saved / 'media.dosemedia/media.json')
        common.check(manifest == result[name + 'Manifest'] and manifest['runtime'] == modules and manifest['ramBytes'] == 32 * 1048576 and
                     manifest['mediaSha256'] == common.digest(saved / 'media.dosemedia/media.json') and
                     manifest['virtualNanoseconds'] == media['virtualNanoseconds'], 'Full capture/VM/sealed-media identity differs.')
        for suffix, file in (('State', 'state.json'), ('Ram', 'ram.bin')):
            common.check(result[name + suffix + 'Sha256'] == common.digest(saved / file), 'The full raw capture bytes differ.')
        identifiers += [manifest['id'], media['id']]
        stable_manifest, stable_media = dict(manifest), dict(media)
        del stable_manifest['id']
        del stable_media['id']
        stable_manifest['mediaSha256'] = dict(onlyVerifiedUuidOmitted='id', canonicalMetadata=stable_media)
        canonical[name] = stable_manifest
    common.check(len({uuid.UUID(value) for value in identifiers}) == 4, 'Four distinct actual capture UUIDs are required.')
    for name, file in (('finalState', 'final-state.json'), ('finalRam', 'final-ram.bin')):
        common.check(result[name + 'Sha256'] == common.digest(evidence / file), 'Full final state bytes differ.')
    final_media = [common.read_json(evidence / ('final-' + str(index) + '.dosemedia/media.json')) for index in range(3)]
    common.check(all(set(value) == {'schemaVersion', 'kind', 'id', 'virtualNanoseconds', 'machine', 'media'} and
                     value['schemaVersion'] == 1 and value['kind'] == 'DOSE-MEDIA-1' for value in final_media),
                 'Unexpected final media wrapper fields or version.')
    common.check(len({uuid.UUID(value['id']) for value in final_media}) == 3, 'Distinct final media capture identities missing.')
    final_binding = {key: final_media[0][key] for key in ('schemaVersion', 'kind', 'media', 'virtualNanoseconds', 'machine')}
    common.check(all({key: value[key] for key in final_binding} == final_binding for value in final_media),
                 'Three complete final media arrays, machine binding or times differ.')
    stable = {key: value for key, value in result.items() if key not in ('hostSeconds', 'bios16Manifest', 'bios32Manifest')}
    stable.update(manifests=canonical, finalMedia=final_binding, ledgerSha256=common.digest(evidence / 'ledger.json'), independentOracle=proof)
    fingerprint = hashlib.sha256(json.dumps(stable, sort_keys=True, separators=(',', ':')).encode('utf-8')).hexdigest().upper()
    return result, proof, stable, fingerprint


def main(args):
    """Prepare only fresh selected DOS copies and execute the requested real firmware/manager matrix."""
    build = common.safe(args.build, True)
    root = common.safe(args.output if args.output else REPO / 'artifacts/pci-bios-m4' / ('acceptance-' + uuid.uuid4().hex))
    common.check(not root.exists(), 'A wholly new output directory is required.')
    executable, driver = build / 'Release/net10.0/DOSEmulator.Tests.dll', build / 'Release/drivers/DOSECD.SYS'
    common.check(executable.is_file() and driver.is_file(), 'The selected product build/test client/own DOS driver is missing.')
    selected_systems = [('msdos', args.disk, args.overlay), ('freedos', args.freedos_disk, None),
                        ('emm386-noems', args.configured_disk, args.noems_overlay), ('emm386-ram', args.configured_disk, args.ram_overlay)]
    if args.only:
        selected_systems = [row for row in selected_systems if row[0] == args.only]
    selected = {str(common.safe(path, True)): common.digest(path) for _, disk, overlay in selected_systems
                for path in (disk, overlay) if path is not None}
    source_tools = [Path(__file__), REPO / 'scripts/create-pci-bios-test.py', REPO / 'scripts/verify-pci-bios-test.py',
        REPO / 'scripts/test-pci-bios.ps1', REPO / 'tests/guests/pci-bios-test.asm', REPO / 'tests/DOSEmulator.Tests/PciBiosGuestRunner.cs',
        REPO / 'firmware/pci-bios.inc', REPO / 'tests/run-atapi.py', REPO / 'scripts/create-mscdex-test.py',
        REPO / 'scripts/create-atapi-test.py', REPO / 'scripts/verify-mscdex-test.py', REPO / 'scripts/verify-atapi-test.py',
        REPO / 'scripts/verify-piix-ide.py', REPO / 'tests/prepare-font-media.py', REPO / 'tests/verify-msdos-install.py',
        REPO / 'tests/verify-p54c-pc.py', REPO / 'tests/verify-snapshot-continuation.py', REPO / 'tests/verify-media-checkpoint.py',
        REPO / 'docs/state-schema-v1.json', REPO / 'tests/verify-font-frame.py', REPO / 'tests/verify-cp437-frame.py',
        REPO / 'firmware/fonts/cp437-8x8.txt']
    tools = {str(path): common.digest(path) for path in source_tools}
    assemblies = common.dll_inventory(build)
    modules = {name: common.digest(executable.parent / (name + '.dll')) for name in common.VM_MODULES}
    root.mkdir(parents=True)
    (root / 'sources').mkdir()
    immutable, copies, rows, repeated, fingerprints = {str(driver): common.digest(driver)}, {}, [], [], {}
    try:
        for index, (name, expected) in enumerate(selected.items(), 1):
            destination = root / 'sources' / ('source-%02d' % index + Path(name).suffix)
            copies[name] = common.copy_new(name, destination, expected)
            immutable[str(destination)] = expected
        generated = generator.generate(root / 'media', common.safe(args.firmware, True))
        common.write_json_new(root / 'generated.json', generated)
        for name in ('program', 'floppy', 'iso', 'firmware'):
            immutable[generated[name]] = common.digest(generated[name])
        source_base, source_cow = common.safe(args.disk, True), common.safe(args.overlay, True)
        for path in (source_base, source_cow):
            if str(path) not in copies:
                expected = common.digest(path)
                selected[str(path)] = expected
                destination = root / 'sources' / ('mscdex-source' + path.suffix)
                copies[str(path)] = common.copy_new(path, destination, expected)
                immutable[str(destination)] = expected
        dos_disk, _ = fat.materialize(copies[str(source_base)], copies[str(source_cow)])
        source_files, _ = fat.Fat(dos_disk, struct.unpack_from('<I', dos_disk, 454)[0] * 512).files()
        original_mscdex = source_files['DOS/MSCDEX.EXE']
        common.check(hashlib.sha256(original_mscdex).hexdigest().upper() == '9B292B062CD33644A7706670E92BA7E68D39D7655CBCBFF293F7D626E721441A',
                     'The selected original MSCDEX identity differs.')
        mscdex = root / 'sources/MSCDEX.EXE'
        generator.cd.write_new(mscdex, original_mscdex)
        immutable[str(mscdex)] = common.digest(mscdex)
        for system, disk, overlay in selected_systems:
            private_base = root / 'sources' / (system + '-with-cd.img')
            preparation = generator.cd.prepare_dos(copies[str(common.safe(disk, True))],
                copies[str(common.safe(overlay, True))] if overlay else None, driver, mscdex, system, private_base)
            private_disk = private_base.read_bytes()
            files, _ = fat.Fat(private_disk, struct.unpack_from('<I', private_disk, 454)[0] * 512).files()
            common.check(not {'PCIBIOS.DAT', 'PCIBRAW.DAT', 'PCIBCD.TXT'} & files.keys(), 'The own proof names already belong to the source DOS fixture.')
            common.write_json_new(root / 'sources' / (system + '-installation.json'), preparation)
            immutable[str(private_base)] = common.digest(private_base)
            expected_rom = generator.expected_firmware(generated['firmware'], private_base, root / 'sources' / (system + '-expected-rom.bin'))
            immutable[expected_rom['path']] = expected_rom['sha256']
            common.write_json_new(root / 'sources' / (system + '-firmware.json'), expected_rom)
            for repeat in range(1, args.repeats + 1):
                common.guards(build, assemblies, immutable)
                row_work = root / (system + '-' + str(repeat))
                row_work.mkdir()
                hardware = common.hardware()
                hardware.update(id='dose-430fx-piix-s3-atapi-pcibios-dev-v1', name='DOSE own PCI BIOS and BIOS32 firmware fixture')
                machine = dict(schemaVersion=1, id=uuid.uuid5(uuid.NAMESPACE_URL, 'DOSE/PCI-BIOS/own-validation/' + system).hex,
                    name='Own PCI BIOS firmware and original DOS/MSCDEX ' + system, hardware=hardware,
                    hardDisks=[dict(slot=0, imagePath=str(private_base), overlayPath=str(row_work / 'original.cow'))],
                    opticalDiscs=[dict(slot=2, imagePath=generated['iso'])], floppies=[dict(slot=0, imagePath=generated['floppy'])], builtInTestDisk=False)
                machine_path, input_path = row_work / 'source.dospc.json', row_work / 'input.json'
                common.write_json_new(machine_path, machine)
                common.write_json_new(input_path, dict(machine=str(machine_path), output=str(row_work / 'evidence'),
                    germanKeyboard=system != 'freedos', probe=generated['program'], system=system, iso=generated['iso'],
                    firmware=generated['firmware'], expectedFirmware=expected_rom['path'], firstCutTimeoutSeconds=240))
                common.run(['dotnet', executable, '--pci-bios-guest', input_path], row_work / 'run.log', 1200)
                result, proof, stable, fingerprint = certify_row(row_work, system, generated, expected_rom, modules)
                common.guards(build, assemblies, immutable)
                common.check(not common.guard_errors(tools, 'Named reproduction source'), 'A named source changed during the run.')
                if system in fingerprints:
                    common.check(stable == fingerprints[system], 'Actual repeated whole state/firmware/ledger/media/clock differs for ' + system)
                    repeated.append(dict(system=system, passed=True, fingerprintSha256=fingerprint))
                else:
                    fingerprints[system] = stable
                rows.append(dict(passed=True, system=system, repeat=repeat, directory=str(row_work), result=result,
                                 independentOracle=proof, repetitionSha256=fingerprint))
                common.write_json_new(row_work / 'accepted-row.json', rows[-1])
                print('PASS', system, repeat, 'full comparisons', result['comparisons'], 'negative oracles', proof['negativeOracles'], flush=True)
        common.check(not common.guard_errors(selected, 'Named own source fixture') and not common.guard_errors(tools, 'Named source'),
                     'A selected source fixture or source changed before acceptance.')
        common.guards(build, assemblies, immutable)
        report = dict(passed=True, coldStarts=len(rows), restoredContinuations=2 * len(rows), systems=[row[0] for row in selected_systems],
            repeats=args.repeats, fullEightRunMatrix=not args.only and args.repeats == 2, rows=rows, repeatedDeterminism=repeated,
            sourceInputs=selected, privateInputs=immutable, tools=tools, assemblies=assemblies, runtimeModules=modules, generated=generated,
            originalMscdexSha256=common.digest(mscdex), fullComparisons=sum(row['result']['comparisons'] for row in rows),
            negativeOracles=sum(row['independentOracle']['negativeOracles'] for row in rows),
            limitations=['Own fixed PCI BIOS2.1 subset, BIOS32 and named original DOS/EMM386/MSCDEX only.',
                         'Two full32MiB captures during genuine16/32-bit firmware mechanism1 transactions.',
                         'No Windows95/native input/original display-driver/complete chipset/host endpoint acceptance.'])
        common.write_json_new(root / 'acceptance.json', report)
        print(root, flush=True)
    except Exception as error:
        common.write_json_new(root / 'failure.json', dict(passed=False, error=repr(error), completedRows=rows,
            guardFailures=common.guard_errors(selected, 'Named own source') + common.guard_errors(tools, 'Named source') +
                          common.guard_errors(immutable, 'Named private immutable input')))
        raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('build', 'disk', 'overlay', 'freedos-disk', 'configured-disk', 'noems-overlay', 'ram-overlay', 'firmware'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--repeats', type=int, choices=(1, 2), default=2)
    parser.add_argument('--only', choices=common.SYSTEMS)
    main(parser.parse_args())
