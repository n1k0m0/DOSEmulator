#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Nils Kopal
"""Headless WHPX acceptance of one Win9x DOSEIMPORT transfer.

The caller supplies a known VM and a compiled product directory. This script
creates one new, ignored private branch; it never opens the source COW for
writing. The host gateway ACK and an independent FAT readback must both pass.
"""

from __future__ import annotations

import argparse
import configparser
import hashlib
import json
from pathlib import Path
import queue
import re
import shutil
import subprocess
import sys
import threading
import uuid
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts" / "glide-bridge"))
from install_guest import read_guest_file  # noqa: E402


CONTROLLER = r'''// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;
using DOSEmulator.Runtime.Networking;
using DOSEmulator.Whpx;

if (args.Length != 1) throw new ArgumentException("Supply one private machine definition.");
var pc = MachineLaunch.Open(option => option == "--machine" ? args[0] : null);
using var session = new MachineSession(pc, enableHostNetworking: true,
    executionFactory: new WhpxExecutionFactory());
Task<bool>? gateway = null;
void Reply(object value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, IpcProtocol.JsonOptions));
    Console.Out.Flush();
}
IpcResponse Command(string name, object? payload = null)
{
    var task = session.Enqueue(IpcProtocol.CreateRequest(name, payload));
    session.PumpCommands();
    return task.GetAwaiter().GetResult();
}
var resume = Command("Resume");
if (!resume.Success) throw new InvalidOperationException("Guest resume failed: " + resume.ErrorCode);
Reply(new { ready = true, engine = session.CpuEngine, status = session.GetStatus() });
string? line;
while ((line = Console.ReadLine()) is not null)
{
    try
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        string command = request.GetProperty("command").GetString() ?? "";
        object result;
        switch (command)
        {
            case "run":
                int milliseconds = request.GetProperty("milliseconds").GetInt32();
                if (milliseconds is < 1 or > 30000) throw new ArgumentOutOfRangeException("milliseconds");
                for (int remaining = milliseconds; remaining > 0;)
                {
                    int slice = Math.Min(remaining, 250);
                    session.PumpCommands();
                    session.RunRealtime(TimeSpan.FromMilliseconds(slice), () => { });
                    session.PumpCommands();
                    if (session.GetStatus().Fault is not null)
                        throw new InvalidOperationException("Guest CPU fault: " + session.GetStatus().Fault);
                    remaining -= slice;
                }
                result = new { status = session.GetStatus(), gatewayCompleted = gateway?.IsCompleted ?? false };
                break;
            case "keys":
                byte[] codes = request.GetProperty("scanCodes").EnumerateArray()
                    .Select(item => item.GetByte()).ToArray();
                if (codes.Length is < 1 or > 16) throw new ArgumentException("Scan packet size must be 1..16.");
                var response = Command("SendScanCode", new { scanCodes = codes.Select(value => (int)value).ToArray() });
                result = new { accepted = response.Success, error = response.ErrorCode };
                break;
            case "serve":
                if (gateway is not null) throw new InvalidOperationException("Only one host file is served per run.");
                string code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                gateway = DoseTransferGateway.ServeOneAsync(request.GetProperty("path").GetString()!, code);
                result = new { serving = true, code };
                break;
            case "gateway":
                result = new { completed = gateway?.IsCompleted ?? false,
                    acknowledged = gateway is { IsCompletedSuccessfully: true } && gateway.Result,
                    canceled = gateway?.IsCanceled ?? false,
                    faulted = gateway?.IsFaulted ?? false,
                    error = gateway?.Exception?.GetBaseException().Message };
                break;
            case "status":
                result = new { status = session.GetStatus(), text = session.Machine.Video.ReadText(),
                    network = Command("GetNetworkStatus").Payload };
                break;
            case "capture":
                var frame = session.Machine.CaptureFrame();
                byte[] pixels = new byte[frame.ArgbPixels.Length * sizeof(uint)];
                Buffer.BlockCopy(frame.ArgbPixels, 0, pixels, 0, pixels.Length);
                File.WriteAllBytes(request.GetProperty("path").GetString()!, pixels);
                result = new { width = frame.Width, height = frame.Height, bytes = pixels.Length };
                break;
            case "exit":
                Reply(new { ok = true, result = new { exited = true, gatewayCompleted = gateway?.IsCompleted ?? false } });
                return;
            default: throw new ArgumentException("Unknown controller command: " + command);
        }
        Reply(new { ok = true, result });
    }
    catch (Exception error)
    {
        Reply(new { ok = false, error = error.GetType().Name + ": " + error.Message });
    }
}
'''


def create_controller(output: Path, product: Path) -> Path:
    """Compile a private console owner against the exact supplied product DLLs."""
    project = output / "controller"
    project.mkdir()
    required = ["Contracts", "Core", "Cpu.X86", "Hardware", "Runtime", "Whpx"]
    references = []
    for part in required:
        path = product / f"DOSEmulator.{part}.dll"
        if not path.is_file():
            raise FileNotFoundError(f"Compiled product module missing: {path}")
        references.append(f'    <Reference Include="DOSEmulator.{part}"><HintPath>{escape(str(path))}</HintPath><Private>true</Private></Reference>')
    (project / "Controller.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable></PropertyGroup><ItemGroup>\n' + "\n".join(references)
        + '\n</ItemGroup></Project>\n', encoding="utf-8")
    (project / "Program.cs").write_text(CONTROLLER, encoding="utf-8")
    build = subprocess.run(["dotnet", "build", str(project / "Controller.csproj"), "-c", "Release",
                            "-o", str(project / "bin")], cwd=ROOT, capture_output=True, text=True)
    (output / "controller-build.txt").write_text(build.stdout + build.stderr, encoding="utf-8")
    if build.returncode:
        raise RuntimeError("Private controller compilation failed; see controller-build.txt")
    native = product / "native" / "win-x64"
    if not (native / "network-runtime.json").is_file():
        raise FileNotFoundError(f"Pinned libslirp runtime missing from compiled product: {native}")
    shutil.copytree(native, project / "bin" / "native" / "win-x64")
    return project / "bin" / "Controller.dll"


def make_branch(source: Path, output: Path, version: str, installer_iso: Path | None = None) -> Path:
    """Use the existing read-only base and a new copy of the exact source COW."""
    allowed = (ROOT / "VMs" / f"win{version}").resolve()
    if source.resolve() != allowed / "machine.dospc.json":
        raise ValueError(f"Only the explicit VMs/win{version}/machine.dospc.json source is accepted")
    definition = json.loads(source.read_text(encoding="utf-8-sig"))
    disks = definition["hardDisks"]
    if len(disks) != 1 or disks[0]["slot"] != 0 or not disks[0].get("overlayPath"):
        raise ValueError("Expected exactly one known primary disk with a source COW")
    disk = disks[0]
    base = (source.parent / disk["imagePath"]).resolve(strict=True)
    cow = (source.parent / disk["overlayPath"]).resolve(strict=True)
    if base.parent != (allowed / "Disks") or cow.parent != (allowed / "Disks"):
        raise ValueError("Source disk paths do not match the known private VM directory")
    branch = output / "windows.dosebranch"
    branch.mkdir()
    shutil.copyfile(cow, branch / "ide0.cow")
    disk["imagePath"] = str(base)
    disk["overlayPath"] = "ide0.cow"
    definition["id"] = uuid.uuid4().hex
    definition["name"] = f"DOSE Tools bridge test Win{version}"
    definition["opticalDiscs"] = ([{"slot": definition["hardware"]["atapiSlot"],
                                    "imagePath": str(installer_iso)}] if installer_iso else [])
    definition["floppies"] = []
    cards = definition["hardware"].get("networkAdapters", [])
    if not cards:
        raise ValueError("This VM has no configured network card")
    cards[0]["mode"] = "nat"
    cards[0]["cableConnected"] = True
    for card in cards[1:]:
        card["cableConnected"] = False
    machine = branch / "branch.dospc.json"
    machine.write_text(json.dumps(definition, indent=2) + "\n", encoding="utf-8")
    return branch


def make_upgrade_branch(previous: Path, output: Path, version: str,
                        installer_iso: Path) -> tuple[Path, dict, bytes, dict[str, Path]]:
    """Copy one proven private installer branch and independently check its pre-upgrade Share file."""
    review = (ROOT / "artifacts" / "review-2026-11-13").resolve()
    if not previous.is_relative_to(review) or previous == review or previous.parent != review:
        raise ValueError("--upgrade-from must name one direct private test directory under artifacts/review-2026-11-13")
    report = json.loads((previous / "result.json").read_text(encoding="utf-8"))
    if (report.get("passed") is not True or report.get("windows") != version or
        not report.get("installerIso") or report.get("uninstallAfter") is True or
        report.get("independentFatReadback") is not True or
        not isinstance(report.get("installedFileHashes"), dict)):
        raise ValueError("Upgrade source is not a passed, installed, same-version guest test")
    old_iso = Path(report["installerIso"]).resolve(strict=True)
    if not old_iso.is_relative_to(review) or old_iso == installer_iso:
        raise ValueError("Upgrade source ISO must be a previous private package")
    if hashlib.sha256(old_iso.read_bytes()).digest() == hashlib.sha256(installer_iso.read_bytes()).digest():
        raise ValueError("Upgrade source and selected current ISO have identical bytes")
    old_payload = installer_payload(old_iso, version)
    old_branch = previous / "windows.dosebranch"
    definition = json.loads((old_branch / "branch.dospc.json").read_text(encoding="utf-8-sig"))
    disks = definition["hardDisks"]
    if len(disks) != 1 or disks[0]["slot"] != 0 or not disks[0].get("overlayPath"):
        raise ValueError("Upgrade source must contain one known primary COW disk")
    allowed_disk = (ROOT / "VMs" / f"win{version}" / "Disks" / "Disk-0.img").resolve(strict=True)
    base = (old_branch / disks[0]["imagePath"]).resolve(strict=True)
    cow = (old_branch / disks[0]["overlayPath"]).resolve(strict=True)
    if base != allowed_disk or cow.parent != old_branch or cow.name != "ide0.cow":
        raise ValueError("Upgrade source media paths differ from the proven private branch")
    branch = output / "windows.dosebranch"
    branch.mkdir()
    shutil.copyfile(cow, branch / "ide0.cow")
    disks[0]["imagePath"] = str(base)
    disks[0]["overlayPath"] = "ide0.cow"
    definition["id"] = uuid.uuid4().hex
    definition["name"] = f"DOSE Tools bridge test Win{version} upgrade"
    definition["opticalDiscs"] = [{"slot": definition["hardware"]["atapiSlot"], "imagePath": str(installer_iso)}]
    definition["floppies"] = []
    cards = definition["hardware"].get("networkAdapters", [])
    if not cards:
        raise ValueError("Upgrade source has no configured network card")
    cards[0]["mode"] = "nat"
    cards[0]["cableConnected"] = True
    for card in cards[1:]:
        card["cableConnected"] = False
    (branch / "branch.dospc.json").write_text(json.dumps(definition, indent=2) + "\n", encoding="utf-8")
    old_guest = report.get("guestFile", "")
    match = re.fullmatch(r"C:\\Share\\([A-Za-z0-9_-]{1,8}\.[A-Za-z0-9]{1,3})", old_guest, re.I)
    if not match:
        raise ValueError("Upgrade source result has no bounded 8.3 Share-file oracle")
    previous_bytes = read_guest_file(branch, "SHARE/" + match.group(1))
    if (previous_bytes is None or len(previous_bytes) != report.get("bytes") or
        hashlib.sha256(previous_bytes).hexdigest() != report.get("sha256")):
        raise RuntimeError("First imported Share file no longer matches the passed source result")
    return branch, report, previous_bytes, old_payload


def stage_importer(branch: Path, product: Path, importer: Path) -> str:
    """Install the exact candidate EXE offline through DiskTool on this new COW."""
    disktool = product / "DOSEmulator.DiskTool.dll"
    if not disktool.is_file():
        raise FileNotFoundError(f"Compiled DiskTool is missing: {disktool}")
    if importer.name.upper() != "DOSEIMPORT.EXE" or not importer.is_file():
        raise ValueError("--importer must name an existing DOSEIMPORT.EXE")
    machine = branch / "branch.dospc.json"
    listed = subprocess.run(["dotnet", str(disktool), "list", "--vm", str(machine),
                             "--path", "DOSETOOLS"], capture_output=True, text=True)
    if listed.returncode:
        result = subprocess.run(["dotnet", str(disktool), "mkdir", "--vm", str(machine),
                                 "--path", "DOSETOOLS"], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError("DiskTool could not create DOSETOOLS on the private COW: " + result.stderr)
    command = ["dotnet", str(disktool), "import", "--vm", str(machine),
               "--from", str(importer), "--to", "DOSETOOLS", "--overwrite"]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError("DiskTool could not stage DOSEIMPORT on the private COW: " + result.stderr)
    export = branch.parent / "importer-readback"
    result = subprocess.run(["dotnet", str(disktool), "export", "--vm", str(machine),
                             "--path", "DOSETOOLS/DOSEIMPORT.EXE", "--output", str(export)],
                            capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError("DiskTool could not independently export the staged importer: " + result.stderr)
    staged = (export / "DOSEIMPORT.EXE").read_bytes()
    if staged != importer.read_bytes():
        raise RuntimeError("Private COW importer differs from the selected candidate EXE")
    return hashlib.sha256(staged).hexdigest()


def installer_payload(iso: Path, version: str) -> dict[str, Path]:
    """Bind one exact built package manifest to its matching read-only ISO."""
    manifest = json.loads((iso.parent / "build.json").read_text(encoding="utf-8"))
    package = manifest["packages"][version]
    if Path(package["iso"]).resolve(strict=True) != iso:
        raise ValueError("--installer-iso is not the selected package's built ISO")
    if hashlib.sha256(iso.read_bytes()).hexdigest().lower() != package["isoSha256"].lower():
        raise ValueError("Installer ISO differs from its package manifest")
    payload = Path(package["payload"]).resolve(strict=True)
    names = ["DOSEIMPORT.EXE", f"APM{version}.INF", "APMHELP.TXT"]
    files = {name: payload / name for name in names}
    for name, path in files.items():
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest().lower() != package["files"][name].lower():
            raise ValueError("Installer payload differs from its manifest: " + name)
    setup = Path(package["installer"]).resolve(strict=True)
    if hashlib.sha256(setup.read_bytes()).hexdigest().lower() != package["installerSha256"].lower():
        raise ValueError("Installer executable differs from its package manifest")
    files["SETUP.EXE"] = setup
    return files


def verify_guest_install(branch: Path, product: Path, expected: dict[str, Path], version: str,
                         phase: str = "installed") -> dict:
    """Read installed long names from the stopped guest using DiskTool export."""
    disktool = product / "DOSEmulator.DiskTool.dll"
    machine = branch / "branch.dospc.json"
    names = {"DOSEIMPORT.EXE": "DOSETOOLS/DOSEIMPORT.EXE",
             f"APM{version}.INF": f"DOSETOOLS/APM/APM{version}.INF",
             "APMHELP.TXT": "DOSETOOLS/APM/README.TXT",
             "DOSEPKG.INI": "DOSETOOLS/DOSEPKG.INI"}
    verified: dict[str, str] = {}
    for name, guest_path in names.items():
        destination = branch.parent / (phase + "-" + name.replace(".", "-"))
        result = subprocess.run(["dotnet", str(disktool), "export", "--vm", str(machine),
                                 "--path", guest_path, "--output", str(destination)],
                                capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(f"Guest installer did not leave {guest_path}: {result.stderr}")
        actual = (destination / guest_path.split("/")[-1]).read_bytes()
        if name in expected and actual != expected[name].read_bytes():
            raise RuntimeError("Installed guest file differs from the ISO package: " + guest_path)
        if name == "DOSEPKG.INI":
            ini = configparser.ConfigParser(interpolation=None)
            ini.read_string(actual.decode("cp1252"))
            if (ini.get("Package", "Product", fallback="") != "DOSE Guest Tools" or
                any(ini.get("Installed", item, fallback="") != "1" for item in ("Time", "Import", "APM"))):
                raise RuntimeError("Guest installer record does not confirm Tools and APM installation")
        verified[name] = hashlib.sha256(actual).hexdigest()
    return verified


def read_guest_path(branch: Path, product: Path, guest_path: str, label: str) -> bytes | None:
    """Distinguish a missing LFN guest path from an actual DiskTool read failure."""
    disktool = product / "DOSEmulator.DiskTool.dll"
    machine = branch / "branch.dospc.json"
    parts = guest_path.replace("\\", "/").split("/")
    if any(not part for part in parts):
        raise ValueError("Invalid guest path")
    directory = ""
    for index, part in enumerate(parts):
        listed = subprocess.run(["dotnet", str(disktool), "list", "--vm", str(machine),
                                 "--path", directory], capture_output=True, text=True)
        if listed.returncode:
            raise RuntimeError("DiskTool could not list a known guest parent: " + directory + " " + listed.stderr)
        entries = json.loads(listed.stdout)["entries"]
        found = next((entry for entry in entries if entry["Name"].casefold() == part.casefold()), None)
        if found is None:
            return None
        if index < len(parts) - 1 and not found["IsDirectory"]:
            return None
        directory = "/".join(parts[:index + 1])
    destination = branch.parent / ("uninstall-" + label)
    exported = subprocess.run(["dotnet", str(disktool), "export", "--vm", str(machine),
                               "--path", guest_path, "--output", str(destination)],
                              capture_output=True, text=True)
    if exported.returncode:
        raise RuntimeError("DiskTool could not export an existing guest file: " + guest_path + " " + exported.stderr)
    return (destination / parts[-1]).read_bytes()


def verify_guest_removal(branch: Path, product: Path, expected: dict[str, Path], version: str) -> dict:
    """Check conditional restore, LAST/backups, retained maintenance and removal state."""
    state = read_guest_path(branch, product, "DOSETOOLS/DOSEPKG.INI", "state")
    if state is None:
        raise RuntimeError("Uninstaller removed its required maintenance record")
    ini = configparser.ConfigParser(interpolation=None)
    ini.read_string(state.decode("cp1252"))
    if ini.get("Package", "Product", fallback="") != "DOSE Guest Tools" or ini.get("Package", "Removed", fallback="") != "1":
        raise RuntimeError("Guest maintenance record does not confirm removal")
    maintenance = read_guest_path(branch, product, "DOSETOOLS/DSETUP.EXE", "maintenance")
    if maintenance != expected["SETUP.EXE"].read_bytes():
        raise RuntimeError("Uninstaller did not retain its exact maintenance executable")
    checked: dict[str, str] = {"DOSEPKG.INI": hashlib.sha256(state).hexdigest(),
                               "DSETUP.EXE": hashlib.sha256(maintenance).hexdigest()}
    targets = [("IMPORT", "DOSETOOLS/DOSEIMPORT.EXE", "DOSEIMPORT.EXE"),
               ("APM", f"DOSETOOLS/APM/APM{version}.INF", f"APM{version}.INF"),
               ("APMHELP", "DOSETOOLS/APM/README.TXT", "APMHELP.TXT")]
    for tag, target, source in targets:
        last = read_guest_path(branch, product, f"DOSETOOLS/BACKUP/{tag}.LAST", tag + "-last")
        if last != expected[source].read_bytes():
            raise RuntimeError("Uninstaller did not preserve exact installed payload in " + tag + ".LAST")
        recorded = ini.get("Originals", tag, fallback="")
        if recorded not in ("0", "1"):
            raise RuntimeError("Missing original-file policy in guest maintenance record: " + tag)
        current = read_guest_path(branch, product, target, tag + "-target")
        if recorded == "0":
            if current is not None:
                raise RuntimeError("Uninstaller retained a newly installed guest file: " + target)
            checked[target] = "removed"
        else:
            backup = read_guest_path(branch, product, f"DOSETOOLS/BACKUP/{tag}.BAK", tag + "-backup")
            if backup is None or current != backup:
                raise RuntimeError("Uninstaller did not restore the first backup for " + target)
            checked[target] = hashlib.sha256(current).hexdigest()
        checked[f"BACKUP/{tag}.LAST"] = hashlib.sha256(last).hexdigest()
    return checked


def main() -> None:
    """Require an actual guest ACK and independently reread the committed guest file."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--windows-version", choices=["95", "98"], required=True)
    parser.add_argument("--source", type=Path, help="Fresh-install source: explicit known VM machine.dospc.json")
    parser.add_argument("--upgrade-from", type=Path, help="Upgrade source: one passed private installer test directory")
    parser.add_argument("--product", type=Path, required=True, help="Compiled product directory containing DOSEmulator.*.dll")
    parser.add_argument("--importer", type=Path, help="Offline mode: exact candidate DOSEIMPORT.EXE staged only on the new COW")
    parser.add_argument("--installer-iso", type=Path, help="Guest mode: matching built DOSE Tools ISO; executes SETUP.EXE /S /apm on the known guest CD letter")
    parser.add_argument("--uninstall-after", action="store_true", help="Installer mode: uninstall after a guest-ACKed import and verify restore/retention")
    parser.add_argument("--prompt-mode", action="store_true", help="Enter the one-time code in the guest helper's own window with virtual keys")
    parser.add_argument("--payload-bytes", type=int, default=2536, help="Controlled transfer size, 1..60000 bytes")
    parser.add_argument("--output", type=Path, required=True, help="New private directory under artifacts/")
    args = parser.parse_args()
    if not 1 <= args.payload_bytes <= 60000:
        raise ValueError("--payload-bytes must be in 1..60000")
    if (args.importer is None) == (args.installer_iso is None):
        raise ValueError("Choose exactly one of --importer (offline staging) or --installer-iso (actual guest installer)")
    if (args.source is None) == (args.upgrade_from is None):
        raise ValueError("Choose exactly one of --source or --upgrade-from")
    if args.upgrade_from and args.installer_iso is None:
        raise ValueError("--upgrade-from requires a new --installer-iso")
    if args.upgrade_from and args.uninstall_after:
        raise ValueError("Upgrade and uninstall acceptance require separate private branches")
    if args.uninstall_after and args.installer_iso is None:
        raise ValueError("--uninstall-after requires --installer-iso")
    iso = args.installer_iso.resolve(strict=True) if args.installer_iso else None
    package = installer_payload(iso, args.windows_version) if iso else None
    output = args.output.resolve()
    if not output.is_relative_to((ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("--output must be a new private directory under artifacts/")
    output.mkdir(parents=True)
    product = args.product.resolve(strict=True)
    old_report: dict | None = None
    old_share: bytes | None = None
    if args.upgrade_from:
        branch, old_report, old_share, old_package = make_upgrade_branch(
            args.upgrade_from.resolve(strict=True), output, args.windows_version, iso)
        preupgrade_hashes = verify_guest_install(branch, product, old_package,
                                                args.windows_version, phase="preupgrade")
        if preupgrade_hashes != old_report["installedFileHashes"]:
            raise RuntimeError("Copied installer branch differs from its accepted installed-file result")
    else:
        branch = make_branch(args.source.resolve(strict=True), output, args.windows_version, iso)
    importer_hash = (hashlib.sha256(package["DOSEIMPORT.EXE"].read_bytes()).hexdigest()
                     if package else stage_importer(branch, product, args.importer.resolve(strict=True)))
    controller = create_controller(output, product)
    name = "T" + uuid.uuid4().hex[:6].upper() + ".BIN"
    pattern = b"DOSE Tools NAT guest import acceptance\r\n" + bytes(range(256))
    payload = (pattern * ((args.payload_bytes + len(pattern) - 1) // len(pattern)))[:args.payload_bytes]
    host_file = output / name
    host_file.write_bytes(payload)
    expected_hash = hashlib.sha256(payload).hexdigest()
    try:
        existing = read_guest_file(branch, "SHARE/" + name)
    except ValueError as error:
        if "Guest directory missing" not in str(error):
            raise
        existing = None
    if existing is not None:
        raise RuntimeError("The randomly selected guest target already exists")
    log = (output / "controller.jsonl").open("w", encoding="utf-8")
    error = (output / "controller-stderr.txt").open("w", encoding="utf-8")
    proc = subprocess.Popen(["dotnet", str(controller), str(branch / "branch.dospc.json")],
                            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=error, text=True, encoding="utf-8", bufsize=1)
    lines: queue.Queue[str] = queue.Queue()
    def read_lines() -> None:
        """Move owner responses off its pipe so each request can have a hard timeout."""
        for line in proc.stdout:
            lines.put(line)
        lines.put("")
    threading.Thread(target=read_lines, daemon=True).start()
    def receive() -> dict:
        """Read one JSON owner result or fail if the finite guest run stalls."""
        try:
            line = lines.get(timeout=240)
        except queue.Empty as error:
            raise TimeoutError("Headless owner did not answer within 240 host seconds") from error
        if not line:
            raise RuntimeError("Headless owner exited early; inspect controller-stderr.txt")
        response = json.loads(line)
        # The one-time CLI code is carried to the guest but never written to disk.
        record = json.loads(line)
        if isinstance(record.get("result"), dict) and "code" in record["result"]:
            record["result"]["code"] = "<redacted>"
        log.write(json.dumps(record) + "\n")
        log.flush()
        if response.get("ok") is False:
            raise RuntimeError(f"Guest controller rejected request: {response['error']}")
        return response
    def call(command: str, **fields):
        """Send one command to the private headless guest owner."""
        proc.stdin.write(json.dumps(dict(command=command, **fields)) + "\n")
        proc.stdin.flush()
        return receive()["result"]
    transfer_failure: str | None = None
    try:
        ready = receive()
        if not ready.get("ready") or ready.get("engine") != "whpx":
            raise RuntimeError("Native headless owner did not start")
        # Normal graphical Win9x startup is an actual cold boot of this fresh COW.
        for _ in range(3):
            call("run", milliseconds=30000)
        mapping = dict(zip("qwertyuiopasdfghjklzxcvbnm",
                           [16,17,18,19,20,21,22,23,24,25,30,31,32,33,34,35,36,37,38,44,45,46,47,48,49,50]))
        mapping.update({".":52, "\\":43, " ":57, "/":53})
        mapping.update(dict(zip("1234567890", [2,3,4,5,6,7,8,9,10,11])))
        def keys(codes):
            """Inject one atomic translated-set1 packet with bounded KBC retries."""
            for _ in range(20):
                result = call("keys", scanCodes=codes)
                if result["accepted"]:
                    return
                if result["error"] != "InputNotAccepted":
                    raise RuntimeError(f"Virtual keyboard rejected packet: {result}")
                call("run", milliseconds=50)
            raise RuntimeError("Virtual keyboard remained full")
        def key(code):
            """Press and release one virtual key through guest execution time."""
            keys([code]); call("run", milliseconds=70)
            keys([code | 128]); call("run", milliseconds=70)
        def start_command(command: str) -> None:
            """Enter one command through the original virtual Win9x Run-dialog sequence."""
            keys([224,91,19]); call("run", milliseconds=150)
            keys([147,224,219]); call("run", milliseconds=900)
            keys([29,30]); call("run", milliseconds=80)
            keys([158,157]); call("run", milliseconds=80)
            for char in command:
                if args.windows_version == "95" and char == "\\":
                    keys([29,56,12]); call("run", milliseconds=70)
                    keys([140,184,157]); call("run", milliseconds=70)
                elif args.windows_version == "95" and char == "/":
                    keys([42,8]); call("run", milliseconds=70)
                    keys([136,170]); call("run", milliseconds=70)
                elif char == "\\":
                    key(43)
                elif char == ":":
                    keys([42,52 if args.windows_version == "95" else 39]); call("run", milliseconds=70)
                    keys([180 if args.windows_version == "95" else 167,170]); call("run", milliseconds=70)
                elif args.windows_version == "95" and char == "y":
                    key(44)
                else:
                    key(mapping[char])
            call("run", milliseconds=1000)
            key(28)
            call("run", milliseconds=2000)
        if iso:
            # Silent installer still executes native Win9x file, registry and shortcut APIs.
            # /apm selects the optional APM package without an unreliable mouse click.
            installer_drive = "r" if args.windows_version == "95" else "d"
            start_command(installer_drive + r":\setup.exe /s /apm")
            call("run", milliseconds=15000)
            setup_frame = call("capture", path=str(output / "after-setup.bgra"))
            (output / "after-setup.json").write_text(json.dumps(setup_frame, indent=2) + "\n", encoding="utf-8")
        lease = call("serve", path=str(host_file))
        code = lease.get("code")
        if not lease.get("serving") or not isinstance(code, str) or re.fullmatch(r"[0-9A-F]{16}", code) is None:
            raise RuntimeError("Gateway did not return one valid 16-hex transfer code")
        if args.prompt_mode:
            start_command(r"c:\dosetools\doseimport.exe")
            prompt_frame = call("capture", path=str(output / "code-prompt-frame.bgra"))
            (output / "code-prompt-frame.json").write_text(json.dumps(prompt_frame, indent=2) + "\n", encoding="utf-8")
            # PromptCode focuses its edit control. Lowercase is valid hexadecimal and
            # maps directly to the existing virtual scan-code table on both guests.
            for char in code.lower():
                key(mapping[char])
            key(28)  # BS_DEFPUSHBUTTON dispatches Import through IsDialogMessageA.
        else:
            bad_code = ("0" if code[0] != "0" else "1") + code[1:]
            start_command(r"c:\dosetools\doseimport.exe " + bad_code.lower())
            call("run", milliseconds=15000)
            rejected = call("gateway")
            if rejected["completed"] or rejected["faulted"] or rejected["canceled"]:
                raise RuntimeError("Bad guest code consumed or faulted the one-time host listener: " + str(rejected))
            bad_frame = call("capture", path=str(output / "bad-code-frame.bgra"))
            (output / "bad-code-frame.json").write_text(json.dumps(bad_frame, indent=2) + "\n", encoding="utf-8")
            # Close only the bad-code guest helper's modal error; retain its host listener.
            key(28)
            call("run", milliseconds=2000)
            start_command(r"c:\dosetools\doseimport.exe " + code.lower())
        gateway = {}
        for _ in range(120):
            call("run", milliseconds=1000)
            gateway = call("gateway")
            if gateway["completed"]:
                break
        if gateway.get("acknowledged") is not True:
            diagnostic = call("status")
            (output / "failure-status.json").write_text(json.dumps(diagnostic, indent=2) + "\n", encoding="utf-8")
            frame = call("capture", path=str(output / "failure-frame.bgra"))
            (output / "failure-frame.json").write_text(json.dumps(frame, indent=2) + "\n", encoding="utf-8")
            transfer_failure = f"Guest importer did not ACK the controlled host file: {gateway}"
        else:
            call("run", milliseconds=5000)
            if args.uninstall_after:
                # The importer has completed its file/ACK work; close only its guest MessageBox.
                key(28)
                start_command(r"c:\dosetools\dsetup.exe /s /uninstall")
                call("run", milliseconds=15000)
        call("exit")
        if proc.wait(timeout=30):
            raise RuntimeError("Controller exited with an error")
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=30)
        error.close()
        log.close()
    # The guest is closed before this independent FAT/overlay readback.
    installed_files = (verify_guest_removal(branch, product, package, args.windows_version)
                       if args.uninstall_after else
                       verify_guest_install(branch, product, package, args.windows_version) if package else None)
    if old_report is not None:
        old_name = old_report["guestFile"].split("\\")[-1]
        if read_guest_file(branch, "SHARE/" + old_name) != old_share:
            raise RuntimeError("Setup update or second import changed the earlier Share file")
    if transfer_failure:
        raise RuntimeError(transfer_failure)
    actual = read_guest_file(branch, "SHARE/" + name)
    if actual != payload:
        raise RuntimeError("Gateway ACK occurred, but the independent guest disk bytes differ")
    (output / "result.json").write_text(json.dumps({"passed": True, "windows": args.windows_version,
        "guestFile": "C:\\Share\\" + name, "bytes": len(actual), "sha256": expected_hash,
        "gatewayAcknowledged": True, "independentFatReadback": True,
        "authenticatedCliCodeUsed": not args.prompt_mode,
        "guestCodePromptUsed": args.prompt_mode,
        "guestCodePromptFrame": "code-prompt-frame.bgra" if args.prompt_mode else None,
        "wrongCodeAttemptedFirst": not args.prompt_mode,
        "gatewayRetainedAfterWrongCode": not args.prompt_mode,
        "importerSha256": importer_hash, "importerStaging": ("native Windows guest installer update from read-only ATAPI ISO"
            if old_report else "native Windows guest installer from read-only ATAPI ISO"
            if iso else "offline DiskTool import into private COW; guest installer not tested"),
        "installerIso": str(iso) if iso else None,
        "installedFileHashes": installed_files if not args.uninstall_after else None,
        "removalEvidence": installed_files if args.uninstall_after else None,
        "uninstallAfter": args.uninstall_after,
        "upgradeFrom": str(args.upgrade_from.resolve()) if args.upgrade_from else None,
        "previousInstallerIso": old_report["installerIso"] if old_report else None,
        "previousGuestFile": old_report["guestFile"] if old_report else None,
        "previousSharePreserved": old_report is not None,
        "source": old_report["source"] if old_report else str(args.source.resolve()), "product": str(product),
        "note": ("Cold boot, native installer, import and silent uninstaller; no guest OS shutdown or sustained throughput is claimed."
            if args.uninstall_after else "Cold boot from an accepted private COW, silent installer update and second import; no guest OS shutdown or sustained throughput is claimed."
            if old_report else "Cold boot, silent native installer and import; no guest OS shutdown or sustained throughput is claimed."
            if iso else "Cold boot and import only; no guest OS shutdown or sustained throughput is claimed.")}, indent=2) + "\n")
    print(f"PASS Win{args.windows_version} {name} {len(actual)} bytes {expected_hash}")


if __name__ == "__main__":
    main()
