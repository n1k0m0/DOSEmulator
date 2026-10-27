// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class VgaFontTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "VGA font assertion failed")
    { if (!condition) throw new Exception(message); }
    /// <summary>Writes an actual indexed VGA register for the controlled fixture setup.</summary>
    private static void Reg(VgaAdapter v, ushort port, int index, int value) => v.Write(port, (uint)(index | value << 8), 2);
    /// <summary>Reads an actual indexed device register for the surrounding independent assertions.</summary>
    private static byte Get(VgaAdapter v, ushort port, int index) { v.Write(port, (uint)index, 1); return (byte)v.Read((ushort)(port + 1), 1); }
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot()
    {
        var m = PcMachine.CreateDemo(); m.RunSteps(100000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not halt"); return m;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(200000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return");
    }
    /// <summary>Prepares the explicit test source required by the following guest instruction or BIOS operation.</summary>
    private static void Load(PcMachine m, int bank, int first, int height, byte[] bytes, ushort ax = 0x1100)
    {
        m.Cpu.SetRegister(Register.Bx, (uint)(height << 8 | bank));
        m.Cpu.SetRegister(Register.Cx, (uint)(bytes.Length / height)); m.Cpu.SetRegister(Register.Dx, (uint)first);
        m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bp, 0x3000);
        bytes.CopyTo(m.Memory.Ram, 0x23000); Bios(m, ax);
    }
    /// <summary>Reads one actual VGA plane while preserving the fixture's original access registers.</summary>
    private static byte[] Plane(PcMachine m, int plane)
    {
        int si = (int)m.Video.Read(0x3c4, 1), gi = (int)m.Video.Read(0x3ce, 1);
        int s4 = Get(m.Video, 0x3c4, 4), g4 = Get(m.Video, 0x3ce, 4), g5 = Get(m.Video, 0x3ce, 5), g6 = Get(m.Video, 0x3ce, 6);
        Reg(m.Video, 0x3c4, 4, 6); Reg(m.Video, 0x3ce, 4, plane); Reg(m.Video, 0x3ce, 5, 0); Reg(m.Video, 0x3ce, 6, 4);
        byte[] result = new byte[65536]; for (uint i = 0; i < 65536; i++) result[i] = m.Video.ReadByte(i);
        Reg(m.Video, 0x3c4, 4, s4); Reg(m.Video, 0x3ce, 4, g4); Reg(m.Video, 0x3ce, 5, g5); Reg(m.Video, 0x3ce, 6, g6);
        m.Video.Write(0x3c4, (uint)si, 1); m.Video.Write(0x3ce, (uint)gi, 1); return result;
    }
    /// <summary>Registers the named vga font regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        int[] addresses = [0, 0x4000, 0x8000, 0xc000, 0x2000, 0x6000, 0xa000, 0xe000]; // IBM table, independent of implementation
        foreach (int bank in Enumerable.Range(0, 8))
            test($"BIOS alpha load places partial glyphs in VGA block {bank} without touching neighbors or other planes", () =>
            {
                using var m = Boot(); byte[][] before = Enumerable.Range(0, 4).Select(p => Plane(m, p)).ToArray();
                byte[] font = Enumerable.Range(0, 64).Select(i => (byte)(i ^ 0xa5)).ToArray();
                Load(m, bank, 254, 32, font);
                for (int p = 0; p < 4; p++)
                {
                    byte[] expected = before[p].ToArray(); if (p == 2) font.CopyTo(expected, addresses[bank] + 254 * 32);
                    Check(Plane(m, p).SequenceEqual(expected), $"Block {bank}, plane {p}: write escaped the requested glyphs");
                }
                Check(m.Memory.ReadWord(0x485) == 16 && m.Memory.ReadByte(0x484) == 24 && m.Video.Height == 400);
            });
        test("BIOS font upload wraps caller offset and preserves CPU flags, registers and altered VGA register indices/data", () =>
        {
            using var m = Boot();
            int[] seq = [3, 1, 11, 0x39, 2], gc = [13, 7, 3, 0x1e, 3, 0x13, 0x0e, 15, 0x51];
            for (int i = 0; i < seq.Length; i++) Reg(m.Video, 0x3c4, i, seq[i]);
            for (int i = 0; i < gc.Length; i++) Reg(m.Video, 0x3ce, i, gc[i]);
            m.Video.Write(0x3c4, 1, 1); m.Video.Write(0x3ce, 7, 1);
            m.Memory.WriteByte(0x2ffff, 0x81); m.Memory.WriteByte(0x20000, 0x42); m.Memory.WriteByte(0x20001, 0x24); m.Memory.WriteByte(0x20002, 0x18);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetSegment(Segment.Ds, 0x1234); m.Cpu.SetRegister(Register.Bp, 0xabcdffff);
            m.Cpu.SetRegister(Register.Bx, 0x98760204); m.Cpu.SetRegister(Register.Cx, 0x23450002); m.Cpu.SetRegister(Register.Dx, 0x56780030);
            m.Cpu.SetRegister(Register.Si, 0x87654321); m.Cpu.SetRegister(Register.Di, 0x76543210);
            m.Cpu.Eflags = 2 | X86Cpu.Direction | X86Cpu.Carry; uint sp = m.Cpu.GetRegister(Register.Sp);
            Bios(m, 0x1100);
            Check(m.Cpu.Eflags == (2 | X86Cpu.Direction | X86Cpu.Carry));
            Check(m.Cpu.GetRegister(Register.Bx) == 0x98760204 && m.Cpu.GetRegister(Register.Cx) == 0x23450002 && m.Cpu.GetRegister(Register.Dx) == 0x56780030);
            Check(m.Cpu.GetRegister(Register.Bp) == 0xabcdffff && m.Cpu.GetRegister(Register.Sp) == sp);
            Check(m.Cpu.GetRegister(Register.Si) == 0x87654321 && m.Cpu.GetRegister(Register.Di) == 0x76543210);
            Check(m.Cpu.GetSegment(Segment.Es) == 0x2000 && m.Cpu.GetSegment(Segment.Ds) == 0x1234);
            Check(m.Video.Read(0x3c4, 1) == 1 && m.Video.Read(0x3ce, 1) == 7);
            for (int i = 0; i < seq.Length; i++) Check(Get(m.Video, 0x3c4, i) == seq[i]);
            for (int i = 0; i < gc.Length; i++) Check(Get(m.Video, 0x3ce, i) == gc[i]);
            byte[] actual = Plane(m, 2); int offset = 0x2000 + 0x30 * 32;
            Check(actual[offset] == 0x81 && actual[offset + 1] == 0x42 && actual[offset + 32] == 0x24 && actual[offset + 33] == 0x18);
        });
        test("BIOS rejects invalid font ranges/heights/blocks atomically and a zero-count load is a no-op", () =>
        {
            using var m = Boot(); byte[] before = Plane(m, 2);
            foreach (var (block, height, first, count) in new[] { (8,16,0,1), (0,0,0,1), (0,1,0,1), (0,33,0,1), (0,16,255,2), (0,16,65535,2), (0,16,0,257), (0,16,0,0) })
            {
                m.Cpu.SetRegister(Register.Bx, (uint)(height << 8 | block)); m.Cpu.SetRegister(Register.Cx, (uint)count); m.Cpu.SetRegister(Register.Dx, (uint)first);
                Bios(m, 0x1110); Check(Plane(m, 2).SequenceEqual(before));
                Check(m.Video.Height == 400 && m.Memory.ReadWord(0x485) == 16 && m.Memory.ReadWord(0x44c) == 4096);
            }
        });
        test("VGA text scanout selects all eight nonlinear font maps and keeps intensity when A equals B", () =>
        {
            using var m = Boot(); Bios(m, 3);
            m.Memory.WriteWord(0xb8000, 0x0741); m.Memory.WriteWord(0xb8002, 0x0f41); Reg(m.Video, 0x3d4, 10, 32);
            for (int bank = 0; bank < 8; bank++)
            {
                Load(m, bank, 65, 16, [(byte)(128 >> bank), 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]);
                int select = (bank & 3) | ((bank & 3) << 2) | ((bank & 4) << 2) | ((bank & 4) << 3);
                m.Cpu.SetRegister(Register.Bx, (uint)select); Bios(m, 0x1103);
                var pixels = m.Video.Capture(0).ArgbPixels;
                for (int x = 0; x < 8; x++)
                {
                    Check(pixels[x] == (x == bank ? 0xffaaaaaa : 0xff000000));
                    Check(pixels[x + 8] == (x == bank ? 0xffffffff : 0xff000000));
                }
            }
        });
        test("VGA attribute bit three selects font A for 512 characters instead of foreground intensity", () =>
        {
            using var m = Boot(); Bios(m, 3);
            Load(m, 0, 65, 8, [0x80,0,0,0,0,0,0,0]); Load(m, 6, 65, 8, [1,0,0,0,0,0,0,0]);
            m.Memory.WriteWord(0xb8000, 0x0741); m.Memory.WriteWord(0xb8002, 0x0f41); Reg(m.Video, 0x3d4, 10, 32);
            m.Cpu.SetRegister(Register.Bx, 0x28); Bios(m, 0x1103); var pixels = m.Video.Capture(0).ArgbPixels;
            Check(pixels[0] == 0xffaaaaaa && pixels[7] == 0xff000000 && pixels[8] == 0xff000000 && pixels[15] == 0xffaaaaaa);
        });
        test("VGA nine-dot line replication uses the selected font only for C0..DF and obeys line-graphics enable", () =>
        {
            using var m = Boot(); Bios(m, 3); Reg(m.Video, 0x3c4, 1, 0); Reg(m.Video, 0x3d4, 10, 32);
            Load(m, 7, 0xbf, 1, [1,1,1]); m.Cpu.SetRegister(Register.Bx, 0x3f); Bios(m, 0x1103);
            m.Memory.WriteWord(0xb8000, 0x07bf); m.Memory.WriteWord(0xb8002, 0x07c0); m.Memory.WriteWord(0xb8004, 0x07c1);
            var frame = m.Video.Capture(0); Check(frame.Width == 720 && frame.ArgbPixels[8] == 0xff000000 && frame.ArgbPixels[17] == 0xffaaaaaa);
            m.Video.Read(0x3da, 1); m.Video.Write(0x3c0, 0x10, 1); m.Video.Write(0x3c0, 8, 1); m.Video.Write(0x3c0, 0x20, 1);
            Check(m.Video.Capture(0).ArgbPixels[17] == 0xff000000);
        });
        test("BIOS ROM font services load own 8/14/16 tables, preserve text and recalculate rows, cursor and page size", () =>
        {
            using var m = Boot(); Bios(m, 3); m.Memory.WriteWord(0xb8000, 0x0e51); int generation = m.Video.ModeGeneration, previousLines = m.Video.Height;
            foreach (var (ax, height, rows, address) in new[] { (0x1112,8,50,0xa000), (0x1111,14,28,0xa800), (0x1112,8,50,0xa000), (0x1114,16,25,0xc000) })
            {
                m.Cpu.SetRegister(Register.Bx, 0); Bios(m, (ushort)ax);
                Check(m.Memory.ReadWord(0xb8000) == 0x0e51 && m.Memory.ReadWord(0x485) == height && m.Memory.ReadByte(0x484) == rows - 1, $"{ax:X}: text or BDA font geometry");
                Check(m.Video.Height == rows * height && m.Memory.ReadWord(0x44c) == rows * 160, $"{ax:X}: height={m.Video.Height}, page={m.Memory.ReadWord(0x44c)}");
                Check(m.Memory.ReadWord(0x460) == ((height - 2) << 8 | (height - 1)) && Get(m.Video, 0x3d4, 10) == height - 2 && Get(m.Video, 0x3d4, 11) == height - 1, $"{ax:X}: cursor shape");
                byte[] plane = Plane(m, 2);
                for (int ch = 0; ch < 256; ch++) for (int y = 0; y < height; y++) Check(plane[ch * 32 + y] == m.Memory.ReadByte((uint)(0xf0000 + address + ch * height + y)));
                if (previousLines != rows * height) Check(m.Video.ModeGeneration > generation, "A changed viewport must publish a new mode generation");
                generation = m.Video.ModeGeneration; previousLines = m.Video.Height;
            }
        });
        test("BIOS user font geometry supports a 32-scanline cell", () =>
        {
            using var m = Boot(); Load(m, 0, 0, 32, new byte[32], 0x1110);
            Check(m.Video.Height == 384 && m.Memory.ReadWord(0x485) == 32 && m.Memory.ReadByte(0x484) == 11);
        });
        test("BIOS font information returns live vectors, separate own ROM sizes, and current row/height metadata", () =>
        {
            using var m = Boot(); m.Memory.WriteDword(0x7c, 0x12345678); m.Memory.WriteDword(0x10c, 0x23456789);
            foreach (var (selector, segment, offset) in new[] { (0,0x1234,0x5678), (1,0x2345,0x6789), (2,0xf000,0xa800), (3,0xf000,0xa000), (4,0xf000,0xa400), (6,0xf000,0xc000) })
            {
                m.Cpu.SetRegister(Register.Bx, (uint)(selector << 8)); m.Cpu.SetRegister(Register.Bp, 0xabcd1111); m.Cpu.SetRegister(Register.Dx, 0xbeef1234);
                Bios(m, 0x1130);
                Check(m.Cpu.GetSegment(Segment.Es) == segment && m.Cpu.GetRegister(Register.Bp) == (0xabcd0000u | (uint)offset));
                Check(m.Cpu.GetRegister(Register.Cx) == 16 && m.Cpu.GetRegister(Register.Dx) == 0xbeef1218);
            }
        });
        test("BIOS teletype scrolling and AH09 address all fifty rows after an eight-high font", () =>
        {
            using var m = Boot(); Bios(m, 3); m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x1112);
            m.Cpu.SetRegister(Register.Dx, 0x3100); Bios(m, 0x0200); m.Cpu.SetRegister(Register.Bx, 14); m.Cpu.SetRegister(Register.Cx, 80); Bios(m, 0x095a);
            Check(m.Memory.ReadWord(0xb8000 + 49 * 160) == 0x0e5a && m.Memory.ReadWord(0xb8000 + 50 * 160 - 2) == 0x0e5a, "AH09 must write the entire last row");
            m.Memory.WriteWord(0xb8000 + 160, 0x0741); Bios(m, 0x0e0a);
            Check(m.Memory.ReadWord(0x450) == 0x3100 && m.Memory.ReadWord(0xb8000) == 0x0741 && m.Memory.ReadWord(0xb8000 + 48 * 160) == 0x0e5a, $"Scroll cursor={m.Memory.ReadWord(0x450):X}, first={m.Memory.ReadWord(0xb8000):X}, row48={m.Memory.ReadWord(0xb8000 + 48 * 160):X}");
            Check(m.Memory.ReadWord(0xb8000 + 49 * 160) == 0x0720, "Scroll must clear the new bottom row");
        });
        test("BIOS scanline selection takes effect at the next text mode set with correct own ROM fonts", () =>
        {
            using var m = Boot();
            foreach (var (selection, height, lines, pointer) in new[] { (0,8,200,0xa000), (1,14,350,0xa800), (2,16,400,0xc000) })
            {
                int before = m.Video.Height;
                m.Cpu.SetRegister(Register.Bx, 0x30); Bios(m, (ushort)(0x1200 | selection));
                Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0x12 && m.Video.Height == before, "Selection must not immediately change the active mode");
                Bios(m, 3); Check(m.Video.Height == lines && m.Memory.ReadWord(0x485) == height && m.Memory.ReadByte(0x484) == 24);
                Check(m.Memory.ReadDword(0x10c) == (0xf0000000u | (uint)pointer));
                Check(((Get(m.Video, 0x3d4, 9) & 0x80) != 0) == (selection == 0));
                Check(m.Video.ReadText().Length == 0);
            }
            m.Cpu.SetRegister(Register.Bx, 0x30); Bios(m, 0x1203); Bios(m, 3);
            Check(m.Video.Height == 400, "Invalid selection must retain the previous family");
        });
        test("BIOS eight-high geometry retains 25/43/50 rows in the selected 200/350/400 text families", () =>
        {
            using var m = Boot();
            foreach (var (selection, rows, lines) in new[] { (0,25,200), (1,43,344), (2,50,400) })
            {
                m.Cpu.SetRegister(Register.Bx, 0x30); Bios(m, (ushort)(0x1200 | selection)); Bios(m, 3);
                m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x1112);
                Check(m.Video.Height == lines && m.Memory.ReadByte(0x484) == rows - 1 && m.Memory.ReadWord(0x44c) == rows * 160);
            }
        });
        test("VGA double-scan with a one-raster-line viewport still publishes a nonempty bounded frame", () =>
        {
            var v = new VgaAdapter(); Reg(v, 0x3d4, 7, 0); Reg(v, 0x3d4, 9, 0x80); Reg(v, 0x3d4, 0x12, 0);
            var frame = v.Capture(0); Check(frame.Width == 640 && frame.Height == 1 && frame.ArgbPixels.Length == 640);
        });
        test("BIOS functionality returns a bounded honest mode table and current text/graphics state, including eight font maps", () =>
        {
            using var m = Boot();
            foreach (var (mode, columns, rows, height, colors, lines) in new[] { (3,80,25,16,16,2), (0x12,80,30,16,16,3), (0x13,40,25,8,256,0) })
            {
                Bios(m, (ushort)mode); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Bx, 0);
                m.Memory.WriteByte(0x22fff, 0xa5); m.Memory.WriteByte(0x23040, 0x5a); Bios(m, 0x1b00);
                Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0x1b && m.Cpu.GetRegister(Register.Di) == 0x3000 && m.Cpu.GetSegment(Segment.Es) == 0x2000);
                uint address = 0x23000;
                Check(m.Memory.ReadByte(address + 4) == mode && m.Memory.ReadWord(address + 5) == columns);
                Check(m.Memory.ReadByte(address + 0x22) == rows && m.Memory.ReadWord(address + 0x23) == height && m.Memory.ReadWord(address + 0x27) == colors);
                Check(m.Memory.ReadByte(address + 0x25) == 8 && m.Memory.ReadByte(address + 0x26) == 0 && m.Memory.ReadByte(address + 0x29) == (mode == 3 ? 8 : 1));
                Check(m.Memory.ReadByte(address + 0x2a) == lines && m.Memory.ReadByte(address + 0x31) == 3 && m.Memory.ReadByte(address + 0x32) == 0);
                uint table = (uint)m.Memory.ReadWord(address + 2) * 16 + m.Memory.ReadWord(address);
                Check(m.Memory.ReadByte(table) == 8 && m.Memory.ReadByte(table + 1) == 0 && m.Memory.ReadByte(table + 2) == 12);
                Check(m.Memory.ReadByte(table + 7) == 7 && m.Memory.ReadByte(table + 8) == 8 && m.Memory.ReadByte(table + 9) == 2);
                Check(m.Memory.ReadByte(table + 10) == 0x54 && m.Memory.ReadByte(table + 11) == 0 && m.Memory.ReadByte(table + 14) == 1);
                Check(m.Memory.ReadByte(0x22fff) == 0xa5 && m.Memory.ReadByte(0x23040) == 0x5a);
            }
            Bios(m, 3); m.Cpu.SetRegister(Register.Bx, 0x3e); Bios(m, 0x1103);
            m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x1b00);
            Check(m.Memory.ReadByte(0x2302b) == 6 && m.Memory.ReadByte(0x2302c) == 7 && m.Memory.ReadByte(0x23032) == 1 && m.Memory.ReadByte(0x2302d) == 0x30);
            Reg(m.Video, 0x3c4, 4, 0); m.Video.Write(0x3c4, 1, 1); Bios(m, 0x1b00);
            Check(m.Memory.ReadByte(0x23032) == 0 && m.Video.Read(0x3c4, 1) == 1, "State must reflect disabled extended-font selection and restore the sequencer index");
        });
        test("BIOS functionality accepts the final 64-byte segment window and rejects unsupported/crossing requests without writes", () =>
        {
            using var m = Boot(); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0xffc0); m.Cpu.SetRegister(Register.Bx, 0);
            Bios(m, 0x1b00); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0x1b && m.Memory.ReadByte(0x2ffc4) == 3);
            byte[] before = m.Memory.Ram.AsSpan(0x20000, 65536).ToArray();
            m.Cpu.SetRegister(Register.Di, 0xffc1); Bios(m, 0x1b00);
            Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0 && m.Memory.Ram.AsSpan(0x20000, 65536).SequenceEqual(before));
            m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Bx, 1); Bios(m, 0x1b00);
            Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0 && m.Memory.Ram.AsSpan(0x20000, 65536).SequenceEqual(before));
        });
    }
}
