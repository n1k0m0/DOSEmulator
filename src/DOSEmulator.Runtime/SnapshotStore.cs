// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Integrity and exact-runtime identity for a complete local running-state generation.</summary>
public sealed record SnapshotManifest(string Kind, int Version, string Id, ulong VirtualNanoseconds,
    int RamBytes, string RamSha256, long StateBytes, string StateSha256, string MediaSha256,
    Dictionary<string, string> Runtime);

/// <summary>Atomically publishes full PC snapshots and prepares isolated resumable machines before adoption.</summary>
public static class SnapshotStore
{
    private const string Kind = "DOSE-PC-STATE-1";

    /// <summary>Captures at one owner boundary, sealing media, raw RAM, raw FPU, devices and named scheduler events.</summary>
    public static SnapshotManifest Capture(PcMachine machine, string directory)
    {
        ArgumentNullException.ThrowIfNull(machine); machine.RequireSnapshotBoundary();
        string target = NewDirectory(directory, ".dossav");
        var state = machine.CaptureHardwareState(); SnapshotValidation.Check(state);
        if (machine.Profile.IsPiixReset) SnapshotValidation.CheckProfile(state, machine.Profile, machine.Memory.Ram.Length);
        var runtime = RuntimeIdentity();
        string staging = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        try
        {
            MediaCheckpointStore.Capture(machine, Path.Combine(staging, "media.dosemedia"), true);
            WriteBytes(Path.Combine(staging, "ram.bin"), machine.Memory.Ram);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json);
            WriteBytes(Path.Combine(staging, "state.json"), data);
            var manifest = new SnapshotManifest(Kind, 1, Guid.NewGuid().ToString("N"), state.Scheduler.Now,
                machine.Memory.Ram.Length, Hash(machine.Memory.Ram), data.LongLength, Hash(data),
                HashFile(Path.Combine(staging, "media.dosemedia", "media.json")), runtime);
            WriteBytes(Path.Combine(staging, "snapshot.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
            Directory.Move(staging, target); return manifest;
        }
        finally { if (Directory.Exists(staging)) RemoveOwnedDirectory(staging); }
    }

    /// <summary>Validates protected inputs, then returns a fully restored PC on fresh branch journals; failures never touch the old PC.</summary>
    public static PcMachine Load(string directory, string branchDirectory)
    {
        try { return LoadChecked(directory, branchDirectory); }
        catch (Exception error) when (error is FormatException or OverflowException)
        { throw new InvalidDataException("Snapshot numeric values are outside their supported representation.", error); }
    }

    /// <summary>Parses and stages an import while keeping every old-machine mutation outside the operation.</summary>
    private static PcMachine LoadChecked(string directory, string branchDirectory)
    {
        string source = Path.GetFullPath(directory), branch = NewDirectory(branchDirectory, ".dosebranch");
        using var manifestFile = Open(Path.Combine(source, "snapshot.json"));
        var manifest = Read<SnapshotManifest>(manifestFile, 65536);
        if (manifest.Kind != Kind || manifest.Version != 1 || !Guid.TryParseExact(manifest.Id, "N", out _) ||
            manifest.RamBytes is < 1048576 or > 268435456 || manifest.StateBytes is < 1 or > 67108864 || manifest.Runtime is null ||
            !SameRuntime(manifest.Runtime, RuntimeIdentity()))
            throw new InvalidDataException("Unsupported snapshot format or different runtime build.");
        using var ramFile = Open(Path.Combine(source, "ram.bin"));
        using var stateFile = Open(Path.Combine(source, "state.json"));
        using var mediaFile = Open(Path.Combine(source, "media.dosemedia", "media.json"));
        if (ramFile.Length != manifest.RamBytes || stateFile.Length != manifest.StateBytes ||
            HashFile(ramFile) != manifest.RamSha256 || HashFile(stateFile) != manifest.StateSha256 || HashFile(mediaFile) != manifest.MediaSha256)
            throw new InvalidDataException("Snapshot payload integrity differs.");
        ramFile.Position = 0; byte[] ram = new byte[manifest.RamBytes]; ramFile.ReadExactly(ram);
        stateFile.Position = 0; var state = Read<HardwareState>(stateFile, 67108864);
        if (state.Scheduler is null || state.Scheduler.Now != manifest.VirtualNanoseconds) throw new InvalidDataException("Snapshot clocks differ.");
        SnapshotValidation.Check(state);
        var (sealedDefinition, sealedMedia) = MediaCheckpointStore.ReadSnapshotMetadata(mediaFile, Path.Combine(source, "media.dosemedia"));
        SnapshotValidation.CheckProfile(state, sealedDefinition.Hardware, manifest.RamBytes);
        SnapshotValidation.CheckIdeMedia(state, sealedDefinition, sealedMedia);
        PcMachine? machine = null; bool published = false;
        try
        {
            var definition = MediaCheckpointStore.CreateBranch(Path.Combine(source, "media.dosemedia"), branch); published = true;
            // Cold PC loads intentionally use the PC display name as profile label. A full resume preserves both independent saved names.
            machine = MachineFactory.FromDefinition(definition with { Name = definition.Hardware.Name });
            machine.RestoreHardwareState(state, ram);
            return machine;
        }
        catch
        {
            try { machine?.Dispose(); }
            finally { if (published) RemoveOwnedDirectory(branch); }
            throw;
        }
    }

    /// <summary>Compares keys and actual hash values; KeyValuePair JSON would omit readonly properties under the state options.</summary>
    private static bool SameRuntime(Dictionary<string, string> saved, Dictionary<string, string> current) =>
        saved.Count == current.Count && current.All(v => saved.TryGetValue(v.Key, out var hash) && hash == v.Value);

    /// <summary>Uses executed assembly bytes, not filenames alone; the first format intentionally accepts only the identical VM build.</summary>
    private static Dictionary<string, string> RuntimeIdentity() => new[]
        { typeof(SnapshotStore).Assembly, typeof(X86Cpu).Assembly, typeof(DeviceScheduler).Assembly, typeof(VgaAdapter).Assembly, typeof(MachineProfile).Assembly }
        .ToDictionary(a => a.GetName().Name!, ExecutedAssemblyIdentity, StringComparer.Ordinal);

    /// <summary>Rejects a replaced on-disk build instead of labeling old loaded code with a new file hash.</summary>
    private static string ExecutedAssemblyIdentity(Assembly assembly)
    {
        using var file = Open(assembly.Location);
        using (var pe = new PEReader(file, PEStreamOptions.LeaveOpen))
        {
            var metadata = pe.GetMetadataReader();
            if (metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
                throw new InvalidOperationException("The loaded VM assembly was replaced on disk; restart before saving or loading snapshots.");
        }
        return HashFile(file);
    }

    /// <summary>Requires an unused target with an existing parent, before creating or deleting anything.</summary>
    private static string NewDirectory(string directory, string extension)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected a new " + extension + " directory.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Snapshot targets must not exist.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Snapshot parent must exist.");
        return target;
    }

    /// <summary>Opens protected metadata or data without permitting host writes or replacement while it is checked.</summary>
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    /// <summary>Hashes bytes without touching an active journal or advancing a guest device.</summary>
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    /// <summary>Hashes one protected stream from its beginning.</summary>
    private static string HashFile(FileStream file) { file.Position = 0; return Convert.ToHexString(SHA256.HashData(file)); }
    /// <summary>Hashes a protected immutable file.</summary>
    private static string HashFile(string path) { using var file = Open(path); return HashFile(file); }
    /// <summary>Creates and durably flushes a fresh payload, never overwriting any existing file.</summary>
    private static void WriteBytes(string path, byte[] data)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(data); file.Flush(true); }
    /// <summary>Reads strict bounded JSON and rejects duplicate keys, including nested case aliases.</summary>
    private static T Read<T>(FileStream file, long maximum)
    {
        if (file.Length > maximum) throw new InvalidDataException("Snapshot document exceeds its size limit.");
        using var doc = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 64 });
        CheckKeys(doc.RootElement);
        return doc.RootElement.Deserialize<T>(StateValues.Json) ?? throw new InvalidDataException("Empty snapshot document.");
    }
    /// <summary>Checks duplicate object keys recursively before any typed deserialization occurs.</summary>
    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject())
            { if (!names.Add(p.Name)) throw new InvalidDataException("Duplicate snapshot field."); CheckKeys(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckKeys(child);
    }
    /// <summary>Removes only this operation's freshly created unpublished staging directory or failed new branch.</summary>
    private static void RemoveOwnedDirectory(string path) => Directory.Delete(Path.GetFullPath(path), true);
}
