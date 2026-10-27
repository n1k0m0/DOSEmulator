# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Alternate complete original-Doom boots with sound and compare private RAM, pixels and PCM.

All guest writes use new journals. The supplied installations and game are read-only
inputs; personal play journals are deliberately excluded. Timing is host work for
the same virtual execution, not a historical 486 calibration or a long game test.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import time
import wave


def sha(path):
    """Hash an explicit test artifact without traversing personal game journals."""
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def checked_path(value):
    """Resolve an explicit path and refuse the user's persistent play directory."""
    path = Path(value).resolve()
    if "doom2-play" in (part.lower() for part in path.parts):
        raise ValueError("Personal play journals are excluded")
    return path


def check_run(prefix, game_hud, original_journal_hash):
    """Verify real DOS return, WAD pixels and active PCM/FM, then summarize comparable evidence."""
    report = json.loads(prefix.with_suffix(".json").read_text(encoding="utf-8-sig"))
    status = report["status"]
    # ANSI writes its own aperture and need not repeat DOS output on BIOS/E9.
    # Inspect the actual final text screen for commands issued through that driver.
    text = report["text"]
    assert status["fault"] is None and "PERF-DOOM-END" in text and text.rstrip().endswith("C:\\GAMES\\DOOM2>")
    assert len(re.findall(r"timed 32 gametics in [0-9]+ realtics", text)) == 1
    assert report["cpu"]["protectedModeEntries"] > 0 and report["ataSectorsWritten"] == 0
    assert report["scenario"]["complete"]
    assert sha(prefix.with_suffix(".cow")) == original_journal_hash
    frames = report["graphicsCapture"]
    scenes = []
    views = set()
    all_pixels = hashlib.sha256()
    for frame in frames["frames"]:
        filename = frame["filename"]
        assert Path(filename).name == filename
        data = (Path(frames["directory"]) / filename).read_bytes()
        all_pixels.update(data)
        magic, dimensions, maximum, rgb = data.split(b"\n", 3)
        assert magic == b"P6" and maximum == b"255"
        if dimensions != b"320 200":
            continue
        assert len(rgb) == 320 * 200 * 3
        if all(rgb[((y + 168) * 320 + x) * 3:((y + 168) * 320 + x) * 3 + 3] == color
               for (x, y), color in game_hud.items()):
            scenes.append(frame["frameHash"])
            views.add(hashlib.sha256(rgb[:320 * 168 * 3]).hexdigest())
    assert len(scenes) >= 2 and len(views) >= 2
    sb = report["sb16"]
    assert sb["softwareIrq8Requests"] == 2 and sb["transferredSamples"] > 0 and sb["dma8Grants"] > 0
    assert sb["irq8Requests"] == sb["completedBlocks"] + 2
    assert sb["unsupportedCommands"] == sb["unsupportedMixerAccesses"] == sb["rejectedWrites"] == 0
    assert report["opl3"]["keyOnCount"] > 0
    with wave.open(str(prefix.with_suffix(".wav")), "rb") as stream:
        assert stream.getnchannels() == 2 and stream.getsampwidth() == 2
        audio_frames = stream.getnframes()
        pcm = stream.readframes(audio_frames)
        rate = stream.getframerate()
    assert sum(byte != 0 for byte in pcm) > 1000
    cpu = {key: value for key, value in report["cpu"].items() if key != "tlbHits"}
    identical = dict(cpu=cpu, virtualNanoseconds=status["virtualNanoseconds"], instructions=status["instructions"],
                     frameHash=report["frameHash"], allCapturedPixelsSha256=all_pixels.hexdigest(),
                     guestRamSha256=sha(prefix.with_suffix(".private-ram.bin")), pcmSha256=hashlib.sha256(pcm).hexdigest(),
                     audioFrames=audio_frames, audioRate=rate, sb16=sb, opl3=report["opl3"],
                     numericError=report["numericError"], sceneFrames=len(scenes))
    return identical, dict(passed=True, wadFrames=len(scenes), distinctViews=len(views), hudPixels=len(game_hud),
                          audioFrames=audio_frames, report=str(prefix.with_suffix(".json")),
                          originalJournalUnchanged=True)


def main():
    """Run ordered baseline/candidate pairs with ordinary .NET settings and no concurrent test worker."""
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("baseline", "candidate", "disk", "himem-overlay", "ram-overlay", "profile", "game", "out"):
        parser.add_argument("--" + name, type=checked_path, required=True)
    parser.add_argument("--repeats", type=int, choices=range(2, 10), default=2)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    protected = [args.disk, args.himem_overlay, args.ram_overlay, args.profile,
                 args.game / "DOOM2.EXE", args.game / "DOOM2.WAD"]
    assemblies = list(args.baseline.glob("*.dll")) + list(args.candidate.glob("*.dll"))
    before = {str(path): sha(path) for path in protected + assemblies}
    spec = importlib.util.spec_from_file_location("doom_oracle", Path(__file__).with_name("verify-doom-extender.py"))
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    hud, _ = oracle.hud_reference(args.game)
    installations = []
    for journal in (args.himem_overlay, args.ram_overlay):
        image, _ = oracle.fat.materialize(args.disk, journal)
        start = int.from_bytes(image[454:458], "little") * 512
        files, _ = oracle.fat.Fat(image, start).files()
        for name in ("DOOM2.EXE", "DOOM2.WAD"):
            assert files["GAMES/DOOM2/" + name] == (args.game / name).read_bytes()
        assert files["GAMES/DOOM2/FASTDEMO.LMP"] == bytes([109, 2, 1, 1, 0, 0, 0, 1, 0, 1, 0, 0, 0]) + bytes([12, 0, 4, 0]) * 32 + b"\x80"
        installations.append(dict(journal=str(journal), originalGameAndOwnDemoVerified=True))
        del image, files
    rows = []
    for mode, journal, prompt in (("HIMEM", args.himem_overlay, "VIDEO-PC-READY"),
                                  ("RAM", args.ram_overlay, "ANSI-TEST-READY")):
        reference = None
        for repeat in range(1, args.repeats + 1):
            order = ("baseline", "candidate") if repeat % 2 else ("candidate", "baseline")
            for variant in order:
                prefix = args.out / f"{mode}-{repeat}-{variant}"
                shutil.copyfile(journal, prefix.with_suffix(".cow"))
                scenario = [dict(waitForScreen=prompt, text="cd \\games\\doom2\r\ndoom2 -config sound.cfg -timedemo fastdemo\r\n"),
                            dict(waitForScreen="timed 32 gametics", text="ver\r\necho PERF-DOOM-END\r\n")]
                prefix.with_suffix(".scenario.json").write_text(json.dumps(scenario), encoding="utf-8")
                command = ["dotnet", str(getattr(args, variant) / "DOSEmulator.Headless.dll"), "--disk", str(args.disk),
                           "--overlay", str(prefix.with_suffix(".cow")), "--profile", str(args.profile),
                           "--scenario", str(prefix.with_suffix(".scenario.json")), "--steps", "1000000000",
                           "--stop-screen", "\r\nPERF-DOOM-END\r\n\r\nC:\\GAMES\\DOOM2>",
                           "--report", str(prefix.with_suffix(".json")), "--memory-dump", str(prefix.with_suffix(".private-ram.bin")),
                           "--graphics-capture", str(prefix) + "-graphics", "--audio-out", str(prefix.with_suffix(".wav"))]
                with prefix.with_suffix(".log").open("w", encoding="utf-8") as log:
                    start = time.perf_counter()
                    subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=900)
                    milliseconds = (time.perf_counter() - start) * 1000
                identical, proof = check_run(prefix, hud, before[str(journal)])
                assert reference is None or reference == identical, "CPU/RAM/pixels/audio/device state or virtual time changed"
                reference = identical
                proof["comparedState"] = identical
                prefix.with_suffix(".verified.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
                row = dict(mode=mode, repeat=repeat, variant=variant, milliseconds=milliseconds, **proof)
                rows.append(row)
                (args.out / "progress.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
                print(mode, repeat, variant, round(milliseconds, 2), "PASS", flush=True)
    assert all(sha(path) == digest for path, digest in before.items()), "Protected input or tested assembly changed"
    timings = []
    for mode in ("HIMEM", "RAM"):
        medians = {variant: statistics.median(row["milliseconds"] for row in rows
                                            if row["variant"] == variant and row["mode"] == mode)
                   for variant in ("baseline", "candidate")}
        timings.append(dict(mode=mode, mediansMilliseconds=medians,
                            lessHostTimePercent=(1 - medians["candidate"] / medians["baseline"]) * 100))
    summary = dict(passed=True, coldBoots=len(rows), rows=rows, timings=timings, protectedInputs=before, installations=installations,
                   timingIncludesProcessStartGraphicsAndWav=True, translationHitsExcluded=True,
                   guestTimingModelChanged=False, originalInputsUnchanged=True, nativeInputUsed=False,
                   independentMathematicalSoundOracle=False, fullGameCompatibilityAccepted=False)
    (args.out / "acceptance-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(dict(passed=True, coldBoots=len(rows), timings=timings, directory=str(args.out)), indent=2))


if __name__ == "__main__":
    main()
