// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DOSEmulator.DiskTools;

/// <summary>Locked, read-only IMG/COW view; writable instances are private unpublished transaction journals.</summary>
internal sealed class DiskImageStream : Stream
{
    private readonly FileStream _base;
    private FileStream? _journal;
    private readonly Dictionary<long, long> _sectors = [];
    private readonly bool _writable;
    private long _position, _append;
    private byte[]? _identity;
    private bool _disposed;
    /// <summary>Canonical path of the immutable source image.</summary>
    public string ImagePath { get; }
    /// <summary>Canonical changes-file path; it may not exist until the first successful import.</summary>
    public string? CowPath { get; }
    /// <summary>Length of the complete authenticated journal prefix, excluding an incomplete tail.</summary>
    public long CommittedJournalLength => _append;
    /// <summary>Fixed image capacity in bytes, independent of journal growth.</summary>
    public override long Length => _base.Length;
    /// <summary>Whether this media lease is still open for reads.</summary>
    public override bool CanRead => !_disposed;
    /// <summary>Whether this fixed-size media lease remains open for positioning.</summary>
    public override bool CanSeek => !_disposed;
    /// <summary>Only private unpublished transaction instances allow writes.</summary>
    public override bool CanWrite => !_disposed && _writable;
    /// <summary>Logical disk cursor constrained to the image capacity.</summary>
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    /// <summary>Authenticates existing committed records without truncating or otherwise writing source media.</summary>
    public DiskImageStream(string image, string? cow, bool privateWritable = false, byte[]? knownIdentity = null, bool allowRename = false)
    {
        ImagePath = Path.GetFullPath(image);
        CowPath = cow is null ? null : Path.GetFullPath(cow);
        TransferPaths.NoLinks(ImagePath);
        if (CowPath is not null) TransferPaths.NoLinks(CowPath);
        TransferPaths.Leaf(Path.GetFileName(ImagePath));
        if (CowPath is not null) TransferPaths.Leaf(Path.GetFileName(CowPath));
        if (!File.Exists(ImagePath)) throw new FileNotFoundException("Choose an existing regular disk-image file.", ImagePath);
        if (string.Equals(ImagePath, CowPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The image and changes file must be different.");
        _writable = privateWritable;
        _identity = knownIdentity;
        _base = new(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (Length == 0 || Length % 512 != 0) throw new InvalidDataException("The IMG size must be a nonzero multiple of 512 bytes.");
            if (CowPath is not null && File.Exists(CowPath))
            {
                _journal = new(CowPath, FileMode.Open, privateWritable ? FileAccess.ReadWrite : FileAccess.Read,
                    privateWritable ? FileShare.None : FileShare.Read | (allowRename ? FileShare.Delete : 0), 64 * 1024);
                if (_journal.Length != 0)
                {
                    Span<byte> header = stackalloc byte[48];
                    _journal.ReadExactly(header);
                    if (!header[..8].SequenceEqual("DOSCOW01"u8) ||
                        BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != Length / 512 ||
                        !header[16..].SequenceEqual(Identity))
                        throw new InvalidDataException("The COW format, capacity or base-image identity does not match.");
                    _append = 48;
                    byte[] record = new byte[552];
                    Span<byte> checksum = stackalloc byte[32];
                    while (_journal.Length - _append >= 552)
                    {
                        _journal.Position = _append;
                        _journal.ReadExactly(record);
                        long lba = BinaryPrimitives.ReadInt64LittleEndian(record);
                        SHA256.HashData(record.AsSpan(0, 520), checksum);
                        if (lba < 0 || lba >= Length / 512 || !checksum.SequenceEqual(record.AsSpan(520)))
                            throw new InvalidDataException("A committed COW record is corrupt.");
                        _sectors[lba] = _append + 8;
                        _append += 552;
                    }
                    // Any incomplete final record was never committed. Source bytes stay intact.
                    if (privateWritable && _journal.Length != _append) _journal.SetLength(_append);
                }
                if (privateWritable && _append == 0) WriteHeader();
            }
            else if (privateWritable)
            {
                if (CowPath is null) throw new ArgumentException("A private transaction requires a journal.");
                _journal = new(CowPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024);
                WriteHeader();
            }
        }
        catch { _journal?.Dispose(); _base.Dispose(); throw; }
    }

    /// <summary>Obtains the SHA-256 identity while the base handle excludes all writers and deletion.</summary>
    public byte[] Identity
    {
        get
        {
            if (_identity is null)
            {
                _base.Position = 0;
                _identity = SHA256.HashData(_base);
            }
            return _identity;
        }
    }

    /// <summary>Writes the original DOSCOW01 header only into a new private transaction.</summary>
    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[48];
        "DOSCOW01"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], Length / 512);
        Identity.CopyTo(header[16..]);
        _journal!.Write(header);
        _append = 48;
    }

    /// <summary>Clones exactly the committed source generation; incomplete source tails are left untouched.</summary>
    public void CopyJournal(string destination, bool includeUncommittedTail = false)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = includeUncommittedTail ? _journal?.Length ?? 0 : _append;
        if (remaining > 0)
        {
            _journal!.Position = 0;
            byte[] buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                _journal.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer, 0, count);
                digest.AppendData(buffer, 0, count);
                remaining -= count;
            }
        }
        output.Flush(true);
        output.Position = 0;
        if (!SHA256.HashData(output).AsSpan().SequenceEqual(digest.GetHashAndReset()))
            throw new IOException("Changes-file copy verification failed.");
    }

    /// <summary>Reads every requested byte from the immutable base and latest committed sector records.</summary>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int count = (int)Math.Min(buffer.Length, Length - _position);
        if (count == 0) return 0;
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(_base.SafeFileHandle, buffer[done..count], _position + done);
            if (n == 0) throw new EndOfStreamException("The base image became truncated.");
            done += n;
        }
        if (_sectors.Count != 0)
        {
            long first = _position / 512, last = (_position + count - 1) / 512;
            Span<byte> sector = stackalloc byte[512];
            for (long lba = first; lba <= last; lba++)
            {
                if (!_sectors.TryGetValue(lba, out long offset)) continue;
                _journal!.Position = offset;
                _journal.ReadExactly(sector);
                int start = (int)Math.Max(0, lba * 512 - _position);
                int within = (int)Math.Max(0, _position - lba * 512);
                int length = Math.Min(512 - within, count - start);
                sector.Slice(within, length).CopyTo(buffer.Slice(start, length));
            }
        }
        _position += count;
        return count;
    }

    /// <summary>Adapts array reads to the checked span implementation.</summary>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>Appends complete checksummed sectors to the unpublished candidate; partial writes preserve adjacent bytes.</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!CanWrite) throw new NotSupportedException("Source disks are read-only.");
        if (buffer.Length > Length - _position) throw new IOException("Write exceeds the disk capacity.");
        Span<byte> record = stackalloc byte[552];
        while (!buffer.IsEmpty)
        {
            long lba = _position / 512;
            int within = (int)(_position % 512);
            int length = Math.Min(512 - within, buffer.Length);
            if (within != 0 || length != 512)
            {
                long original = _position;
                _position = lba * 512;
                ReadExactly(record.Slice(8, 512));
                _position = original;
            }
            buffer[..length].CopyTo(record.Slice(8 + within, length));
            BinaryPrimitives.WriteInt64LittleEndian(record, lba);
            SHA256.HashData(record[..520], record[520..]);
            _journal!.Position = _append;
            _journal.Write(record);
            _sectors[lba] = _append + 8;
            _append += 552;
            _position += length;
            buffer = buffer[length..];
        }
    }

    /// <summary>Adapts array writes to complete private-journal sector updates.</summary>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Makes a complete candidate durable before any atomic publication.</summary>
    public override void Flush() { if (_writable) _journal!.Flush(true); }

    /// <summary>Bounds every seek to this fixed-size disk.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long value = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (value < 0 || value > Length) throw new IOException("Seek exceeds the disk capacity.");
        return _position = value;
    }

    /// <summary>Rejects attempts to resize an existing disk.</summary>
    public override void SetLength(long value) => throw new NotSupportedException("Disk capacity is fixed.");

    /// <summary>Releases locks without altering the immutable base or a source journal.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _journal?.Dispose(); _base.Dispose(); }
        base.Dispose(disposing);
    }
}
