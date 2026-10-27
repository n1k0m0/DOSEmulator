# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent software-IRQ proof: real guest handlers, ports, PIC ACKs and RAM.

No emulator imports or synthetic device events. IRQ source latches are checked
separately from DMA/block completion, physical PIC ACKs and handler IRETs.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def verify(report, ram, io, irqs, program, port, irq):
    """Require exact independent counters, handler records, program bytes and actual port traffic."""
    assert report["status"]["fault"] is None
    debug = report["status"]["debugOutput"]
    assert "SB16-DIAG-PASSSB16-DIAG-RETURN" in debug and "SB16-DIAG-FAILED" not in debug
    assert "\nSB16-DIAG-END\r\n" in debug
    assert report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    sb = report["sb16"]
    for field in ["transferredSamples", "recordedSamples", "convertedSamples", "dma8Grants", "dma16Grants", "completedBlocks",
                  "remainingSamples", "interruptSources", "unsupportedCommands", "unsupportedMixerAccesses", "rejectedWrites", "rejectedParameters", "rejectedResourceWrites"]:
        assert sb[field] == 0, field
    assert not sb["active"] and not sb["paused"]
    assert sb["irq8Requests"] == sb["softwareIrq8Requests"] == 5
    assert sb["irq16Requests"] == sb["softwareIrq16Requests"] == 2
    assert sb["legacyOutputControlAccesses"] == 4 and sb["resetCount"] == 1
    assert sb["ioBasePort"] == port and sb["irq"] == irq
    assert not sb["hostMicrophone"] and not sb["fullCardCompatibility"]
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    marker = b"SB16DIAG-PROOF01"
    expected = bytes.fromhex("aa550500ffff00") + bytes([1, 2, 3, 1, 1, 1, 2, 1, 1, 1]) + bytes(5)
    matches, offset = [], 0
    while (offset := ram.find(marker, offset)) >= 0:
        end = offset + len(marker)
        if ram[end:end + len(expected)] == expected:
            matches.append(offset)
        offset += 1
    assert len(matches) == 1, "Missing/ambiguous actual completed guest proof"
    address = matches[0]
    program_offset = program.index(marker)
    start = address - program_offset
    # Eight working bytes before the marker hold old vector/PIC/resource state.
    immutable_length = program_offset - 7
    assert ram[start:start + immutable_length] == program[:immutable_length], "Executed guest code differs from the assembled program"
    commands = [x["value"] for x in io if x["port"] == port + 12 and x["write"]]
    assert commands == [0xf2, 0xf3, 0xf2, 0xf3, 0xf2, 0xf2, 0xf2]
    assert all(x["width"] == 1 for x in io)
    index, legacy = None, []
    for x in io:
        if x["port"] == port + 4 and x["write"]:
            index = x["value"]
        elif x["port"] == port + 5 and index == 0x0e:
            legacy.append((x["write"], x["value"]))
    assert legacy == [(False, 255), (True, 253), (False, 255), (True, 255)]
    assert len(irqs) == 5 and all(x["kind"] == "hardware" and x["irq"] == irq and not x["spurious"] for x in irqs)
    assert all(irqs[i]["time"] < irqs[i + 1]["time"] for i in range(4))
    for pic in ([0x20, 0xa0] if irq >= 8 else [0x20]):
        assert len([x for x in io if x["write"] and x["port"] == pic and x["value"] == 0x20]) >= 5
    return dict(passed=True, handlers=5, softwareIrq8=5, softwareIrq16=2, zeroPcmAndDma=True,
                proofAddress=address, proofSha256=hashlib.sha256(expected).hexdigest(), physicalVectors=[x["vector"] for x in irqs])


def main():
    """Read private evidence and prove that forged device, code, handler and PIC results are rejected."""
    p = argparse.ArgumentParser()
    for name in ["report", "memory", "io", "irq", "program"]:
        p.add_argument("--" + name, required=True, type=Path)
    p.add_argument("--port", type=int, default=0x220)
    p.add_argument("--line", type=int, default=5)
    p.add_argument("--negative-controls", action="store_true")
    a = p.parse_args()
    report = json.loads(a.report.read_text(encoding="utf-8-sig"))
    ram, program = a.memory.read_bytes(), a.program.read_bytes()
    io = [json.loads(line) for line in a.io.read_text().splitlines()]
    irqs = [json.loads(line) for line in a.irq.read_text().splitlines()]
    result = verify(report, ram, io, irqs, program, a.port, a.line)
    if a.negative_controls:
        mutations = []
        for field in ["completedBlocks", "transferredSamples", "dma8Grants", "dma16Grants", "irq8Requests", "irq16Requests",
                      "softwareIrq8Requests", "softwareIrq16Requests", "legacyOutputControlAccesses", "interruptSources"]:
            changed = copy.deepcopy(report)
            changed["sb16"][field] += 1
            mutations.append((changed, ram, io, irqs, program))
        address = result["proofAddress"] + len(b"SB16DIAG-PROOF01")
        for delta in [0, 2, 4, 6, 7, 12, 17]:
            changed = bytearray(ram)
            changed[address + delta] ^= 1
            mutations.append((report, changed, io, irqs, program))
        first_command = next(i for i, x in enumerate(io) if x["write"] and x["port"] == a.port + 12)
        mutations += [(report, ram, io, irqs[:-1], program), (report, ram, io[:first_command] + io[first_command + 1:], irqs, program),
                      (report, ram, io, irqs, bytes([program[0] ^ 1]) + program[1:])]
        rejected = 0
        for values in mutations:
            try:
                verify(*values, a.port, a.line)
            except (AssertionError, ValueError):
                rejected += 1
            else:
                raise AssertionError("Forged evidence accepted")
        result["negativeControlsRejected"] = rejected
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
