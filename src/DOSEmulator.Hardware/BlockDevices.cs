// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

/// <summary>Immutable locations of a protected base image and its optional private journal.</summary>
public sealed record BlockDeviceImage(string BasePath, string? OverlayPath);

/// <summary>Exposes media identity for configuration persistence without giving the UI a writable device.</summary>
public interface IFileBackedBlockDevice {
    /// <summary>Immutable canonical base-image and private-overlay locations for persistent PC configuration.</summary>
    BlockDeviceImage Image { get; } }

public interface IReadOnlyBlockDevice : IDisposable
{
    const int SectorSize = 512;
    /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
    long SectorCount { get; }
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    string Name { get; }
    /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
    void ReadSector(long lba, Span<byte> destination);
    /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
    IReadOnlyBlockDevice Reopen();
}

public interface IWritableBlockDevice : IReadOnlyBlockDevice
{
    /// <summary>Commits one checked 512-byte sector through the private writable-media implementation.</summary>
    void WriteSector(long lba, ReadOnlySpan<byte> source);
    /// <summary>Flushes pending component output at its documented commit boundary.</summary>
    void Flush();
}

public sealed class MemoryDisk : IReadOnlyBlockDevice
{
    private readonly byte[] _bytes;
    /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
    public long SectorCount => _bytes.LongLength / 512;
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    public string Name { get; }
    /// <summary>Initializes MemoryDisk and connects the supplied dependencies before the component is used.</summary>
    public MemoryDisk(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length == 0 || bytes.Length % 512 != 0) throw new ArgumentException("A sector image must contain whole 512-byte sectors.");
        _bytes = bytes.ToArray(); Name = name;
    }
    /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
    public void ReadSector(long lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
        _bytes.AsSpan(checked((int)lba * 512), 512).CopyTo(destination);
    }
    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
    public void Dispose() { }
    /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
    public IReadOnlyBlockDevice Reopen() => new MemoryDisk(_bytes, Name);
}

/// <summary>Original images are opened read-only. No guest write can reach the host file.</summary>
public sealed class ReadOnlyRawDisk : IReadOnlyBlockDevice, IFileBackedBlockDevice
{
    private readonly FileStream _stream;
    private readonly string _path;
    private readonly long _sectorCount;
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    public string Name { get; }
    /// <summary>Immutable canonical base-image and private-overlay locations for persistent PC configuration.</summary>
    public BlockDeviceImage Image => new(_path, null);
    /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
    public long SectorCount => _sectorCount;
    /// <summary>Initializes ReadOnlyRawDisk and connects the supplied dependencies before the component is used.</summary>
    public ReadOnlyRawDisk(string path)
    {
        _path = Path.GetFullPath(path); Name = Path.GetFileName(path);
        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long length = _stream.Length;
            if (length == 0 || length % 512 != 0) throw new InvalidDataException("Raw disk length must be a nonzero multiple of 512 bytes.");
            _sectorCount = length / 512;
        }
        catch { _stream.Dispose(); throw; }
    }
    /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
    public void ReadSector(long lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
        long offset = checked(lba * 512); int read = 0;
        while (read < 512)
        {
            int count = RandomAccess.Read(_stream.SafeFileHandle, destination[read..512], offset + read);
            if (count == 0) throw new EndOfStreamException("Protected disk sector is truncated.");
            read += count;
        }
    }
    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
    public void Dispose() => _stream.Dispose();
    /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
    public IReadOnlyBlockDevice Reopen() => new ReadOnlyRawDisk(_path);
}
