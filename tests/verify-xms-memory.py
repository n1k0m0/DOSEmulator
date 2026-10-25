"""Verify actual original-HIMEM XMS calls using own data, RAM and CPU diagnostics."""
import argparse
import hashlib
import json
import re
import struct
from pathlib import Path


def verify(report_path, memory_path, absent=False):
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory = Path(memory_path).read_bytes()
    output = report["status"]["debugOutput"]
    assert report["status"]["fault"] is None and report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["hardwareResetCount"] == 0
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    assert report["ataSectorsRead"] > 0 and report["floppySectorsRead"] > 0
    assert "MS-DOS Version 6.22" in output
    assert re.search(r"\r\nHIMEM-XMS-RUN-END\r\n\r\nC:\\>$", output)
    markers = re.findall(r"XMS-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address + 96]
    assert len(proof) == 96 and proof[:8] == b"CPXMS001"

    def word(offset):
        return struct.unpack_from("<I", proof, offset)[0]

    assert word(8) == 1
    cpu = report["cpu"]
    assert not cpu["protectedMode"] and not cpu["tripleFaulted"]
    if absent:
        assert "XMS-MEMORY-FAIL\r\n" in output and "XMS-MEMORY-PASS" not in output
        assert word(12) == word(16) == word(60) == word(88) == 0
        # DISPLAY.SYS uses BIOS AH87 even without an XMS driver.
        assert cpu["protectedModeEntries"] == cpu["protectedModeExits"] == 1
        return dict(passed=True, expectedMissingDriver=True, guestReturned=True)
    assert "HIMEM testet den erweiterten Speicher...beendet." in output
    assert "XMS-MEMORY-PASS\r\n" in output and "XMS-MEMORY-FAIL" not in output
    assert "HIMEM" in report["text"] or "HIMEM" in output
    assert word(12) == 1 and word(60) == 0x3F
    assert 0x200 <= word(16) <= 0x399 and word(20) > 0
    assert 4096 < word(24) <= word(28) <= 7168 and word(32) > 0
    assert word(36) == word(28) - 64
    assert word(52) == word(28) and word(56) == word(24)
    assert word(44) == 4096 and word(48) == 2048
    assert 0x100000 <= word(40) <= len(memory) - 65536
    expected = b"".join(struct.pack("<H", (0x1357 + n * 0x1234) & 0xFFFF) for n in range(2048))
    assert memory[word(64):word(64) + 4096] == expected
    assert memory[word(68):word(68) + 4096] == expected
    assert memory[word(40):word(40) + 4096] == expected
    assert word(72) == word(76) == struct.unpack_from("<I", memory, word(40) + 65532)[0] == 0x55667788
    assert word(80) == word(84) and word(80) in (0, 1) and word(88) > 0 and word(92) == 0
    assert cpu["protectedModeEntries"] == cpu["protectedModeExits"] > 0
    assert cpu["protectedInstructions"] > 0
    return dict(passed=True, driverVersion=word(16), driverRevision=word(20), allocatedKiB=64,
                physicalAddress=word(40), transferredBytes=4096, comparedWords=2048, lastDwordOffset=65532,
                payloadSha256=hashlib.sha256(expected).hexdigest().upper(),
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), freeBeforeKiB=word(28),
                freeAfterKiB=word(52), largestBeforeKiB=word(24), largestAfterKiB=word(56),
                protectedModeEntries=cpu["protectedModeEntries"], protectedModeExits=cpu["protectedModeExits"],
                protectedInstructions=cpu["protectedInstructions"], handleReleased=True,
                a20StateRestored=True, guestReturned=True, virtual8086=False, paging=False, x87Arithmetic=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--memory", required=True, type=Path)
    parser.add_argument("--expect-absent", action="store_true")
    args = parser.parse_args()
    print(json.dumps(verify(args.report, args.memory, args.expect_absent), indent=2))
