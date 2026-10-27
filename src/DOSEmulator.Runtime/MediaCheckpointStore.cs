// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Publishes complete sets of media generations and prepares independent cold-start branches.</summary>
public static class MediaCheckpointStore
{
    private const string Kind = "DOSE-MEDIA-1", Manifest = "media.json";
    private static readonly JsonSerializerOptions Options = new(IpcProtocol.JsonOptions)
    { WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>
    /// Captures all four IDE and both floppy positions between CPU steps on the sole emulation owner.
    /// Synchronous disk commits are complete; the running machine and its active journals remain usable.
    /// </summary>
    public static MediaCheckpoint Capture(PcMachine machine, string directory) => Capture(machine, directory, false);

    /// <summary>Full snapshots may additionally seal the known immutable in-memory test medium as a private RAW base.</summary>
    internal static MediaCheckpoint Capture(PcMachine machine, string directory, bool includeMemory)
    {
        ArgumentNullException.ThrowIfNull(machine);
        string target = Destination(directory, ".dosemedia");
        var bindings = Enumerable.Range(0, 4).Select(slot => (Device: "ide", Slot: slot, Disk: machine.DiskAt(slot)))
            .Concat(Enumerable.Range(0, 2).Select(slot => (Device: "floppy", Slot: slot, Disk: machine.Floppy.MediaAt(slot))))
            .Where(v => v.Disk is not null).ToArray();
        if (bindings.Any(v => v.Disk is not (CopyOnWriteDisk or ReadOnlyRawDisk) && !(includeMemory && v.Disk is MemoryDisk)))
            throw new InvalidOperationException("Media checkpoints require file-backed RAW media; memory/custom devices are not supported.");
        MachineDefinition definition = machine.CaptureDefinition();
        ulong now = machine.Scheduler.Now;
        MediaCheckpoint? checkpoint = null;
        Publish(target, staging =>
        {
            var rows = new List<MediaGeneration>();
            var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (device, slot, disk) in bindings)
            {
                string basis;
                if (disk is MemoryDisk memory)
                {
                    string name = $"{device}{slot}.img";
                    using (var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] sector = new byte[512];
                        for (long lba = 0; lba < memory.SectorCount; lba++) { memory.ReadSector(lba, sector); output.Write(sector); }
                        output.Flush(true);
                    }
                    basis = Path.Combine(staging, name);
                    var attachment = new DiskAttachment(slot, Path.Combine(target, name), null);
                    definition = device == "ide" ? definition with { BuiltInTestDisk = false, HardDisks = definition.HardDisks.Append(attachment).ToArray() }
                        : definition with { Floppies = definition.Floppies.Append(attachment).ToArray() };
                }
                else basis = ((IFileBackedBlockDevice)disk!).Image.BasePath;
                if (!identities.TryGetValue(basis, out string? identity))
                {
                    using var source = OpenProtected(basis);
                    identity = Hash(source); identities.Add(basis, identity);
                }
                string? journal = disk is CopyOnWriteDisk ? JournalName(device, slot) : null;
                OverlayGeneration? exported = disk is CopyOnWriteDisk writable
                    ? writable.ExportGeneration(Path.Combine(staging, journal!)) : null;
                rows.Add(new(device, slot, disk!.SectorCount, identity, journal, exported?.Bytes ?? 0, exported?.Sha256));
            }
            // These paths are relative to the final checkpoint, not the temporary staging directory.
            DiskAttachment Relative(DiskAttachment item, string device) => item with
            {
                ImagePath = Path.GetRelativePath(target, item.ImagePath),
                OverlayPath = item.OverlayPath is null ? null : JournalName(device, item.Slot)
            };
            var portable = definition with
            {
                HardDisks = definition.HardDisks.Select(v => Relative(v, "ide")).ToArray(),
                Floppies = definition.Floppies.Select(v => Relative(v, "floppy")).ToArray()
            };
            checkpoint = new(1, Kind, Guid.NewGuid().ToString("N"), now, portable, rows.ToArray());
            WriteDocument(Path.Combine(staging, Manifest), checkpoint);
        });
        return checkpoint!;
    }

    /// <summary>
    /// Checks every dependency before publishing a new independent media branch and cold-start PC file.
    /// Read locks protect bases and sealed journals through copying; no active machine or old journal is replaced.
    /// </summary>
    public static MachineDefinition CreateBranch(string checkpointDirectory, string branchDirectory)
    {
        string sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkpointDirectory));
        string target = Destination(branchDirectory, ".dosebranch");
        using var manifest = OpenProtected(Path.Combine(sourceDirectory, Manifest));
        var checkpoint = ReadCheckpoint(manifest);
        MachineDefinition definition = Validate(checkpoint, sourceDirectory);
        var locked = new List<FileStream>();
        var journals = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var bases = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in checkpoint.Media)
            {
                var attachment = Attachments(definition, row.Device).Single(v => v.Slot == row.Slot);
                if (!bases.TryGetValue(attachment.ImagePath, out var basis))
                {
                    basis = OpenProtected(attachment.ImagePath); locked.Add(basis); bases.Add(attachment.ImagePath, basis);
                }
                if (basis.Length / 512 != row.Sectors || basis.Length % 512 != 0 || Hash(basis) != row.BaseSha256)
                    throw new InvalidDataException("Checkpoint base image identity or capacity differs.");
                if (row.JournalName is not null)
                {
                    var journal = OpenProtected(attachment.OverlayPath!); locked.Add(journal);
                    if (journal.Length != row.JournalBytes || Hash(journal) != row.JournalSha256)
                        throw new InvalidDataException("Sealed checkpoint journal identity or length differs.");
                    journals.Add(row.JournalName, journal);
                }
            }
            MachineDefinition? branch = null;
            Publish(target, staging =>
            {
                foreach (var (name, input) in journals)
                {
                    using (var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { input.Position = 0; input.CopyTo(output); output.Flush(true); }
                    var row = checkpoint.Media.Single(v => v.JournalName == name);
                    var attachment = Attachments(definition, row.Device).Single(v => v.Slot == row.Slot);
                    // Normal COW validation checks header, base identity, every LBA and checksum on the private copy.
                    using var checkedCopy = new CopyOnWriteDisk(attachment.ImagePath, Path.Combine(staging, name));
                }
                // A published branch names its own journals and keeps original bases read-only.
                DiskAttachment Relative(DiskAttachment item) => item with
                {
                    ImagePath = Path.GetRelativePath(target, item.ImagePath),
                    OverlayPath = item.OverlayPath is null ? null : Path.GetFileName(item.OverlayPath)
                };
                branch = definition with
                {
                    HardDisks = definition.HardDisks.Select(Relative).ToArray(),
                    Floppies = definition.Floppies.Select(Relative).ToArray()
                };
                WriteDocument(Path.Combine(staging, "branch.dospc.json"), branch);
            });
            return MachineDefinitionStore.Resolve(branch!, target);
        }
        finally { foreach (var file in locked) file.Dispose(); }
    }

    /// <summary>Reads the already protected metadata for full-state/profile checks without creating a media branch.</summary>
    internal static MachineDefinition ReadSnapshotDefinition(FileStream manifest, string directory) =>
        Validate(ReadCheckpoint(manifest), directory);

    /// <summary>Uses the same bounded, strict metadata decoder for full-state preflight and media branching.</summary>
    private static MediaCheckpoint ReadCheckpoint(FileStream manifest)
    {
        if (manifest.Length > 1024 * 1024) throw new InvalidDataException("Media checkpoint manifest exceeds 1 MiB.");
        manifest.Position = 0;
        using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<MediaCheckpoint>(Options) ?? throw new InvalidDataException("Empty media checkpoint.");
    }

    /// <summary>Rejects unsupported formats, absent/duplicate slots and every journal path outside its fixed generation name.</summary>
    private static MachineDefinition Validate(MediaCheckpoint checkpoint, string directory)
    {
        if (checkpoint.SchemaVersion != 1 || checkpoint.Kind != Kind || !Guid.TryParseExact(checkpoint.Id, "N", out _) ||
            checkpoint.Machine is null || checkpoint.Machine.BuiltInTestDisk || checkpoint.Media is null || checkpoint.Media.Length > 6)
            throw new InvalidDataException("Unsupported or incomplete media-only checkpoint.");
        var definition = MachineDefinitionStore.Resolve(checkpoint.Machine, directory);
        int count = definition.HardDisks.Length + definition.Floppies.Length;
        if (checkpoint.Media.Length != count || checkpoint.Media.Any(v => v is null) ||
            checkpoint.Media.Select(v => (v.Device, v.Slot)).Distinct().Count() != count)
            throw new InvalidDataException("Checkpoint media slots differ from its PC definition.");
        foreach (var row in checkpoint.Media)
        {
            var attachment = Attachments(definition, row.Device).SingleOrDefault(v => v.Slot == row.Slot)
                ?? throw new InvalidDataException("Unexpected checkpoint media slot.");
            if (row.Sectors <= 0 || row.Sectors > RawDiskImage.MaximumBytes / 512 || !IsHash(row.BaseSha256))
                throw new InvalidDataException("Invalid checkpoint base identity or capacity.");
            if (row.Device == "floppy")
            {
                var geometry = FloppyGeometry.FromId(row.Slot == 0 ? definition.Hardware.FloppyDriveA : definition.Hardware.FloppyDriveB);
                if (geometry is null || geometry.SectorCount != row.Sectors)
                    throw new InvalidDataException("Checkpoint floppy does not fit its configured physical drive.");
            }
            if (attachment.OverlayPath is null)
            {
                if (row.JournalName is not null || row.JournalBytes != 0 || row.JournalSha256 is not null)
                    throw new InvalidDataException("A read-only base cannot claim a writable generation.");
            }
            else if (row.JournalName != JournalName(row.Device, row.Slot) ||
                !MachineDefinitionStore.Same(attachment.OverlayPath, Path.Combine(directory, row.JournalName)) ||
                row.JournalBytes < 48 || (row.JournalBytes - 48) % 552 != 0 || !IsHash(row.JournalSha256))
                throw new InvalidDataException("Invalid sealed journal path, length or content identity.");
        }
        return definition;
    }

    /// <summary>Selects one explicit media bus without accepting aliases for unknown future device types.</summary>
    private static DiskAttachment[] Attachments(MachineDefinition definition, string device) => device switch
    {
        "ide" => definition.HardDisks, "floppy" => definition.Floppies,
        _ => throw new InvalidDataException("Unknown checkpoint media bus.")
    };

    /// <summary>Produces a fixed child filename; manifest paths cannot choose arbitrary journal sources or destinations.</summary>
    private static string JournalName(string device, int slot) => $"{device}{slot}.cow";

    /// <summary>Accepts only canonical uppercase SHA-256 text, avoiding ambiguous partial identities.</summary>
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>Hashes an already protected handle from its beginning, never trusting a manifest's path alone.</summary>
    private static string Hash(FileStream file) { file.Position = 0; return Convert.ToHexString(SHA256.HashData(file)); }

    /// <summary>Holds a dependency against host writes and deletion while validating and copying its bytes.</summary>
    private static FileStream OpenProtected(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Requires a new explicitly typed directory and an existing parent; no existing file or folder is replaced.</summary>
    private static string Destination(string path, string extension)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a new " + extension + " directory.");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Checkpoint destinations must not already exist.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Checkpoint parent directory must exist.");
        return path;
    }

    /// <summary>Writes and flushes one complete metadata document into the unpublished generation directory.</summary>
    private static void WriteDocument<T>(string path, T value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, value, Options); file.Flush(true);
    }

    /// <summary>Publishes one prepared directory by rename; failure removes only files in our own fresh staging directory.</summary>
    private static void Publish(string target, Action<string> prepare)
    {
        string parent = Path.GetDirectoryName(target)!;
        string staging = Path.Combine(parent, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        try { prepare(staging); Directory.Move(staging, target); }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (string file in Directory.GetFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    /// <summary>Rejects duplicate JSON keys including case aliases before binding any format or media data.</summary>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate checkpoint property: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
