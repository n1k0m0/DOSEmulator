# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent stdlib verification of port protocol and private scratch overlays.

No emulator modules: parse actual FDC/DMA port bytes and the documented journal,
then check every sector against the probe's fixed CHRN/fill requests.
"""
import hashlib
import json
import pathlib
import struct
import sys


def overlay(base_path, overlay_path, expected):
    """Parses and validates checksummed journal records without opening any medium for writing."""
    base = pathlib.Path(base_path).read_bytes()
    data = pathlib.Path(overlay_path).read_bytes()
    assert data[:8] == b"DOSCOW01" and struct.unpack_from("<q", data, 8)[0] == 2880
    assert data[16:48] == hashlib.sha256(base).digest()
    assert (len(data) - 48) % 552 == 0
    actual = bytearray(base)
    writes = []
    for start in range(48, len(data), 552):
        record = data[start:start + 552]
        assert hashlib.sha256(record[:520]).digest() == record[520:]
        lba, = struct.unpack_from("<q", record)
        assert lba in expected, ("unexpected sector write", lba)
        assert record[8:520] == bytes([expected[lba]]) * 512
        actual[lba * 512:(lba + 1) * 512] = record[8:520]
        writes.append(lba)
    assert len(writes) == len(expected) and set(writes) == set(expected)
    for lba in range(2880):
        assert actual[lba * 512:(lba + 1) * 512] == bytes([expected.get(lba, 0x39)]) * 512, lba
    return {"writtenSectors": len(writes), "all2880SectorsChecked": True,
            "overlaySha256": hashlib.sha256(data).hexdigest().upper()}


def parse_protocol(path):
    """Decodes actual FDC command/result observations into independently checked track transfers."""
    lengths = {3: 3, 4: 2, 7: 2, 8: 1, 15: 3, 5: 9, 6: 9, 13: 6}
    responses = {4: 1, 8: 2, 5: 7, 6: 7, 13: 7}
    dma = {"address": 0, "count": 0, "page": 0, "mode": 0, "masked": True}
    high = False
    command, packets, pending, needs_mask = [], [], None, None
    for line in pathlib.Path(path).read_text(encoding="utf-8-sig").splitlines():
        io = json.loads(line)
        port, value, write = io["port"], io["value"], io["write"]
        assert io["width"] == 1
        if write:
            if port == 0x0c:
                high = False
            elif port in (4, 5):
                key = "address" if port == 4 else "count"
                dma[key] = (dma[key] & (0xff if high else 0xff00)) | (value << (8 if high else 0))
                high = not high
            elif port == 0x81:
                dma["page"] = value
            elif port == 0x0b and value & 3 == 2:
                dma["mode"] = value
            elif port == 0x0a and value & 3 == 2:
                dma["masked"] = bool(value & 4)
                if dma["masked"] and needs_mask is not None:
                    needs_mask["maskedAfterResult"] = True
                    needs_mask = None
        if port != 0x3f5:
            continue
        if write:
            assert pending is None, ("command before draining result", pending)
            command.append(value)
            opcode = command[0] & 31
            assert opcode in lengths, ("unknown command", command)
            if len(command) == lengths[opcode]:
                packet = {"time": io["time"], "command": command, "result": [], "dma": dict(dma)}
                packets.append(packet)
                pending = packet if opcode in responses else None
                command = []
        else:
            assert pending is not None, "unexpected FIFO read"
            pending["result"].append(value)
            pending["completedAt"] = io["time"]
            if len(pending["result"]) == responses[pending["command"][0] & 31]:
                if pending["command"][0] == 0x4d:
                    needs_mask = pending
                pending = None
    assert pending is None and not command
    return packets


def protocol(path, mode):
    """Parses actual FDC commands, result bytes and DMA observations without assuming the guest report is correct."""
    packets = parse_protocol(path)
    formats = [p for p in packets if p["command"][0] == 0x4d]
    expected = [] if mode == "verify" else ([[0x4d, 0, 2, 18, 0x6c, 0xf6]] if mode == "readonly" else [
        [0x4d, 0, 2, 18, 0x6c, 0xf6], [0x4d, 5, 2, 9, 0x57, 0xa7], [0x4d, 5, 2, 9, 0x57, 0xa7]])
    assert [p["command"] for p in formats] == expected
    for i, packet in enumerate(formats):
        sc = packet["command"][3]
        assert packet["dma"] == {"address": 0xfe00, "count": sc * 4 - 1, "page": 9, "mode": 0x4a, "masked": False}
        expected_error = 2 if mode == "readonly" else (4 if i == 2 else 0)
        assert packet["result"][1:3] == [expected_error, 0]
        assert packet["result"][0] & 0xc0 == (0x40 if expected_error else 0)
        assert packet["completedAt"] > packet["time"]
        assert packet.get("maskedAfterResult"), "DMA2 not masked after format completion"
    return {"formatCommands": len(formats), "realDma2Programming": True, "drainedResults": True,
            "formats": formats}


if __name__ == "__main__":
    trace, mode = sys.argv[1:3]
    assert mode in ("write", "verify", "readonly")
    result = {"passed": True, "mode": mode, "portProtocol": protocol(trace, mode)}
    if mode != "readonly":
        a_base, a_cow, b_base, b_cow = sys.argv[3:7]
        result["a"] = overlay(a_base, a_cow, {108 + i: 0xf6 for i in range(18)})
        result["b"] = overlay(b_base, b_cow, {2862 + i: 0xa7 for i in range(1, 18, 2)})
    print(json.dumps(result, indent=2))
