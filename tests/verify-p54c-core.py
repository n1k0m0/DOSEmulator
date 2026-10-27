# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independently check the own P54C reset guest's register/memory/flag proof and CPU-only continuation."""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def digest(data):
    """Return the byte identity without depending on any emulator helper."""
    return hashlib.sha256(data).hexdigest().upper()


def verify(program, ram, result):
    """Interpret only the own fixed metadata and independently compute every eight-byte exchange result."""
    assert result['passed'] and result['cpuOnly'] and not result['fullPcSnapshot'] and not result['dosBoot']
    assert result['model'] == 'IntelP54cB5Subset' and result['programSha256'] == digest(program)
    assert result['cutInstructions'] == 200 and result['comparisons'] > 500 and result['ramComparisons'] > 10
    assert result['output'] == 'DOSE P54C CORE DONE\n'
    marker = program.index(b'P54META1')
    assert program.count(b'P54META1') == 1
    boundary, table, count, identity, rows, row_count, pi, done = struct.unpack_from('<8H', program, marker + 8)
    assert count == 8 and row_count == 32 and 0x1000 < boundary < len(program) + 0x1000
    assert ram[0x1000:boundary] == program[:boundary - 0x1000], 'Guest program or vector table modified'
    assert struct.unpack_from('<8I', ram, identity) == (1, 0x756e6547, 0x49656e69, 0x6c65746e, 0x524, 0, 0, 0x101)
    assert ram[pi:pi + 10] == bytes.fromhex('35C26821A2DA0FC90040'), 'Raw 80-bit PI differs'
    assert ram[done] == 0xa5
    for index in range(row_count):
        initial, replacement = struct.unpack_from('<QQ', program, table - 0x1000 + (index // 4) * 16)
        match = index % 2 == 0
        destination, accumulator, bx, cx, flags, before, after = struct.unpack_from('<QQIIIHH', ram, rows + index * 32)
        assert destination == (replacement if match else initial), ('destination', index)
        assert accumulator == initial, ('accumulator', index)
        assert bx | (cx << 32) == replacement, ('source registers', index)
        assert flags == (0x200c97 | (0x40 if match else 0)), ('flags', index, hex(flags))
        assert (before, after) == (0xa55a, 0x5aa5), ('guard', index)
    return dict(passed=True, rows=row_count, comparisons=result['comparisons'], ramComparisons=result['ramComparisons'])


def main(args):
    """Check both actual futures and reject independent mutations without treating them as CPU executions."""
    root, program_path = Path(args.evidence), Path(args.program)
    assert all('doom2-play' not in str(p).lower() for p in (root, program_path))
    program = program_path.read_bytes()
    a, b = (root / 'ram-original.bin').read_bytes(), (root / 'ram-restored.bin').read_bytes()
    result = json.loads((root / 'result.json').read_text(encoding='utf-8-sig'))
    assert len(a) == len(b) == 2 * 1024 * 1024 and a == b and digest(a) == result['ramSha256']
    assert (root / 'cpu-original.json').read_bytes() == (root / 'cpu-restored.json').read_bytes()
    answer = verify(program, a, result)
    boundary, table, count, identity, rows, row_count, pi, done = struct.unpack_from('<8H', program, program.index(b'P54META1') + 8)
    positions = [0x1000, table, identity, identity + 16, pi, done]
    positions += [rows + i * 32 + offset for i in range(row_count) for offset in (0, 8, 16, 20, 24, 28, 30)]
    for at in positions:
        damaged = bytearray(a)
        damaged[at] ^= 1
        try:
            verify(program, damaged, result)
        except (AssertionError, ValueError):
            continue
        raise AssertionError(('Corrupted proof accepted', at))
    answer['negativeOracles'] = len(positions)
    answer['cpuOnly'] = True
    (root / 'independent-oracle.json').write_text(json.dumps(answer, indent=2), encoding='utf-8')
    print(json.dumps(answer))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--program', required=True)
    parser.add_argument('--evidence', required=True)
    main(parser.parse_args())
