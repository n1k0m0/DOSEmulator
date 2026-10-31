// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using DOSEmulator.Contracts;

namespace DOSEmulator.Runtime;

/// <summary>Shared GUI/headless launch interpretation so saved PCs and direct media options cannot silently disagree.</summary>
public static class MachineLaunch
{
    /// <summary>Remembers legacy launch configurations in explicit frontend storage; snapshots remain unbound and media are never copied.</summary>
    public static PcMachine OpenPersistent(Func<string, string?> value, string directory, out string? definitionPath)
    {
        if (value("--load-snapshot") is not null) { definitionPath = null; return Open(value); }
        if (value("--machine") is { } explicitPath) { definitionPath = Path.GetFullPath(explicitPath); return Open(value); }
        if (value("--snapshot-branch") is not null) throw new ArgumentException("--snapshot-branch requires --load-snapshot.");
        string[] names = ["--profile", "--disk", "--overlay", "--disk1", "--overlay1", "--disk2", "--overlay2", "--disk3", "--overlay3",
            "--cdrom", "--floppy-a", "--floppy-a-overlay", "--floppy-b", "--floppy-b-overlay", "--boot"];
        string key = string.Join("\n", names.Select(name => name + "=" + (value(name) is { } item ? name == "--boot" ? item : Path.GetFullPath(item) : "")));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        directory = Path.GetFullPath(directory); definitionPath = Path.Combine(directory, hash + ".dospc.json");
        if (File.Exists(definitionPath)) return MachineFactory.FromDefinition(MachineDefinitionStore.Load(definitionPath));
        var machine = Open(value);
        try
        {
            Directory.CreateDirectory(directory);
            MachineDefinitionStore.Save(definitionPath, machine.CaptureDefinition()); return machine;
        }
        catch { machine.Dispose(); throw; }
    }

    /// <summary>Opens either a saved machine or explicit legacy media; mixing both is rejected before files are opened.</summary>
    public static PcMachine Open(Func<string, string?> value)
    {
        if (value("--load-snapshot") is { } snapshot)
        {
            string[] overrides = ["--machine", "--profile", "--disk", "--overlay", "--disk1", "--overlay1", "--disk2", "--overlay2", "--disk3", "--overlay3",
                "--cdrom", "--floppy-a", "--floppy-a-overlay", "--floppy-b", "--floppy-b-overlay", "--boot", "--serial-echo", "--parallel-sink", "--adc-test-signal", "--sb16-adc-test-signal"];
            if (overrides.Any(o => value(o) is not null)) throw new ArgumentException("A snapshot supplies its hardware, media and virtual peers.");
            string branch = value("--snapshot-branch") ?? throw new ArgumentException("--load-snapshot requires a new --snapshot-branch directory.");
            return SnapshotStore.Load(snapshot, branch);
        }
        if (value("--snapshot-branch") is not null) throw new ArgumentException("--snapshot-branch requires --load-snapshot.");
        if (value("--machine") is { } path)
        {
            string[] overrides = ["--profile", "--disk", "--overlay", "--disk1", "--overlay1", "--disk2", "--overlay2", "--disk3", "--overlay3",
                "--cdrom", "--floppy-a", "--floppy-a-overlay", "--floppy-b", "--floppy-b-overlay", "--boot"];
            if (overrides.Any(o => value(o) is not null)) throw new ArgumentException("Use --machine by itself; edit the saved PC to change its hardware or media.");
            return MachineFactory.FromDefinition(MachineDefinitionStore.Load(path));
        }
        var profile = value("--profile") is { } profilePath
            ? JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(profilePath), IpcProtocol.JsonOptions) ?? throw new ArgumentException("Empty profile.") : new();
        return MachineFactory.FromImagesWithOptical(profile, value("--cdrom"), value("--disk"), value("--overlay"), value("--floppy-a"), value("--floppy-a-overlay"),
            value("--floppy-b"), value("--floppy-b-overlay"), value("--boot"), value("--disk1"), value("--overlay1"), value("--disk2"),
            value("--overlay2"), value("--disk3"), value("--overlay3"));
    }
}
