// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.DiskTools;

/// <summary>Restricts filesystem reads/writes to one checked physical volume, without owning its disk.</summary>
internal sealed class PartitionStream(Stream disk, long start, long length) : Stream
{
    private long _position;
    /// <summary>Inherits read availability from the containing disk.</summary>
    public override bool CanRead => disk.CanRead;
    /// <summary>Inherits only the private transaction disk write permission.</summary>
    public override bool CanWrite => disk.CanWrite;
    /// <summary>Supports bounded positioning within the selected volume.</summary>
    public override bool CanSeek => true;
    /// <summary>Fixed checked byte length of this partition.</summary>
    public override long Length => length;
    /// <summary>Partition-relative cursor with no access to neighboring media.</summary>
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    /// <summary>Reads only bytes inside this partition, adapting array calls when needed.</summary>
    public override int Read(Span<byte> buffer)
    {
        disk.Position = checked(start + _position);
        int n = disk.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]);
        _position += n;
        return n;
    }
    /// <summary>Reads only bytes inside this partition, adapting array calls when needed.</summary>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    /// <summary>Writes only bytes inside this partition, adapting array calls when needed.</summary>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > length - _position) throw new IOException("Filesystem write exceeds its partition.");
        disk.Position = checked(start + _position);
        disk.Write(buffer);
        _position += buffer.Length;
    }
    /// <summary>Writes only bytes inside this partition, adapting array calls when needed.</summary>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    /// <summary>Rejects positions outside the selected partition.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position, SeekOrigin.End => length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (position < 0 || position > length) throw new IOException("Filesystem seek exceeds its partition.");
        return _position = position;
    }
    /// <summary>Flushes only the containing private transaction journal.</summary>
    public override void Flush() => disk.Flush();
    /// <summary>Rejects attempts to resize a partition.</summary>
    public override void SetLength(long value) => throw new NotSupportedException("Partition capacity is fixed.");
}
