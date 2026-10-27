# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Independent guest/DMA/IRQ and time-integrated stereo PCM oracle. No emulator modules are imported."""
import argparse
import array
import copy
import hashlib
import json
import math
import re
import struct
import sys
import wave
from pathlib import Path


def check(value, message):
    """Validates sb16 evidence from actual guest state, immutable program bytes and independently expected results."""
    if not value:
        raise ValueError(message)


def sha(data):
    """Computes an uppercase SHA-256 identity for immutable input/evidence comparisons."""
    return hashlib.sha256(data).hexdigest().upper()


def signed16(value):
    """Interprets a raw sixteen-bit PCM component as a signed integer."""
    return (value + 32768) % 65536 - 32768


def signed_sample(raw, bits, signed):
    """Converts the selected signed/unsigned byte/word component into the independent PCM amplitude."""
    if bits == 16:
        return signed16(raw ^ (0 if signed else 0x8000))
    return ((raw if raw < 128 else raw - 256) if signed else raw - 128) * 256


def initial_buffer(case):
    """Constructs the independent expected initial DMA data, including guard regions."""
    if case["bits"] == 16:
        return bytearray(b"".join(struct.pack("<H", (i * 4093 + (case["id"] + 1) * 0x1234) % 65536) for i in range(64)))
    return bytearray((i * 37 + (case["id"] + 1) * 13) % 256 for i in range(128))


def commands(io, port):
    """Splits actual DSP port writes into complete commands and their defined parameter bytes."""
    result, parameters, command, expected = [], [], None, 0
    for row in io:
        if not row["write"] or row["port"] != port + 12:
            continue
        check(row["width"] == 1, "DSP writes must be bytes")
        value = row["value"]
        if command is None:
            command, parameters = value, []
            expected = 3 if 0xB0 <= value <= 0xCF else 2 if value in (0x41, 0x42) else 0
        else:
            parameters.append(value)
        if len(parameters) == expected:
            result.append(dict(command=command, parameters=parameters, time=row["time"] + 1000))
            command = None
    check(command is None, "partial DSP packet")
    return result


def pcm_reference(events, io, port, frames):
    # Independent sample-and-hold integration. Hardware gain is applied to each interval before
    # the host's documented 20-Hz DC filter; float32 and PCM16 rounding are separate steps.
    """Reconstructs expected stereo PCM from actual component timing, mixer changes and the documented DC filter."""
    changes = []
    for index, event in enumerate(events):
        if event["kind"] == "sample" and not event["sample"]["recording"]:
            changes.append((event["sample"]["time"], 1, index, "sample", event["sample"]))
    selected = 0
    for index, row in enumerate(io):
        if not row["write"]:
            continue
        if row["port"] == port + 4:
            selected = row["value"]
        elif row["port"] == port + 5 and selected in (0x30, 0x31, 0x32, 0x33, 0x41, 0x42):
            changes.append((row["time"], 0, index, "gain", (selected, row["value"])))
        elif row["port"] == port + 6 and row["value"] & 1:
            changes.append((row["time"], 0, index, "reset", None))
    changes.sort()
    values, volume, previous, filtered = [0, 0], [24, 24, 24, 24, 0, 0], [0.0, 0.0], [0.0, 0.0]
    gains, channels, position, index = [10 ** (-28 / 20)] * 2, 1, 0, 0
    # Transfer-start events set mono/stereo interpretation for subsequent DAC samples.
    for n, event in enumerate(events):
        if event["kind"] == "start":
            changes.append((event["transfer"]["time"], 0, len(io) + n, "format", event["transfer"]["stereo"]))
    changes.sort()
    decay = math.exp(-2 * math.pi * 20 / 48000)
    output = array.array("h")
    f32 = lambda x: struct.unpack("<f", struct.pack("<f", x))[0]
    for frame in range(frames):
        end = ((frame + 1) * 1_000_000_000 + 47999) // 48000
        area = [0.0, 0.0]
        begin = position
        while position < end:
            stop = min(end, changes[index][0]) if index < len(changes) else end
            check(stop >= position, "unordered audio state changes")
            for side in (0, 1):
                area[side] += (stop - position) * values[side] * gains[side]
            position = stop
            while index < len(changes) and changes[index][0] == position:
                _, _, _, kind, data = changes[index]
                if kind == "format":
                    channels = 2 if data else 1
                elif kind == "reset":
                    values = [0, 0]
                elif kind == "sample":
                    if channels == 1:
                        values = [data["value"], data["value"]]
                    else:
                        values[int(data["right"])] = data["value"]
                elif kind == "gain":
                    register, value = data
                    slot = {0x30: 0, 0x31: 1, 0x32: 2, 0x33: 3, 0x41: 4, 0x42: 5}[register]
                    volume[slot] = value >> (6 if slot >= 4 else 3)
                    gains = [10 ** ((2 * (volume[s] - 31) + 2 * (volume[s + 2] - 31) + 6 * volume[s + 4]) / 20) for s in (0, 1)]
                index += 1
        for side in (0, 1):
            average = area[side] / ((end - begin) * 32768)
            filtered[side] = decay * (filtered[side] + average - previous[side])
            previous[side] = average
            sample = f32(max(-1.0, min(1.0, filtered[side] * 0.5)))
            output.append(round(f32(sample * 32767)))
    return output


def verify(vectors, report, memory, program, events, io, irqs, pcm, expected_pcm=None):
    """Validates sb16 evidence from actual guest state, immutable program bytes and independently expected results."""
    check(len(memory) == 8 * 1048576 and report["status"]["fault"] is None and report["hardwareResetCount"] == 0, "guest RAM/CPU/reset")
    check(report["scenario"]["complete"] and report["scenario"]["inputBytesSent"] == report["scenario"]["inputBytesTotal"], "incomplete input")
    check(report["ataSectorsWritten"] == 0 and report["floppySectorsWritten"] == 0, "guest media writes")
    debug = report["status"]["debugOutput"]
    check("SB16 PCM probe returned" in debug and "SB16-END\r\n" in debug and "C:\\>" in report["text"], "DOS return")
    matches = re.findall(r"SB16-PROOF=([0-9A-F]{4}):([0-9A-F]{4})", debug)
    check(len(matches) == 1, "proof pointer")
    segment, offset = (int(x, 16) for x in matches[0])
    physical = segment * 16 + offset
    check(memory[physical:physical + 8] == b"SB16PCM1", "proof magic")
    header = list(struct.unpack_from("<26H", memory, physical + 8))
    (version, count, port, irq, dma8, dma16, allocation, window, owner, size, completed, error, dsp,
     irq_resource, dma_resource, irq_total, old_ip, old_cs, masks, joint, after8, after16, locks, unlocks, disables, enables) = header
    check((version, count, completed, error, dsp) == (1, len(vectors), len(vectors), 0, 0x405), "header/completion/DSP version")
    config = report["status"]["configuration"]
    check((port, irq, dma8, dma16) == tuple(config[k] for k in ("soundBlasterBasePort", "soundBlasterIrq", "soundBlasterDmaChannel", "soundBlasterDma16Channel")), "physical profile")
    check(config["soundCard"] == "sb16-ct1740-dsp405-development", "concrete CT1740 model")
    check(owner == segment and size == 0x4000 and allocation <= window and window % 0x2000 == 0 and
          window + 0x2000 <= allocation + size and window * 16 + 0x20000 < 0xA0000, "DOS-owned aligned buffer/MCB")
    check(irq_resource == {9: 1, 5: 2, 7: 4, 10: 8}[irq] and dma_resource == (1 << dma8 | 1 << dma16), "mixer resources")
    check((joint, after8, after16, irq_total) == (3, 2, 0, 18), "actual shared IRQ and independent acknowledgments")
    managed = report["cpu"]["protectedMode"]
    if managed:
        check(report["cpu"]["virtual8086Entries"] > 0 and report["cpu"]["virtual8086Instructions"] > 0 and report["cpu"]["cr3"] != 0, "actual V86 monitor")
    check((locks, unlocks, disables, enables) == ((1, 1, 2, 2) if managed else (0, 0, 0, 0)), "VDS lock/translation balance")
    dds = struct.unpack_from("<IIHHI", memory, physical + 60)
    check(dds == (0x20000, 0, window, 0, window * 16 if managed else 0), "actual VDS identity buffer")
    check(struct.unpack_from("<H", memory, physical + 76)[0] == 0xBEEF and
          struct.unpack_from("<H", memory, physical + 78 + len(vectors) * 160)[0] == 0xFACE, "proof guards")
    vector = irq + 8 if irq < 8 else irq + 0x68
    check(struct.unpack_from("<HH", memory, vector * 4) == (old_ip, old_cs), "original interrupt vector restored")
    for target, value in ((0x21, masks & 255), (0xA1, masks >> 8)):
        check([x["value"] for x in io if x["write"] and x["port"] == target][-1] == value, "PIC mask restoration")
    check(len(irqs) == 18 and all(x["kind"] == "hardware" and x["irq"] == irq and not x["spurious"] for x in irqs), "physical IRQ acknowledgments")
    for delivery in irqs:
        # EMM386 remaps the PIC. CPU vector 0Dh can instead be #GP; it is not an IRQ5 oracle.
        base, pending_icw2 = (8 if irq < 8 else 0x70), False
        command_port, data_port = (0x20, 0x21) if irq < 8 else (0xA0, 0xA1)
        for row in io:
            if row["time"] > delivery["time"]:break
            if row["write"] and row["port"] == command_port and row["value"] & 0x10:pending_icw2=True
            elif row["write"] and row["port"] == data_port and pending_icw2:base=row["value"] & 0xF8;pending_icw2=False
        check(delivery["vector"] == base + (irq & 7), "actual remapped PIC vector")
    code = memory[segment * 16 + 0x100:segment * 16 + 0x100 + len(program)]
    mutable = program.index(b"SB16-PROOF=") - 18
    proof_at = program.index(b"SB16PCM1")
    check(code[:mutable] == program[:mutable] and code[mutable + 18:proof_at] == program[mutable + 18:proof_at], "guest executable/table bytes")
    packets = commands(io, port)
    starts = [x["transfer"] for x in events if x["kind"] == "start"]
    streams = []
    for event in events:
        if event["kind"] == "start":
            streams.append(dict(start=event["transfer"], samples=[], blocks=[]))
        elif event["kind"] == "sample":
            check(streams, "sample without start")
            streams[-1]["samples"].append(event["sample"])
        elif event["kind"] == "block":
            check(streams, "block without start")
            streams[-1]["blocks"].append(event)
        else:
            raise ValueError("unknown trace event")
    pcm_packets = [x for x in packets if 0xB0 <= x["command"] <= 0xCF]
    check(len(streams) == len(vectors) + 2 and len(pcm_packets) == len(streams), "actual DSP stream count")
    for stream, packet in zip(streams, pcm_packets):
        check(stream["start"]["time"] == packet["time"], "DMA began before/after command completion")
    output_count = capture_count = byte_grants = word_grants = 0
    for index, case in enumerate(vectors):
        stream = streams[index]
        start, samples = stream["start"], stream["samples"]
        expected_start = dict(time=pcm_packets[index]["time"], rate=case["rate"], word=case["bits"] == 16, stereo=case["stereo"],
                              signed=case["signed"], recording=case["recording"], autoInit=case["auto"], fifoRequested=bool(case["command"] & 2),
                              samples=case["samples"], dmaChannel=dma16 if case["bits"] == 16 and not case["fallback"] else dma8, periodNanoseconds=0)
        check(start == expected_start and pcm_packets[index]["parameters"] == [case["mode"], (case["samples"]-1)&255, (case["samples"]-1)>>8], "DSP rate/format/count/resource")
        check(len(samples) == case["samples"] * case["blocks"] and len(stream["blocks"]) == case["blocks"], "sample/block counts")
        data = initial_buffer(case)
        unit = case["bits"] // 8
        for n, sample in enumerate(samples):
            time = start["time"] + (n // (2 if case["stereo"] else 1) + 1) * 1_000_000_000 // case["rate"]
            right = case["stereo"] and bool(n % 2)
            at = (n % case["bufferSamples"]) * unit
            if case["recording"]:
                signal = signed16((time // 125000 * 17 + (1000 if right else 0)) % 65536)
                raw = (signal % 65536) ^ (0 if case["signed"] else 0x8000) if unit == 2 else ((signal >> 8) + (0 if case["signed"] else 128)) % 256
                data[at:at+unit] = raw.to_bytes(unit, "little")
                capture_count += 1
            else:
                raw = int.from_bytes(data[at:at+unit], "little")
                output_count += 1
            check(sample == dict(time=time, word=unit==2, recording=case["recording"], right=right, raw=raw,
                                 value=signed_sample(raw, case["bits"], case["signed"]), dmaResult="Transferred"), f"independent sample {index}:{n}")
        for n, block in enumerate(stream["blocks"]):
            check(block == dict(kind="block",time=samples[(n+1)*case["samples"]-1]["time"],word=unit==2), "IRQ block deadline")
        physical_word = unit == 2 and not case["fallback"]
        transferred = len(samples) if physical_word else len(samples) * unit
        dma_length = case["bufferSamples"] if physical_word else case["bufferSamples"] * unit
        after = transferred % dma_length if case["auto"] else transferred
        address = (0x1000 if physical_word else 0x2000) + after
        count_left = (dma_length - after - 1) & 65535
        status_bit = (1 << (dma16 - 4)) if physical_word else (1 << dma8)
        bit = 2 if unit == 2 else 1
        volumes = [0xF8, 0xE8 if case["asymmetricGain"] else 0xF8, 0xF8, 0xD8 if case["asymmetricGain"] else 0xF8]
        expected = [index, case["blocks"], bit, bit, 0, bit, address, count_left, status_bit, 0] + volumes + [0xA55A, 0x5AA5]
        actual = list(struct.unpack_from("<16H", memory, physical + 78 + index * 160))
        check(actual == expected, f"guest DMA/mixer/IRQ snapshot case {index}: {actual} != {expected}")
        check(memory[physical + 110 + index * 160:physical + 238 + index * 160] == data, f"guest sample buffer/guards case {index}")
        if physical_word: word_grants += transferred
        else: byte_grants += transferred
    # The pair after the matrix deliberately coalesces two sources into one real handler.
    for n, stream in enumerate(streams[-2:]):
        check(len(stream["samples"]) == len(stream["blocks"]) == 1 and stream["start"]["samples"] == 1 and
              stream["start"]["word"] == bool(n) and stream["start"]["rate"] == 8000, "joint source setup")
        sample = stream["samples"][0]
        data = initial_buffer(vectors[-1])
        raw = data[0] if n == 0 else int.from_bytes(data[:2], "little")
        check(sample["time"] == stream["start"]["time"] + 125000 and sample["raw"] == raw and not sample["recording"] and
              sample["value"] == signed_sample(raw,16 if n else 8,bool(n)), "joint real DMA payload")
    sb = report["sb16"]
    check((sb["transferredSamples"], sb["recordedSamples"], sb["convertedSamples"], sb["dma8Grants"], sb["dma16Grants"]) ==
          (output_count+2, capture_count, capture_count, byte_grants+1, word_grants+1), "card transfer counters")
    check((sb["completedBlocks"],sb["irq8Requests"],sb["irq16Requests"]) == (19,8,11), "card IRQ counters")
    check(not sb["active"] and not sb["paused"] and sb["remainingSamples"]==0 and sb["interruptSources"]==0 and
          sb["dacLeft"]==sb["dacRight"]==0 and sb["resetCount"]==2, "final stopped/reset state")
    check(all(sb[k]==0 for k in ("rejectedWrites","rejectedParameters","unsupportedCommands","unsupportedMixerAccesses","rejectedResourceWrites")), "unexpected DSP/mixer access")
    check(not any(sb[k] for k in ("hostMicrophone","mpu401","csp","fifoBusTiming","fullCardCompatibility")), "hardware scope")
    if sb["opl3"]:
        check(report["opl3"]["keyOnCount"] == 0 and report["opl3"]["activeOperators"] == 0 and
              report["opl3"]["registerWrites"] == 0, "unexpected FM in PCM-only probe")
    check(len(pcm)%4==0 and sha(pcm)==report["audio"]["dataSha256"], "WAV payload checksum")
    frames=len(pcm)//4
    check(frames==report["audio"]["recordedFrames"]==report["audio"]["generatedFrames"]==report["status"]["virtualNanoseconds"]*48000//1_000_000_000, "WAV virtual clock")
    actual_pcm=array.array("h",pcm)
    if sys.byteorder!="little":actual_pcm.byteswap()
    if expected_pcm is None:expected_pcm=pcm_reference(events,io,port,frames)
    check(len(actual_pcm)==len(expected_pcm), "independent PCM frame length")
    differences=[abs(a-b) for a,b in zip(actual_pcm,expected_pcm)]
    check(max(differences,default=0)<=1,"independent stereo integration/filter/rounding")
    check(any(actual_pcm[i]!=actual_pcm[i+1] for i in range(0,len(actual_pcm),2)),"stereo collapsed to mono")
    return dict(passed=True,cases=len(vectors),actualHandlerReturns=irq_total,logicalBlockInterrupts=19,
                realEmm386=managed,outputSamples=output_count+2,inputSamples=capture_count,dma8Grants=byte_grants+1,dma16Grants=word_grants+1,
                independentStereoFrames=frames,pcmMaximumError=max(differences,default=0),pcmSha256=sha(pcm),
                proofSha256=sha(memory[physical:physical+80+len(vectors)*160]), fullCardCompatibility=False),expected_pcm


def main():
    """Runs the explicit sb16 preparation/check command and reports its reproducible evidence."""
    parser=argparse.ArgumentParser()
    for name in ("vectors","report","memory","program","trace","io","irq","wave"):
        parser.add_argument("--"+name,required=True)
    parser.add_argument("--negative-controls",action="store_true")
    args=parser.parse_args()
    load=lambda p:json.loads(Path(p).read_text(encoding="utf-8-sig"))
    lines=lambda p:[json.loads(s) for s in Path(p).read_text(encoding="utf-8-sig").splitlines()]
    v,r=load(args.vectors),load(args.report);m,p=Path(args.memory).read_bytes(),Path(args.program).read_bytes()
    e,io,irq=lines(args.trace),lines(args.io),lines(args.irq)
    with wave.open(args.wave,"rb") as wav:
        check(wav.getparams()[:3]==(2,2,48000),"WAV format")
        pcm=wav.readframes(wav.getnframes())
    result,expected=verify(v,r,m,p,e,io,irq,pcm)
    if args.negative_controls:
        mutations=[]
        for key in ("transferredSamples","recordedSamples","dma8Grants","dma16Grants","irq8Requests","irq16Requests","unsupportedCommands"):
            bad=copy.deepcopy(r);bad["sb16"][key]+=1;mutations.append((bad,m,e,pcm))
        for key in ("raw","value","time","right"):
            bad=copy.deepcopy(e);x=next(x["sample"] for x in bad if x["kind"]=="sample")
            x[key]=not x[key] if key=="right" else x[key]+1;mutations.append((r,m,bad,pcm))
        seg,off=(int(s,16) for s in re.search(r"SB16-PROOF=([0-9A-F]{4}):([0-9A-F]{4})",r["status"]["debugOutput"]).groups());base=seg*16+off
        for offset in (0,8,10,12,14,16,18,22,24,26,28,30,32,34,38,46,48,50,52,54,56,58,60,64,68,70,72,76,78,80,82,84,86,88,90,92,94,96,98,100,102,104,106,108,110,112,78+160*len(v)):
            bad=bytearray(m);bad[base+offset]^=1;mutations.append((r,bad,e,pcm))
        data=array.array("h",pcm)
        for kind in ("left-zero","right-zero","swap"):
            bad=array.array("h",data)
            for i in range(0,len(bad),2):
                if kind=="left-zero":bad[i]=0
                elif kind=="right-zero":bad[i+1]=0
                else:bad[i],bad[i+1]=bad[i+1],bad[i]
            payload=bad.tobytes();badr=copy.deepcopy(r);badr["audio"]["dataSha256"]=sha(payload)
            mutations.append((badr,m,e,payload))
        for index,(badr,badm,bade,badpcm) in enumerate(mutations):
            try:verify(v,badr,badm,p,bade,io,irq,badpcm,expected)
            except (ValueError,IndexError,KeyError,struct.error):pass
            else:raise ValueError(f"Oracle accepted mutation {index}")
        result["negativeControlsRejected"]=len(mutations)
    print(json.dumps(result,indent=2))


if __name__=="__main__":main()
