"""Independent private RAM, page-table, executable-byte and DOS-return oracle."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def verify(report_path, memory_path, program_path, kind, unavailable=False, source=None, ram_mib=8):
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory, program = Path(memory_path).read_bytes(), Path(program_path).read_bytes()
    output, cpu = report["status"]["debugOutput"], report["cpu"]
    assert len(memory) == ram_mib * 1048576
    assert report["status"]["fault"] is None and report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["hardwareResetCount"] == report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    assert report["ataSectorsRead"] > 0
    if source is not None:
        assert (report["floppySectorsRead"] > 0) == (source == "floppy")
    prefix, magic, label_magic = (("VCPI-PROTECTED", b"CPVCP001", b"CPVCADDR") if kind == "vcpi"
                                   else ("EMS-BANK", b"CPEMS001", b"CPEMADDR"))
    assert "MS-DOS Version 6.22" in output and re.search(r"\r\nMEMORY-GUEST-END\r\n\r\nC:\\>$", output)
    markers = re.findall(prefix + r"-PROOF=([0-9A-F]{8})\r\n", output)
    assert len(markers) == 1
    address = int(markers[0], 16)
    raw = lambda a: struct.unpack_from("<I", memory, a)[0]

    def physical(a, directory=None):
        if directory is None and not cpu["pagingEnabled"]:
            return a
        cr3 = cpu["cr3"] if directory is None else directory
        pde = raw((cr3 & ~4095) + (a >> 22) * 4)
        assert pde & 1
        pte = raw((pde & ~4095) + ((a >> 12) & 1023) * 4)
        assert pte & 1
        result = (pte & ~4095) | (a & 4095)
        assert result < len(memory)
        return result

    read = lambda a, count: bytes(memory[physical(a + i)] for i in range(count))
    proof = read(address, 256)
    assert proof[:8] == magic
    u32 = lambda n: struct.unpack_from("<I", proof, n)[0]
    u16 = lambda n: struct.unpack_from("<H", proof, n)[0]
    assert u32(8) == 1
    if unavailable:
        assert prefix + "-FAIL\r\n" in output and prefix + "-PASS" not in output
        assert u32(12) == u32(16) == 0
        return dict(passed=True, kind=kind, expectedUnavailable=True, guestReturned=True)
    assert "HIMEM testet den erweiterten Speicher...beendet." in output and "  EMM386 " in output
    assert prefix + "-PASS\r\n" in output and prefix + "-FAIL" not in output
    assert u32(12) == 1 and u32(20) == 0
    assert cpu["protectedMode"] and cpu["pagingEnabled"] and not cpu["tripleFaulted"]
    assert cpu["virtual8086Entries"] > 1000 and cpu["virtual8086Instructions"] > 100000
    assert cpu["virtual8086Entries"] - cpu["virtual8086Exits"] in (0, 1)
    assert cpu["currentPrivilegeLevel"] == (3 if cpu["virtual8086"] else 0)
    assert cpu["trCache"]["access"] == 0x8b and cpu["tlbHits"] > 10000
    assert cpu["pageFaults"] == 0
    labels = struct.unpack_from("<" + ("16H" if kind == "vcpi" else "5H"), program, program.index(label_magic) + 8)
    basis = u32(24 if kind == "vcpi" else 56)
    assert basis & 15 == 0 and 0x500 <= basis < 0x90000
    proof_label, code_end = (labels[11], labels[15]) if kind == "vcpi" else (labels[2], labels[1])
    assert basis + proof_label == address
    assert read(basis + 0x100, code_end - 0x100) == program[:code_end - 0x100]

    if kind == "vcpi":
        assert u32(16) == 15 and u32(28) == 0x100
        assert u32(32) == u32(80) and u32(124) == u32(132) == u32(32) - 2
        assert u32(72) == u32(76) == 2 and u32(136) == u32(140) == 0
        directory, pd_physical, pt0, pt1 = (u32(n) for n in (44, 48, 52, 56))
        assert directory == (basis + labels[14] + 4095) & ~4095
        assert [pd_physical, pt0, pt1, u32(60)] == [physical(directory + n * 4096) for n in range(4)]
        assert u32(84) == pd_physical and u32(88) & 0x80000001 == 0x80000001
        assert u32(92) & (0x20000 | 0x200 | 0x4000) == 0
        assert (u16(96), u32(98)) == (55, basis + labels[5])
        # IDTR's recorded base must equal the actual descriptor installed by the client.
        idtr = read(basis + labels[9], 6)
        assert (u16(104), u32(106)) == struct.unpack("<HI", idtr)
        assert (u16(112), u16(114), u16(116), u16(118)) == (24, 0, 8, 16)
        assert u32(120) == basis + labels[12]
        assert raw(pd_physical) == pt0 | 0x27 and raw(pd_physical + 4) == pt1 | 0x23
        assert not any(memory[pd_physical + 8:pd_physical + 4096])
        pages = [u32(n) for n in (36, 40, 128)]
        assert len(set(pages)) == 3
        for n, page in enumerate(pages):
            assert 0x100000 <= page <= len(memory) - 4096 and page & 4095 == 0
            assert raw(pt1 + n * 4) == page | 0x63
            expected = b"".join(struct.pack("<I", (0x13579bdf + (n * 1024 + j) * 0x1020304) & 0xffffffff)
                                for j in range(1024))
            assert memory[page:page + 4096] == expected
            assert physical(0x400000 + n * 4096, pd_physical) == page
        gdt = read(basis + labels[5], 56)
        assert gdt[24 + 5] == 0x8b
        assert gdt[32 + 5] & 0x9a == 0x9a
        assert gdt[40 + 5] & 0x92 == 0x92
        # VCPI's caller supplies a 32-bit FAR frame. The original EMM386
        # actually returns a USE16 server descriptor; do not invent CS.D=1.
        server_base = int.from_bytes(gdt[34:36], "little") | gdt[36] << 16 | gdt[39] << 24
        assert physical(server_base + u32(64), pd_physical) >= 0x100000
        server_ptr = read(basis + labels[13], 6)
        assert struct.unpack("<IH", server_ptr) == (u32(64), 32)
        table_end = u32(68) - ((directory + 4096 - basis) & 65535)
        assert 1024 <= table_end <= 4096 and table_end & 3 == 0
        assert cpu["trCache"]["base"] != basis + labels[10] and cpu["cr3"] != pd_physical
        assert cpu["tlbInvalidations"] >= 14
        details = dict(protectedClientEntries=2, protectedClientReturns=2, protectedEntryCpl=0,
                       protectedFreeCountUnchanged=True, allocatedAndFreedPages=3,
                       directlyWrittenBytes=12288, physicalPayloadPages=pages,
                       ownCr3=pd_physical, originalMonitorRestored=True)
    else:
        assert u32(16) == 31 and u32(24) == 0x40
        assert 0xc000 <= u32(28) <= 0xe000 and u32(28) & 0x3ff == 0
        assert 8 < u16(32) <= u16(34) and u16(38) == u16(32) - 8 and u16(40) == u16(32)
        assert u16(36) > 0 and u32(44) == 0 and u32(48) == 24 and u32(52) == 12
        pages = struct.unpack_from("<32I", proof, 64)
        assert len(set(pages)) == 32
        for n, page in enumerate(pages):
            assert 0x100000 <= page <= len(memory) - 4096 and page & 4095 == 0
            logical, quarter = divmod(n, 4)
            expected = b"".join(struct.pack("<I", (0x12345678 + logical * 0x11111111 +
                                                  (quarter * 1024 + j) * 0x1020305) & 0xffffffff)
                                for j in range(1024))
            assert memory[page:page + 4096] == expected
        assert cpu["tlbInvalidations"] >= 25
        details = dict(emsVersion="4.0", pageFrameSegment=f"{u32(28):04X}", allocatedAndFreed16KiBPages=8,
                       wholePageChecks=12, mapCalls=24, aliasSlots=4, savedMapRestored=True,
                       payloadBytes=131072, freePagesBefore=u16(32), freePagesAfter=u16(40))
    return dict(passed=True, kind=kind, ramMiB=ram_mib, programSource=source, proofAddress=address,
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), guestReturned=True,
                originalFreeCountRestored=True, **details)


def negative_controls(report_path, memory_path, program_path, kind, directory):
    verify(report_path, memory_path, program_path, kind)
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    memory, program = Path(memory_path).read_bytes(), Path(program_path).read_bytes()
    prefix = "VCPI-PROTECTED" if kind == "vcpi" else "EMS-BANK"
    address = int(re.search(prefix + "-PROOF=([0-9A-F]{8})", report["status"]["debugOutput"])[1], 16)
    # Own conventional data remains identity mapped in this controlled fixture.
    assert memory[address:address + 8] == (b"CPVCP001" if kind == "vcpi" else b"CPEMS001")
    directory = Path(directory)
    directory.mkdir(exist_ok=False)
    rp, mp, pp = directory / "corrupted.json", directory / "corrupted.private-ram.bin", directory / "corrupted.com"
    controls = []

    def reject(name, r=report_path, m=memory_path, p=program_path):
        try:
            verify(r, m, p, kind)
        except AssertionError:
            controls.append(dict(name=name, rejected=True))
        else:
            raise AssertionError("Oracle accepted corrupted evidence: " + name)

    offsets = ([80, 112, 76, 84] if kind == "vcpi" else [28, 38, 40, 48])
    for offset in offsets:
        changed = bytearray(memory)
        changed[address + offset] ^= 1
        mp.write_bytes(changed)
        reject("proof-offset-" + str(offset), m=mp)
    changed = bytearray(memory)
    page = struct.unpack_from("<I", memory, address + (36 if kind == "vcpi" else 64))[0]
    changed[page + 4095] ^= 1
    mp.write_bytes(changed)
    reject("physical-payload", m=mp)
    if kind == "vcpi":
        changed = bytearray(memory)
        table = struct.unpack_from("<I", memory, address + 56)[0]
        changed[table] ^= 1
    else:
        changed = bytearray(memory)
        changed[address + 68:address + 72] = changed[address + 64:address + 68]
    mp.write_bytes(changed)
    reject("actual-page-mapping", m=mp)
    changed_program = bytearray(program)
    changed_program[0] ^= 1
    pp.write_bytes(changed_program)
    reject("actual-program-byte", p=pp)
    changed_report = copy.deepcopy(report)
    changed_report["cpu"]["tlbInvalidations"] = 0
    rp.write_text(json.dumps(changed_report), encoding="utf-8")
    reject("missing-tlb-invalidations", r=rp)
    return dict(passed=True, negativeControls=controls)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for option in ("report", "memory", "program"):
        parser.add_argument("--" + option, type=Path, required=True)
    parser.add_argument("--kind", choices=("vcpi", "ems"), required=True)
    parser.add_argument("--source", choices=("ide", "floppy"))
    parser.add_argument("--ram-mib", type=int, choices=(4, 8, 16), default=8)
    parser.add_argument("--expect-unavailable", action="store_true")
    parser.add_argument("--negative-controls", type=Path)
    args = parser.parse_args()
    result = (negative_controls(args.report, args.memory, args.program, args.kind, args.negative_controls)
              if args.negative_controls else verify(args.report, args.memory, args.program, args.kind,
                                                    args.expect_unavailable, args.source, args.ram_mib))
    print(json.dumps(result, indent=2))
