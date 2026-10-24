namespace DOSEmulator.Hardware;

public interface IReadOnlyBlockDevice : IDisposable
{
    const int SectorSize = 512;
    long SectorCount { get; }
    string Name { get; }
    void ReadSector(long lba, Span<byte> destination);
    IReadOnlyBlockDevice Reopen();
}

public interface IWritableBlockDevice : IReadOnlyBlockDevice
{
    void WriteSector(long lba, ReadOnlySpan<byte> source);
    void Flush();
}

public sealed class MemoryDisk : IReadOnlyBlockDevice
{
    private readonly byte[] _bytes;
    public long SectorCount => _bytes.LongLength / 512;
    public string Name { get; }
    public MemoryDisk(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length == 0 || bytes.Length % 512 != 0) throw new ArgumentException("A sector image must contain whole 512-byte sectors.");
        _bytes = bytes.ToArray(); Name = name;
    }
    public void ReadSector(long lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
        _bytes.AsSpan(checked((int)lba * 512), 512).CopyTo(destination);
    }
    public void Dispose() { }
    public IReadOnlyBlockDevice Reopen() => new MemoryDisk(_bytes, Name);
}

/// <summary>Original images are opened read-only. No guest write can reach the host file.</summary>
public sealed class ReadOnlyRawDisk : IReadOnlyBlockDevice
{
    private readonly FileStream _stream;
    private readonly string _path;
    public string Name { get; }
    public long SectorCount => _stream.Length / 512;
    public ReadOnlyRawDisk(string path)
    {
        _path = Path.GetFullPath(path); Name = Path.GetFileName(path);
        _stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_stream.Length == 0 || _stream.Length % 512 != 0)
        {
            _stream.Dispose(); throw new InvalidDataException("Raw disk length must be a nonzero multiple of 512 bytes.");
        }
    }
    public void ReadSector(long lba, Span<byte> destination)
    {
        if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
        _stream.Position = checked(lba * 512);
        _stream.ReadExactly(destination[..512]);
    }
    public void Dispose() => _stream.Dispose();
    public IReadOnlyBlockDevice Reopen() => new ReadOnlyRawDisk(_path);
}
