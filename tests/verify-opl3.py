# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent DOS/YMF262 functional, quad-routing and stereo integration oracle.

YMFM is a separate executable. It is never an emulator execution backend.
Comparisons bound selected functions; they do not assert equal die waveforms.
"""
import argparse
import array
import cmath
import copy
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import subprocess
import sys
import wave

MASTER, DIVIDER, HOST = 14318180, 288, 48000


def check(value, why):
    """Validates opl3 evidence from actual guest state, immutable program bytes and independently expected results."""
    if not value:
        raise AssertionError(why)


def load(path):
    """Loads the explicit report, memory and program evidence without modifying its files."""
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def samples(raw, code):
    """Reads explicitly timed native reference frames without invoking that backend in the emulator."""
    result = array.array(code)
    result.frombytes(raw)
    if sys.byteorder != "little":
        result.byteswap()
    return result


def sha(raw):
    """Computes an uppercase SHA-256 identity for immutable input/evidence comparisons."""
    return hashlib.sha256(raw).hexdigest().upper()


def rms(values):
    """Measures RMS over the explicit captured sample window, independently from the synthesizer."""
    return math.sqrt(sum(x*x for x in values) / len(values))


def pitch(values):
    """Estimates signal frequency from the captured waveform independently from the chip's phase accumulator."""
    edges = [i for i in range(1, len(values)) if values[i-1] < 0 <= values[i]]
    check(len(edges) > 8, "missing tonal periods")
    return (len(edges)-1) * (MASTER / DIVIDER) / (edges[-1]-edges[0])


def harmonics(values, hz):
    """Measures normalized waveform harmonic amplitudes independently from internal operator state."""
    result = []
    for harmonic in range(1, 9):
        step = cmath.exp(-2j * math.pi * harmonic * hz / (MASTER / DIVIDER))
        phase, total = 1+0j, 0j
        for value in values:
            total += phase * value
            phase *= step
        result.append(2 * abs(total) / len(values))
    scale = math.sqrt(sum(x*x for x in result))
    return [x/scale for x in result]


def verify(args):
    """Validates opl3 evidence from actual guest state, immutable program bytes and independently expected results."""
    report = load(args.report)
    rows = load(args.vectors)
    io = [json.loads(line) for line in args.io.read_text(encoding="utf-8-sig").splitlines()]
    ram, program = args.memory.read_bytes(), args.program.read_bytes()
    raw = args.native.read_bytes()
    with wave.open(str(args.audio), "rb") as wav:
        check((wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (2, 2, HOST), "WAV format")
        pcm = wav.readframes(wav.getnframes())
    state, dsp = report["opl3"], report["sb16"]
    base = dsp["ioBasePort"]
    end_time = report["status"]["virtualNanoseconds"]
    native_count = end_time * MASTER // (DIVIDER * 1_000_000_000)
    frame_count = end_time * HOST // 1_000_000_000

    def check_report(r):
        """Checks report completion, actual device counters and mode consistency independently from the guest success marker."""
        s, d = r["opl3"], r["sb16"]
        check(r["status"]["fault"] is None and r["scenario"]["complete"], "guest fault/scenario")
        check(r["scenario"]["inputBytesSent"] == r["scenario"]["inputBytesTotal"], "guest input unfinished")
        check(r["ataSectorsWritten"] == r["floppySectorsWritten"] == 0, "guest wrote media")
        check(re.search(r"\r\nOPL3-RUN-END\r\n(?:\r\n)?C:\\>$", r["status"]["debugOutput"]), "DOS did not return")
        check(s["model"] == "YMF262 / 4MF262A6 functional reference", "chip model")
        check(s["masterFrequency"] == MASTER and s["clocksPerSample"] == DIVIDER, "chip clock")
        check(s["generatedSamples"] == s["nativeCaptureFrames"] == native_count, "native frame counter")
        check(s["keyOnCount"] == 194, "actual operator key edges")
        check(s["registerWrites"] == len(registers), "register write counter")
        check(s["rejectedWrites"] == s["unsupportedWrites"] == s["ignoredBankWrites"] == 0, "unsupported traffic")
        check(s["compatibilityAliases"] == 5, "upper-bank compatibility aliases")
        check(s["newMode"] and s["fourOperatorMask"] == 0 and not s["rhythmEnabled"] and s["activeOperators"] == 0, "final chip state")
        check(s["timer1Overflows"] == s["timer2Overflows"] == 1 and not s["timerInterruptAsserted"], "timer contract")
        check(s["leftMixerGain"] == s["rightMixerGain"] == 1, "final FM gains")
        check(not s["csm"] and not s["siliconExact"] and not s["physicalDacModel"], "chip scope")
        check(d["resetCount"] == 3 and d["dacLeft"] == d["dacRight"] == 0, "DSP reset/coexistence")
        check(d["transferredSamples"] == d["dma8Grants"] == d["dma16Grants"] == d["completedBlocks"] == d["interruptSources"] == 0, "unexpected DMA")
        check(not any(d[k] for k in ("hostMicrophone", "mpu401", "csp", "fullCardCompatibility")), "card scope")
        check(d["rejectedWrites"] == d["unsupportedCommands"] == d["unsupportedMixerAccesses"] == 0, "DSP diagnostics")
        if args.manager:
            check(r["cpu"]["virtual8086Instructions"] > 1000 and r["cpu"]["pageWalks"] > 0, "real EMM386 missing")

    def port_offset(port):
        """Normalizes an actual board FM alias to its shared address/data register offset."""
        if 0x388 <= port <= 0x38b:
            return port - 0x388
        if base <= port < base + 4:
            return port - base
        if base+8 <= port < base+10:
            return port - base - 8
        return None

    fm = [e for e in io if e["write"] and port_offset(e["port"]) is not None]
    check(all(e["width"] == 1 for e in fm), "non-byte FM traffic")
    registers, address, last, new_mode = [], None, -1, False
    for e in fm:
        check(e["time"] >= last + 750 or last < 0, "ISA FM byte timing")
        last = e["time"]
        off = port_offset(e["port"])
        if off % 2 == 0:
            address = e["value"] | (256 if off == 2 and (new_mode or e["value"] == 5) else 0)
        else:
            check(address is not None, "data before register address")
            registers.append((e["time"], address, e["value"]))
            if address == 0x105:
                new_mode = bool(e["value"] & 1)
    check({port_offset(e["port"]) for e in fm} >= {0, 1, 2, 3}, "missing physical bank paths")
    check(any(e["port"] == base+8 for e in fm) and any(e["port"] == base+2 for e in fm), "missing board aliases")
    check_report(report)
    marker = re.search(r"OPL3-PROOF=([0-9A-F]{4}):([0-9A-F]{4})", report["status"]["debugOutput"])
    check(marker is not None, "missing RAM proof")
    segment, proof_offset = (int(x, 16) for x in marker.groups())
    proof_address = segment * 16 + proof_offset
    immutable = program.index(b"OPL3-PROOF=") - 3
    proof_file = program.index(b"OPL3P001")
    check(proof_offset == proof_file + 256, "proof address not in program")

    def check_ram(memory):
        """Validates executed program/table bytes, guest proof buffers and guard regions through the actual reported memory mapping."""
        check(memory[segment*16+256:segment*16+256+immutable] == program[:immutable], "program/table changed")
        check(memory[proof_address:proof_address+8] == b"OPL3P001", "RAM signature")
        h = struct.unpack_from("<13H", memory, proof_address+8)
        check(h[:10] == (1, 74, base, 74, 0, 0x0405, 0, 0xc0, 0xa0, 0), "guest status/timer proof")
        check(h[10] == h[11] and h[12] == 0xbeef, "PIC masks/guard")

    check_ram(ram)
    chars = [e for e in io if e["write"] and e["port"] == 0xe9]
    text = "".join(chr(e["value"]) for e in chars)

    def marker_time(marker_text):
        """Locates the virtual time of the requested actual guest debug-port marker."""
        check(text.count(marker_text) == 1, "marker duplicated/missing")
        return chars[text.index(marker_text) + len(marker_text)-1]["time"]

    windows = []
    for row in rows:
        start = marker_time(f'OPL3-CASE={row["id"]:02X}')
        end = marker_time(f'OPL3-END={row["id"]:02X}')
        check(100_000_000 < end-start < 250_000_000, "guest did not wait real BIOS ticks")
        check(not windows or start > windows[-1][1], "case order")
        windows.append((start, end))
    operator_offsets = (0, 3, 1, 4, 2, 5, 8, 11, 9, 12, 10, 13, 16, 19, 17, 20, 18, 21)

    def check_descriptors(descriptors):
        """Compares the guest's case descriptors and original program/table bytes against the independent vector file."""
        check(len(descriptors) == 74 and [r["id"] for r in descriptors] == list(range(74)), "case descriptors")
        cursor, state_ = 0, {}
        for row, (start, _) in zip(descriptors, windows):
            while cursor < len(registers) and registers[cursor][0] < start:
                _, a, v = registers[cursor]
                state_[a] = v
                cursor += 1
            check(state_[0x105] == (0 if row["kind"] == 5 else 1), "guest NEW mode")
            check(state_[0x104] == (1 << row["pair"] if row["kind"] == 2 else 0), "guest pair selection")
            if row["kind"] == 4:
                check(state_[0xbd] == 0x20 | row["drum"], "guest drum selection")
                continue
            ch = row["channel"]
            bank, local = (256 if ch >= 9 else 0), ch % 9
            check(state_[bank+0xa0+local] | ((state_[bank+0xb0+local] & 3) << 8) == row["fnum"], "guest F-number")
            check((state_[bank+0xb0+local] >> 2) & 7 == row["block"], "guest octave block")
            cnt = (row["algorithm"] >> 1) & 1 if row["kind"] == 2 else 0
            check(state_[bank+0xc0+local] == (row["routing"] << 4) | cnt, "guest routing/connection")
            if row["kind"] == 2:
                check(state_[bank+0xc3+local] & 1 == row["algorithm"] & 1, "second CNT")
                slot = ch*2 + row["solo"] % 2 + (6 if row["solo"] >= 2 else 0)
            else:
                slot = ch*2+1
            op = (256 if slot >= 18 else 0) + operator_offsets[slot % 18]
            check(state_[0x40+op] == 0 and state_[0xe0+op] == row["wave"], "guest audible source/wave")

    check_descriptors(rows)
    events = args.report.with_suffix(".ymfm-events.txt")
    events.write_text("".join(f'{e["time"]} {port_offset(e["port"])} {e["value"]}\n' for e in fm), encoding="ascii")
    reference = args.report.with_suffix(".ymfm.private.s32")
    subprocess.run([str(args.reference_exe), str(events), str(reference), str(end_time)], check=True)
    chip_reference = samples(reference.read_bytes(), "i")
    check(len(chip_reference) == native_count*4, "independent native frame count")

    def interval(values, channel, start, end):
        """Selects captured signal frames by exact virtual timestamps and the documented source sample clock."""
        first = (start+25_000_000)*MASTER//(DIVIDER*1_000_000_000)
        last = (end-10_000_000)*MASTER//(DIVIDER*1_000_000_000)
        return values[first*4+channel:last*4+channel:4]

    comparison = []

    def check_native(bytes_, details=False):
        """Compares actual guest-driven FM output with the isolated YMFM reference under the documented tolerances."""
        check(len(bytes_) == native_count*8, "raw quad length")
        values = samples(bytes_, "h")
        for row, (start, end) in zip(rows, windows):
            entries = []
            for channel in range(4):
                av, bv = interval(values, channel, start, end), interval(chip_reference, channel, start, end)
                ar, br = rms(av), rms(bv)
                mask = 3 if row["kind"] in (4, 5, 6) else row["routing"]
                check(ar > 100 if mask & (1 << channel) else ar == 0, "intended output mask")
                if br == 0:
                    check(ar == 0, f'quad output mask case {row["id"]}/{channel}')
                else:
                    check(abs(ar/br-1) < .05, f'native relative level case {row["id"]}/{channel}: {ar/br}')
                    if row["kind"] in (0, 1, 2, 6, 7):
                        check(abs(pitch(av)/pitch(bv)-1) < .005, "native pitch")
                        target = row["fnum"] * 2**(row["block"]-1) * (MASTER/DIVIDER) / 524288
                        if row["kind"] == 6 and channel == 1:
                            target *= 2
                        check(abs(pitch(av)/target-1) < .005, "mathematical pitch")
                    if row["kind"] in (3, 5):
                        hz = row["fnum"] * 2**(row["block"]-1) * (MASTER/DIVIDER) / 524288
                        ha, hb = harmonics(av, hz), harmonics(bv, hz)
                        check(max(abs(a-b) for a, b in zip(ha, hb)) < .08, "waveform harmonics")
                entries.append(dict(output=channel, rms=ar, referenceRms=br, ratio=ar/br if br else None))
            if details:
                comparison.append(dict(id=row["id"], kind=row["kind"], outputs=entries))
        return values

    native = check_native(raw, True)
    # Independent piecewise integration of the observed raw chip signal. Native
    # waveform validity is checked above against the separate engine. DSP and
    # CT1745 transitions are decoded solely from actual guest port traffic.
    mix = {x: 0xc0 for x in range(0x30, 0x36)}
    mix.update({0x41: 0, 0x42: 0})
    mixer_index = 0
    gains, dac, pending = [], [], None

    def gain(channel, source):
        """Derives the documented mixer gain from observed hardware register values."""
        db = 2*((mix[0x30+channel]>>3)-31) + 2*((mix[source+channel]>>3)-31) + 6*(mix[0x41+channel]>>6)
        return 10**(db/20)

    for e in io:
        if not e["write"]:
            continue
        p, v, t = e["port"], e["value"], e["time"]
        if p == base+4:
            mixer_index = v
        elif p == base+5 and mixer_index in mix:
            mix[mixer_index] = v & (0xc0 if mixer_index >= 0x41 else 0xf8)
            gains.append((t, [gain(c, 0x34) for c in range(2)], [gain(c, 0x32) for c in range(2)]))
        elif p == base+6 and v & 1:
            dac.append((t, 0))
            pending = None
        elif p == base+0xc:
            if pending == 0x10:
                dac.append((t+1000, (v-128)*256))
                pending = None
            else:
                pending = v
    check(any(g[1][0] != g[1][1] for g in gains), "guest did not change stereo FM gain")
    check(any(v != 0 for _, v in dac), "no concurrent direct DAC")
    expected_pcm = array.array("h")
    pos = native_index = gain_index = dac_index = 0
    native_next = (DIVIDER*1_000_000_000+MASTER-1)//MASTER
    fmgain = [10**(-28/20)]*2
    pcgain = fmgain[:]
    fmvalue, pcvalue = [0, 0], 0
    previous = [[0., 0.], [0., 0.]]
    filtered = [[0., 0.], [0., 0.]]
    decay = math.exp(-2*math.pi*20/HOST)
    f32 = lambda x: struct.unpack("<f", struct.pack("<f", x))[0]
    for frame in range(frame_count):
        end = ((frame+1)*1_000_000_000+HOST-1)//HOST
        start = pos
        areas = [[0., 0.], [0., 0.]]
        while pos < end:
            gain_next = gains[gain_index][0] if gain_index < len(gains) else end_time+1
            dac_next = dac[dac_index][0] if dac_index < len(dac) else end_time+1
            boundary = min(end, native_next, gain_next, dac_next)
            for c in range(2):
                areas[0][c] += (boundary-pos)*fmvalue[c]*fmgain[c]
                areas[1][c] += (boundary-pos)*pcvalue*pcgain[c]
            pos = boundary
            if pos == native_next:
                fmvalue = native[native_index*4:native_index*4+2]
                native_index += 1
                native_next = ((native_index+1)*DIVIDER*1_000_000_000+MASTER-1)//MASTER
            if pos == gain_next:
                _, fmgain, pcgain = gains[gain_index]
                gain_index += 1
            if pos == dac_next:
                pcvalue = dac[dac_index][1]
                dac_index += 1
        for c in range(2):
            mixed = 0.
            for source in range(2):
                value = areas[source][c] / ((end-start)*32768)
                filtered[source][c] = decay*(filtered[source][c]+value-previous[source][c])
                previous[source][c] = value
                mixed += filtered[source][c]*.5
            expected_pcm.append(round(f32(max(-1., min(1., mixed)))*32767))

    def check_pcm(payload):
        """Reconstructs host PCM from observed raw FM buses, timed gain changes and output filtering."""
        check(len(payload) == frame_count*4, "PCM frame count")
        actual = samples(payload, "h")
        largest = max(abs(a-b) for a, b in zip(actual, expected_pcm))
        check(largest <= 1, f'stereo integration error {largest}')
        return largest

    check(sha(pcm) == report["audio"]["dataSha256"], "WAV payload SHA")
    check(report["audio"]["recordedFrames"] == report["audio"]["generatedFrames"] == frame_count, "audio virtual clock")
    maximum_error = check_pcm(pcm)
    negatives = 0
    if args.negative_controls:
        def reject(function, value, name):
            """Requires an independently corrupted input to fail instead of accepting the guest's success marker alone."""
            nonlocal negatives
            try:
                function(value)
            except (AssertionError, ValueError, IndexError, KeyError, struct.error):
                negatives += 1
            else:
                raise AssertionError("oracle accepted mutation: " + name)
        for field in ("generatedSamples", "nativeCaptureFrames", "keyOnCount", "registerWrites", "rejectedWrites",
                      "unsupportedWrites", "ignoredBankWrites", "activeOperators", "fourOperatorMask",
                      "timer1Overflows", "timer2Overflows", "compatibilityAliases"):
            changed = copy.deepcopy(report)
            changed["opl3"][field] += 1
            reject(check_report, changed, field)
        for field in ("newMode", "rhythmEnabled", "timerInterruptAsserted", "csm", "siliconExact", "physicalDacModel"):
            changed = copy.deepcopy(report)
            changed["opl3"][field] = not changed["opl3"][field]
            reject(check_report, changed, field)
        for row_index, field in ((0, "channel"), (0, "routing"), (0, "fnum"), (0, "block"),
                                 (34, "pair"), (34, "algorithm"), (34, "solo"), (58, "wave")):
            changed = copy.deepcopy(rows)
            changed[row_index][field] += 1
            reject(check_descriptors, changed, "descriptor " + field)
        for offset in (0, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32):
            changed = bytearray(ram)
            changed[proof_address+offset] ^= 1
            reject(check_ram, changed, "RAM proof")
        changed = bytearray(ram)
        changed[segment*16+300] ^= 1
        reject(check_ram, changed, "program")
        for c in range(4):
            changed = samples(raw, "h")
            changed[c::4] = array.array("h", [0])*(len(changed)//4)
            reject(check_native, changed.tobytes(), f'quad {c} zero')
        reject(check_native, raw[:-8], "quad length")
        for swap in ((0, 1), (0, 2), (2, 3)):
            changed = samples(raw, "h")
            changed[swap[0]::4], changed[swap[1]::4] = changed[swap[1]::4], changed[swap[0]::4]
            reject(check_native, changed.tobytes(), "quad swapped")
        for c in range(2):
            changed = samples(pcm, "h")
            changed[c::2] = array.array("h", [0])*(len(changed)//2)
            reject(check_pcm, changed.tobytes(), "valid-format PCM channel zero")
        changed = samples(pcm, "h")
        changed[::2], changed[1::2] = changed[1::2], changed[::2]
        reject(check_pcm, changed.tobytes(), "valid-format PCM stereo swap")
    result = dict(passed=True, cases=74, nativeQuadFrames=native_count, stereoPcmFrames=frame_count,
                  maxPcm16Error=maximum_error, keyEdges=194, timer1=1, timer2=1,
                  independentYmfm=True, descriptorsAndMathematicalPitchVerified=True,
                  maximumRelativeLevelError=max(abs(x["ratio"]-1) for e in comparison for x in e["outputs"] if x["ratio"] is not None),
                  pcmSha256=sha(pcm), quadSha256=sha(raw), proofSha256=sha(ram[proof_address:proof_address+34]),
                  actualEmm386=report["cpu"]["virtual8086Instructions"] > 0,
                  negativeControlsRejected=negatives, siliconExact=False, fullCardCompatibility=False)
    args.report.with_suffix(".comparison.json").write_text(json.dumps(comparison, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("report", "vectors", "io", "audio", "native", "memory", "program", "reference-exe"):
        parser.add_argument("--"+name, required=True, type=Path)
    parser.add_argument("--negative-controls", action="store_true")
    parser.add_argument("--manager", action="store_true")
    verify(parser.parse_args())
