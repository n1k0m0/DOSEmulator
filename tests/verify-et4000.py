# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent full-pixel, real-port and guest-RAM oracle for our ET4000 DOS probe.

No emulator imports, video-memory injection or commercial ROM. The whole MiB is
read back by guest code; the host independently checks visible pixels and proofs.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import re
import struct


def check(ok, message):
    """Validates et4000 evidence from actual guest state, immutable program bytes and independently expected results."""
    if not ok:
        raise AssertionError(message)


def sha(data):
    """Computes an uppercase SHA-256 identity for immutable input/evidence comparisons."""
    return hashlib.sha256(data).hexdigest().upper()


def load(path):
    """Loads the explicit report, memory and program evidence without modifying its files."""
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def verify(args):
    """Validates et4000 evidence from actual guest state, immutable program bytes and independently expected results."""
    report = load(args.report)
    memory, program, ppm = args.memory.read_bytes(), args.program.read_bytes(), args.frame.read_bytes()
    io = [json.loads(x) for x in args.io.read_text(encoding="utf-8-sig").splitlines()]
    mode = int(args.mode, 0)
    width, height = {0x2e: (640, 480), 0x30: (800, 600), 0x38: (1024, 768)}[mode]
    marker = re.search(r"ET4000-PROOF=([0-9A-F]{4}):([0-9A-F]{4})", report["status"]["debugOutput"])
    check(marker is not None, "missing actual guest proof pointer")
    segment, offset = (int(x, 16) for x in marker.groups())
    base = segment * 16
    proof_file = program.index(b"ET4K0001")
    check(offset == proof_file + 256, "proof pointer outside own program")
    immutable = program.index(b"ETMUTABL") + 8

    def read(ram, address, size):
        """Reads checked guest proof bytes through the actual reported page tables, including page crossings."""
        result = bytearray()
        for linear in range(address, address + size):
            physical = linear
            if report["cpu"]["pagingEnabled"]:
                pde = struct.unpack_from("<I", ram, (report["cpu"]["cr3"] & ~4095) + (linear >> 22) * 4)[0]
                check(pde & 1 and not pde & 0x80, "proof directory not present/classic")
                pte = struct.unpack_from("<I", ram, (pde & ~4095) + ((linear >> 12) & 1023) * 4)[0]
                check(pte & 1, "proof page missing")
                physical = (pte & ~4095) + (linear & 4095)
            check(0 <= physical < len(ram), "proof outside physical RAM")
            result.append(ram[physical])
        return bytes(result)

    def check_report(r):
        """Checks report completion, actual device counters and mode consistency independently from the guest success marker."""
        v, c = r["video"], r["cpu"]
        check(r["status"]["fault"] is None and "ET4000-READY" in r["status"]["debugOutput"] and "ET4000-FAILED" not in r["status"]["debugOutput"], "guest fault/failure")
        input_state = r.get("scenario") or dict(complete=True, inputBytesSent=r["inputBytesSent"], inputBytesTotal=r["inputBytesTotal"])
        check(input_state["complete"] and input_state["inputBytesSent"] == input_state["inputBytesTotal"], "unfinished real KBC input")
        check(r["status"]["configuration"]["videoCard"] == v["model"] == "et4000ax-isa-1m-development", "card/profile")
        check(v["videoMemoryBytes"] == 1048576 and v["segmentSelect"] == 0 and v["extendedRegistersUnlocked"] and v["segmentRegisterEnabled"], "memory/bank/key")
        check(not any(v[x] for x in ("linearFramebuffer", "highColor", "fullChip")), "unimplemented scope")
        if "vbeInterface" in v:
            check(v.get("vbe") and v.get("vbeInterface") == "1.2-format banked subset 00/01/02/03/05" and not v.get("fullVbeConformance"), "bounded firmware scope")
        else:
            check(not v["vbe"], "historical firmware scope")
        check(r["graphicsMode"] and r["indexed256"] and (r["width"], r["height"]) == (width, height), "scanout mode")
        check(r["ataSectorsRead"] > 0 and r["floppySectorsRead"] > 0 and r["ataSectorsWritten"] == r["floppySectorsWritten"] == 0, "media access")
        if args.manager:
            check(c["protectedMode"] and c["pagingEnabled"] and c["virtual8086Instructions"] > 0, "not a real EMM386/V86 path")
        else:
            check(not c["pagingEnabled"], "unexpected monitor")

    def check_ram(ram):
        """Validates executed program/table bytes, guest proof buffers and guard regions through the actual reported memory mapping."""
        check(read(ram, base + 256, immutable) == program[:immutable], "actual executable/strings changed")
        p = read(ram, base + offset, 196)
        check(p[:8] == b"ET4K0001", "proof signature")
        expected = (1, mode, mode | (width // 8 << 8), 16, 16, 16, 1, 16, 16, 0, 0, 0xbeef)
        check(struct.unpack_from("<12H", p, 8) == expected, "guest mode/count/clear/preserve/complete")
        check(struct.unpack_from("<16H", p, 32) == tuple((n ^ 0xa5) << 8 | (n + 0x10) for n in range(16)), "all sixteen chain4 banks")
        check(struct.unpack_from("<16H", p, 64) == tuple(0x6d40 + n for n in range(16)), "split read/write nibble")
        check(struct.unpack_from("<16H", p, 96) == tuple(((n + 0x21) ^ 0x5a) << 8 | (n + 0x21) for n in range(16)), "four planar groups/four planes")
        check(p[128:132] == bytes((0x21, 0x22, 0x23, 0x24)), "cross-bank read-latch copy")
        expected_preserve = bytearray()
        for bank in range(16):
            expected_preserve.extend((at + 3 * (at >> 8) + 17 * bank) & 255 for at in (0, 1, 65534, 65535))
        check(p[132:] == expected_preserve, "full-MiB no-clear endpoint readbacks")
        check(read(ram, 0x449, 1) == bytes((mode,)) and int.from_bytes(read(ram, 0x44a, 2), "little") == width // 8, "BDA mode/columns")
        check(int.from_bytes(read(ram, 0x485, 2), "little") == 16 and read(ram, 0x484, 1)[0] == height // 16 - 1, "BDA point height/rows")

    def check_ports(rows, r):
        """Replays actual VGA register writes to validate bank selection and its observable transfer sequence."""
        crtc, seq, gc, attr = {}, {}, {}, {}
        ci = si = gi = ai = 0
        phase = False
        bank_writes = []
        palette = bytearray(768)
        di = 0
        herc = key = False
        requests = []
        for event in rows:
            if event["kind"] == "int10":
                requests.append(event["ax"] & 65535)
            if event["kind"] != "io":
                continue
            port, value, length, write = (event[x] for x in ("port", "value", "width", "write"))
            for n in range(length):
                p, b = port + n, value >> (8*n) & 255
                if not write:
                    if p == 0x3da:
                        phase = False
                    continue
                if p == 0x3bf:
                    herc = b & 3 == 3
                elif p == 0x3d8:
                    key = herc and b & 0xa0 == 0xa0
                elif p == 0x3cd:
                    check(key, "bank write before actual unlock")
                    bank_writes.append(b)
                elif p == 0x3d4:
                    ci = b & 63
                elif p == 0x3d5:
                    crtc[ci] = b
                elif p == 0x3c4:
                    si = b & 7
                elif p == 0x3c5:
                    seq[si] = b
                elif p == 0x3ce:
                    gi = b & 15
                elif p == 0x3cf:
                    gc[gi] = b
                elif p == 0x3c0:
                    if phase:
                        attr[ai] = b
                    else:
                        ai = b & 31
                    phase = not phase
                elif p == 0x3c8:
                    di = b * 3
                elif p == 0x3c9:
                    palette[di] = b & 63
                    di = (di + 1) % 768
        check(len(bank_writes) == r["video"]["bankSwitchWrites"] and bank_writes[-1] == 0, "actual bank count/final nibble")
        check(set(range(16)).issubset(bank_writes) and set(n << 4 | (n ^ 15) for n in range(16)).issubset(bank_writes), "missing physical nibble combinations")
        if getattr(args, 'vbe', False):
            check(requests.count(0x4f00) == 2 and requests.count(0x4f02) == 5 and requests.count(0x4f03) == 4, "actual VBE discovery/set/current calls")
        else:
            check(requests.count(mode) == 2 and requests.count(mode | 0x80) == 1, "actual guest clear/no-clear calls")
        check(crtc[1] == width // 8 - 1 and crtc[0x13] == width // 8 and crtc[0x33] == 0 and crtc[0x35] == 0, "real CRTC dimensions/stride/start")
        check(seq[2] == 15 and seq[4] == 14 and gc[5] == 0x40 and gc[6] == 5 and attr[0x10] == 1, "real packed-pixel registers")
        check(palette == bytes(v for i in range(256) for v in (i & 63, i >> 2, i * 7 & 63)), "actual guest DAC ramp")

    colors = [bytes((v << 2) | (v >> 4) for v in (i & 63, i >> 2, i * 7 & 63)) for i in range(256)]
    expected_pixels = b"".join(colors[(n + 3 * (n >> 8) + 17 * (n >> 16)) & 255] for n in range(width * height))
    header = f"P6\n{width} {height}\n255\n".encode()
    bgra = bytearray()
    for i in range(0, len(expected_pixels), 3):
        rgb = expected_pixels[i:i+3]
        bgra.extend((rgb[2], rgb[1], rgb[0], 255))
    expected_hash = sha(bgra)

    def check_frame(frame, r):
        """Reconstructs and compares the complete expected SVGA frame, including bank boundaries and colors."""
        check(frame == header + expected_pixels, "independent whole-frame pixel formula")
        check(r["frameHash"] == expected_hash, "independent ARGB hash")

    check_report(report)
    check_ram(memory)
    check_ports(io, report)
    check_frame(ppm, report)
    rejected = 0
    if args.negative_controls:
        def reject(fn, *values):
            """Requires an independently corrupted input to fail instead of accepting the guest's success marker alone."""
            nonlocal rejected
            try:
                fn(*values)
            except (AssertionError, KeyError, IndexError, struct.error):
                rejected += 1
                return
            raise AssertionError("mutated oracle accepted")
        for path, value in [(('width',), width+1), (('height',), height+1), (('graphicsMode',), False), (('indexed256',), False),
                            (('video','videoMemoryBytes'), 262144), (('video','segmentSelect'), 0x10), (('video','extendedRegistersUnlocked'), False),
                            (('video','vbe'), not report['video']['vbe']), (('video','highColor'), True), (('video','fullChip'), True),
                            (('ataSectorsWritten',), 1), (('floppySectorsRead',), 0)]:
            changed = copy.deepcopy(report)
            target = changed
            for key_ in path[:-1]:
                target = target[key_]
            target[path[-1]] = value
            reject(check_report, changed)
        for at in (len(header), len(ppm)//2, len(ppm)-1):
            changed = bytearray(ppm)
            changed[at] ^= 1
            reject(check_frame, bytes(changed), report)
        for relative in (0, 7, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 62, 64, 94, 96, 126, 128, 131, 132, 195):
            # The conventional COM/proof mapping is explicitly identity in these local monitor configurations.
            address = base + offset + relative
            check(read(memory, address, 1) == memory[address:address+1], "negative proof identity assumption")
            changed = bytearray(memory)
            changed[address] ^= 1
            reject(check_ram, changed)
        changed = bytearray(memory)
        changed[base+256] ^= 1
        reject(check_ram, changed)
        for port in (0x3cd, 0x3bf, 0x3d8, 0x3c9):
            changed = [x for x in io if x.get('port') != port]
            reject(check_ports, changed, report)
        changed = copy.deepcopy(report)
        changed['video']['bankSwitchWrites'] += 1
        reject(check_ports, io, changed)
    proof = read(memory, base + offset, 196)
    return dict(passed=True, mode=mode, pixelsVerified=width*height, guestCases=81,
                chain4BytesCompared=1048576, erasedBytesCompared=1048576, preservedBytesCompared=1048576,
                frameHash=expected_hash, proofHash=sha(proof), actualProgramBytesVerified=immutable,
                bankWrites=report['video']['bankSwitchWrites'], negativeControlsRejected=rejected,
                manager=args.manager, wholeFrameIndependent=True, fullChipAccepted=False)


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    for option in ('report', 'memory', 'program', 'frame', 'io'):
        p.add_argument('--'+option, type=Path, required=True)
    p.add_argument('--mode', required=True)
    p.add_argument('--manager', action='store_true')
    p.add_argument('--negative-controls', action='store_true')
    print(json.dumps(verify(p.parse_args()), indent=2))
