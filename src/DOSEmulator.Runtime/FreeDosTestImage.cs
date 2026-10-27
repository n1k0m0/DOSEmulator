// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DOSEmulator.Runtime;

/// <summary>Creates a private minimal-startup copy of official media. Kernel, shell and boot code are unchanged.</summary>
public static class FreeDosTestImage
{
    /// <summary>Creates a fresh private minimal FreeDOS IDE copy while preserving original boot/system files.</summary>
    public static object Create(string source, string destination)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A derived test image must have a different path.");
        if (File.Exists(destination)) throw new IOException("The destination already exists; original/test images are never overwritten.");
        byte[] image = File.ReadAllBytes(source);
        string originalHash = Convert.ToHexString(SHA256.HashData(image));
        if (originalHash != "F539D456B792594BC3CA59D4E0F4C23D4F1FEE73370C1390B2DA245400718D36")
            throw new InvalidDataException("Expected the unchanged, verified FreeDOS 1.4 LiteUSB image.");
        int partition = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(454)) * 512);
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(partition + 11));
        int clusterBytes = image[partition + 13] * bps;
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(partition + 14));
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(partition + 17));
        int sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(partition + 22));
        if (bps != 512 || clusterBytes is < 512 or > 32768 || entries == 0 || sectorsPerFat == 0)
            throw new InvalidDataException("Expected a partitioned FAT16 FreeDOS LiteUSB image.");
        int fat = partition + reserved * bps;
        int root = partition + (reserved + image[partition + 16] * sectorsPerFat) * bps;
        int data = root + ((entries * 32 + bps - 1) / bps) * bps;
        // Finds the explicitly named FAT entry in the private test image or rejects a missing file.
        int Find(string name)
        {
            for (int i = 0; i < entries; i++)
            {
                int offset = root + i * 32;
                if (Encoding.ASCII.GetString(image, offset, 11) == name) return offset;
            }
            throw new InvalidDataException($"Missing file {name}.");
        }
        // Reads a checked file from the private FAT image through its actual cluster chain.
        byte[] ReadFile(string name)
        {
            int entry = Find(name), length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 28)));
            byte[] bytes = new byte[length]; int written = 0;
            int cluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entry + 26));
            while (written < length)
            {
                if (cluster is < 2 or >= 0xfff8) throw new InvalidDataException("Invalid FAT chain.");
                int count = Math.Min(clusterBytes, length - written);
                image.AsSpan(data + (cluster - 2) * clusterBytes, count).CopyTo(bytes.AsSpan(written)); written += count;
                cluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fat + cluster * 2));
            }
            return bytes;
        }
        // Replaces only the selected test startup file in the fresh private image, retaining the original medium.
        void Replace(string name, string text)
        {
            int entry = Find(name), oldLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(entry + 28)));
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            if (bytes.Length > oldLength) throw new InvalidDataException("Replacement must fit existing allocation.");
            int cluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(entry + 26)), copied = 0;
            while (copied < bytes.Length)
            {
                int count = Math.Min(clusterBytes, bytes.Length - copied);
                bytes.AsSpan(copied, count).CopyTo(image.AsSpan(data + (cluster - 2) * clusterBytes)); copied += count;
                cluster = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(fat + cluster * 2));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 28), (uint)bytes.Length);
        }
        byte[] kernel = ReadFile("KERNEL  SYS"), shell = ReadFile("COMMAND COM");
        string config = "DOS=LOW\r\nFILES=20\r\nBUFFERS=10\r\nLASTDRIVE=Z\r\nSHELL=C:\\COMMAND.COM C:\\ /E:512 /P=C:\\FDAUTO.BAT\r\n";
        string auto = "@ECHO OFF\r\nVER\r\nECHO M1-FREEDOS-BOOT-OK\r\nDIR C:\\\r\nECHO M1-DIR-OK\r\n";
        Replace("FDCONFIGSYS", config); Replace("FDAUTO  BAT", auto);
        if (!ReadFile("KERNEL  SYS").AsSpan().SequenceEqual(kernel) || !ReadFile("COMMAND COM").AsSpan().SequenceEqual(shell))
            throw new InvalidDataException("Kernel or shell unexpectedly changed.");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) file.Write(image);
        return new { originalHash, derivedHash = Convert.ToHexString(SHA256.HashData(image)),
            kernelHash = Convert.ToHexString(SHA256.HashData(kernel)), shellHash = Convert.ToHexString(SHA256.HashData(shell)),
            changedFiles = new[] { "FDCONFIG.SYS", "FDAUTO.BAT" }, config, auto };
    }
}
