# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Create new own ISO9660 storage data and optional read-only FAT12 Win32-probe media.
No assembly, emulator build, guest, OS-media read, mount or host executable launch.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
BLOCK = 2048
BLOCKS = 160
PAYLOAD_BYTES = 262157
MARKER = b'DOSE95-STORAGE-CD-1\r\nOWN DATA ONLY; ISO9660\r\nPAYLOAD=262157\r\n   '
MARKER_SHA = 'C42F7F1D8CE49FD95842743E8778D392F89864EBE83E4A1ACA8550749B5673C2'
PAYLOAD_SHA = 'C60379B0720AB0EB747D6F85EDF31F6237CFC347501868EB864CF8B245DC95C0'
PAYLOAD_FNV = 0x0C078EBE


def check(condition, reason):
    """Reject inconsistent own inputs and malformed serialized fixtures explicitly."""
    if not condition:
        raise ValueError(reason)


def safe(path):
    """Resolve one named artifact path, excluding originals, personal play COWs and source overwrites."""
    result = Path(path).resolve()
    check(result.is_relative_to(REPO / 'artifacts') and 'doom2-play' not in str(result).casefold(),
          'Only an explicitly named own artifact path is allowed.')
    return result


def module(name, path):
    """Load one known existing pure fixture/parser helper without Python-cache writes."""
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def sha(data):
    """Bind a complete known byte extent rather than a sampled prefix."""
    return hashlib.sha256(data).hexdigest().upper()


def fnv(data):
    """Compute a full unsigned32 FNV-1a for cross-checking actual guest file data."""
    value = 2166136261
    for byte in data:
        value = ((value ^ byte) * 16777619) & 0xffffffff
    return value


def payload():
    """Generate the agreed own262157-byte fixture, crossing both optical blocks and64KiB boundaries."""
    data = bytes((29 * index + 7 * (index >> 8) + 11 * (index >> 16) + 0x53) & 255
                 for index in range(PAYLOAD_BYTES))
    check(sha(data) == PAYLOAD_SHA and fnv(data) == PAYLOAD_FNV, 'Own payload constants disagree.')
    return data


def own_iso():
    """Reuse only the existing own ISO directory/PVD tools and replace all data with the agreed fixture."""
    iso = module('dose_storage_own_iso', REPO / 'scripts/create-atapi-test.py')
    image = bytearray(iso.own_iso()[0][:20 * BLOCK]) + bytearray((BLOCKS - 20) * BLOCK)
    pvd = bytearray(image[16 * BLOCK:17 * BLOCK])
    pvd[40:72] = b'DOSE95_STORAGE_V1'.ljust(32, b' ')
    pvd[80:88] = iso.both32(BLOCKS)
    pvd[190:318] = b'DOSE WINDOWS95 OWN STORAGE VALIDATION'.ljust(128, b' ')
    pvd[446:574] = b'DOSEIO95 OWN DATA'.ljust(128, b' ')
    for start in (813, 830):
        pvd[start:start + 17] = b'2026102800000000\x00'
    image[16 * BLOCK:17 * BLOCK] = pvd
    data = payload()
    entries = [iso.directory(20, BLOCK, b'\x00', 2), iso.directory(20, BLOCK, b'\x01', 2),
               iso.directory(21, len(MARKER), b'DOSEIO.MRK;1'),
               iso.directory(22, len(data), b'PAYLOAD.BIN;1')]
    directory = b''.join(entries)
    image[20 * BLOCK:20 * BLOCK + len(directory)] = directory
    image[21 * BLOCK:21 * BLOCK + len(MARKER)] = MARKER
    image[22 * BLOCK:22 * BLOCK + len(data)] = data
    result = bytes(image)
    check(len(MARKER) == 64 and sha(MARKER) == MARKER_SHA, 'Own marker identity differs.')
    inspect_iso(result)
    return result


def inspect_iso(image):
    """Independently decode duplicated ISO fields and all named extents from the serialized bytes."""
    image = bytes(image)
    check(len(image) == BLOCKS * BLOCK, 'Whole own ISO must have exactly160 optical blocks.')
    pvd = image[16 * BLOCK:17 * BLOCK]
    check(pvd[:7] == b'\x01CD001\x01' and image[17 * BLOCK:17 * BLOCK + 7] == b'\xffCD001\x01',
          'Missing primary descriptor or terminator; Joliet is not part of this fixture.')
    check(struct.unpack_from('<I', pvd, 80)[0] == struct.unpack_from('>I', pvd, 84)[0] == BLOCKS,
          'ISO volume-space endian fields differ.')
    check(struct.unpack_from('<H', pvd, 128)[0] == struct.unpack_from('>H', pvd, 130)[0] == BLOCK,
          'ISO logical-block fields differ.')
    check(pvd[40:72].rstrip(b' ') == b'DOSE95_STORAGE_V1' and pvd[156] == 34, 'Own volume/root identity differs.')
    root = pvd[156:190]
    start = struct.unpack_from('<I', root, 2)[0]
    check(start == struct.unpack_from('>I', root, 6)[0] == 20 and root[25] == 2, 'Invalid own root extent.')
    check(struct.unpack_from('<I', root, 10)[0] == struct.unpack_from('>I', root, 14)[0] == BLOCK,
          'Invalid root length.')
    found = {}
    at, end = start * BLOCK, (start + 1) * BLOCK
    while at < end and image[at] != 0:
        count = image[at]
        record = image[at:at + count]
        check(34 <= count <= 255 and at + count <= end and len(record) == count, 'Invalid root record bound.')
        lba, size = struct.unpack_from('<I', record, 2)[0], struct.unpack_from('<I', record, 10)[0]
        check(lba == struct.unpack_from('>I', record, 6)[0] and size == struct.unpack_from('>I', record, 14)[0],
              'Directory endian fields differ.')
        check(33 + record[32] <= count and lba * BLOCK + size <= len(image), 'Directory extent escapes own image.')
        name = record[33:33 + record[32]]
        if name not in (b'\0', b'\1'):
            check(record[25] == 0 and name not in found, 'Duplicate or unsupported own data entry.')
            found[name] = image[lba * BLOCK:lba * BLOCK + size]
        at += count
    check(set(found) == {b'DOSEIO.MRK;1', b'PAYLOAD.BIN;1'}, 'The ISO contains files outside the two own fixtures.')
    check(found[b'DOSEIO.MRK;1'] == MARKER and sha(found[b'PAYLOAD.BIN;1']) == PAYLOAD_SHA and
          len(found[b'PAYLOAD.BIN;1']) == PAYLOAD_BYTES, 'Complete serialized own optical data differs.')
    return {'rootLba': start, 'files': {'DOSEIO.MRK': len(MARKER), 'PAYLOAD.BIN': PAYLOAD_BYTES}}


def validate_program(path, expected):
    """Read only an explicitly named already assembled own PE32 image, with required full identity."""
    path = safe(path)
    check(expected is not None and len(expected) == 64 and all(c in '0123456789abcdefABCDEF' for c in expected),
          '--program requires its exact --program-sha256 seal.')
    check(path.is_file() and 512 <= path.stat().st_size <= 1048576, 'Own PE input length is outside the FAT12 bound.')
    binary = path.read_bytes()
    check(sha(binary) == expected.upper() and binary[:2] == b'MZ', 'Own PE image hash/header differs.')
    pe = struct.unpack_from('<I', binary, 0x3c)[0]
    check(0x40 <= pe <= len(binary) - 128 and binary[pe:pe + 4] == b'PE\0\0' and
          struct.unpack_from('<H', binary, pe + 4)[0] == 0x14c and
          struct.unpack_from('<H', binary, pe + 24)[0] == 0x10b, 'Need the actual own i386 PE32 image.')
    check(binary.count(b'DOSEIOM1') == 1 and b'DOSEIO95' in binary, 'Own storage-probe metadata is absent.')
    return binary


def fat12_image(binary):
    """Build one nonboot own FAT12 file using the already reviewed layout and an independent FAT parser."""
    count = (len(binary) + 511) // 512
    check(1 <= count <= 2847, 'Own executable exceeds the1.44MiB FAT12 data area.')
    image = bytearray(1474560)
    image[:11] = b'\xeb\x3c\x90DOSESTOR'
    struct.pack_into('<HBHBHHBHHHII', image, 11, 512, 1, 1, 2, 224, 2880, 0xf0, 9, 18, 2, 0, 0)
    image[38] = 0x29
    struct.pack_into('<I', image, 39, 0x95102812)
    image[43:54] = b'DOSEIO95   '
    image[54:62] = b'FAT12   '
    image[62:66] = b'\xfa\xf4\xeb\xfd'
    image[510:512] = b'\x55\xaa'
    fat = bytearray(4608)
    fat[:3] = b'\xf0\xff\xff'
    for cluster in range(2, 2 + count):
        following = cluster + 1 if cluster < count + 1 else 0xfff
        offset = cluster + cluster // 2
        word = struct.unpack_from('<H', fat, offset)[0]
        word = (word & 0x000f) | (following << 4) if cluster & 1 else (word & 0xf000) | following
        struct.pack_into('<H', fat, offset, word)
    image[512:5120] = fat
    image[5120:9728] = fat
    root = 19 * 512
    image[root:root + 11] = b'DOSEIO95   '
    image[root + 11] = 8
    entry = root + 32
    image[entry:entry + 11] = b'DOSEIO95EXE'
    image[entry + 11] = 0x21
    struct.pack_into('<HH', image, entry + 22, 0, ((2026 - 1980) << 9) | (10 << 5) | 28)
    struct.pack_into('<HI', image, entry + 26, 2, len(binary))
    image[33 * 512:33 * 512 + len(binary)] = binary
    oracle = module('dose_storage_fat_oracle', REPO / 'tests/verify-msdos-install.py')
    files, allocation = oracle.Fat(image).files()
    check(files == {'DOSEIO95.EXE': binary}, 'Independent FAT parser found a changed or extra executable.')
    check(image[entry + 11] & 1, 'The own executable short entry is not read-only.')
    return bytes(image), allocation


def write_new(path, data):
    """Publish one new own artifact without overwriting a prior fixture."""
    with safe(path).open('xb') as output:
        output.write(data)


def generate(output, program=None, program_sha256=None):
    """Create deterministic own data and optionally wrap a preassembled EXE; never run a compiler or guest."""
    output = safe(output)
    check(not output.exists() and output.parent.is_dir(), 'Output must be a new directory with an existing parent.')
    binary = validate_program(program, program_sha256) if program is not None else None
    check(program is not None or program_sha256 is None, '--program-sha256 without --program is ambiguous.')
    optical = own_iso()
    floppy, allocation = fat12_image(binary) if binary is not None else (None, None)
    output.mkdir()
    iso = output / 'own-windows95-storage.iso'
    write_new(iso, optical)
    iso.chmod(0o444)
    result = {'kind': 'DOSE-OWN-WINDOWS95-STORAGE-MEDIA-1', 'onlyOwnData': True,
              'guestExecutionPerformed': False, 'productBuildPerformed': False, 'assemblyPerformed': False,
              'iso': {'path': str(iso), 'bytes': len(optical), 'blocks': BLOCKS, 'blockSize': BLOCK,
                      'sha256': sha(optical), 'volumeLabel': 'DOSE95_STORAGE_V1', 'joliet': False},
              'marker': {'name': 'DOSEIO.MRK', 'bytes': 64, 'sha256': MARKER_SHA, 'hex': MARKER.hex().upper()},
              'payload': {'name': 'PAYLOAD.BIN', 'bytes': PAYLOAD_BYTES, 'sha256': PAYLOAD_SHA,
                          'fnv1a': f'{PAYLOAD_FNV:08X}', 'formula': '(29*i+7*(i>>8)+11*(i>>16)+0x53)&255'},
              'hddOwnDirectory': 'C:\\DOSEIO', 'hddLongName': 'Long File Name 95.bin',
              'proofPath': 'C:\\DOSEIO.BIN', 'proofBytes': 4096, 'floppy': None,
              'readOnlyMountRequired': True, 'originalOperatingSystemFilesIncluded': False,
              'helperSources': {str(p.relative_to(REPO)): sha(p.read_bytes()) for p in
                                (REPO / 'scripts/create-atapi-test.py', REPO / 'tests/verify-msdos-install.py')}}
    if binary is not None:
        target = output / 'windows95-storage-probe.img'
        write_new(target, floppy)
        target.chmod(0o444)
        result['floppy'] = {'path': str(target), 'bytes': len(floppy), 'sha256': sha(floppy),
                           'file': 'DOSEIO95.EXE', 'fileAttribute': '21h=read-only+archive',
                           'programInput': str(safe(program)), 'programBytes': len(binary),
                           'programSha256': sha(binary), 'allocation': allocation}
    write_new(output / 'fixture.json', (json.dumps(result, indent=2) + '\n').encode('utf-8'))
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--program', type=Path, help='Already assembled own DOSEIO95.EXE; never executed here.')
    parser.add_argument('--program-sha256', help='Required exact full PE32 identity when --program is supplied.')
    args = parser.parse_args()
    print(json.dumps(generate(args.output, args.program, args.program_sha256), indent=2))
