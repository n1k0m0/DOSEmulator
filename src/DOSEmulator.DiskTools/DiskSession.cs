// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Streams;

namespace DOSEmulator.DiskTools;

/// <summary>One checked FAT partition or unpartitioned floppy volume.</summary>
public sealed record DiskVolume(int Index, long StartBytes, long Length, string FileSystem, string Label)
{
    /// <summary>Displays the supported filesystem, label and volume capacity.</summary>
    public override string ToString() => $"{Index + 1}: {FileSystem} {Label} ({Length / 1048576.0:N1} MiB)";
}

/// <summary>One immediate directory entry, with a guest-relative path independent from host path syntax.</summary>
public sealed record DiskEntry(string Name, string GuestPath, bool IsDirectory, long Length, DateTime Modified);

/// <summary>Progress notification shared by the CLI and graphical explorer.</summary>
public sealed record TransferProgress(string Message, long Bytes = 0);

/// <summary>Offline FAT browsing and transactional, verified COW imports; source IMG files are never writable.</summary>
public sealed class DiskSession : IDisposable
{
    private DiskImageStream _image;
    private readonly object _gate = new();
    private readonly Encoding _encoding;
    private bool _disposed;
    /// <summary>Explicit immutable image/journal binding owned by this session.</summary>
    public DiskSelection Selection { get; }
    /// <summary>Supported physical FAT volumes discovered without starting a VM.</summary>
    public IReadOnlyList<DiskVolume> Volumes { get; }
    /// <summary>Whether the selected machine has a writable journal binding.</summary>
    public bool CanImport => Selection.CowPath is not null;

    /// <summary>Locks selected source media and discovers supported FAT volumes without starting an emulator.</summary>
    public DiskSession(DiskSelection selection, int codePage = 437)
    {
        if (codePage is not (437 or 850)) throw new ArgumentOutOfRangeException(nameof(codePage));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _encoding = Encoding.GetEncoding(codePage);
        Selection = selection with { ImagePath = Path.GetFullPath(selection.ImagePath),
            CowPath = selection.CowPath is null ? null : Path.GetFullPath(selection.CowPath) };
        _image = new(Selection.ImagePath, Selection.CowPath);
        try { Volumes = Discover(); }
        catch { _image.Dispose(); throw; }
    }

    /// <summary>Finds unpartitioned FAT or primary/logical volumes using the maintained partition reader.</summary>
    private List<DiskVolume> Discover()
    {
        List<DiskVolume> result = [];
        _image.Position = 0;
        if (FatFileSystem.Detect(_image)) Add(0, _image.Length);
        else
        {
            _image.Position = 0;
            var manager = new VolumeManager(_image);
            foreach (var volume in manager.GetLogicalVolumes()) Add(checked(volume.PhysicalStartSector * 512), volume.Length);
        }
        if (result.Count == 0) throw new InvalidDataException("No supported FAT12, FAT16 or FAT32 volume was found.");
        return result;

        // Checks a physical volume boundary and detects supported FAT before exposing it.
        void Add(long start, long length)
        {
            if (start < 0 || length <= 0 || start > _image.Length - length) throw new InvalidDataException("Partition bounds exceed the image.");
            using var stream = new PartitionStream(_image, start, length);
            if (!FatFileSystem.Detect(stream)) return;
            stream.Position = 0;
            using var fs = new FatFileSystem(stream, Ownership.None);
            if (fs.SectorSize != 512 || fs.TotalSectors > length / 512) throw new InvalidDataException("Unsupported FAT geometry.");
            result.Add(new(result.Count, start, length, fs.FatVariant.ToString().ToUpperInvariant(), fs.VolumeLabel.Trim()));
        }
    }

    /// <summary>Opens a FAT view restricted to a volume that actually belongs to this session.</summary>
    private FatFileSystem Open(DiskImageStream image, DiskVolume volume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Volumes.Contains(volume)) throw new ArgumentException("Unknown volume.");
        var stream = new PartitionStream(image, volume.StartBytes, volume.Length);
        try
        {
            var fs = new FatFileSystem(stream, Ownership.Dispose);
            fs.FatOptions.FileNameEncoding = _encoding;
            return fs;
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Lists only immediate entries, including hidden/system files and their actual long names.</summary>
    public DiskEntry[] List(DiskVolume volume, string directory = "")
    {
        lock (_gate)
        {
            using var fs = Open(_image, volume);
            return fs.GetFileSystemEntries(TransferPaths.Guest(directory)).Select(path =>
            {
                bool dir = (fs.GetAttributes(path) & FileAttributes.Directory) != 0;
                string normalized = path.TrimStart('\\');
                return new DiskEntry(normalized.Split('\\')[^1], normalized, dir, dir ? 0 : fs.GetFileLength(path), fs.GetLastWriteTime(path));
            }).OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Copies selected files/directories into one entirely new host directory; existing host files are never overwritten.</summary>
    public string Export(DiskVolume volume, IEnumerable<string> guestPaths, string destination,
        CancellationToken cancel = default, IProgress<TransferProgress>? progress = null)
    {
        lock (_gate)
        {
            destination = Path.GetFullPath(destination);
            TransferPaths.Leaf(Path.GetFileName(destination));
            TransferPaths.NoLinks(destination);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The export destination must be a new directory.");
            string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Choose an export directory.");
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            string stage = Path.Combine(parent, ".doseexport-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(stage) || File.Exists(stage)) throw new IOException("Private export destination already exists.");
            Directory.CreateDirectory(stage);
            try
            {
                using var fs = Open(_image, volume);
                int entries = 0;
                foreach (string path in guestPaths.Select(TransferPaths.Guest).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (path.Length == 0) throw new IOException("Select files or directories, not the volume root.");
                    Copy(path, stage, 0);
                }
                cancel.ThrowIfCancellationRequested();
                TransferPaths.NoLinks(parent);
                Directory.Move(stage, destination);
                return destination;

                // Traverses one bounded subtree while checking cancellation and safe host/guest names.
                void Copy(string guest, string targetParent, int depth)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (depth > 64 || ++entries > 1000000) throw new IOException("Directory traversal limit exceeded.");
                    string output = TransferPaths.Child(targetParent, guest.Split('\\')[^1]);
                    if (fs.DirectoryExists(guest))
                    {
                        Directory.CreateDirectory(output);
                        foreach (string child in fs.GetFileSystemEntries(guest)) Copy(TransferPaths.Guest(child), output, depth + 1);
                    }
                    else
                    {
                        progress?.Report(new("Exporting " + guest));
                        using var source = fs.OpenFile(guest, FileMode.Open, FileAccess.Read);
                        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                        CopyAndCheck(source, target, cancel);
                        target.Flush(true);
                    }
                }
            }
            finally
            {
                // This unique staging tree contains only files created above.
                if (Directory.Exists(stage) && Path.GetDirectoryName(stage) == parent &&
                    Path.GetFileName(stage).StartsWith(".doseexport-", StringComparison.Ordinal))
                    Directory.Delete(stage, true);
            }
        }
    }

    /// <summary>Copies complete host files/folders into a private COW, verifies file readback, then atomically publishes it.</summary>
    public void Import(DiskVolume volume, IEnumerable<string> hostPaths, string guestDirectory = "", bool overwrite = false,
        CancellationToken cancel = default, IProgress<TransferProgress>? progress = null)
    {
        string[] sources = hostPaths.Select(Path.GetFullPath).ToArray();
        string directory = TransferPaths.Guest(guestDirectory);
        if (sources.Length == 0) throw new ArgumentException("Select at least one host file or folder.");
        Edit(volume, (fs, verified) =>
        {
            if (!fs.DirectoryExists(directory)) throw new DirectoryNotFoundException(directory);
            int count = 0;
            foreach (string source in sources) Copy(source, directory, 0);

            // Traverses one bounded subtree while checking cancellation and safe host/guest names.
            void Copy(string host, string parent, int depth)
            {
                cancel.ThrowIfCancellationRequested();
                TransferPaths.NoLinks(host);
                if (depth > 64 || ++count > 1000000) throw new IOException("Directory traversal limit exceeded.");
                if (string.Equals(host, Selection.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, Selection.CowPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A disk cannot import its own image or changes file.");
                string leaf = TransferPaths.Leaf(Path.GetFileName(Path.TrimEndingDirectorySeparator(host)));
                string guest = parent.Length == 0 ? leaf : parent + "\\" + leaf;
                if (Directory.Exists(host))
                {
                    if (fs.FileExists(guest)) throw new IOException("A file already occupies the target directory: " + guest);
                    fs.CreateDirectory(guest);
                    verified.TryAdd(guest, null);
                    foreach (string child in Directory.EnumerateFileSystemEntries(host)) Copy(child, guest, depth + 1);
                }
                else
                {
                    if (fs.DirectoryExists(guest) || !overwrite && fs.FileExists(guest))
                        throw new IOException("The guest target already exists: " + guest + ". Enable Replace existing files to replace a file.");
                    progress?.Report(new("Importing " + guest));
                    using var input = new FileStream(host, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var output = fs.OpenFile(guest, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        int n;
                        while ((n = input.Read(buffer)) != 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, n);
                            hash.AppendData(buffer, 0, n);
                        }
                    }
                    fs.SetLastWriteTimeUtc(guest, File.GetLastWriteTimeUtc(host));
                    verified.Add(guest, hash.GetHashAndReset());
                }
            }
        }, cancel, progress);
    }

    /// <summary>Creates one new guest directory through the same private-journal transaction.</summary>
    public void CreateDirectory(DiskVolume volume, string path, CancellationToken cancel = default)
    {
        path = TransferPaths.Guest(path);
        if (path.Length == 0) throw new IOException("Enter a folder name.");
        Edit(volume, (fs, verified) =>
        {
            if (fs.DirectoryExists(path) || fs.FileExists(path)) throw new IOException("The guest target already exists.");
            fs.CreateDirectory(path);
            verified.Add(path, null);
        }, cancel, null);
    }

    /// <summary>Prepares and verifies an edit before replacing one journal, retaining its complete previous generation.</summary>
    private void Edit(DiskVolume volume, Action<FatFileSystem, Dictionary<string, byte[]?>> action,
        CancellationToken cancel, IProgress<TransferProgress>? progress)
    {
        lock (_gate)
        {
            if (!CanImport) throw new IOException("This VM attaches the image read-only. Open a writable machine variant or open the IMG with its own changes file.");
            string cow = Selection.CowPath!;
            string parent = Path.GetDirectoryName(cow)!;
            TransferPaths.NoLinks(parent);
            Directory.CreateDirectory(parent);
            string candidate = Path.Combine(parent, ".doseedit-" + Guid.NewGuid().ToString("N") + ".cow");
            byte[] identity = _image.Identity;
            bool hadCow = File.Exists(cow);
            try
            {
                progress?.Report(new("Preparing private changes…"));
                _image.CopyJournal(candidate);
                Dictionary<string, byte[]?> verified = new(StringComparer.OrdinalIgnoreCase);
                using (var writable = new DiskImageStream(Selection.ImagePath, candidate, true, identity))
                {
                    using (var fs = Open(writable, volume)) action(fs, verified);
                    InvalidateFat32Hints(writable, volume);
                    writable.Flush();
                }
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new("Verifying imported bytes…"));
                // Keep a read lease on the replacement across the rename. It
                // permits our rename while excluding a competing guest writer.
                using var prepared = new DiskImageStream(Selection.ImagePath, candidate, knownIdentity: identity, allowRename: true);
                using (var fs = Open(prepared, volume))
                    foreach (var pair in verified)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (pair.Value is null)
                        {
                            if (!fs.DirectoryExists(pair.Key)) throw new IOException("Imported folder verification failed: " + pair.Key);
                            continue;
                        }
                        using var file = fs.OpenFile(pair.Key, FileMode.Open, FileAccess.Read);
                        if (!SHA256.HashData(file).AsSpan().SequenceEqual(pair.Value))
                            throw new IOException("Imported file verification failed: " + pair.Key);
                    }
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new("Saving changes…"));
                // Back up while the old read lease still excludes every writer.
                if (hadCow) _image.CopyJournal(cow + ".backup-" + Guid.NewGuid().ToString("N"), includeUncommittedTail: true);
                cancel.ThrowIfCancellationRequested();
                // Existing source handles deny writes/deletion until this point.
                // The same-directory rename fails if another owner holds the
                // old journal. The prepared lease excludes writers to the new
                // generation throughout its atomic replacement and adoption.
                _image.Dispose();
                try
                {
                    File.Move(candidate, cow, overwrite: hadCow);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("The changes file could not be replaced. Close other disk tools and check that its folder is writable. The prepared changes were not published.", error);
                }
                finally { _image = new(Selection.ImagePath, Selection.CowPath, knownIdentity: identity); }
            }
            finally { if (File.Exists(candidate)) File.Delete(candidate); }
        }
    }

    /// <summary>Invalidates primary/backup FAT32 allocation hints after edits; Windows will recount the actual FAT.</summary>
    private static void InvalidateFat32Hints(DiskImageStream disk, DiskVolume volume)
    {
        byte[] boot = new byte[512];
        disk.Position = volume.StartBytes; disk.ReadExactly(boot);
        if (BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22)) != 0) return;
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
        int info = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(48));
        int backup = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50));
        foreach (int sector in new[] { info, backup > 0 && backup < reserved ? backup + info : -1 }.Distinct())
        {
            if (info <= 0 || info >= reserved || sector <= 0 || sector >= reserved) continue;
            byte[] bytes = new byte[512];
            disk.Position = checked(volume.StartBytes + sector * 512L); disk.ReadExactly(bytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x41615252 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(484)) != 0x61417272) continue;
            bytes.AsSpan(488, 8).Fill(0xFF);
            disk.Position = volume.StartBytes + sector * 512L; disk.Write(bytes);
        }
    }

    /// <summary>Streams the complete current disk into a new standalone IMG, with independent full readback.</summary>
    public void SaveMergedImage(string destination, CancellationToken cancel = default, IProgress<TransferProgress>? progress = null)
    {
        lock (_gate)
        {
            destination = Path.GetFullPath(destination);
            TransferPaths.Leaf(Path.GetFileName(destination));
            TransferPaths.NoLinks(destination);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The destination already exists.");
            string partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                progress?.Report(new("Writing complete IMG…"));
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    _image.Position = 0;
                    CopyAndCheck(_image, output, cancel);
                    output.Flush(true);
                }
                cancel.ThrowIfCancellationRequested();
                File.Move(partial, destination, false);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }

    /// <summary>Hashes copied bytes and checks a separately reread destination before publication.</summary>
    private static void CopyAndCheck(Stream input, Stream output, CancellationToken cancel)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int n;
        while ((n = input.Read(buffer)) != 0)
        {
            cancel.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            digest.AppendData(buffer, 0, n);
        }
        output.Flush();
        output.Position = 0;
        if (!SHA256.HashData(output).AsSpan().SequenceEqual(digest.GetHashAndReset()))
            throw new IOException("Export readback verification failed.");
    }

    /// <summary>Releases media locks so the unchanged emulator can start this VM again.</summary>
    public void Dispose() { lock (_gate) { if (!_disposed) { _disposed = true; _image.Dispose(); } } }
}
