# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Clone a Win9x DOSE machine, install Glide 2 in its private COW, and write a launcher."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import uuid
from pathlib import Path

from install_guest import replace_file
from branding import brand_renderer_descriptions, configure_host


def machine_file(value: str) -> Path:
    """Resolve a branch directory or explicit machine-definition filename."""
    path = Path(value).resolve()
    return path / "branch.dospc.json" if path.is_dir() else path


def bound_path(directory: Path, value: str) -> Path:
    """Resolve a machine media path relative to its owning branch directory."""
    return (directory / value).resolve()


def portable_machine_paths(definition: dict, directory: Path) -> dict:
    """Express same-volume media references relative to their machine without reading any media."""
    result = json.loads(json.dumps(definition))
    for collection in ('hardDisks', 'opticalDiscs', 'floppies'):
        for medium in result.get(collection, []):
            for key in ('imagePath', 'overlayPath'):
                if not medium.get(key):
                    continue
                target = bound_path(directory, medium[key])
                try:
                    medium[key] = os.path.relpath(target, directory)
                except ValueError:
                    medium[key] = str(target)
    return result


def launcher_text(frontend: Path, machine: Path, host: Path, data: Path) -> str:
    """Keep same-volume paths relative to the launcher, with a window-free inspection mode."""
    paths = (frontend, machine, host, data)
    if any(any(char in str(path) for char in '"\n\r%!') for path in paths):
        raise ValueError("A launcher path contains an unsupported character")

    def relative(path: Path) -> str:
        """Resolve same-volume resources from this CMD's own directory, regardless of caller CWD."""
        try:
            return "%~dp0" + os.path.relpath(path, machine.parent).replace("/", "\\")
        except ValueError:
            return str(path)  # An explicitly selected external drive cannot move with this branch.

    return ("@echo off\nsetlocal DisableDelayedExpansion\n"
            f'set "DOSE_GLIDE_HOST_DLL={relative(host)}"\n'
            f'set "DOSE_GLIDE_FRONTEND={relative(frontend)}"\n'
            f'set "DOSE_GLIDE_MACHINE={relative(machine)}"\n'
            f'set "DOSE_GLIDE_DATA={relative(data)}"\n'
            'if not exist "%DOSE_GLIDE_FRONTEND%" exit /b 1\n'
            'if not exist "%DOSE_GLIDE_MACHINE%" exit /b 1\n'
            f'if not exist "%DOSE_GLIDE_HOST_DLL%" exit /b 1\n'
            'if /i "%~1"=="--inspect" (\n'
            '  echo Frontend: "%DOSE_GLIDE_FRONTEND%"\n'
            '  echo Machine: "%DOSE_GLIDE_MACHINE%"\n'
            '  echo Host: "%DOSE_GLIDE_HOST_DLL%"\n'
            '  echo Data: "%DOSE_GLIDE_DATA%"\n'
            '  exit /b 0\n)\n'
            'start "" "%DOSE_GLIDE_FRONTEND%" --engine whpx --machine "%DOSE_GLIDE_MACHINE%" --data-dir "%DOSE_GLIDE_DATA%"\n'
            'exit /b %errorlevel%\n')


def main() -> None:
    """Create or update an isolated Glide test branch, its guest payloads and launcher."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="Existing .dosebranch or branch.dospc.json")
    parser.add_argument("--output", required=True, type=Path, help="New isolated .dosebranch directory")
    parser.add_argument("--shim", required=True, type=Path, help="Built 32-bit GLIDE2X.DLL")
    parser.add_argument("--guest-launcher", required=True, type=Path, help="Built Win9x MiniGlide launcher")
    parser.add_argument("--host", required=True, type=Path, help="64-bit host Glide library")
    parser.add_argument("--frontend", required=True, type=Path, help="DOSEmulator.exe with WHPX bridge")
    parser.add_argument("--cd", type=Path, help="Optional ISO to mount in the new branch")
    parser.add_argument("--cpu-mhz", type=int, choices=(133, 166, 200, 233),
                        help="Use the existing P54C 133 MHz or MMX board profile")
    parser.add_argument("--ram-mib", type=int, choices=(16, 32, 64, 128), help="RAM for the new branch")
    parser.add_argument("--reuse", action="store_true", help="Update an existing isolated bridge test branch")
    args = parser.parse_args()

    source_file = machine_file(args.source)
    source_dir = source_file.parent
    target = args.output.resolve()
    if target.suffix.lower() != ".dosebranch" or target == source_dir:
        raise ValueError("Output must be a separate .dosebranch directory")
    if not source_file.is_file():
        raise FileNotFoundError(source_file)
    source = json.loads(source_file.read_text(encoding="utf-8-sig"))
    disks = [item for item in source["hardDisks"] if item["slot"] == 0]
    if len(disks) != 1 or not disks[0].get("overlayPath"):
        raise ValueError("Source requires one writable primary COW disk")
    base = bound_path(source_dir, disks[0]["imagePath"])
    journal = bound_path(source_dir, disks[0]["overlayPath"])
    if journal.parent != source_dir or not base.is_file() or not journal.is_file():
        raise ValueError("Source COW must be local and its base image must exist")
    shim = args.shim.resolve()
    guest_launcher = args.guest_launcher.resolve()
    host = args.host.resolve()
    frontend = args.frontend.resolve()
    for path in (shim, guest_launcher, host, frontend):
        if not path.is_file():
            raise FileNotFoundError(path)
    host_config = configure_host(host)
    replacement = shim.read_bytes()
    if replacement[:2] != b"MZ" or len(replacement) > 4 * 1024 * 1024:
        raise ValueError("The guest Glide DLL is not a supported PE image")
    launcher_bytes = guest_launcher.read_bytes()
    if launcher_bytes[:2] != b"MZ" or len(launcher_bytes) > 4 * 1024 * 1024:
        raise ValueError("The MiniGlide launcher is not a supported PE image")
    cd = args.cd.resolve() if args.cd else None
    if cd is not None and not cd.is_file():
        raise FileNotFoundError(cd)

    destination_file = target / "branch.dospc.json"
    fresh = not target.exists()
    if not fresh:
        if not args.reuse or not destination_file.is_file():
            raise FileExistsError(f"Output already exists: {target}")
        definition = json.loads(destination_file.read_text(encoding="utf-8-sig"))
        target_disk = next((item for item in definition["hardDisks"] if item["slot"] == 0), None)
        if ("bridge test" not in definition["name"].lower() or target_disk is None or
                bound_path(target, target_disk["imagePath"]) != base or
                bound_path(target, target_disk["overlayPath"]).parent != target):
            raise ValueError("Existing output is not an isolated bridge test of this source disk")
        if args.cpu_mhz is not None and definition["hardware"]["cpuHz"] != args.cpu_mhz * 1_000_000:
            raise ValueError("Existing bridge test uses a different CPU speed")
    else:
        definition = json.loads(json.dumps(source))
        definition["id"] = uuid.uuid4().hex
        definition["name"] = source["name"] + " / Glide bridge test"
        for disk in definition["hardDisks"]:
            disk["imagePath"] = str(bound_path(source_dir, disk["imagePath"]))
            disk["overlayPath"] = "ide0.cow" if disk["slot"] == 0 else disk.get("overlayPath")
        for medium in definition.get("opticalDiscs", []):
            medium["imagePath"] = str(bound_path(source_dir, medium["imagePath"]))
        for medium in definition.get("floppies", []):
            medium["imagePath"] = str(bound_path(source_dir, medium["imagePath"]))
            if medium.get("overlayPath"):
                raise ValueError("Writable floppy overlays need their own copy before setup")
        if cd is not None:
            definition["opticalDiscs"] = [{"slot": definition["hardware"]["atapiSlot"], "imagePath": str(cd)}]
        if args.cpu_mhz is not None:
            hardware = definition["hardware"]
            if not hardware.get("isWindows95Board"):
                raise ValueError("MMX upgrade requires the implemented Windows PCI/S3 board")
            if args.cpu_mhz == 133:
                hardware["id"] = "dose-430fx-piix-s3-win95-133-dev-v1"
                hardware["cpu"] = "p54c-b5-subset-development"
                hardware["name"] = "Windows 95 Unreal Glide / P54C 133 MHz"
                hardware.pop("isP55c", None)
                hardware["isP54c"] = True
            else:
                hardware["id"] = f"dose-p55c-430fx-s3-{args.cpu_mhz}-dev-v1"
                hardware["cpu"] = "p55c-mmx-subset-development"
                hardware["isP55c"] = True
                hardware["isP54c"] = False
            hardware["cpuHz"] = args.cpu_mhz * 1_000_000
        if args.ram_mib is not None:
            definition["hardware"]["ramMiB"] = args.ram_mib
        target.mkdir(parents=True)
        source_stat = journal.stat()
        shutil.copyfile(journal, target / "ide0.cow")
        after = journal.stat()
        if (source_stat.st_size, source_stat.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise RuntimeError("Source overlay changed during copy; discard the incomplete output")
        definition = portable_machine_paths(definition, target)
        destination_file.write_text(json.dumps(definition, indent=2) + "\n", encoding="utf-8")

    result = replace_file(target, "WINDOWS/SYSTEM/GLIDE2X.DLL", replacement)
    if fresh and result.original is not None and not result.unchanged:
        backup = target / "original-GLIDE2X.DLL"
        if not backup.exists():
            backup.write_bytes(result.original)
    mini_result = replace_file(target, "MGLIDE.EXE", launcher_bytes)
    if fresh and mini_result.original is not None and not mini_result.unchanged:
        backup = target / "original-MGLIDE.EXE"
        if not backup.exists():
            backup.write_bytes(mini_result.original)
    renderer_descriptions = brand_renderer_descriptions(target)
    launcher = target / "Start-Glide.cmd"
    launcher.write_text(launcher_text(frontend, destination_file, host, target / "frontend-data"), encoding="ascii")
    manifest = {
        "source": str(source_file), "machine": str(destination_file), "launcher": str(launcher),
        "guestPath": r"C:\WINDOWS\SYSTEM\GLIDE2X.DLL", "hostLibrary": str(host),
        "miniGlidePath": r"C:\MGLIDE.EXE", "miniGlideBytes": mini_result.installed_size,
        "branding": "MiniGlide", "providerConfiguration": str(host_config) if host_config else None,
        "rendererDescriptions": renderer_descriptions,
        "guestDllBytes": result.installed_size, "createdGuestFile": result.created,
        "alreadyInstalled": result.unchanged and mini_result.unchanged,
    }
    (target / "glide-setup.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
