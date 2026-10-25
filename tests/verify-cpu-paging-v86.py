"""Independent assembled-instruction, page-table and private RAM oracle."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path, memory_path, program_path, os_name):
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory, program = Path(memory_path).read_bytes(), Path(program_path).read_bytes()
    output, cpu = report["status"]["debugOutput"], report["cpu"]
    assert report["status"]["fault"] is None and report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["hardwareResetCount"] == 0
    assert report["ataSectorsRead"] > 0 and report["floppySectorsRead"] > 0
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    assert re.search(r"\r\nPAGING-GUEST-END\r\n(?:\r\n)?C:\\>$", output)
    assert "CPU-PAGING-PASS\r\n" in output and "CPU-PAGING-FAIL" not in output
    assert ("MS-DOS Version 6.22" if os_name == "msdos" else "FreeCom version 0.86") in output
    markers = re.findall(r"CPU-PAGING-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address + 256]
    assert len(proof) == 256 and proof[:8] == b"CPPG0001"
    word = lambda offset: struct.unpack_from("<I", proof, offset)[0]
    raw = lambda offset: struct.unpack_from("<I", memory, offset)[0]
    labels = struct.unpack_from("<14I", program, program.index(b"CPPGADDR") + 8)
    wp, after_wp, missing, after_missing, cli, port, read, interrupt, exit_vm, label_proof, space, stack, vm_stack, tss = labels
    # Verify the actual fault-trigger bytes, including addressing and width.
    assert program[wp - 0x100:after_wp - 0x100] == bytes.fromhex("64c70500003000d0bad0ba")
    assert program[missing - 0x100:after_missing - 0x100] == bytes.fromhex("64a100003000")
    assert program[cli - 0x100] == 0xFA and program[port - 0x100:port - 0x100 + 2] == b"\xe4\x61"
    assert program[read - 0x100:read - 0x100 + 3] == b"\xa1\x00\x00"
    assert program[interrupt - 0x100:interrupt - 0x100 + 2] == b"\xcd\x60"
    assert program[exit_vm - 0x100:exit_vm - 0x100 + 2] == b"\xcd\x61"
    basis = word(200)
    assert basis & 15 == 0 and basis + label_proof == address
    assert word(8) == word(12) == 1 and word(16) == 7
    assert word(20) == word(24) and word(20) & 0x80000001 == 0
    assert word(28) == word(32) == 0
    directory = (basis + space + 4095) & ~4095
    table, payload_a, payload_b = directory + 4096, directory + 8192, directory + 12288
    assert [word(i) for i in (36, 40, 44, 48)] == [directory, table, payload_a, payload_b]
    assert [word(i) for i in (52, 56, 60, 64, 204)] == [0x11223344, 0x11223344, 0x55667788, 0xABCDEF78, 0xABCDEF78]
    assert raw(payload_a) == 0x11223344 and raw(payload_b) == 0xABCDEF78
    assert word(68) == payload_b | 0x67 and word(72) == table | 0x27
    assert raw(directory) == table | 0x27 and not any(memory[directory + 4:directory + 4096])
    assert raw(table + (0x300000 >> 12) * 4) == payload_b | 0x27
    assert raw(table + (0x60000 >> 12) * 4) == payload_b | 0x27
    assert word(76) == 3
    assert [word(i) for i in (80, 84, 88, 92, 96, 100, 104, 108, 112)] == [0x300000, 3, wp, 0x300000, 0, missing, 0x60000, 5, read]
    assert word(116) == 0xEF78 and word(120) == 4
    assert [word(i) for i in (124, 128, 132, 136)] == [cli, port, interrupt, exit_vm]
    assert word(140) == 0x30002 and word(144) == vm_stack
    selector = basis >> 4
    assert [word(i) for i in (148, 152, 156, 160, 164, 168)] == [selector, selector, 0x6000, selector, 0, selector]
    assert word(172) == 32 and word(176) == stack - 40 and word(180) == 0 and word(192) == 0x5A and word(212) == 0
    assert memory[basis + tss + 0x68:basis + tss + 0x68 + 8193] == b"\xff" * 8193
    assert cpu["pageFaults"] == 3 and cpu["cr2"] == 0x60000
    assert cpu["virtual8086Entries"] == cpu["virtual8086Exits"] == cpu["privilegeStackSwitches"] == 5
    assert cpu["virtual8086Instructions"] == 10 and cpu["outerPrivilegeReturns"] == 0
    assert cpu["pageWalks"] >= 11 and cpu["tlbHits"] > 1000 and cpu["tlbInvalidations"] == 10
    # German DISPLAY.SYS performs one BIOS AH87 block copy during this boot.
    assert cpu["protectedModeEntries"] == cpu["protectedModeExits"] == (2 if os_name == "msdos" else 1)
    assert not cpu["protectedMode"] and not cpu["pagingEnabled"] and not cpu["virtual8086"] and not cpu["tripleFaulted"]
    assert cpu["currentPrivilegeLevel"] == 0 and cpu["cr0"] == word(24) and cpu["cr3"] == word(32)
    assert cpu["tr"] == 48 and cpu["trCache"]["base"] == basis + tss and cpu["trCache"]["limit"] == 8296
    assert cpu["gdtr"] == dict(limit=65535, base=0) and cpu["idtr"] == dict(limit=1023, base=0)
    return dict(passed=True, os=os_name, proofAddress=address, proofSha256=hashlib.sha256(proof).hexdigest().upper(),
                executableSha256=hashlib.sha256(program).hexdigest().upper(), pageFaults=3, virtual8086Exits=5,
                virtual8086Instructions=10, ioBitmapTrap=True, userProtectionTrap=True, wpTrap=True,
                missingPageRetried=True, staleTlbObserved=True, invlpgObserved=True, accessedDirtyObserved=True,
                originalTablesAndCr0Restored=True, guestReturned=True, hardwareTaskSwitch=False)


def negative_controls(report_path, memory_path, program_path, os_name, directory):
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory, program = Path(memory_path).read_bytes(), Path(program_path).read_bytes()
    address = int(re.search("CPU-PAGING-PROOF=([0-9A-F]{8})", report["status"]["debugOutput"])[1], 16)
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    ram_path, json_path, com_path = directory / "corrupted.private-ram.bin", directory / "corrupted-report.json", directory / "corrupted-own-program.com"
    controls = []

    def reject(name, rp=report_path, mp=memory_path, pp=program_path):
        try:
            verify(rp, mp, pp, os_name)
        except AssertionError:
            controls.append(dict(name=name, rejected=True))
        else:
            raise AssertionError("Negative control accepted: " + name)

    for name, offset in (("stale-tlb-value", address + 56), ("fault-error-code", address + 84),
                         ("v86-frame", address + 156), ("v86-kernel-stack", address + 176),
                         ("page-table", struct.unpack_from("<I", memory, address + 40)[0] + 0xC00),
                         ("translated-payload", struct.unpack_from("<I", memory, address + 48)[0])):
        changed = bytearray(memory)
        changed[offset] ^= 1
        ram_path.write_bytes(changed)
        reject(name, mp=ram_path)
    changed = json.loads(json.dumps(report))
    changed["cpu"]["virtual8086Exits"] = 0
    json_path.write_text(json.dumps(changed), encoding="utf-8")
    reject("actual-v86-count", rp=json_path)
    changed = bytearray(program)
    changed[program.index(b"CPPGADDR") + 8] ^= 1
    com_path.write_bytes(changed)
    reject("assembled-fault-address", pp=com_path)
    return dict(passed=True, rejectedControls=len(controls), controls=controls)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("report", "memory", "program"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--os", required=True, choices=("msdos", "freedos"))
    parser.add_argument("--negative-controls", type=Path)
    args = parser.parse_args()
    result = verify(args.report, args.memory, args.program, args.os)
    if args.negative_controls:
        result["negativeControls"] = negative_controls(args.report, args.memory, args.program, args.os, args.negative_controls)
    print(json.dumps(result, indent=2))
