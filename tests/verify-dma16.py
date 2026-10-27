# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent AT word address/count model; inspect real guest RAM, register I/O and device deadlines."""
import argparse
import copy
import hashlib
import json
import re
import struct
from pathlib import Path

MAGIC = b"D16-PROOF-v1\0\0\0\0"


def require(condition, message):
    """Raises a diagnostic assertion when an independent device/guest invariant is not satisfied."""
    if not condition:
        raise ValueError(message)


def verify(vectors, report, memory, program):
    """Validates dma16 evidence from actual guest state, immutable program bytes and independently expected results."""
    require(len(memory) == 8 * 1048576, "RAM length")
    require(report["status"]["fault"] is None and report["hardwareResetCount"] == 0, "guest CPU fault/reset")
    require(report["scenario"]["complete"] and report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"], "incomplete keyboard scenario")
    require(report["ataSectorsWritten"] == 0, "guest HDD writes")
    debug = report["status"]["debugOutput"]
    require("DMA16 probe returned" in debug and "DMA16-END\r\n" in debug and "C:\\>" in report["text"], "DOS return")
    if report["system"] == "freedos":
        require("FreeDOS" in debug, "FreeDOS version")
    else:
        require("MS-DOS Version 6.22" in debug, "MS-DOS version")
    if report["system"].startswith("emm386"):
        require(report["cpu"]["protectedMode"] and report["cpu"]["cr0"] & 0x80000001 == 0x80000001 and
                report["cpu"]["cr3"] != 0 and report["cpu"]["virtual8086Entries"] > 0 and report["cpu"]["virtual8086Instructions"] > 0, "actual EMM386 V86/paging")
    else:
        require(not report["cpu"]["protectedMode"] and report["cpu"]["cr3"] == 0, "unexpected monitor")
    matches = re.findall(r"DMA16-PROOF=([0-9A-F]{4}):([0-9A-F]{4})", debug)
    require(len(matches) == 1, "proof address count")
    segment, offset = (int(s, 16) for s in matches[0])
    physical = segment * 16 + offset
    require(memory[physical:physical + 16] == MAGIC and program.count(MAGIC) == 1, "proof magic")
    version, total, allocation, window, error, completed, original_mask, guard = struct.unpack_from("<8H", memory, physical + 16)
    cases, positions = vectors["cases"], vectors["positions"]
    require((version, total, error, completed, guard) == (1, len(cases), 0, len(cases), 0xA55A), "proof header/error/guard")
    require(window % 0x2000 == 0 and allocation <= window and window + 0x2000 <= allocation + 0x4000 and
            window * 16 + 0x20000 < 0xA0000, "owned aligned 128-KiB buffer")
    require(window == (allocation + 0x1FFF) & 0xE000 and
            any(x["segment"] == allocation and x["owner"] == segment and x["paragraphs"] == 0x4000 and x["marker"] in (0x4D, 0x5A)
                for x in report["freeRequests"]), "actual DOS-owned memory block at free request")
    record_words = 11 + len(positions)
    footer_offset = 32 + 2 * len(cases) * record_words
    proof_size = footer_offset + 36 + 2
    require(struct.unpack_from("<H", memory, physical + proof_size - 2)[0] == 0x5AA5, "trailing proof guard")
    proof = memory[physical:physical + proof_size]
    require(proof[footer_offset:footer_offset + 8] == b"D16VDS01", "VDS footer")
    vds, locks, unlocks, disables, enables, disabled_mask = struct.unpack_from("<6H", proof, footer_offset + 8)
    size, dds_offset, dds_segment, buffer_id, buffer_physical = struct.unpack_from("<IIHHI", proof, footer_offset + 20)
    managed = report["system"].startswith("emm386")
    require(vectors["managed"] == managed, "guest/monitor vector variant")
    require((vds, locks, unlocks, disables, enables, disabled_mask) == ((0x100, 1, 1, 3, 3, 0) if managed else (0, 0, 0, 0, 0, 0)), "balanced actual VDS calls")
    require((size, dds_offset, dds_segment, buffer_id, buffer_physical) ==
            (0x20000, 0, window, 0, window * 16 if managed else 0), "real VDS contiguous 128-KiB identity buffer")
    requests = report["vdsRequests"]
    # A V86 INT followed by the monitor's real-mode reflection can deliver the same entry twice.
    # Keep both in the report; compare the independently specified logical requests here.
    requests = [x for i, x in enumerate(requests) if i == 0 or x != requests[i - 1]]
    expected_functions = [0x8102, 0x8103, 0x810B, 0x810B, 0x810B, 0x810C, 0x810C, 0x810C, 0x8104] if managed else []
    require([x["ax"] for x in requests] == expected_functions, "actual resident VDS entries")
    if managed:
        require([x["bx"] for x in requests if x["ax"] in (0x810B, 0x810C)] == [5, 6, 7, 5, 6, 7], "VDS high DMA channel numbers")
        require([x["dx"] for x in requests] == [0, 0x2C, 0, 0, 0, 0, 0, 0, 0], "VDS flags")
    code_base = segment * 16 + 0x100
    code = memory[code_base:code_base + len(program)]
    header_offset = program.index(MAGIC)
    mutable_port = program.index(b"DMA16-PROOF=") - 2
    require(code[:mutable_port] == program[:mutable_port] and code[mutable_port + 2:header_offset] == program[mutable_port + 2:header_offset], "guest code/source table changed")
    fixture = report["fixture"]
    require(fixture["testOnly"] and fixture["periodNanoseconds"] == 100000, "explicit test-only fixture contract")
    batches, beats = fixture["batches"], fixture["beats"]
    terminal = {5: False, 6: False, 7: False}
    batch_index = beat_index = transfers = blocked_total = invalid_total = terminal_events = 0
    read_words = write_words = 0
    for index, row in enumerate(cases):
        ch, mode = row["channel"], row["mode"]
        addr, count = row["address"], row["count"]
        data = {p: p ^ 0x8000 for p in positions}
        status_tc = False
        masked = bool(row["controls"] & 1)
        disabled = bool(row["controls"] & 2)
        for phase in range(1 + row["resume"]):
            if phase:
                masked = disabled = False
            require(batch_index < len(batches), "missing device batch")
            batch = batches[batch_index]
            require((batch["id"], batch["channel"], batch["write"], batch["attempts"], batch["seed"]) ==
                    (batch_index, ch, row["direction"] == 2, row["attempts"], row["seed"]), "batch metadata")
            if batch_index:
                require(batch["start"] > beats[beat_index - 1]["time"], "batch started before prior completion")
            value = row["seed"]
            transferred = blocked = invalid = 0
            for attempt in range(row["attempts"]):
                require(beat_index < len(beats), "missing device deadline")
                actual = beats[beat_index]
                write = row["direction"] == 2
                valid = (mode // 4) % 4 == (1 if write else 2) and mode < 0xC0
                if masked or disabled:
                    result, observed = "Blocked", value if write else 0
                    blocked += 1
                elif not valid:
                    result, observed = "InvalidMode", value if write else 0
                    invalid += 1
                else:
                    result = "Transferred"
                    require(addr in data, "vector exceeds independently observed window positions")
                    observed = value if write else data[addr]
                    if write:
                        data[addr] = value
                        value = (value + 1) % 65536
                        write_words += 1
                    else:
                        value = observed
                        read_words += 1
                    transferred += 1
                    addr = (addr + (-1 if mode & 0x20 else 1)) % 65536
                    terminal[ch] = count == 0
                    count = (count - 1) % 65536
                    if terminal[ch]:
                        terminal_events += 1
                        status_tc = True
                        if mode & 0x10:
                            addr, count = row["address"], row["count"]
                        else:
                            masked = True
                require(actual == dict(batch=batch_index, channel=ch, write=write, time=batch["start"] + (attempt + 1) * 100000,
                                       result=result, value=observed, terminalCount=terminal[ch]), f"device result/deadline at case {index} attempt {attempt}")
                beat_index += 1
            transfers += transferred
            blocked_total += blocked
            invalid_total += invalid
            batch_index += 1
        mask = (original_mask & 1) | (0xE if masked else (0xE & ~(1 << (ch - 4))))
        expected = [index, addr, count, (window >> 12) | row["odd"], mask,
                    (1 << (ch - 4)) if status_tc else 0, 0, transferred, blocked, invalid, value] + [data[p] for p in positions]
        actual_words = list(struct.unpack_from(f"<{record_words}H", proof, 32 + index * record_words * 2))
        require(actual_words == expected, f"actual guest DMA registers/counts/data/guards in case {index}: {actual_words[:11]} vs {expected[:11]}")
    require(len(batches) == batch_index and len(beats) == beat_index, "extra or missing DMA work")
    # Actual guest programming must be present. No host setup of controller or sample memory exists.
    io = report["io"]
    signature_index = next(i for i, x in enumerate(io) if x["port"] == 0x300 and not x["write"] and x["value"] == 0xD6)
    original_mask_read = next(x for x in io[signature_index + 1:] if x["port"] == 0xDE and not x["write"])
    require(original_mask == original_mask_read["value"] & 15, "actual original high-controller mask")
    writes = [(x["port"], x["value"], x["width"]) for x in io if x["write"]]
    require(sum(p == 0x300 for p, _, _ in writes) == batch_index, "device start I/O count")
    for port in (0xC4, 0xC6, 0xC8, 0xCA, 0xCC, 0xCE):
        per_channel = sum(r["channel"] == 5 for r in cases)
        require(sum(p == port and width == 1 for p, _, width in writes) == 2 * per_channel, "word-register programming must use two bytes")
    for channel, port in ((5, 0x8B), (6, 0x89), (7, 0x8A)):
        # The monitor preserves the guest-visible latch value but removes its non-address bit
        # before forwarding the physical port write. Raw DOS exercises both latch bit values.
        expected = [(window >> 12) | (0 if managed else r["odd"]) for r in cases if r["channel"] == channel]
        require([v for p, v, width in writes if p == port and width == 1] == expected, "page latch programming")
    return dict(passed=True, cases=len(cases), batches=batch_index, deviceDeadlines=beat_index, transferredWords=transfers,
                readWords=read_words, writtenWords=write_words, blockedRequests=blocked_total, invalidRequests=invalid_total,
                terminalEvents=terminal_events, checkedSnapshotWords=len(cases) * len(positions),
                proofSha256=hashlib.sha256(proof).hexdigest().upper(), testFixtureIsSoundBlaster=False,
                actualEmm386=managed, balancedVdsWordChannels=managed)


def main():
    """Runs the explicit dma16 preparation/check command and reports its reproducible evidence."""
    parser = argparse.ArgumentParser()
    for name in ("vectors", "report", "memory", "program"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--negative-controls", action="store_true")
    args = parser.parse_args()
    v = json.loads(Path(args.vectors).read_text(encoding="utf-8-sig"))
    r = json.loads(Path(args.report).read_text(encoding="utf-8-sig"))
    m = Path(args.memory).read_bytes()
    p = Path(args.program).read_bytes()
    result = verify(v, r, m, p)
    if args.negative_controls:
        mutations = []
        for field in ("time", "value", "batch", "channel", "terminalCount", "result"):
            for i in (0, 3, len(r["fixture"]["beats"]) - 1):
                bad = copy.deepcopy(r)
                x = bad["fixture"]["beats"][i]
                x[field] = ("InvalidMode" if x[field] != "InvalidMode" else "Blocked") if field == "result" else (not x[field] if field == "terminalCount" else x[field] + 1)
                mutations.append((bad, m))
        s, o = (int(x, 16) for x in re.search(r"DMA16-PROOF=([0-9A-F]{4}):([0-9A-F]{4})", r["status"]["debugOutput"]).groups())
        base = s * 16 + o
        footer = 32 + 2 * (11 + len(v["positions"])) * len(v["cases"])
        offsets = (0, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54)
        offsets += tuple(footer + i for i in (0, 8, 10, 12, 14, 16, 18, 20, 24, 28, 30, 32, 36))
        for offset in offsets:
            bad = bytearray(m)
            bad[base + offset] ^= 1
            mutations.append((r, bad))
        rejected = 0
        for mutation_index, (badr, badm) in enumerate(mutations):
            try:
                verify(v, badr, badm, p)
            except (ValueError, KeyError, IndexError):
                rejected += 1
            else:
                raise ValueError(f"Oracle accepted intentional corruption {mutation_index}")
        result["negativeControlsRejected"] = rejected
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
