# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent stdlib oracle for actual KBC bytes, PIC masks and IRQ12 in guest tests."""
import json
from pathlib import Path
import sys


def verify(report_path, trace_path):
    """Validates mouse evidence from actual guest state, immutable program bytes and independently expected results."""
    report = json.loads(Path(report_path).read_text(encoding="utf-8-sig"))
    expected = [[0x29, 4, 253], [0x1A, 254, 1], [8, 0, 0], [9, 0, 0], [8, 0, 0]]
    commands, replies, packet, packets = [], [], [], []
    controller_write = parameter = None
    auxiliary = False
    master_mask = slave_mask = 0xFF
    interrupts = slave_eois = 0
    previous_time = -1
    for line in Path(trace_path).read_text(encoding="utf-8-sig").splitlines():
        event = json.loads(line)
        assert event["time"] >= previous_time
        previous_time = event["time"]
        if event["kind"] == "interrupt":
            assert event["vector"] == 0x74 and not master_mask & 4 and not slave_mask & 0x10
            interrupts += 1
            continue
        port, value, write = event["port"], event["value"], event["write"]
        assert event["width"] == 1
        if write:
            if port == 0x21: master_mask = value
            if port == 0xA1: slave_mask = value
            if port == 0xA0 and value == 0x20: slave_eois += 1
            if port == 0x64: controller_write = value
            if port == 0x60:
                if controller_write == 0xD4:
                    assert not replies and not packet
                    commands.append(value)
                    if parameter is not None:
                        assert value in (10, 20, 40, 60, 80, 100, 200) if parameter == 0xF3 else value <= 3
                        replies = [0xFA]; parameter = None
                    elif value in (0xF3, 0xE8): replies = [0xFA]; parameter = value
                    elif value == 0xF2: replies = [0xFA, 0]
                    elif value == 0xFF: replies = [0xFA, 0xAA, 0]
                    else:
                        assert value in (0xF4, 0xF5, 0xF6, 0xE6, 0xE7), f"Unexpected driver command {value:02X}"
                        replies = [0xFA]
                controller_write = None
        elif port == 0x64:
            auxiliary = value & 0x21 == 0x21
        elif port == 0x60 and auxiliary:
            if replies:
                assert value == replies.pop(0), f"Incorrect mouse response {value:02X}"
            else:
                packet.append(value)
                if len(packet) == 3:
                    assert packet[0] & 0xCC == 8, f"Invalid ID00 packet {packet}"
                    packets.append(packet); packet = []
            auxiliary = False
    assert not replies and not packet and parameter is None
    assert packets == expected, packets
    assert interrupts == 15 and slave_eois == interrupts
    assert len(commands) == report["ps2Mouse"]["commandCount"] == 31
    assert report["ps2Mouse"]["packetCount"] == len(packets)
    assert report["scenario"]["mouseInputsSent"] == 5 and report["scenario"]["complete"]
    assert report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"]
    assert report["status"]["fault"] is None
    assert report["ataSectorsWritten"] == report["floppySectorsWritten"] == 0
    assert "PS2-DRIVER-FAILED" not in report["status"]["debugOutput"]
    assert "PS2-GRAPHICS-FAILED" not in report["status"]["debugOutput"]
    for text in ("PS2-LEFT-MOTION-OK", "PS2-RIGHT-MOTION-OK", "PS2-DRIVER-RETURN-OK", "Driver successfully unloaded",
                 "PS2-GRAPHICS-13-READY", "PS2-GRAPHICS-12-READY", "PS2-GRAPHICS-RETURN-OK"):
        assert text in report["status"]["debugOutput"], text
    assert report["text"].rstrip().endswith("C:\\>")
    return {"passed": True, "packets": packets, "irq12": interrupts, "slaveEois": slave_eois, "commandBytes": len(commands),
            "guestInputsOnly": True, "guestWrites": 0}


if __name__ == "__main__": print(json.dumps(verify(*sys.argv[1:]), indent=2))
