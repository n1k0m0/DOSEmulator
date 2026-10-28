# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble an own DOS filesystem client, create an own ISO and add the own CD driver only to fresh DOS fixture images."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]


def check(condition, reason):
    """Reject inconsistent fixtures without relying on optimized-away assertions."""
    if not condition:
        raise ValueError(reason)


def safe(path):
    """Resolve one explicitly selected path, excluding the personal play tree before access."""
    path = Path(path)
    check('doom2-play' not in str(path.absolute()).casefold(), 'Personal play paths are excluded.')
    result = path.resolve()
    check('doom2-play' not in str(result).casefold(), 'Resolved personal play paths are excluded.')
    return result


def module(name, path):
    """Load a named existing independent fixture helper without writing Python caches."""
    spec = importlib.util.spec_from_file_location(name, safe(path))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def sha(data):
    """Compute the identity of a known in-memory byte stream."""
    return hashlib.sha256(data).hexdigest().upper()


def write_new(path, data):
    """Create one own artifact without overwriting an existing output."""
    with safe(path).open('xb') as stream:
        stream.write(data)


def root_patch(disk, replacements, destination):
    """Replace only explicitly named root files in a fresh FAT16 copy, preserving all other file bytes and boot geometry."""
    fat = module('mscdex_fat', REPO / 'tests/verify-msdos-install.py')
    start = struct.unpack_from('<I', disk, 454)[0] * 512
    volume = fat.Fat(disk, start)
    check(volume.bits == 16, 'The selected own DOS fixture must be FAT16.')
    old, _ = volume.files()
    check(all('/' not in name and name.upper() == name for name in replacements), 'Only named uppercase root files may change.')
    result = bytearray(disk)
    fat_start = start + volume.reserved * 512
    root_start = start + (volume.reserved + volume.copies * volume.spf) * 512
    cluster_bytes = volume.spc * 512
    entries = {}
    for index in range(volume.entries):
        address = root_start + index * 32
        entry = result[address:address + 32]
        if entry[0] in (0, 0xe5) or entry[11] & 0x18:
            continue
        name = entry[:8].decode('cp850').rstrip()
        extension = entry[8:11].decode('cp850').rstrip()
        entries[name + ('.' + extension if extension else '')] = address
    released = set()
    for name in replacements:
        if name not in entries:
            continue
        address = entries[name]
        first = struct.unpack_from('<H', result, address + 26)[0]
        released.update(volume.chain(first) if first else [])
        result[address] = 0xe5
    free = [number for number in range(2, volume.clusters + 2) if volume.entry(number) == 0 or number in released]
    for number in released:
        for copy in range(volume.copies):
            struct.pack_into('<H', result, fat_start + copy * volume.spf * 512 + number * 2, 0)
    slots = [root_start + index * 32 for index in range(volume.entries) if result[root_start + index * 32] in (0, 0xe5)]
    check(len(slots) >= len(replacements), 'The fresh fixture root has insufficient entries.')
    for (name, data), address in zip(replacements.items(), slots):
        count = (len(data) + cluster_bytes - 1) // cluster_bytes
        check(count <= len(free), 'The fresh fixture has insufficient free clusters.')
        chain, free = free[:count], free[count:]
        for index, number in enumerate(chain):
            next_number = chain[index + 1] if index + 1 < len(chain) else 0xffff
            for copy in range(volume.copies):
                struct.pack_into('<H', result, fat_start + copy * volume.spf * 512 + number * 2, next_number)
            data_address = start + (volume.data_sector + (number - 2) * volume.spc) * 512
            result[data_address:data_address + cluster_bytes] = data[index * cluster_bytes:(index + 1) * cluster_bytes].ljust(cluster_bytes, b'\0')
        parts = name.split('.')
        check(1 <= len(parts) <= 2 and len(parts[0]) <= 8 and (len(parts) == 1 or len(parts[1]) <= 3), 'Invalid short name.')
        result[address:address + 32] = bytes(32)
        result[address:address + 11] = (parts[0].ljust(8) + (parts[1] if len(parts) == 2 else '').ljust(3)).encode('ascii')
        result[address + 11] = 0x20
        struct.pack_into('<HI', result, address + 26, chain[0] if chain else 0, len(data))
    actual, allocation = fat.Fat(result, start).files()
    check(actual == old | replacements, 'Unrelated DOS files changed during fresh-copy preparation.')
    check(result[:512] == disk[:512] and result[start:start + 512] == disk[start:start + 512], 'DOS boot geometry changed.')
    write_new(destination, result)
    return dict(path=str(safe(destination)), bytes=len(result), sha256=sha(result), originalFiles=len(old), files=len(actual), allocation=allocation,
                changedFiles={name: dict(bytes=len(data), sha256=sha(data), replaced=name in old) for name, data in replacements.items()},
                allOtherOriginalFilesUnchanged=True, bootSectorsUnchanged=True)


def prepare_dos(base, overlay, driver, mscdex, system, destination):
    """Install a fixed own driver and the existing original MSCDEX into a new private DOS image only."""
    fat = module('mscdex_source_fat', REPO / 'tests/verify-msdos-install.py')
    base, destination = safe(base), safe(destination)
    check(not destination.exists(), 'Fresh DOS image already exists.')
    disk, journal = fat.materialize(base, safe(overlay)) if overlay else (base.read_bytes(), None)
    start = struct.unpack_from('<I', disk, 454)[0] * 512
    old, _ = fat.Fat(disk, start).files()
    config_name, auto_name = ('FDCONFIG.SYS', 'FDAUTO.BAT') if system == 'freedos' else ('CONFIG.SYS', 'AUTOEXEC.BAT')
    check(config_name in old and auto_name in old, 'Original selected DOS boot scripts missing.')
    for name in ('DOSECD.SYS', 'MSCDEX.BIN', 'MSCDPRF.BIN', 'MSCDRAW.BIN', 'MSCDEX.TXT'):
        check(name not in old, 'Own driver/evidence name already belongs to the source fixture.')
    config = old[config_name].rstrip(b'\r\n') + b'\r\nLASTDRIVE=Z\r\nDEVICE=C:\\DOSECD.SYS\r\n'
    command = b'C:\\MSCDEX.EXE' if system == 'freedos' else b'C:\\DOS\\MSCDEX.EXE'
    original_mscdex = safe(mscdex).read_bytes()
    check(sha(original_mscdex) == '9B292B062CD33644A7706670E92BA7E68D39D7655CBCBFF293F7D626E721441A', 'Selected original MSCDEX fixture differs.')
    auto = old[auto_name].rstrip(b'\r\n') + b'\r\nPROMPT $p$g\r\n' + command + b' /D:DOSECD01 /L:R /M:4 /V\r\nECHO CDREADY\r\n'
    replacements = {config_name: config, auto_name: auto, 'DOSECD.SYS': safe(driver).read_bytes()}
    if system == 'freedos':
        check('MSCDEX.EXE' not in old, 'Fresh FreeDOS root already owns MSCDEX.EXE.')
        replacements['MSCDEX.EXE'] = original_mscdex
    else:
        check(old.get('DOS/MSCDEX.EXE') == original_mscdex, 'Installed original DOS MSCDEX differs from selected original.')
    report = root_patch(disk, replacements, destination)
    report.update(system=system, sourceJournal=journal, driverSha256=sha(replacements['DOSECD.SYS']), mscdexSha256=sha(original_mscdex))
    return report


def generate(output, nasm=None):
    """Generate a fresh own client disk and ISO, without reading operating-system files."""
    output = safe(output)
    output.mkdir(parents=True, exist_ok=False)
    nasm = safe(nasm or REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    program, floppy, iso = output / 'MSCDTEST.COM', output / 'mscdex-test.img', output / 'own-mscdex.iso'
    result = subprocess.run([str(nasm), '-Werror', '-f', 'bin', str(REPO / 'tests/guests/mscdex-test.asm'), '-o', str(program)],
                            cwd=REPO, text=True, capture_output=True, check=False)
    (output / 'assemble.log').write_text(result.stdout + result.stderr, encoding='utf-8', newline='\n')
    check(result.returncode == 0, 'Own MSCDEX client assembly failed; see assemble.log.')
    binary = program.read_bytes()
    check(binary.count(b'MSCMETA1') == 1, 'Own filesystem guest metadata absent.')
    fat = module('mscdex_own_fat12', REPO / 'tests/prepare-font-media.py')
    report = fat.prepare(program, floppy, 'MSCDTEST.COM')
    atapi = module('mscdex_own_iso', REPO / 'scripts/create-atapi-test.py')
    optical, text, payload = atapi.own_iso()
    write_new(iso, optical)
    return dict(program=str(program), floppy=str(floppy), programBytes=len(binary), programSha256=sha(binary), floppySha256=report['floppySha256'],
                iso=str(iso), isoSha256=sha(optical), isoBlocks=len(optical)//2048, blockSize=2048,
                payloadSha256=sha(payload), textSha256=sha(text))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--nasm', type=Path)
    args = parser.parse_args()
    print(json.dumps(generate(args.output, args.nasm), indent=2))
