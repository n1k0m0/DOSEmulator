// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Hardware;

/// <summary>
/// Append-only private sector overlay. Base images are never writable. A pending reopen lease
/// becomes active only when the previous machine releases its lease, supporting prepared power cycles.
/// </summary>
public sealed partial class CopyOnWriteDisk : IWritableBlockDevice, IFileBackedBlockDevice
{
    private sealed class Store : IDisposable
    {
        public readonly ReadOnlyRawDisk Base;
        public readonly byte[] Identity;
        public readonly FileStream File;
        public readonly Dictionary<long, long> Sectors = [];
        private readonly Dictionary<long, byte[]> _cache = [];
        private readonly Queue<long> _cacheOrder = [];
        private readonly byte[] _writeRecord = new byte[552];
        private long _appendOffset;
        /// <summary>Number of sector payload buffers currently retained in the bounded journal cache.</summary>
        public int CachedSectors => _cache.Count;
        private const int CacheCapacity = 4096;
        public readonly object Gate = new();
        public readonly string OverlayPath;
        public int Active = 1, Pending, Next = 1, References = 1;
        public bool Failed;
        private const int HeaderBytes = 48, RecordBytes = 552;

        /// <summary>Initializes Store and connects the supplied dependencies before the component is used.</summary>
        public Store(string basePath, string overlayPath)
        {
            string source = Path.GetFullPath(basePath); OverlayPath = Path.GetFullPath(overlayPath);
            if (source.Equals(OverlayPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Overlay path must differ from the base image.");
            Base = new ReadOnlyRawDisk(source);
            try
            {
                using var original = System.IO.File.OpenRead(source);
                byte[] identity = Identity = SHA256.HashData(original);
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
                    Span<byte> checksum = stackalloc byte[32];
                    while (File.Length - validEnd >= RecordBytes)
                    {
                        File.Position = validEnd; File.ReadExactly(record);
                        long lba = BinaryPrimitives.ReadInt64LittleEndian(record);
                        if (lba < 0 || lba >= Base.SectorCount)
                            throw new InvalidDataException("Overlay sector journal is corrupt.");
                        SHA256.HashData(record.AsSpan(0, 520), checksum);
                        if (!checksum.SequenceEqual(record.AsSpan(520)))
                            throw new InvalidDataException("Overlay sector journal is corrupt.");
                        Sectors[lba] = validEnd + 8; Remember(lba, record.AsSpan(8, 512)); validEnd += RecordBytes;
                    }
                    // An incomplete last record never reached the commit boundary. Retain all valid sectors.
                    if (File.Length != validEnd) { File.SetLength(validEnd); File.Flush(true); }
                    File.Position = validEnd;
                    _appendOffset = validEnd;
                }
                catch { File.Dispose(); throw; }
            }
            catch { Base.Dispose(); throw; }
        }
        /// <summary>Appends and flushes a checksummed sector record before exposing its new index/cache contents.</summary>
        public void Write(long lba, ReadOnlySpan<byte> bytes)
        {
            if (Failed) throw new IOException("Overlay is faulted and must be reopened before further writes.");
            Span<byte> record = _writeRecord;
            BinaryPrimitives.WriteInt64LittleEndian(record, lba);
            bytes[..512].CopyTo(record[8..520]); SHA256.HashData(record[..520], record[520..]);
            long start = _appendOffset;
            try { RandomAccess.Write(File.SafeFileHandle, record, start); File.Flush(true); }
            catch { Failed = true; throw; }
            _appendOffset = checked(start + RecordBytes);
            Sectors[lba] = start + 8; Remember(lba, bytes);
        }
        /// <summary>Reads a committed journal offset on a cache miss without disturbing the append cursor.</summary>
        public void Read(long lba, Span<byte> destination)
        {
            if (_cache.TryGetValue(lba, out var cached)) { cached.CopyTo(destination); return; }
            if (!Sectors.TryGetValue(lba, out long offset)) { Base.ReadSector(lba, destination); return; }
            int read = 0;
            while (read < 512)
            {
                int count = RandomAccess.Read(File.SafeFileHandle, destination[read..512], offset + read);
                if (count == 0) throw new EndOfStreamException("Committed overlay sector is truncated.");
                read += count;
            }
            Remember(lba, destination);
        }
        /// <summary>Bounds retained sector payloads to 2 MiB and reuses the evicted buffer; the complete index contains offsets only.</summary>
        private void Remember(long lba, ReadOnlySpan<byte> bytes)
        {
            if (!_cache.TryGetValue(lba, out var cached))
            {
                if (_cache.Count == CacheCapacity)
                {
                    long evicted = _cacheOrder.Dequeue(); cached = _cache[evicted]; _cache.Remove(evicted);
                }
                else cached = new byte[512];
                _cache.Add(lba, cached); _cacheOrder.Enqueue(lba);
            }
            bytes[..512].CopyTo(cached);
        }
        /// <summary>Releases the resources or ownership lease held by this instance.</summary>
        public void Dispose() { File.Dispose(); Base.Dispose(); }
    }
    private readonly Store _store;
    private readonly int _lease;
    private bool _disposed;
    /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
    public long SectorCount => _store.Base.SectorCount;
    /// <summary>Number of distinct sectors indexed by the committed private journal.</summary>
    public int OverlaySectorCount { get { lock (_store.Gate) return _store.Sectors.Count; } }
    /// <summary>Number of retained sector payloads, bounded independently from the journal's total size.</summary>
    public int CachedSectorCount { get { lock (_store.Gate) return _store.CachedSectors; } }
    /// <summary>Immutable canonical base-image and private-overlay locations for persistent PC configuration.</summary>
    public BlockDeviceImage Image => new(_store.Base.Image.BasePath, _store.OverlayPath);
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    public string Name => _store.Base.Name + " + " + Path.GetFileName(_store.OverlayPath);
    /// <summary>Initializes CopyOnWriteDisk and connects the supplied dependencies before the component is used.</summary>
    public CopyOnWriteDisk(string basePath, string overlayPath) { _store = new(basePath, overlayPath); _lease = 1; }
    /// <summary>Initializes CopyOnWriteDisk and connects the supplied dependencies before the component is used.</summary>
    private CopyOnWriteDisk(Store store, int lease) { _store = store; _lease = lease; }
    /// <summary>Rejects disposed or still-pending overlay leases before accessing shared media state.</summary>
    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_store.Active != _lease) throw new InvalidOperationException("A prepared overlay lease is not active until the old machine is disposed.");
    }
    /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
    public void ReadSector(long lba, Span<byte> destination)
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            _store.Read(lba, destination);
        }
    }
    /// <summary>Commits one checked 512-byte sector through the private writable-media implementation.</summary>
    public void WriteSector(long lba, ReadOnlySpan<byte> source)
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (lba < 0 || lba >= SectorCount || source.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            _store.Write(lba, source);
        }
    }
    /// <summary>Flushes pending component output at its documented commit boundary.</summary>
    public void Flush() { lock (_store.Gate) { RequireActive(); _store.File.Flush(true); } }
    /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
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
    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
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
