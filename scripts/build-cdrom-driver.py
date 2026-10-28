# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Assemble the own fixed-secondary ATAPI SYS and validate its public resident ABI."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import uuid

REPO = Path(__file__).resolve().parents[1]
SYMBOLS = (
    'strategy', 'interrupt', 'resident_end', 'request_pending', 'request_active',
    'driver_busy', 'command_counter', 'packet_counter', 'sectors_read',
    'last_request_lba', 'current_lba', 'destination_pointer', 'sectors_remaining',
    'packet_buffer', 'scratch_buffer', 'transfer_received', 'last_status',
    'last_reason', 'last_sense', 'current_command', 'read_requested',
    'read_completed',
)


def safe(path):
    """Resolve exactly one named compile path and exclude personal overlay directories."""
    result = Path(path).resolve()
    if 'doom2-play' in str(result).casefold():
        raise ValueError('Excluded personal overlay path.')
    return result


def digest(data):
    """Return an uppercase content identity without reading any unrelated file."""
    return hashlib.sha256(data).hexdigest().upper()


def inspect(data):
    """Validate the Microsoft header and decode the own read-only symbol manifest."""
    if not 24 + 12 + 2 * len(SYMBOLS) < len(data) < 65536:
        raise ValueError('Unexpected resident SYS length.')
    if data[:6] != b'\xff\xff\xff\xff\x00\xc8':
        raise ValueError('Invalid CD-ROM character-device header.')
    strategy, interrupt = struct.unpack_from('<HH', data, 6)
    if data[10:18] != b'DOSECD01' or data[18:24] != b'\0\0\0\1\0\0':
        raise ValueError('Wrong fixed name, reserved bytes, drive or unit fields.')
    if data[24:32] != b'DOSECDM1':
        raise ValueError('Missing own resident metadata signature.')
    version, count = struct.unpack_from('<HH', data, 32)
    if version != 1 or count != len(SYMBOLS):
        raise ValueError('Unknown resident metadata schema.')
    values = struct.unpack_from('<' + 'H' * count, data, 36)
    symbols = dict(zip(SYMBOLS, values))
    if symbols['strategy'] != strategy or symbols['interrupt'] != interrupt:
        raise ValueError('Header entries and metadata disagree.')
    if symbols['resident_end'] != len(data) or len(data) % 16:
        raise ValueError('Unaligned or incomplete resident extent.')
    if any(not 36 + 2 * count <= value < len(data) for key, value in symbols.items()
           if key != 'resident_end'):
        raise ValueError('A resident symbol points outside its owned bytes.')
    if symbols['scratch_buffer'] + 2048 > strategy:
        raise ValueError('Scratch buffer overlaps executable instructions.')
    return {'version': version, 'signatureOffset': 24, 'headerBytes': 22,
            'headerWindowBytes': 24, 'name': 'DOSECD01', 'attributes': 0xC800,
            'units': 1, 'residentBytes': len(data), 'symbols': symbols,
            'taskFileBase': 0x170, 'controlPort': 0x376, 'cookedBlockSize': 2048,
            'packetBytes': 12, 'pollLimit': 200000,
            'addressing': 'HSG-only', 'transport': 'fixed-secondary-master-PIO0',
            'bufferPolicy': 'contiguous-conventional-span-up-to-1MiB'}


def main():
    """Compile into private staging files, validate, then replace only explicit outputs."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--listing', type=Path)
    parser.add_argument('--nasm', type=Path)
    args = parser.parse_args()
    output = safe(args.output)
    listing = safe(args.listing or output.with_suffix('.lst'))
    source = REPO / 'firmware' / 'dose-cdrom.asm'
    assembler = safe(args.nasm or REPO / 'artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe')
    if output == listing or output == source or listing == source:
        raise ValueError('Compile input and output paths must be distinct.')
    if not assembler.is_file():
        raise FileNotFoundError('The configured NASM 3.02 assembler is missing.')
    output.parent.mkdir(parents=True, exist_ok=True)
    listing.parent.mkdir(parents=True, exist_ok=True)
    staging_id = uuid.uuid4().hex
    temp_binary = output.with_name(output.name + '.' + staging_id + '.tmp')
    temp_listing = listing.with_name(listing.name + '.' + staging_id + '.tmp')
    try:
        result = subprocess.run([str(assembler), '-Werror', '-f', 'bin',
                                 '-o', str(temp_binary), '-l', str(temp_listing),
                                 str(source)], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        data = temp_binary.read_bytes()
        metadata = inspect(data)
        temp_binary.replace(output)
        temp_listing.replace(listing)
    finally:
        temp_binary.unlink(missing_ok=True)
        temp_listing.unlink(missing_ok=True)
    print(json.dumps({'driver': str(output), 'listing': str(listing),
                      'bytes': len(data), 'binaryHash': digest(data),
                      'sha256': digest(data), 'sourceHash': digest(source.read_bytes()),
                      'metadata': metadata}, indent=2))


if __name__ == '__main__':
    main()
