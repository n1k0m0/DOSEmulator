// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text;
using DiscUtils;
using DiscUtils.Fat;
using DOSEmulator.DiskTools;
using DOSEmulator.Hardware;

/// <summary>Only newly generated original filesystems; no OS, game or existing VM is modified.</summary>
internal sealed class Fixture
{
    /// <summary>Fresh private directory containing only this generated fixture.</summary>
    public string Root { get; }
    /// <summary>Path of the original test image whose bytes must remain unchanged.</summary>
    public string Image { get; }
    /// <summary>Path of the fixture journal, separate from its image.</summary>
    public string Cow { get; }
    public readonly byte[] Sentinel = Enumerable.Range(0, 1027).Select(n => (byte)(n * 37 + 11)).ToArray();
    /// <summary>Media binding passed to the editor for this own fixture.</summary>
    public DiskSelection Selection => new("Own fixture", Image, Cow);

    /// <summary>Attaches only an explicitly prepared private integration copy, without modifying any bytes.</summary>
    public Fixture(string existingRoot)
    {
        Root = existingRoot; Image = Path.Combine(Root, "own.img"); Cow = Path.Combine(Root, "own.cow");
    }

    /// <summary>Creates FAT12, an MBR-partitioned FAT16 disk, or an independently laid out FAT32 volume.</summary>
    public Fixture(string work, string name, int bits)
    {
        Root = Path.Combine(work, name);
        Directory.CreateDirectory(Root);
        Image = Path.Combine(Root, "own.img"); Cow = Path.Combine(Root, "own.cow");
        using (var output = new FileStream(Image, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (bits == 12)
            {
                using var fs = FatFileSystem.FormatFloppy(output, FloppyDiskType.HighDensity, "DOSE OWN");
                using var file = fs.OpenFile("KEEP.DAT", FileMode.Create, FileAccess.Write);
                file.Write(Sentinel);
            }
            else if (bits == 16)
            {
                const int sectors = 32768, start = 63;
                output.SetLength((sectors + start) * 512L);
                byte[] mbr = new byte[512]; mbr[446] = 0x80; mbr[450] = 6;
                BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), start);
                BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458), sectors);
                mbr[510] = 0x55; mbr[511] = 0xAA; output.Write(mbr);
                using var partition = new PartitionStream(output, start * 512L, sectors * 512L);
                using var fs = FatFileSystem.FormatPartition(partition, "DOSE OWN", Geometry.FromCapacity(output.Length), start, sectors, 1);
                using var file = fs.OpenFile("KEEP.DAT", FileMode.Create, FileAccess.Write);
                file.Write(Sentinel);
            }
            else
            {
                const int sectors = 131072, reserved = 32, fatSectors = 1009;
                output.SetLength(sectors * 512L);
                byte[] boot = new byte[512];
                boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90;
                Encoding.ASCII.GetBytes("DOSE OWN").CopyTo(boot, 3);
                BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), 512);
                boot[13] = 1; BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), reserved);
                boot[16] = 2; boot[21] = 0xF8;
                BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24), 63);
                BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26), 16);
                BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), sectors);
                BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), fatSectors);
                BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);
                boot[64] = 0x80; boot[66] = 0x29;
                BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(67), 0x1234ABCD);
                Encoding.ASCII.GetBytes("DOSE OWN   ").CopyTo(boot, 71);
                Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
                boot[510] = 0x55; boot[511] = 0xAA;
                output.Write(boot); output.Position = 6 * 512; output.Write(boot);
                byte[] info = new byte[512];
                BinaryPrimitives.WriteUInt32LittleEndian(info, 0x41615252);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484), 0x61417272);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), sectors - reserved - 2 * fatSectors - 1);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(508), 0xAA550000);
                output.Position = 512; output.Write(info); output.Position = 7 * 512; output.Write(info);
                byte[] fat = new byte[512];
                BinaryPrimitives.WriteUInt32LittleEndian(fat, 0x0FFFFFF8);
                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), 0x0FFFFFFF);
                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(8), 0x0FFFFFFF);
                output.Position = reserved * 512; output.Write(fat);
                output.Position = (reserved + fatSectors) * 512; output.Write(fat);
                output.Position = 0;
                using var fs = new FatFileSystem(output);
                using var file = fs.OpenFile("KEEP.DAT", FileMode.Create, FileAccess.Write);
                file.Write(Sentinel);
            }
        }
        using var journal = new CopyOnWriteDisk(Image, Cow);
    }
}

/// <summary>Independent sector-level FAT/LFN reader using the emulator's original COW backend, not DiscUtils.</summary>
internal sealed class FatOracle : IDisposable
{
    private readonly CopyOnWriteDisk _disk;
    private readonly int _bits, _spc, _reserved, _fats, _fatSectors, _rootEntries;
    private readonly long _start, _root, _data;
    private readonly uint _rootCluster;
    private readonly int _info, _backupInfo;
    private readonly byte[] _fat;
    public readonly record struct Entry(string Name, uint Cluster, uint Bytes, byte Attributes);

    /// <summary>Reads the independent BPB and authenticates both FAT copies before looking up any file.</summary>
    public FatOracle(Fixture fixture, int bits)
    {
        _disk = new(fixture.Image, fixture.Cow); _bits = bits;
        byte[] first = Sector(0);
        if (U16(first, 11) == 512 && first[13] != 0) _start = 0;
        else
        {
            var partitions = Enumerable.Range(0, 4).Select(n => first.AsSpan(446 + n * 16, 16).ToArray())
                .Where(p => p[4] is 1 or 4 or 6 or 11 or 12 or 14).ToArray();
            if (partitions.Length != 1) throw new Exception("Independent oracle expects one FAT partition.");
            _start = U32(partitions[0], 8);
        }
        byte[] boot = Sector(_start);
        _spc = boot[13]; _reserved = U16(boot, 14); _fats = boot[16]; _rootEntries = U16(boot, 17);
        _fatSectors = bits == 32 ? (int)U32(boot, 36) : U16(boot, 22);
        _rootCluster = bits == 32 ? U32(boot, 44) : 0;
        _info = bits == 32 ? U16(boot, 48) : -1;
        _backupInfo = bits == 32 ? U16(boot, 50) + _info : -1;
        _root = _start + _reserved + _fats * _fatSectors;
        _data = _root + (_rootEntries * 32 + 511) / 512;
        _fat = Enumerable.Range(0, _fatSectors).SelectMany(n => Sector(_start + _reserved + n)).ToArray();
        for (int n = 0; n < _fatSectors; n++)
            if (!Sector(_start + _reserved + _fatSectors + n).AsSpan().SequenceEqual(_fat.AsSpan(n * 512, 512)))
                throw new Exception("Independent FAT mirror mismatch.");
    }

    /// <summary>Decodes an independent little-endian FAT metadata word.</summary>
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    /// <summary>Decodes an independent little-endian FAT metadata doubleword.</summary>
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    /// <summary>Reads one sector through the original emulator COW backend.</summary>
    private byte[] Sector(long lba) { byte[] b = new byte[512]; _disk.ReadSector(lba, b); return b; }
    /// <summary>Decodes the next FAT cluster independently for each FAT width.</summary>
    private uint Next(uint cluster) => _bits switch
    {
        12 => (uint)((BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)(cluster + cluster / 2))) >> ((cluster & 1) == 0 ? 0 : 4)) & 0xFFF),
        16 => BinaryPrimitives.ReadUInt16LittleEndian(_fat.AsSpan((int)cluster * 2)),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(_fat.AsSpan((int)cluster * 4)) & 0x0FFFFFFF
    };

    /// <summary>Walks a bounded independent cluster chain while rejecting loops and non-data clusters.</summary>
    private byte[] Chain(uint first)
    {
        using var bytes = new MemoryStream();
        HashSet<uint> seen = [];
        uint end = _bits == 12 ? 0xFF8U : _bits == 16 ? 0xFFF8U : 0x0FFFFFF8U;
        for (uint c = first; c >= 2 && c < end; c = Next(c))
        {
            if (!seen.Add(c) || seen.Count > _disk.SectorCount / _spc) throw new Exception("Independent FAT chain loop.");
            for (int n = 0; n < _spc; n++) bytes.Write(Sector(_data + (c - 2) * _spc + n));
        }
        return bytes.ToArray();
    }

    /// <summary>Checks LFN ordinals and short-name checksums before returning each actual directory name.</summary>
    private List<Entry> Directory(uint cluster, bool root)
    {
        byte[] bytes = root && _bits != 32
            ? Enumerable.Range(0, (_rootEntries * 32 + 511) / 512).SelectMany(n => Sector(_root + n)).ToArray()
            : Chain(root ? _rootCluster : cluster);
        List<Entry> entries = [];
        HashSet<string> aliases = [];
        Dictionary<int, string> lfn = [];
        int count = 0; byte checksum = 0;
        for (int p = 0; p + 32 <= bytes.Length && bytes[p] != 0; p += 32)
        {
            var record = bytes.AsSpan(p, 32);
            if (record[0] == 0xE5) { lfn.Clear(); continue; }
            if (record[11] == 15)
            {
                int ordinal = record[0] & 31;
                if ((record[0] & 64) != 0) { lfn.Clear(); count = ordinal; checksum = record[13]; }
                if (record[13] != checksum || ordinal == 0 || ordinal > count) throw new Exception("Independent LFN sequence mismatch.");
                byte[] text = [.. record.Slice(1, 10), .. record.Slice(14, 12), .. record.Slice(28, 4)];
                lfn.Add(ordinal, Encoding.Unicode.GetString(text));
                continue;
            }
            if ((record[11] & 8) != 0) { lfn.Clear(); continue; }
            if (!aliases.Add(Convert.ToHexString(record[..11]))) throw new Exception("Independent DOS short-name alias collision.");
            string name = Encoding.ASCII.GetString(record[..8]).TrimEnd() +
                (record.Slice(8, 3).SequenceEqual("   "u8) ? "" : "." + Encoding.ASCII.GetString(record.Slice(8, 3)).TrimEnd());
            if (lfn.Count != 0)
            {
                byte actual = 0;
                foreach (byte b in record[..11]) actual = (byte)(((actual & 1) << 7) + (actual >> 1) + b);
                if (actual != checksum || lfn.Count != count) throw new Exception("Independent LFN checksum mismatch.");
                name = string.Concat(Enumerable.Range(1, count).Select(n => lfn[n])).TrimEnd('\0', '\uFFFF');
                lfn.Clear();
            }
            uint start = BinaryPrimitives.ReadUInt16LittleEndian(record[26..]);
            if (_bits == 32) start |= (uint)BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) << 16;
            entries.Add(new(name, start, BinaryPrimitives.ReadUInt32LittleEndian(record[28..]), record[11]));
        }
        return entries;
    }

    /// <summary>Reads one known path through independently reconstructed directory and cluster structures.</summary>
    public byte[] ReadFile(string path)
    {
        List<Entry> directory = Directory(0, true);
        string[] parts = path.Split('\\');
        Entry result = default;
        for (int n = 0; n < parts.Length; n++)
        {
            result = directory.Single(e => e.Name.Equals(parts[n], StringComparison.OrdinalIgnoreCase));
            if (n < parts.Length - 1) directory = Directory(result.Cluster, false);
        }
        return result.Bytes == 0 ? [] : Chain(result.Cluster).AsSpan(0, (int)result.Bytes).ToArray();
    }

    /// <summary>Requires exact or explicitly unknown FAT32 free-space hints in primary and backup FSInfo.</summary>
    public void CheckFreeHints()
    {
        if (_bits != 32) return;
        uint total = U32(Sector(_start), 32);
        uint clusters = (uint)(total - (_data - _start)) / (uint)_spc;
        uint free = 0;
        for (uint c = 2; c < clusters + 2; c++) if (Next(c) == 0) free++;
        foreach (int n in new[] { _info, _backupInfo }.Where(n => n > 0 && n < _reserved).Distinct())
        {
            uint hint = U32(Sector(_start + n), 488);
            if (hint != uint.MaxValue && hint != free) throw new Exception("FAT32 FSInfo is stale.");
        }
    }
    /// <summary>Releases the original emulator COW reader after independent verification.</summary>
    public void Dispose() => _disk.Dispose();
}
