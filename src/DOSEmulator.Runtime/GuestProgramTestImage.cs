// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Runtime;

/// <summary>Adds our COM probes to a new private copy of the verified minimal FreeDOS test medium.</summary>
public static class GuestProgramTestImage
{
    /// <summary>Adds only original project probe files to a fresh private verified FAT16 test copy.</summary>
    public static object Create(string source, string programDirectory, string destination)
    {
        if (File.Exists(destination)) throw new IOException("Test images are never overwritten.");
        byte[] image = File.ReadAllBytes(source);
        string baseHash = Convert.ToHexString(SHA256.HashData(image));
        if (baseHash != "D340BFBD23FB7A4C5A6E55C8E23DD56E762B3452A8993148E18DEF5BA194324C")
            throw new InvalidDataException("Expected the verified minimal FreeDOS 1.4 IDE test image.");
        string[] programs = Directory.GetFiles(programDirectory, "*.COM").Order(StringComparer.Ordinal).ToArray();
        if (programs.Length is < 1 or > 16) throw new ArgumentException("Supply 1..16 original COM test programs.");
        // Reads a little-endian BPB/FAT word from the private test-image bytes.
        int Word(int address) => BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(address));
        int partition = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(454)) * 512);
        int bps = Word(partition + 11), clusterBytes = image[partition + 13] * bps;
        int fatCount = image[partition + 16], fatBytes = Word(partition + 22) * bps;
        int entries = Word(partition + 17), fat = partition + Word(partition + 14) * bps;
        int root = fat + fatCount * fatBytes, data = root + ((entries * 32 + bps - 1) / bps) * bps;
        int clusters = Math.Min((image.Length - data) / clusterBytes + 2, fatBytes / 2);
        var added = new List<object>();
        foreach (string program in programs)
        {
            string stem = Path.GetFileNameWithoutExtension(program).ToUpperInvariant();
            if (stem.Length is < 1 or > 8 || stem.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_'))
                throw new ArgumentException("Test programs need an ASCII 8.3 COM filename.");
            string name = stem.PadRight(8) + "COM";
            byte[] bytes = File.ReadAllBytes(program);
            if (bytes.Length is < 1 or > 65280) throw new InvalidDataException("COM probe exceeds its DOS segment.");
            int entry = -1;
            for (int e = 0; e < entries; e++)
            {
                int address = root + e * 32;
                if (Encoding.ASCII.GetString(image, address, 11) == name) throw new InvalidDataException("Test program already exists in the base.");
                if (entry < 0 && image[address] is 0 or 0xe5) entry = address;
            }
            if (entry < 0) throw new InvalidDataException("FAT root directory is full.");
            int count = (bytes.Length + clusterBytes - 1) / clusterBytes;
            int[] allocated = Enumerable.Range(2, clusters - 2).Where(c => Word(fat + c * 2) == 0).Take(count).ToArray();
            if (allocated.Length != count) throw new InvalidDataException("No free clusters for the probe.");
            for (int i = 0; i < count; i++)
            {
                ushort next = i + 1 == count ? (ushort)0xffff : (ushort)allocated[i + 1];
                for (int copy = 0; copy < fatCount; copy++) BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(fat + copy * fatBytes + allocated[i] * 2), next);
                Span<byte> cluster = image.AsSpan(data + (allocated[i] - 2) * clusterBytes, clusterBytes); cluster.Clear();
                bytes.AsSpan(i * clusterBytes, Math.Min(clusterBytes, bytes.Length - i * clusterBytes)).CopyTo(cluster);
            }
            image.AsSpan(entry, 32).Clear(); Encoding.ASCII.GetBytes(name).CopyTo(image, entry); image[entry + 11] = 0x20;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry + 26), (ushort)allocated[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 28), (uint)bytes.Length);
            added.Add(new { file = stem + ".COM", length = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
        }
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(image);
        return new { baseHash, derivedHash = Convert.ToHexString(SHA256.HashData(image)), programs = added,
            description = "Original COM probes added in free FAT16 clusters; boot code, kernel, shell and startup files unchanged." };
    }
}
