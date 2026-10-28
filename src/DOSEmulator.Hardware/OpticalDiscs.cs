// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

/// <summary>Read-only cooked optical blocks, deliberately distinct from 512-byte ATA hard disks.</summary>
public interface IReadOnlyOpticalDisc : IDisposable
{
    /// <summary>Cooked user bytes per supported CD-ROM logical block.</summary>
    const int BlockSize = 2048;
    /// <summary>Number of complete user-data blocks on the inserted medium.</summary>
    long BlockCount { get; }
    /// <summary>Human-readable media name without a host-device binding.</summary>
    string Name { get; }
    /// <summary>Reads one complete logical block without modifying the protected source.</summary>
    void ReadBlock(long lba, Span<byte> destination);
    /// <summary>Acquires a separate read-only lease for a freshly constructed PC.</summary>
    IReadOnlyOpticalDisc Reopen();
}

/// <summary>Identifies a protected cooked ISO basis for machine files and sealed snapshot generations.</summary>
public interface IFileBackedOpticalDisc
{
    /// <summary>Canonical path of the read-only 2048-byte image, with no writable journal.</summary>
    string ImagePath { get; }
}

/// <summary>Protects one cooked ISO image against host writers for the lifetime of its owned lease.</summary>
public sealed class ReadOnlyIsoImage : IReadOnlyOpticalDisc, IFileBackedOpticalDisc
{
    private readonly FileStream _stream;
    /// <summary>Canonical protected source path; it never names a physical host optical drive.</summary>
    public string ImagePath { get; }
    /// <summary>Count of complete cooked blocks, bounded by the implemented 32-bit packet address space.</summary>
    public long BlockCount { get; }
    /// <summary>Media display name, without interpreting its filesystem.</summary>
    public string Name => Path.GetFileName(ImagePath);

    /// <summary>Opens an existing nonempty cooked image with read sharing only; malformed extents close immediately.</summary>
    public ReadOnlyIsoImage(string path)
    {
        ImagePath = Path.GetFullPath(path);
        _stream = new(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (_stream.Length == 0 || _stream.Length % IReadOnlyOpticalDisc.BlockSize != 0 ||
                _stream.Length / IReadOnlyOpticalDisc.BlockSize > 0x1_0000_0000L)
                throw new InvalidDataException("A cooked ISO requires complete 2048-byte blocks within the 32-bit packet address space.");
            BlockCount = _stream.Length / IReadOnlyOpticalDisc.BlockSize;
        }
        catch { _stream.Dispose(); throw; }
    }

    /// <summary>Reads exactly one bounded block; invalid requests do not reposition or partially consume the source.</summary>
    public void ReadBlock(long lba, Span<byte> destination)
    {
        if ((ulong)lba >= (ulong)BlockCount) throw new ArgumentOutOfRangeException(nameof(lba));
        if (destination.Length < IReadOnlyOpticalDisc.BlockSize) throw new ArgumentException("A complete optical block buffer is required.", nameof(destination));
        _stream.Position = checked(lba * IReadOnlyOpticalDisc.BlockSize);
        _stream.ReadExactly(destination[..IReadOnlyOpticalDisc.BlockSize]);
    }

    /// <summary>Reopens only this immutable file basis under the same writer-exclusion policy.</summary>
    public IReadOnlyOpticalDisc Reopen() => new ReadOnlyIsoImage(ImagePath);
    /// <summary>Releases the owned read-only file lease.</summary>
    public void Dispose() => _stream.Dispose();
}
