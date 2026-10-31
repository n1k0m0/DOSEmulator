// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Runtime;

namespace DOSEmulator.Whpx;

/// <summary>Outer manifest seals the WHP register sidecar beside a complete SnapshotStore media generation.</summary>
public sealed record WhpxSnapshotManifest(string Kind, int Version, string HardwareId,
    string HardwareManifestSha256, string CpuSha256, string WhpxRuntimeSha256);

/// <summary>Atomic, separately identified WHP checkpoints; product .dossav files retain their software-CPU meaning.</summary>
public static class WhpxSnapshotStore
{
    private const string Kind = "DOSE-WHPX-PC-STATE-2";

    public static WhpxSnapshotManifest Capture(WhpxPcSession session, string directory)
    {
        ArgumentNullException.ThrowIfNull(session);
        string target = Path.GetFullPath(directory);
        if (!target.EndsWith(".whpxsav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("WHP checkpoints require a .whpxsav directory.", nameof(directory));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("WHP checkpoint target already exists.");
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        try
        {
            var (hardware, cpu) = session.CaptureWithMedia(Path.Combine(staging, "hardware.dossav"));
            byte[] cpuBytes = JsonSerializer.SerializeToUtf8Bytes(cpu);
            if (cpuBytes.Length is < 1 or > 1_048_576) throw new InvalidDataException("WHP CPU state exceeds the checkpoint limit.");
            File.WriteAllBytes(Path.Combine(staging, "cpu.json"), cpuBytes);
            byte[] hardwareManifest = File.ReadAllBytes(Path.Combine(staging, "hardware.dossav", "snapshot.json"));
            var manifest = new WhpxSnapshotManifest(Kind, 2, hardware.Id,
                Hash(hardwareManifest), Hash(cpuBytes), RuntimeHash());
            File.WriteAllBytes(Path.Combine(staging, "whpx.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            Directory.Move(staging, target);
            return manifest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public static WhpxPcSession Load(string directory, string branchDirectory)
    {
        string source = Path.GetFullPath(directory);
        byte[] manifestBytes = ReadBounded(Path.Combine(source, "whpx.json"), 65536);
        var manifest = JsonSerializer.Deserialize<WhpxSnapshotManifest>(manifestBytes)
            ?? throw new InvalidDataException("Missing WHP checkpoint manifest.");
        if (manifest.Kind != Kind || manifest.Version != 2 || !Guid.TryParseExact(manifest.HardwareId, "N", out _) ||
            manifest.WhpxRuntimeSha256 != RuntimeHash())
            throw new InvalidDataException("Unsupported WHP checkpoint format.");
        byte[] cpuBytes = ReadBounded(Path.Combine(source, "cpu.json"), 1_048_576);
        byte[] hardwareManifest = ReadBounded(Path.Combine(source, "hardware.dossav", "snapshot.json"), 65536);
        if (!Hash(cpuBytes).Equals(manifest.CpuSha256, StringComparison.Ordinal) ||
            !Hash(hardwareManifest).Equals(manifest.HardwareManifestSha256, StringComparison.Ordinal))
            throw new InvalidDataException("WHP checkpoint payload integrity differs.");
        var hardware = JsonSerializer.Deserialize<SnapshotManifest>(hardwareManifest, StateValues.Json)
            ?? throw new InvalidDataException("Missing nested hardware manifest.");
        if (hardware.Id != manifest.HardwareId) throw new InvalidDataException("Nested hardware snapshot identity differs.");
        var cpu = JsonSerializer.Deserialize<WhpxCpuState>(cpuBytes)
            ?? throw new InvalidDataException("Missing WHP CPU register state.");
        PcMachine restored = SnapshotStore.Load(Path.Combine(source, "hardware.dossav"), branchDirectory);
        try { return WhpxPcSession.AttachLoaded(restored, cpu); }
        catch { restored.Dispose(); throw; }
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > int.MaxValue || file.Length > maximum)
            throw new InvalidDataException("WHP checkpoint file size is invalid.");
        byte[] result = new byte[(int)file.Length];
        file.ReadExactly(result);
        return result;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string RuntimeHash() => Hash(File.ReadAllBytes(typeof(WhpxSnapshotStore).Assembly.Location));
}
