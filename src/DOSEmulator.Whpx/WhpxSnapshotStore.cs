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
    private const string Kind = "DOSE-WHPX-PC-STATE-4";

    /// <summary>Seals native CPU, RAM, devices and media into a new separately typed native snapshot generation.</summary>
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
            var manifest = new WhpxSnapshotManifest(Kind, 4, hardware.Id,
                Hash(hardwareManifest), Hash(cpuBytes), RuntimeHash());
            File.WriteAllBytes(Path.Combine(staging, "whpx.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            Directory.Move(staging, target);
            return manifest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>Validates native format, payloads, CPU topology and PC contract before adopting a fresh media branch.</summary>
    public static WhpxPcSession Load(string directory, string branchDirectory)
    {
        var prepared = PrepareLoad(directory, branchDirectory);
        try { return WhpxPcSession.AttachLoaded(prepared.Pc, prepared.Cpu); }
        catch
        {
            prepared.Pc.Dispose();
            string created = Path.GetFullPath(branchDirectory);
            if (Directory.Exists(created) && File.Exists(Path.Combine(created, "branch.dospc.json"))) Directory.Delete(created, true);
            throw;
        }
    }

    /// <summary>Validates CPU and media first, allowing the GUI to release the previous partition before native activation.</summary>
    internal static (PcMachine Pc, WhpxCpuState Cpu) PrepareLoad(string directory, string branchDirectory, bool requireP54c = false)
    {
        string source = Path.GetFullPath(directory);
        byte[] manifestBytes = ReadBounded(Path.Combine(source, "whpx.json"), 65536);
        var manifest = JsonSerializer.Deserialize<WhpxSnapshotManifest>(manifestBytes)
            ?? throw new InvalidDataException("Missing WHP checkpoint manifest.");
        if (manifest.Kind != Kind || manifest.Version != 4 || !Guid.TryParseExact(manifest.HardwareId, "N", out _) ||
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
        WhpxMappedMachine.ValidateCpuState(cpu);
        if (requireP54c && cpu.Contract != WhpxCpuContract.IntelP54cB5Subset) throw new InvalidDataException("GUI WHPX requires a P54C checkpoint.");
        string mediaDirectory = Path.Combine(source, "hardware.dossav", "media.dosemedia");
        using (var media = new FileStream(Path.Combine(mediaDirectory, "media.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var definition = MediaCheckpointStore.ReadSnapshotDefinition(media, mediaDirectory);
            if (cpu.Contract != (definition.Hardware.IsP54c ? WhpxCpuContract.IntelP54cB5Subset : WhpxCpuContract.Intel486Dx) ||
                cpu.NominalCpuHz != (definition.Hardware.IsP54c ? (uint)definition.Hardware.CpuHz : 0))
                throw new InvalidDataException("Native CPU contract differs from the saved PC profile.");
        }
        PcMachine restored = SnapshotStore.LoadExternalCpu(Path.Combine(source, "hardware.dossav"), branchDirectory);
        return (restored, cpu);
    }

    /// <summary>Reads one named snapshot payload only within its explicit byte limit.</summary>
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > int.MaxValue || file.Length > maximum)
            throw new InvalidDataException("WHP checkpoint file size is invalid.");
        byte[] result = new byte[(int)file.Length];
        file.ReadExactly(result);
        return result;
    }

    /// <summary>Computes the payload's complete SHA-256 integrity value.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Binds native snapshots to the exact running WHPX backend assembly.</summary>
    private static string RuntimeHash() => Hash(File.ReadAllBytes(typeof(WhpxSnapshotStore).Assembly.Location));
}
