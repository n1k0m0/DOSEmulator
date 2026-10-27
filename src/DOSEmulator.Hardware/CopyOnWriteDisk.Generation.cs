// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DOSEmulator.Hardware;

/// <summary>Content identity of one complete DOSCOW01 generation, independent of its later writable branches.</summary>
public sealed record OverlayGeneration(long Bytes, string Sha256);

public sealed partial class CopyOnWriteDisk
{
    /// <summary>
    /// Copies a checked, durable journal prefix to a new file while holding the active media lease.
    /// The caller must coordinate all drives on the machine owner; this alone is not a hardware snapshot.
    /// </summary>
    public OverlayGeneration ExportGeneration(string path)
    {
        lock (_store.Gate)
        {
            RequireActive();
            if (_store.Failed) throw new IOException("A faulted journal cannot supply a sealed generation.");
            _store.File.Flush(true);
            long length = _store.File.Length;
            if (length < 48 || (length - 48) % 552 != 0) throw new InvalidDataException("Incomplete committed overlay generation.");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] record = new byte[552];
                Span<byte> checksum = stackalloc byte[32];
                ReadJournalBytes(0, record.AsSpan(0, 48));
                if (!record.AsSpan(0, 8).SequenceEqual("DOSCOW01"u8) ||
                    BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8)) != SectorCount ||
                    !record.AsSpan(16, 32).SequenceEqual(_store.Identity))
                    throw new InvalidDataException("Overlay generation header differs from its protected base.");
                output.Write(record.AsSpan(0, 48)); digest.AppendData(record.AsSpan(0, 48));
                for (long offset = 48; offset < length; offset += 552)
                {
                    ReadJournalBytes(offset, record);
                    long lba = BinaryPrimitives.ReadInt64LittleEndian(record);
                    if (lba < 0 || lba >= SectorCount)
                        throw new InvalidDataException("Corrupt overlay generation record.");
                    SHA256.HashData(record.AsSpan(0, 520), checksum);
                    if (!checksum.SequenceEqual(record.AsSpan(520)))
                        throw new InvalidDataException("Corrupt overlay generation record.");
                    output.Write(record); digest.AppendData(record);
                }
                output.Flush(true);
                return new(length, Convert.ToHexString(digest.GetHashAndReset()));
            }
            catch { output.Dispose(); File.Delete(path); throw; }
        }
    }

    /// <summary>Reads an exact journal range without moving the append cursor or touching the sector cache.</summary>
    private void ReadJournalBytes(long offset, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = RandomAccess.Read(_store.File.SafeFileHandle, destination[read..], offset + read);
            if (count == 0) throw new EndOfStreamException("Committed journal generation is truncated.");
            read += count;
        }
    }
}
