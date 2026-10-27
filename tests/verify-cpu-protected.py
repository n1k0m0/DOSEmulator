# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent binary/RAM checks for the own PE16/PE32 DOS guest.
Private memory dumps may contain guest OS bytes and must never be published.
"""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path


def verify(report_path, memory_path, program_path, os_name):
    """Validates cpu protected evidence from actual guest state, immutable program bytes and independently expected results."""
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
    assert re.search(r"\r\nCPU-PE-RUN-END\r\n(?:\r\n)?C:\\>$", output)
    assert "CPU-PE-PASS\r\n" in output and "CPU-PE-FAIL" not in output
    assert ("MS-DOS Version 6.22" if os_name == "msdos" else "FreeCom version 0.86") in output
    markers = re.findall(r"CPU-PE-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address + 224]
    assert len(proof) == 224 and proof[:8] == b"CPPE0001"
    words = struct.unpack("<54I", proof[8:])

    def word(offset):
        """Reads one explicitly little-endian word from the selected guest proof bytes."""
        return words[(offset - 8) // 4]

    assert word(8) == word(12) == 1 and word(16) == 0x3F
    assert word(20) == word(24) == word(140) | 1
    assert word(144) == word(140) and word(144) & 0x80000001 == 0
    assert [word(n) for n in (28, 32, 36, 40, 44)] == [0x11111111, 0x11111111, 0x22222222, 0x23456789, 0xDECAFBAD]
    assert word(48) == word(52) == struct.unpack_from("<I", memory, 0x200000)[0]
    assert word(56) == word(60) == word(64) == word(68) and word(56) > 0xFFFF
    assert [word(n) for n in (72, 76, 80, 84)] == [1, 1, 0x200, 0]
    assert [word(n) for n in (88, 92, 96, 108, 112, 120, 124, 132)] == [2, 0, 0xFFF8, 1, 64, 1, 72, 1]
    selector_setup = program.index(b"\x66\xb8\xf8\xff")
    expected_ips = [
        program.index(b"\x65\xa1\0\0\0\0") + 0x10100,
        program.index(b"\x8e\xe8", selector_setup + 4) + 0x10100,
        program.index(b"\x66\xb8\x40\0\x8e\xd8") + 4 + 0x10100,
        program.index(b"\x66\xb8\x48\0\x8e\xd0") + 4 + 0x10100,
        program.index(b"\x0f\x01\xc0") + 0x10100,
    ]
    assert [word(n) for n in (100, 104, 116, 128, 136)] == expected_ips
    assert word(148) == word(152) and word(156) == word(160)
    assert word(164) == word(168) and word(172) == word(176)
    assert word(180) == 24 and word(184) == 40 and word(188) == 0x93
    assert word(192) == word(196) and word(200) == word(204)
    gdt_address = word(208)
    low, high = struct.unpack_from("<II", memory, gdt_address)
    cache_base = (low >> 16) | ((high & 255) << 16) | (high & 0xFF000000)
    assert low & 0xFFFF == 3 and (high >> 8) & 255 == 0x93
    assert struct.unpack_from("<I", memory, cache_base)[0] == 0x22222222
    assert 0x10100 < word(212) < 0x10100 + len(program)
    assert program[word(212) - 0x10100 - 5] == 0xE8
    assert word(216) == 0
    cpu = report["cpu"]
    assert not cpu["protectedMode"] and not cpu["tripleFaulted"] and cpu["currentPrivilegeLevel"] == 0
    assert cpu["eflags"] & 0x10000 == 0
    # The original German DISPLAY.SYS now uses the implemented BIOS AH87 once.
    assert cpu["protectedModeEntries"] == cpu["protectedModeExits"] == (2 if os_name == "msdos" else 1)
    assert cpu["protectedInstructions"] > 100 and cpu["cr0"] == word(144)
    assert cpu["gdtr"]["base"] == word(148) and cpu["gdtr"]["limit"] == word(164)
    assert cpu["idtr"]["base"] == word(156) and cpu["idtr"]["limit"] == word(172)
    return dict(passed=True, os=os_name, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),
                executableSha256=hashlib.sha256(program).hexdigest().upper(),
                phaseMask=word(16), protectedMode16=True, protectedMode32=True,
                cachedDescriptorSurvivedTableEdit=True, protectedEip=word(212), protectedEsp=word(56),
                restartIps=expected_ips, generalProtection=2, segmentNotPresent=1,
                stackFault=1, invalidOpcode=1, softwareTrap=1, softwareInterrupt=1,
                systemStateRestored=True, extendedRamRestored=True, picMasksRestored=True,
                guestReturned=True, paging=False, virtual8086=False, x87Arithmetic=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("report", "memory", "program"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--os", required=True, choices=("msdos", "freedos"))
    args = parser.parse_args()
    print(json.dumps(verify(args.report, args.memory, args.program, args.os), indent=2))
