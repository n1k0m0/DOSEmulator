using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Hardware;

/// <summary>
/// Append-only private sector overlay. Base images are never writable. A pending reopen lease
/// becomes active only when the previous machine releases its lease, supporting prepared power cycles.
/// </summary>
public sealed class CopyOnWriteDisk : IWritableBlockDevice
{
    private sealed class Store : IDisposable
    {
        public readonly ReadOnlyRawDisk Base;
        public readonly FileStream File;
        public readonly Dictionary<long, byte[]> Sectors = [];
        public readonly object Gate = new();
        public readonly string OverlayPath;
        public int Active = 1, Pending, Next = 1, References = 1;
        public bool Failed;
        private const int HeaderBytes = 48, RecordBytes = 552;

        public Store(string basePath, string overlayPath)
        {
            string source = Path.GetFullPath(basePath); OverlayPath = Path.GetFullPath(overlayPath);
            if (source.Equals(OverlayPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Overlay path must differ from the base image.");
            Base = new ReadOnlyRawDisk(source);
            try
            {
                using var original = System.IO.File.OpenRead(source);
                byte[] identity = SHA256.HashData(original);
                File = new FileStream(OverlayPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    byte[] header = new byte[HeaderBytes]; Encoding.ASCII.GetBytes("DOSCOW01").CopyTo(header, 0);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), Base.SectorCount); identity.CopyTo(header, 16);
                    if (File.Length == 0) { File.Write(header); File.Flush(true); }
                    else
                    {
                        byte[] actual = new byte[HeaderBytes]; File.ReadExactly(actual);
                        if (!actual.AsSpan().SequenceEqual(header)) throw new InvalidDataException("Overlay format, base identity or capacity does not match.");
                    }
                    long validEnd = HeaderBytes;
                    byte[] record = new byte[RecordBytes];
                    while (File.Length - validEnd >= RecordBytes)
                    {
                        File.Position = validEnd; File.ReadExactly(record);
                        long lba = BinaryPrimitives.ReadInt64LittleEndian(record);
                        if (lba < 0 || lba >= Base.SectorCount || !SHA256.HashData(record.AsSpan(0, 520)).AsSpan().SequenceEqual(record.AsSpan(520)))
                            throw new InvalidDataException("Overlay sector journal is corrupt.");
                        Sectors[lba] = record.AsSpan(8, 512).ToArray(); validEnd += RecordBytes;
                    }
                    // An incomplete last record never reached the commit boundary. Retain all valid sectors.
                    if (File.Length != validEnd) { File.SetLength(validEnd); File.Flush(true); }
                    File.Position = validEnd;
                }
                catch { File.Dispose(); throw; }
            }
            catch { Base.Dispose(); throw; }
        }
        public void Write(long lba, ReadOnlySpan<byte> bytes)
        {
            if (Failed) throw new IOException("Overlay is faulted and must be reopened before further writes.");
            byte[] record = new byte[RecordBytes]; BinaryPrimitives.WriteInt64LittleEndian(record, lba);
            bytes[..512].CopyTo(record.AsSpan(8)); SHA256.HashData(record.AsSpan(0, 520)).CopyTo(record, 520);
            try { File.Write(record); File.Flush(true); }
            catch { Failed = true; throw; }
            Sectors[lba] = bytes[..512].ToArray();
        }
        public void Dispose() { File.Dispose(); Base.Dispose(); }
    }
    private readonly Store _store;
    private readonly int _lease;
    private bool _disposed;
    public long SectorCount => _store.Base.SectorCount;
    public string Name => _store.Base.Name + " + " + Path.GetFileName(_store.OverlayPath);
    public CopyOnWriteDisk(string basePath, string overlayPath) { _store = new(basePath, overlayPath); _lease = 1; }
    private CopyOnWriteDisk(Store store, int lease) { _store = store; _lease = lease; }
    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_store.Active != _lease) throw new InvalidOperationException("A prepared overlay lease is not active until the old machine is disposed.");
    }
    public void ReadSector(long lba, Span<byte> destination)
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            if (_store.Sectors.TryGetValue(lba, out byte[]? bytes)) bytes.CopyTo(destination);
            else _store.Base.ReadSector(lba, destination);
        }
    }
    public void WriteSector(long lba, ReadOnlySpan<byte> source)
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (lba < 0 || lba >= SectorCount || source.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            _store.Write(lba, source);
        }
    }
    public void Flush() { lock (_store.Gate) { RequireActive(); _store.File.Flush(true); } }
    public IReadOnlyBlockDevice Reopen()
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (_store.Pending != 0) throw new InvalidOperationException("Only one prepared power-cycle lease is allowed.");
            _store.Pending = ++_store.Next; _store.References++;
            return new CopyOnWriteDisk(_store, _store.Pending);
        }
    }
    public void Dispose()
    {
        lock (_store.Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_store.Pending == _lease) _store.Pending = 0;
            if (_store.Active == _lease) { _store.Active = _store.Pending; _store.Pending = 0; }
            if (--_store.References == 0) _store.Dispose();
        }
    }
}
