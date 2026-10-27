# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Cold-boot original MS-DOS ANSI.SYS in new private configuration journals.

Requires an installed non-ANSI DOS baseline whose current keyboard is US.
All CONFIG/AUTOEXEC changes are performed by real guest commands. No host
filesystem editing, video injection, desktop windows or Windows input.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import uuid


def sha(path):
    """Hash a specifically authorized input, never enumerate personal play journals."""
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load(name, filename):
    """Load an independent read-only media or pixel oracle by its explicit source path."""
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main():
    """Prepare variants, run the real guests and aggregate byte/pixel/determinism evidence."""
    parser = argparse.ArgumentParser()
    for name in ["disk", "overlay"]:
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--build-root", type=Path, default=Path("artifacts/m1-build"))
    parser.add_argument("--root", type=Path)
    parser.add_argument("--ready-screen", default="VIDEO-PC-READY")
    a = parser.parse_args()
    for path in [a.disk, a.overlay]:
        assert "doom2-play" not in str(path).lower()
    a.disk, a.overlay, a.build_root = a.disk.resolve(), a.overlay.resolve(), a.build_root.resolve()
    root = (a.root or Path("artifacts/ansi-clear-m3") / ("acceptance-" + uuid.uuid4().hex)).resolve()
    root.mkdir(parents=True, exist_ok=False)
    oracle = load("ansi_oracle", "verify-ansi-clear.py")
    media = load("probe_media", "prepare-font-media.py")
    original_files, _, _ = oracle.font_oracle.installation(a.disk, a.overlay)
    assert b"ANSI.SYS" not in original_files["CONFIG.SYS"].upper(), "Requires a non-ANSI preparation baseline"
    inputs = {str(p): sha(p) for p in [a.disk, a.overlay]}
    binaries = {str(p): sha(p) for p in (a.build_root / "Release/net10.0").glob("DOSEmulator.*.dll")}
    runner = a.build_root / "Release/net10.0/DOSEmulator.Headless.dll"
    nasm = Path("artifacts/tools/nasm-3.02/nasm-3.02/nasm.exe").resolve()
    program, floppy = root / "ANSICLS.COM", root / "own-probe.img"
    subprocess.run([str(nasm), "-Werror", "-f", "bin", "tests/guests/ansi-clear-test.asm", "-o", str(program)], check=True)
    prepared_media = media.prepare(program, floppy, "ANSICLS.COM")
    results, configurations, negative_count = [], [], 0
    for variant in ["low-no-himem", "himem-low", "himem-high", "emm386-noems", "emm386-ram"]:
        prefix = root / variant
        cow = prefix.with_suffix(".cow")
        shutil.copyfile(a.overlay, cow)
        config = []
        if variant != "low-no-himem":
            config.append(r"DEVICE=C:\DOS\HIMEM.SYS /TESTMEM:ON")
        if variant.startswith("emm386-"):
            config.append(r"DEVICE=C:\DOS\EMM386.EXE " + ("NOEMS" if variant.endswith("noems") else "RAM"))
        config += [r"DEVICE=C:\DOS\SETVER.EXE", r"DEVICE=C:\DOS\DISPLAY.SYS CON=(EGA,,1)", r"DEVICE=C:\DOS\ANSI.SYS",
                   "FILES=40", "BUFFERS=20", "LASTDRIVE=E", "DOS=" + ("LOW" if variant in ["low-no-himem", "himem-low"] else "HIGH"),
                   r"COUNTRY=049,850,C:\DOS\COUNTRY.SYS"]
        auto = ["@ECHO OFF", "PROMPT $p$g", r"PATH C:\DOS;C:\GAMES\DOOM2", "SET BLASTER=A220 I5 D1 H5 T6",
                r"MODE CON CODEPAGE PREPARE=((850) C:\DOS\EGA.CPI)", "MODE CON CODEPAGE SELECT=850", "KEYB US", "ECHO ANSI-TEST-READY"]
        commands = []
        for name, lines in [("CONFIG.SYS", config), ("AUTOEXEC.BAT", auto)]:
            commands += ["echo " + line + (">" if index == 0 else ">>") + name for index, line in enumerate(lines)]
        commands.append("echo ANSI-CONFIG-PREPARED")
        scenario = prefix.with_suffix(".scenario.json")
        scenario.write_text(json.dumps([dict(waitForScreen=a.ready_screen, text="\r\n".join(commands) + "\r\n")]))
        report = prefix.with_suffix(".json")
        with prefix.with_suffix(".log").open("w") as output:
            subprocess.run(["dotnet", str(runner), "--disk", str(a.disk), "--overlay", str(cow), "--scenario", str(scenario),
                            "--steps", "250000000", "--stop-text", "\r\nANSI-CONFIG-PREPARED\r\n", "--report", str(report)],
                           stdout=output, stderr=subprocess.STDOUT, check=True, timeout=600)
        r = json.loads(report.read_text(encoding="utf-8-sig"))
        assert r["status"]["fault"] is None and r["scenario"]["complete"]
        files, _, _ = oracle.font_oracle.installation(a.disk, cow)
        assert {n: d for n, d in files.items() if n not in ["CONFIG.SYS", "AUTOEXEC.BAT"]} == {
            n: d for n, d in original_files.items() if n not in ["CONFIG.SYS", "AUTOEXEC.BAT"]}
        for name, lines in [("CONFIG.SYS", config), ("AUTOEXEC.BAT", auto)]:
            assert files[name].decode("ascii").splitlines() == lines
        configurations.append(dict(variant=variant, configuration=config, autoexec=auto, journalSha256=sha(cow)))
        font = oracle.font_oracle.cpi_fonts(files["DOS/EGA.CPI"], 850)
        for card in ["dose-vga-development", "et4000ax-isa-1m-development"]:
            for rows, points in ([(25, 16), (43, 8), (50, 8)] if variant == "himem-high" else [(25, 16)]):
                previous = None
                for repeat in [1, 2]:
                    name = f"{variant}-{card}-{rows}-{repeat}"
                    p = root / name
                    profile = p.with_suffix(".profile.json")
                    profile.write_text(json.dumps(dict(videoCard=card, ramMiB=8, bootDevice="ide")))
                    test_cow = p.with_suffix(".cow")
                    shutil.copyfile(cow, test_cow)
                    before = sha(test_cow)
                    plan = p.with_suffix(".scenario.json")
                    plan.write_text(json.dumps([dict(waitForScreen="ANSI-TEST-READY", text=f"mode con lines={rows}\r\na:ansicls\r\n")]))
                    frame, report = p.with_suffix(".ppm"), p.with_suffix(".json")
                    with p.with_suffix(".log").open("w") as output:
                        subprocess.run(["dotnet", str(runner), "--profile", str(profile), "--disk", str(a.disk), "--overlay", str(test_cow),
                                        "--floppy-a", str(floppy), "--scenario", str(plan), "--steps", "150000000", "--stop-text", "ANSI-CLS-READY",
                                        "--report", str(report), "--frame", str(frame), "--video-io-trace", str(p.with_suffix(".video.jsonl"))],
                                       stdout=output, stderr=subprocess.STDOUT, check=True, timeout=600)
                    r = json.loads(report.read_text(encoding="utf-8-sig"))
                    verified = oracle.verify(frame.read_bytes(), r, font[points], rows, points)
                    assert sha(test_cow) == before
                    signature = (r["frameHash"], r["status"]["virtualNanoseconds"])
                    if previous is not None:
                        assert signature == previous
                    previous = signature
                    verified.update(name=name, report=str(report), signature=signature, noDiskWrites=True)
                    if repeat == 1:
                        command = [sys.executable, "tests/verify-ansi-clear.py", "--base", str(a.disk), "--overlay", str(test_cow),
                                   "--report", str(report), "--frame", str(frame), "--rows", str(rows), "--points", str(points), "--negative-controls"]
                        negative = json.loads(subprocess.check_output(command))
                        negative_count += negative["negativeControlsRejected"]
                    results.append(verified)
                    p.with_suffix(".verified.json").write_text(json.dumps(verified, indent=2))
    for path, expected in inputs.items() | binaries.items():
        assert sha(path) == expected
    summary = dict(passed=True, directory=str(root), coldBoots=len(configurations) + len(results), captureBoots=len(results),
                   configurations=configurations, results=results, negativeControlsRejected=negative_count, sourceInputs=inputs,
                   testedAssemblies=binaries, sourceMediaUnchanged=True, nativeWindowsOpened=False, nativeInputSent=False, ownProbe=prepared_media)
    (root / "acceptance-summary.json").write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
