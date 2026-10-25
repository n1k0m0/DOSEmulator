"""Independent program-byte/RAM oracle for our private ring/TSS DOS probe.
Memory dumps can contain guest OS data and must never be published.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path, memory_path, program_path, os_name):
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
    assert re.search(r"\r\nCPU-PRIV-RUN-END\r\n(?:\r\n)?C:\\>$", output)
    assert "CPU-PRIV-PASS\r\n" in output and "CPU-PRIV-FAIL" not in output
    assert ("MS-DOS Version 6.22" if os_name == "msdos" else "FreeCom version 0.86") in output
    markers = re.findall(r"CPU-PRIV-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    proof = memory[address:address + 320]
    assert len(proof) == 320 and proof[:8] == b"CPPR0001"

    def word(offset):
        return struct.unpack_from("<I", proof, offset)[0]

    symbols = program.index(b"CPPRADDR") + 8
    entry, after_int, cli, forbidden, stack_a, stack_b, user_stack, tss, gdt_tss, ldt_b = struct.unpack_from("<10I", program, symbols)
    assert program[after_int - 0x100 - 2:after_int - 0x100] == b"\xcd\x60"
    assert program[cli - 0x100] == 0xFA
    assert program[forbidden - 0x100:forbidden - 0x100 + 2] == b"\xcd\x61"
    assert 0x100 < entry < after_int < cli < forbidden < len(program) + 0x100
    assert user_stack == len(program) + 0x100
    assert word(8) == word(12) == 1 and word(16) == 255 and word(248) == 0
    assert word(20) == word(24) and word(24) & 0x80000001 == 0
    assert all(word(a) == word(b) for a, b in ((28, 32), (36, 40), (44, 48), (52, 56), (60, 64)))
    assert [word(n) for n in (68, 72, 76, 80, 84, 88)] == [72, 80, 0x8B, 0x11223344, 0x11223344, 0x55667788]
    assert [word(n) for n in (92, 96, 100, 104, 108, 112)] == [51, 67, 0, 59, 0, 91]
    user_pointer = 0x10000 + user_stack
    assert word(116) == word(120) == word(168) == word(272) == user_pointer > 0xFFFF
    assert word(124) == word(128) == word(164) == 0x202
    assert word(132) == 0xABCD0048 and word(136) == 0xDCBA0050
    assert [word(n) for n in (140, 144, 148)] == [1, 0, 40]
    assert word(152) == 0x10000 + stack_a - 20
    assert word(156) == 0x10000 + after_int and word(160) == 51 and word(172) == 67
    assert word(176) == 2 and word(180) == 0 and word(192) == 0x30A
    assert word(184) == 0x10000 + cli and word(196) == 0x10000 + forbidden
    assert word(188) & 0x10200 == word(200) & 0x10200 == 0x10200
    assert word(204) == word(208) == 0x10000 + stack_b - 24
    assert word(212) == 0x10000 + stack_b - 20
    assert word(216) == word(284) == 0x10000 + stack_b
    assert word(220) == 0xC0DEA001 and word(224) == 0xC0DEB002
    assert word(244) == 0x203 and word(268) == 0x10000 + entry
    assert word(252) == word(256) == word(260) == 0 and word(264) == 80
    # Derive the guest allocation base independently from the assembled TSS label.
    basis = word(228) - tss
    assert basis & 15 == 0 and word(276) == basis + gdt_tss
    assert word(240) == basis + ldt_b and word(280) == 9
    low, high = struct.unpack_from("<II", memory, word(276))
    descriptor_base = (low >> 16) | ((high & 255) << 16) | (high & 0xFF000000)
    assert low & 0xFFFF == 0 and (high >> 8) & 255 == 9 and descriptor_base == word(232) != word(228)
    assert struct.unpack_from("<I", memory, basis + tss + 4)[0] == word(284)
    assert struct.unpack_from("<H", memory, basis + tss + 8)[0] == 40
    assert memory[basis + ldt_b + 5] == 0x93
    cpu = report["cpu"]
    assert not cpu["protectedMode"] and not cpu["tripleFaulted"] and cpu["currentPrivilegeLevel"] == 0
    assert cpu["eflags"] & 0x10000 == 0
    # Includes the original German DISPLAY.SYS BIOS AH87 call during boot.
    assert cpu["protectedModeEntries"] == cpu["protectedModeExits"] == (2 if os_name == "msdos" else 1)
    assert cpu["protectedInstructions"] > 150 and cpu["cr0"] == word(24)
    assert cpu["privilegeStackSwitches"] == cpu["outerPrivilegeReturns"] == 4
    assert cpu["ldtr"] == 0 and not cpu["ldtrCache"]["usable"]
    assert cpu["tr"] == 80 and cpu["trCache"]["usable"]
    assert cpu["trCache"]["base"] == word(228) and cpu["trCache"]["limit"] == 0x67 and cpu["trCache"]["access"] == 0x8B
    assert cpu["gdtr"]["base"] == word(28) and cpu["gdtr"]["limit"] == word(44)
    assert cpu["idtr"]["base"] == word(36) and cpu["idtr"]["limit"] == word(52)
    return dict(passed=True, os=os_name, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(),
                executableSha256=hashlib.sha256(program).hexdigest().upper(),
                privilegeStackSwitches=4, outerPrivilegeReturns=4,
                actualUserEip=word(268), actualUserEsp=user_pointer,
                generalProtection=2, faultIps=[word(184), word(196)],
                cachedLdtSurvivedTableEdit=True, cachedTrSurvivedTableEdit=True,
                tssMemoryStackUpdateObserved=True, dataCacheInvalidation=True,
                picAndTablesRestored=True, ldtrUnloaded=True,
                privateTrCacheRemainsUntilReset=True, guestReturned=True,
                hardwareTaskSwitch=False, callGates=False, paging=False, virtual8086=False)


def negative_controls(report_path, memory_path, program_path, os_name, directory):
    """Corrupt independently checked inputs; each must fail the positive oracle."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory = Path(memory_path).read_bytes()
    program = Path(program_path).read_bytes()
    address = int(re.search(r"CPU-PRIV-PROOF=([0-9A-F]{8})", report["status"]["debugOutput"])[1], 16)
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    ram_path = directory / "corrupted.private-ram.bin"
    json_path = directory / "corrupted-report.json"
    com_path = directory / "corrupted-own-program.com"
    controls = []

    def reject(name, report_input=report_path, memory_input=memory_path, program_input=program_path):
        try:
            verify(report_input, memory_input, program_input, os_name)
        except AssertionError:
            controls.append(dict(name=name, rejected=True))
        else:
            raise AssertionError("Negative control accepted: " + name)

    for name, offset in (("ldt-cache-value", address + 84), ("ring-selector", address + 92),
                         ("interrupt-return-ip", address + 156), ("fault-selector-error", address + 192),
                         ("fault-stack", address + 204), ("stack-guard", address + 220),
                         ("tss-stack-pointer", report["cpu"]["trCache"]["base"] + 4)):
        changed = bytearray(memory)
        changed[offset] ^= 1
        ram_path.write_bytes(changed)
        reject(name, memory_input=ram_path)
    changed = json.loads(json.dumps(report))
    changed["cpu"]["privilegeStackSwitches"] = 0
    json_path.write_text(json.dumps(changed), encoding="utf-8")
    reject("actual-privilege-count", report_input=json_path)
    changed = json.loads(json.dumps(report))
    changed["cpu"]["trCache"]["base"] = struct.unpack_from("<I", memory, address + 232)[0]
    json_path.write_text(json.dumps(changed), encoding="utf-8")
    reject("actual-cached-tr-base", report_input=json_path)
    changed = bytearray(program)
    changed[program.index(b"CPPRADDR") + 12] ^= 1
    com_path.write_bytes(changed)
    reject("program-instruction-address", program_input=com_path)
    result = dict(passed=True, rejectedControls=len(controls), controls=controls)
    (directory / "summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


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
