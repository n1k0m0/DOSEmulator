# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent RAM/guest-binary checks for the real-mode system-register probe.
No emulator imports, guest OS extraction, or golden emulator output.
"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path


def verify(report_path, memory_path, program_path, os_name):
    """Validates cpu system evidence from actual guest state, immutable program bytes and independently expected results."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory = Path(memory_path).read_bytes()
    program = Path(program_path).read_bytes()
    output = report["status"]["debugOutput"]
    assert report["status"]["fault"] is None
    assert report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["ataSectorsRead"] > 0 and report["floppySectorsRead"] > 0
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    assert report["hardwareResetCount"] == 0
    assert re.search(r"\r\nCPU-SYSTEM-RUN-END\r\n(?:\r\n)?C:\\>$", output)
    assert "CPU-SYSTEM-PASS\r\n" in output and "CPU-SYSTEM-FAIL" not in output
    assert ("MS-DOS Version 6.22" if os_name == "msdos" else "FreeCom version 0.86") in output
    markers = re.findall(r"CPU-SYSTEM-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address + 136]
    assert len(proof) == 136 and proof[:8] == b"CPSYS001"
    words = struct.unpack("<32I", proof[8:])

    def word(offset):
        """Reads one explicitly little-endian word from the selected guest proof bytes."""
        return words[(offset - 8) // 4]

    assert word(8) == word(12) == 1
    assert [word(n) for n in (16, 20, 24, 28)] == [0x345678, 0x9A345678, 0x345678, 0x1357]
    assert [word(n) for n in (32, 36, 40, 44)] == [1, 1, 2, 1]
    assert word(48) == 0xABCDEF08 and word(52) == 0x34567018
    original_cr0 = word(64)
    assert original_cr0 & 0x80000001 == 0 and original_cr0 & 0x10
    assert word(56) == (original_cr0 & ~0xE) | 0xE
    assert word(60) == (original_cr0 & ~0xE) | 2
    assert word(68) == original_cr0
    assert word(72) == word(76) == 0 and word(80) == word(84) == 0x3FF
    assert word(88) == word(92) and word(96) == word(100)
    assert word(120) == word(128) and word(124) == word(132)
    # Infer the expected fault IPs from the actual own executable, independently
    # of the emulator and the probe's handler label comparisons. IP includes ORG.
    expected_ips = [
        program.index(b"\x0f\x01\xc0") + 0x100,
        program.index(b"\x26\x0f\x01\x06\xfb\xff") + 0x100,
        program.index(b"\x0f\x22\xc0") + 0x100,
        program.index(b"\x0f\x22\xc0\x9b") + 3 + 0x100,
    ]
    assert [word(n) for n in (104, 108, 112, 116)] == expected_ips
    return dict(passed=True, os=os_name, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),
                executableSha256=hashlib.sha256(program).hexdigest().upper(),
                gdtr24=word(16), gdtr32=word(20), interrupt60=word(32),
                invalidOpcode=word(36), generalProtection=word(40), deviceUnavailable=word(44),
                restartIps=expected_ips, systemStateRestored=True, guestReturned=True,
                protectedMode=False, paging=False, x87Arithmetic=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("report", "memory", "program"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--os", required=True, choices=("msdos", "freedos"))
    args = parser.parse_args()
    print(json.dumps(verify(args.report, args.memory, args.program, args.os), indent=2))
