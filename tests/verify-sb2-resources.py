"""Check every resource probe's real FM port accesses, IRQ vector and PIC masks.

ADC bytes and complete PCM have their separate independent oracle. This adds the
relocated FM latch/timer path and restoration of both real PIC masks, without
using emulator libraries or DSP/OPL snapshots to calculate expectations.
"""
import json
from pathlib import Path
import sys

root = Path(sys.argv[1])
results = []
for address in (0x220, 0x240):
    for irq in (3, 5, 7, 9):
        summary = json.loads((root / f"a{address:03X}-i{irq}.json").read_text(encoding="utf-8-sig"))
        assert summary["passed"] and summary["basePort"] == address and summary["irq"] == irq
        for os in ("msdos", "freedos"):
            for signal in ("ramp", "midpoint"):
                for run in (1, 2):
                    prefix = Path(summary["directory"]) / f"{os}-{signal}-{run}"
                    trace = [json.loads(line) for line in Path(str(prefix)+".io.jsonl").read_text(encoding="utf-8-sig").splitlines()]
                    irq_trace = [json.loads(line) for line in Path(str(prefix)+".irq.jsonl").read_text(encoding="utf-8-sig").splitlines()]
                    adc = [json.loads(line) for line in Path(str(prefix)+".adc.jsonl").read_text(encoding="utf-8-sig").splitlines()]
                    verified = json.loads(Path(str(prefix)+".verified.json").read_text(encoding="utf-8-sig"))
                    assert verified["passed"]
                    fm = [entry for entry in trace if entry["port"] in (address+8,address+9,0x388,0x389)]
                    writes = [entry for entry in fm if entry["write"]]
                    wanted = [(address+8,4),(address+9,0x60),(address+8,2),(0x389,255),
                              (address+8,4),(address+9,0x21),(address+8,4),(address+9,0),(address+8,4),(address+9,0x80)]
                    assert [(entry["port"],entry["value"]) for entry in writes] == wanted
                    for index, (start,end) in enumerate(zip(writes,writes[1:])):
                        # Yamaha: >=12 masterclocks after address, >=84 after data.
                        clocks = 12 if index % 2 == 0 else 84
                        assert (end["time"]-start["time"])*3579545 >= clocks*1000000000
                    assert any(entry["port"] == 0x388 and not entry["write"] and entry["value"] & 0xE0 == 0xC0 for entry in fm)
                    assert fm[-1]["port"] == address+8 and not fm[-1]["write"] and fm[-1]["value"] == 6
                    vector = 8+irq if irq < 8 else 0x70+irq-8
                    assert len(irq_trace) == 11 and all(entry["vector"] == vector for entry in irq_trace)
                    for pic_port in (0x21,0xA1):
                        masks = [entry for entry in trace if entry["port"] == pic_port and entry["write"]]
                        old = [entry["value"] for entry in masks if entry["time"] < adc[0]["time"]][-1]
                        changed = [entry["value"] for entry in masks if entry["time"] >= adc[0]["time"]]
                        if pic_port == 0x21:
                            assert changed == [old & ~(1 << (irq if irq < 8 else 2)),old], changed
                        elif irq >= 8:
                            assert changed == [old & ~(1 << (irq-8)),old], changed
                        else:
                            assert not changed
                    results.append(dict(basePort=address,irq=irq,vector=vector,os=os,signal=signal,run=run,
                                        fmWrites=len(writes),actualIrqEntries=len(irq_trace),picMasksRestored=True,
                                        adcEvents=verified["checkedAdcEvents"],pcmFrames=verified["independentlyReconstructedFrames"]))
assert len(results) == 64
print(json.dumps(dict(passed=True,resourcePairs=8,coldBoots=64,independentlyCheckedFmWrites=sum(case["fmWrites"] for case in results),
                      independentlyCheckedIrqEntries=sum(case["actualIrqEntries"] for case in results),
                      checkedAdcEvents=sum(case["adcEvents"] for case in results),
                      independentlyReconstructedPcmFrames=sum(case["pcmFrames"] for case in results),cases=results)))
