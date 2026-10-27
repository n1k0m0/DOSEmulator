# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Verify genuine VCPI results, paged physical payloads and DOS return privately."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path, memory_path, absent=False, program_source=None):
    """Validates emm386 memory evidence from actual guest state, immutable program bytes and independently expected results."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory = Path(memory_path).read_bytes()
    output, cpu = report["status"]["debugOutput"], report["cpu"]
    assert report["status"]["fault"] is None and report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["hardwareResetCount"] == 0 and report["ataSectorsRead"] > 0
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    if program_source is not None:
        assert (report["floppySectorsRead"] > 0) == (program_source == "floppy")
    assert "MS-DOS Version 6.22" in output and re.search(r"\r\nEMM-GUEST-END\r\n\r\nC:\\>$", output)
    markers = re.findall(r"EMM-MEMORY-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    linear = int(markers[0], 16)
    raw = lambda a: struct.unpack_from("<I", memory, a)[0]

    def physical(address):
        """Walks the reported classic 4-KiB page tables to translate a guest linear proof address."""
        if not cpu["pagingEnabled"]:
            return address
        pde = raw((cpu["cr3"] & ~4095) + (address >> 22) * 4)
        assert pde & 1
        pte = raw((pde & ~4095) + ((address >> 12) & 1023) * 4)
        assert pte & 1
        return (pte & ~4095) | (address & 4095)

    proof = bytes(memory[physical(linear + i)] for i in range(128))
    assert proof[:8] == b"CPEMM001"
    word = lambda offset: struct.unpack_from("<I", proof, offset)[0]
    assert word(8) == 1
    if absent:
        assert "EMM-MEMORY-FAIL\r\n" in output and "EMM-MEMORY-PASS" not in output
        assert word(12) == word(16) == 0 and word(20) & 1 == 0
        assert not cpu["protectedMode"] and not cpu["pagingEnabled"] and not cpu["virtual8086"]
        assert cpu["virtual8086Entries"] == cpu["virtual8086Exits"] == 0
        return dict(passed=True, expectedAbsentMonitor=True, guestReturned=True)
    assert "HIMEM testet den erweiterten Speicher...beendet." in output and "  EMM386 " in output
    assert "EMM-MEMORY-PASS\r\n" in output and "EMM-MEMORY-FAIL" not in output
    assert word(12) == 1 and word(16) == 15 and word(20) & 1 == 1 and word(24) == 0x100
    assert word(28) & 0x80000001 == 0x80000001 and cpu["cr0"] == word(28)
    assert [word(32), word(36)] == [8, 0x70]
    assert 2 < word(40) <= 1792 and word(52) == word(40) - 2 and word(56) == word(40) and word(60) == 0
    pages = word(44), word(48)
    assert pages[0] != pages[1]
    assert all(0x100000 <= page <= len(memory) - 4096 and page & 4095 == 0 for page in pages)
    expected = b"".join(struct.pack("<H", (0x2468 + i * 0x1357) & 65535) for i in range(2048))
    for page in pages:
        assert memory[page:page + 4096] == expected
    for offset in (64, 68):
        assert bytes(memory[physical(word(offset) + i)] for i in range(4096)) == expected
    assert word(72) == 4096
    assert word(76) == word(64) and word(84) == word(68)
    assert word(80) == physical(word(64)) and word(88) == physical(word(68))
    assert cpu["protectedMode"] and cpu["pagingEnabled"] and not cpu["tripleFaulted"]
    assert cpu["virtual8086Entries"] > 1000 and cpu["virtual8086Instructions"] > 100000
    assert cpu["virtual8086Entries"] - cpu["virtual8086Exits"] in (0, 1)
    assert cpu["currentPrivilegeLevel"] == (3 if cpu["virtual8086"] else 0)
    assert cpu["pageWalks"] > 100 and cpu["tlbHits"] > 10000 and cpu["trCache"]["access"] == 0x8B
    ebda = struct.unpack_from("<H", memory, physical(0x40E))[0] << 4
    assert ebda != 0x9FC00 and 0x500 <= ebda < 0x9FC00
    assert struct.unpack_from("<H", memory, physical(0x413))[0] == 640
    return dict(passed=True, vcpiVersion="1.00", allocatedPages=2, physicalPages=pages,
                copiedBytesPerPage=4096, originalFreePages=word(40), remainingFreePages=word(56),
                allocatedPagesFreed=True, payloadSha256=hashlib.sha256(expected).hexdigest().upper(),
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), actualPaging=True,
                virtual8086Entries=cpu["virtual8086Entries"], virtual8086Exits=cpu["virtual8086Exits"],
                relocatedEbda=ebda, conventionalKiB=640, floppySectorsRead=report["floppySectorsRead"],
                programSource=program_source, guestReturned=True, emsPageFrameTested=False, vcpiProtectedClientSwitchTested=False)


def negative_controls(report_path, memory_path, directory):
    """Corrupts selected independent evidence fields and requires each corrupted oracle input to fail."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory = Path(memory_path).read_bytes()
    # This fixture's own conventional program is identity mapped; positive
    # verification above still translates every tested buffer through CR3.
    address = int(re.search("EMM-MEMORY-PROOF=([0-9A-F]{8})", report["status"]["debugOutput"])[1], 16)
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    rp, mp = directory / "corrupted-report.json", directory / "corrupted.private-ram.bin"
    controls = []

    def reject(name, report_input=report_path, memory_input=memory_path):
        """Requires an independently corrupted input to fail instead of accepting the guest's success marker alone."""
        try:
            verify(report_input, memory_input)
        except AssertionError:
            controls.append(dict(name=name, rejected=True))
        else:
            raise AssertionError("Negative control accepted: " + name)

    for name, offset in (("vcpi-free-count", address + 56), ("vcpi-allocated-count", address + 52),
                         ("driver-cr0", address + 28), ("driver-page-translation", address + 80),
                         ("allocated-payload", struct.unpack_from("<I", memory, address + 44)[0]),
                         ("conventional-memory-size", 0x413)):
        changed = bytearray(memory)
        changed[offset] ^= 1
        mp.write_bytes(changed)
        reject(name, memory_input=mp)
    for name, field, value in (("actual-paging-state", "pagingEnabled", False), ("actual-v86-count", "virtual8086Entries", 0)):
        changed = json.loads(json.dumps(report))
        changed["cpu"][field] = value
        rp.write_text(json.dumps(changed), encoding="utf-8")
        reject(name, report_input=rp)
    return dict(passed=True, rejectedControls=len(controls), controls=controls)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("report", "memory"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--expect-absent", action="store_true")
    parser.add_argument("--negative-controls", type=Path)
    parser.add_argument("--program-source", choices=("ide", "floppy"))
    args = parser.parse_args()
    result = verify(args.report, args.memory, args.expect_absent, args.program_source)
    if args.negative_controls:
        result["negativeControls"] = negative_controls(args.report, args.memory, args.negative_controls)
    print(json.dumps(result, indent=2))
