// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class FloppyFormatBiosTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "Format BIOS assertion failed")
    { if (!condition) throw new Exception(message); }
    /// <summary>Creates fresh private test media paths; existing original files are never overwritten.</summary>
    private static (string Base, string Overlay) Files(string id = "1440k")
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string image = Path.Combine(directory, "base.img");
        byte[] bytes = new byte[FloppyGeometry.FromId(id)!.SectorCount * 512]; bytes.AsSpan().Fill(0x39);
        File.WriteAllBytes(image, bytes); return (image, Path.Combine(directory, "private.cow"));
    }
    /// <summary>Builds an isolated machine fixture with the specified guest code and optional identity paging.</summary>
    private static PcMachine Machine(string id, IReadOnlyBlockDevice? a = null, IReadOnlyBlockDevice? b = null)
    {
        var m = new PcMachine(new() { FloppyDriveA = id, FloppyDriveB = id },
            new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "test boot"), a, b);
        m.RunSteps(100000); return m;
    }
    /// <summary>Executes the controlled guest service/instruction path used by the surrounding regression.</summary>
    private static byte Call(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x13); m.RunSteps(5_000_000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "BIOS did not return");
        return (byte)(m.Cpu.GetRegister(Register.Ax) >> 8);
    }
    /// <summary>Sets up the real guest BIOS request with the case's registers and caller-owned source buffer.</summary>
    private static void Request(PcMachine m, int cylinder, int head, int drive, int count, bool custom = true, byte fill = 0xf6, uint source = 0x4000)
    {
        for (int i = 0; i < count; i++)
        {
            m.Memory.WriteByte(source + (uint)i * 4, (byte)cylinder); m.Memory.WriteByte(source + (uint)i * 4 + 1, (byte)head);
            m.Memory.WriteByte(source + (uint)i * 4 + 2, (byte)(i + 1)); m.Memory.WriteByte(source + (uint)i * 4 + 3, 2);
        }
        if (custom)
        {
            new byte[] { 0xdf, 2, 0x25, 2, (byte)count, 0x1b, 0xff, 0x57, fill, 15, 8 }.CopyTo(m.Memory.Ram, 0x3000);
            m.Memory.WriteWord(0x78, 0x3000); m.Memory.WriteWord(0x7a, 0);
        }
        m.Cpu.SetSegment(Segment.Es, source >= 0x1ffff ? (ushort)0x1000 : (ushort)0);
        m.Cpu.SetRegister(Register.Bx, source >= 0x1ffff ? source - 0x10000 : source);
        m.Cpu.SetRegister(Register.Cx, (uint)(cylinder << 8 | 0xff)); // CL is unused, including its high bits.
        m.Cpu.SetRegister(Register.Dx, (uint)(head << 8 | drive));
    }
    /// <summary>Executes the guest service and checks its actual returned status byte.</summary>
    private static void Status(PcMachine m, ushort ax, byte expected)
    {
        Check(Call(m, ax) == expected, $"Expected AH={expected:X2}, AX={m.Cpu.GetRegister(Register.Ax):X}");
        Check(((m.Cpu.Eflags & X86Cpu.Carry) != 0) == (expected != 0));
        Check(m.Memory.ReadByte(0x441) == expected);
    }
    /// <summary>Registers the named floppy format bios regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("BIOS AH05 uses active INT1E parameters and bounce DMA across caller 64K boundary on drive B", () =>
        {
            var files = Files(); byte[] original = File.ReadAllBytes(files.Base);
            using (var m = Machine("1440k", new MemoryDisk(original, "readonly A"), new CopyOnWriteDisk(files.Base, files.Overlay)))
            {
                Request(m, 7, 1, 1, 2, fill: 0xa5, source: 0x1ffff);
                m.Memory.WriteByte(0x20001, 2); m.Memory.WriteByte(0x20005, 1); // reversed sector IDs
                byte[] descriptors = m.Memory.Ram.AsSpan(0x1ffff, 8).ToArray();
                var commands = new List<byte>(); m.Ports.Accessed += (port, value, _, write) => { if (port == 0x3f5 && write) commands.Add((byte)value); };
                m.Cpu.SetRegister(Register.Si, 0x12345678); m.Cpu.SetRegister(Register.Di, 0x87654321);
                m.Cpu.SetSegment(Segment.Ds, 0x1234);
                uint sp = m.Cpu.GetRegister(Register.Sp);
                Status(m, 0x0500, 0); // later AT-compatible path takes count from DPT, not AL
                Check(m.Floppy.WrittenSectorCount == 2 && m.Floppy.ReadSectorCount == 0 && m.Dma.IsTerminalCount(2));
                Check((m.Dma.Read(0x0f, 1) & 4) != 0 && !m.Floppy.Busy && !m.Floppy.InterruptAsserted);
                Check(descriptors.AsSpan().SequenceEqual(m.Memory.Ram.AsSpan(0x1ffff, 8)));
                Check(commands.TakeLast(6).SequenceEqual(new byte[] { 0x4d, 5, 2, 2, 0x57, 0xa5 }));
                Check(m.Cpu.GetRegister(Register.Bx) == 0xffff && m.Cpu.GetRegister(Register.Cx) == 0x7ff && m.Cpu.GetRegister(Register.Dx) == 0x101);
                Check(m.Cpu.GetRegister(Register.Si) == 0x12345678 && m.Cpu.GetRegister(Register.Di) == 0x87654321);
                Check(m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetSegment(Segment.Es) == 0x1000 && m.Cpu.GetRegister(Register.Sp) == sp);
            }
            using var disk = new CopyOnWriteDisk(files.Base, files.Overlay); byte[] sector = new byte[512];
            foreach (int lba in new[] { 269, 270, 271, 272 })
            { disk.ReadSector(lba, sector); Check(sector.All(b => b == (lba is 270 or 271 ? 0xa5 : 0x39))); }
            Check(File.ReadAllBytes(files.Base).AsSpan().SequenceEqual(original));
        });
        foreach (string id in new[] { "360k", "1200k", "720k", "1440k", "2880k" })
            test("BIOS AH18/AH05 formats native geometry through FDC DMA: " + id, () =>
            {
                var files = Files(id); var g = FloppyGeometry.FromId(id)!;
                using var m = Machine(id, new CopyOnWriteDisk(files.Base, files.Overlay));
                m.Cpu.SetRegister(Register.Cx, (uint)((g.Cylinders - 1) << 8 | g.SectorsPerTrack)); m.Cpu.SetRegister(Register.Dx, 0);
                Status(m, 0x1800, 0);
                Check(m.Cpu.GetSegment(Segment.Es) == 0xf000 && m.Cpu.GetRegister(Register.Di) == 0xe020);
                uint table = 0xf0000 + m.Cpu.GetRegister(Register.Di);
                Check(m.Memory.ReadByte(table + 3) == 2 && m.Memory.ReadByte(table + 4) == g.SectorsPerTrack);
                Request(m, g.Cylinders - 1, 1, 0, g.SectorsPerTrack, custom: false);
                Status(m, (ushort)(0x0500 | g.SectorsPerTrack), 0);
                Check(m.Floppy.WrittenSectorCount == g.SectorsPerTrack && m.Dma.IsTerminalCount(2));
                m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bx, 0);
                m.Cpu.SetRegister(Register.Cx, (uint)((g.Cylinders - 1) << 8 | 1)); m.Cpu.SetRegister(Register.Dx, 0x100);
                Status(m, (ushort)(0x0200 | g.SectorsPerTrack), 0);
                Check(m.Memory.Ram.AsSpan(0x20000, g.SectorsPerTrack * 512).ToArray().All(b => b == 0xf6));
            });
        test("BIOS AH18 returns drive B table and preserves pointer on unsupported media geometry", () =>
        {
            using var m = Machine("1440k", b: new MemoryDisk(new byte[1474560], "B"));
            m.Cpu.SetRegister(Register.Cx, 0x4f12); m.Cpu.SetRegister(Register.Dx, 1); Status(m, 0x1800, 0);
            Check(m.Cpu.GetRegister(Register.Di) == 0xe030 && m.Cpu.GetSegment(Segment.Es) == 0xf000);
            m.Cpu.SetRegister(Register.Cx, 0x4f09); Status(m, 0x1800, 0x0c);
            Check(m.Cpu.GetRegister(Register.Di) == 0xe030 && m.Cpu.GetSegment(Segment.Es) == 0xf000 && m.Floppy.WrittenSectorCount == 0);
        });
        test("BIOS AH18 and AH05 report absent media as not ready without writing", () =>
        {
            using var m = Machine("1440k"); m.Cpu.SetRegister(Register.Cx, 0x4f12); m.Cpu.SetRegister(Register.Dx, 0);
            Status(m, 0x1800, 0x80); Request(m, 0, 0, 0, 18); Status(m, 0x0512, 0x80);
            Check(m.Floppy.WrittenSectorCount == 0 && !m.Floppy.Busy);
        });
        test("BIOS AH17 native legacy formats report media change once and reject incompatible requests", () =>
        {
            foreach (var (id, type) in new[] { ("360k", 1), ("1200k", 3), ("720k", 4) })
            {
                using var m = Machine(id, new MemoryDisk(new byte[FloppyGeometry.FromId(id)!.SectorCount * 512], "native"));
                m.Cpu.SetRegister(Register.Dx, 0); Status(m, (ushort)(0x1700 | type), 6); Status(m, (ushort)(0x1700 | type), 0);
                Status(m, 0x1702, 1); Check(m.Floppy.WrittenSectorCount == 0);
            }
        });
        test("BIOS AH05 returns real write protection and AH01 repeats last status", () =>
        {
            using var m = Machine("1440k", new MemoryDisk(new byte[1474560], "readonly"));
            Request(m, 1, 0, 0, 18); Status(m, 0x0512, 3); Status(m, 0x0100, 3);
            Check(m.Floppy.WrittenSectorCount == 0 && !m.Floppy.Busy && (m.Dma.Read(0x0f, 1) & 4) != 0);
            Status(m, 0x0000, 0); Status(m, 0x0100, 0);
        });
        test("BIOS AH05 malformed final descriptor leaves entire track unchanged", () =>
        {
            var files = Files(); using var m = Machine("1440k", new CopyOnWriteDisk(files.Base, files.Overlay));
            foreach (int offset in new[] { 0, 1, 2, 3 })
            {
                Request(m, 2, 1, 0, 18); m.Memory.WriteByte((uint)(0x4000 + 17 * 4 + offset), 0xff);
                Status(m, 0x0512, 4); Check(m.Floppy.WrittenSectorCount == 0 && !m.Floppy.Busy);
            }
        });
        test("BIOS AH05 validates size count and cylinder before any FDC format", () =>
        {
            var files = Files("360k"); using var m = Machine("360k", new CopyOnWriteDisk(files.Base, files.Overlay));
            var writes = new List<byte>(); m.Ports.Accessed += (port, value, _, write) => { if (port == 0x3f5 && write) writes.Add((byte)value); };
            foreach (byte count in new byte[] { 0, 10, 255 })
            { Request(m, 0, 0, 0, 9); m.Memory.WriteByte(0x3004, count); Status(m, 0x0509, 1); }
            Request(m, 0, 0, 0, 9); m.Memory.WriteByte(0x3003, 1); Status(m, 0x0509, 1);
            Request(m, 40, 0, 0, 9); Status(m, 0x0509, 1);
            Request(m, 0, 2, 0, 9); Status(m, 0x0509, 1);
            Check(writes.Count == 0 && m.Floppy.WrittenSectorCount == 0);
        });
    }
}
