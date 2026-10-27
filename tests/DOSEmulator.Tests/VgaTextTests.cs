// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class VgaTextTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "VGA text assertion failed") { if (!value) throw new Exception(message); }
    /// <summary>Writes an actual indexed VGA register for the controlled fixture setup.</summary>
    private static void Reg(VgaAdapter v, int index, int value) => v.Write(0x3d4, (uint)(index | value << 8), 2);
    /// <summary>Reads an actual indexed device register for the surrounding independent assertions.</summary>
    private static int Get(VgaAdapter v, int index) { v.Write(0x3d4, (uint)index, 1); return (int)v.Read(0x3d5, 1); }
    /// <summary>Reads the actual high/low CRTC cursor address registers.</summary>
    private static int HardwareCursor(PcMachine m) => Get(m.Video, 14) << 8 | Get(m.Video, 15);
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot()
    {
        var m = PcMachine.CreateDemo(); m.RunSteps(150000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not halt"); Bios(m, 3); return m;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(3000000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return");
    }
    /// <summary>Positions the cursor through the real guest BIOS service.</summary>
    private static void Cursor(PcMachine m, int page, int row, int column)
    {
        m.Cpu.SetRegister(Register.Bx, (uint)(page << 8)); m.Cpu.SetRegister(Register.Dx, (uint)(row << 8 | column)); Bios(m, 0x0200);
    }
    /// <summary>Reads an actual character/attribute cell from the selected private VGA text page.</summary>
    private static ushort Cell(PcMachine m, int page, int row, int column) => m.Memory.ReadWord((uint)(0xb8000 + page * m.Memory.ReadWord(0x44c) + (row * 80 + column) * 2));
    /// <summary>Draws bounded bitmap UI text using the original project font.</summary>
    private static byte[] Text(PcMachine m) => Enumerable.Range(0, 32768).Select(i => m.Memory.ReadByte((uint)(0xb8000 + i))).ToArray();
    /// <summary>Queries the real BIOS video-state information used by the text-page assertions.</summary>
    private static int Pages(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x1b00);
        return m.Memory.ReadByte(0x23029);
    }
    /// <summary>Registers the named vga text regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int page in Enumerable.Range(0, 8))
            test($"BIOS text page {page} has independent cursor, character attributes, CRTC start and readback", () =>
            {
                using var m = Boot(); Cursor(m, page, page + 1, page + 2);
                m.Cpu.SetRegister(Register.Cx, 3); m.Cpu.SetRegister(Register.Bx, (uint)(page << 8 | 14)); Bios(m, (ushort)(0x0941 + page));
                Check(Cell(m, page, page + 1, page + 2) == (0x0e41 + page));
                Bios(m, (ushort)(0x0a61 + page)); Check(Cell(m, page, page + 1, page + 2) == 0x0e61 + page, "AH0A changed attributes");
                Bios(m, 0x0800); Check((m.Cpu.GetRegister(Register.Ax) & 65535) == 0x0e61 + page);
                Check(m.Memory.ReadWord((uint)(0x450 + 2 * page)) == ((page + 1) << 8 | page + 2));
                int oldIndex = 4; m.Video.Write(0x3d4, (uint)oldIndex, 1); Bios(m, (ushort)(0x0500 | page));
                Check(m.Video.Read(0x3d4, 1) == oldIndex && m.Memory.ReadByte(0x462) == page && m.Memory.ReadWord(0x44e) == page * 4096);
                Check((Get(m.Video, 12) << 8 | Get(m.Video, 13)) == page * 2048);
                Check(HardwareCursor(m) == page * 2048 + (page + 1) * 80 + page + 2);
                Check(m.Video.ReadText().Contains(new string((char)('a' + page), 3)), "Selected page was not scanned out");
                m.Cpu.SetRegister(Register.Bx, (uint)(page << 8)); Bios(m, 0x0300);
                Check((m.Cpu.GetRegister(Register.Dx) & 65535) == ((page + 1) << 8 | page + 2));
                Bios(m, 0x0f00); Check((m.Cpu.GetRegister(Register.Bx) >> 8 & 255) == page && (m.Cpu.GetRegister(Register.Ax) & 65535) == 0x5003);
                Check(Pages(m) == 8 && m.Memory.ReadWord(0x23009) == page * 4096 && m.Memory.ReadByte(0x2301d) == page);
                if (page != 0) Check(Cell(m, 0, page + 1, page + 2) == 0x0720 && m.Memory.ReadWord(0x450) == 0);
            });
        test("Text mode set clears every page and resets all cursor slots; no-clear retains all text bytes", () =>
        {
            using var m = Boot();
            for (int page = 0; page < 8; page++) { Cursor(m, page, 3, page); m.Memory.WriteWord((uint)(0xb8000 + page * 4096), (ushort)(0x1e41 + page)); }
            Bios(m, 0x0507); byte[] before = Text(m); Bios(m, 0x0083);
            Check(Text(m).SequenceEqual(before) && m.Memory.ReadByte(0x462) == 0 && m.Memory.ReadWord(0x44e) == 0 && HardwareCursor(m) == 0);
            for (uint i = 0; i < 8; i++) Check(m.Memory.ReadWord(0x450 + i * 2) == 0);
            Bios(m, 3); for (uint i = 0; i < 32768; i += 2) Check(m.Memory.ReadWord(0xb8000 + i) == 0x0720);
        });
        test("Repeated character writes stop at the visible page boundary and preserve padding and adjacent pages", () =>
        {
            using var m = Boot(); Cursor(m, 6, 24, 79); byte[] before = Text(m);
            m.Cpu.SetRegister(Register.Bx, 0x061d); m.Cpu.SetRegister(Register.Cx, 65535); Bios(m, 0x0958);
            byte[] expected = before.ToArray(); expected[6 * 4096 + 3998] = (byte)'X'; expected[6 * 4096 + 3999] = 0x1d;
            Check(Text(m).SequenceEqual(expected) && m.Memory.ReadWord(0x45c) == 0x184f);
        });
        foreach (var (font, size, rows, pages) in new[] { (0x1114,4000,25,8), (0x1111,4480,28,7), (0x1112,8000,50,4) })
            test($"Text pages honor compact {rows}-row font geometry instead of assuming a 4-KiB stride", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0); Bios(m, (ushort)font);
                Check(m.Memory.ReadWord(0x44c) == size && Pages(m) == pages);
                Cursor(m, pages - 1, rows - 1, 79); m.Cpu.SetRegister(Register.Bx, (uint)((pages - 1) << 8 | 11)); m.Cpu.SetRegister(Register.Cx, 1); Bios(m, 0x0951);
                Bios(m, (ushort)(0x0500 | pages - 1)); Check(m.Memory.ReadWord(0x44e) == (pages - 1) * size && Cell(m, pages - 1, rows - 1, 79) == 0x0b51);
                Check(HardwareCursor(m) == ((pages - 1) * size / 2 + rows * 80 - 1));
                byte[] before = Text(m); Bios(m, (ushort)(0x0500 | pages)); Check(m.Memory.ReadByte(0x462) == pages - 1 && Text(m).SequenceEqual(before));
                m.Cpu.SetRegister(Register.Bx, 0); Bios(m, (ushort)font); Check(m.Memory.ReadByte(0x462) == 0 && m.Memory.ReadWord(0x44e) == 0 && HardwareCursor(m) == 0);
            });
        foreach (var (down, count) in new[] { (false,0), (false,1), (false,255), (true,0), (true,1), (true,255) })
            test($"Active hidden-page rectangular scroll {(down ? "down" : "up")} count {count} preserves every outside cell", () =>
            {
                using var m = Boot();
                for (int row = 0; row < 25; row++) for (int column = 0; column < 80; column++) m.Memory.WriteWord((uint)(0xb8000 + 3 * 4096 + (row * 80 + column) * 2), (ushort)((row + 1) << 8 | column));
                Bios(m, 0x0503); byte[] original = Text(m), expected = original.ToArray();
                for (int row = 4; row <= 8; row++) for (int column = 5; column <= 17; column++)
                {
                    int source = row + (down ? -count : count), destination = 3 * 4096 + (row * 80 + column) * 2;
                    if (count == 0 || source < 4 || source > 8) { expected[destination] = 32; expected[destination + 1] = 0x2e; }
                    else Array.Copy(original, 3 * 4096 + (source * 80 + column) * 2, expected, destination, 2);
                }
                m.Cpu.SetRegister(Register.Cx, 0x0405); m.Cpu.SetRegister(Register.Dx, 0x0811); m.Cpu.SetRegister(Register.Bx, 0x2e00);
                int cursor = HardwareCursor(m); Bios(m, (ushort)((down ? 0x0700 : 0x0600) | count));
                Check(Text(m).SequenceEqual(expected) && HardwareCursor(m) == cursor);
            });
        foreach (int font in new[] { 0x1114, 0x1111, 0x1112 })
        foreach (bool down in new[] { false, true })
        foreach (int count in new[] { 0, 1 })
        foreach (int edge in new[] { 0, 1, 2 })
            test($"BIOS text scroll clips lower/right edge {edge}, font {font:X}, down={down}, count={count} without crossing pages", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0); Bios(m, (ushort)font);
                int rows = m.Memory.ReadByte(0x484) + 1, stride = m.Memory.ReadWord(0x44c), page = 2;
                for (int row = 0; row < rows; row++) for (int column = 0; column < 80; column++)
                    m.Memory.WriteWord((uint)(0xb8000 + page * stride + (row * 80 + column) * 2), (ushort)((row + 1) << 8 | column));
                Bios(m, (ushort)(0x0500 | page)); byte[] original = Text(m), expected = original.ToArray();
                int bottom = edge == 1 ? rows - 2 : rows - 1, right = edge == 0 ? 74 : 79;
                for (int row = 3; row <= bottom; row++) for (int column = 5; column <= right; column++)
                {
                    int source = row + (down ? -count : count), dest = page * stride + (row * 80 + column) * 2;
                    if (count == 0 || source < 3 || source > bottom) { expected[dest] = 32; expected[dest + 1] = 0x2e; }
                    else Array.Copy(original, page * stride + (source * 80 + column) * 2, expected, dest, 2);
                }
                // A one-past row is the actual Microsoft ANSI.SYS CLS request;
                // FFh also checks that clipping happens before address arithmetic.
                int requestedBottom = edge == 1 ? bottom : edge == 0 ? rows : 255;
                m.Cpu.SetRegister(Register.Cx, 0x0305); m.Cpu.SetRegister(Register.Dx, (uint)(requestedBottom << 8 | (edge == 0 ? right : 255)));
                m.Cpu.SetRegister(Register.Bx, 0x2e00); m.Cpu.Eflags = 2 | X86Cpu.Direction | X86Cpu.Carry;
                uint dx = m.Cpu.GetRegister(Register.Dx); int cursor = HardwareCursor(m);
                Bios(m, (ushort)((down ? 0x0700 : 0x0600) | count));
                Check(Text(m).SequenceEqual(expected) && HardwareCursor(m) == cursor, "Clipped scroll changed outside bytes or failed to clear/copy");
                Check(m.Cpu.GetRegister(Register.Dx) == dx && m.Cpu.GetRegister(Register.Cx) == 0x0305 && m.Cpu.GetRegister(Register.Bx) == 0x2e00);
                Check(m.Cpu.Eflags == (2 | X86Cpu.Direction | X86Cpu.Carry));
            });
        test("Teletype uses the active text page, keeps existing attributes and handles wrap, CR, LF and BS", () =>
        {
            using var m = Boot(); Cursor(m, 3, 24, 79); Bios(m, 0x0503);
            m.Memory.WriteWord(0xb8000 + 3 * 4096 + 3998, 0x1e20); m.Memory.WriteWord(0xb8000 + 3 * 4096 + 160, 0x0b4b);
            m.Cpu.SetRegister(Register.Bx, 0x0700); Bios(m, 0x0e58);
            Check(Cell(m, 3, 23, 79) == 0x1e58 && Cell(m, 3, 0, 0) == 0x0b4b && Cell(m, 3, 24, 0) == 0x0720);
            Check(m.Memory.ReadWord(0x456) == 0x1800 && Cell(m, 0, 0, 0) == 0x0720 && m.Memory.ReadWord(0x450) == 0);
            Bios(m, 0x0e41); Bios(m, 0x0e08); Bios(m, 0x0e42); Bios(m, 0x0e0d); Check(m.Memory.ReadWord(0x456) == 0x1800 && Cell(m, 3, 24, 0) == 0x0742);
            Bios(m, 0x0e0a); Check(m.Memory.ReadWord(0x456) == 0x1800 && Cell(m, 3, 23, 0) == 0x0742 && Cell(m, 3, 24, 0) == 0x0720);
        });
        foreach (int form in Enumerable.Range(0, 4))
            test($"BIOS AH13 form {form} consumes text/control bytes and optional attributes on an inactive page", () =>
            {
                using var m = Boot(); Cursor(m, 2, 6, 7); int cursor = HardwareCursor(m);
                byte[] chars = [(byte)'A',13,10,(byte)'B',8,(byte)'C'];
                var data = chars.SelectMany((ch,i) => (form & 2) == 0 ? new byte[] { ch } : new byte[] { ch, (byte)(0x10 + i) }).ToArray();
                data.CopyTo(m.Memory.Ram, 0x23000); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bp, 0x3000);
                m.Cpu.SetRegister(Register.Bx, 0x020e); m.Cpu.SetRegister(Register.Cx, (uint)chars.Length); m.Cpu.SetRegister(Register.Dx, 0x0304); Bios(m, (ushort)(0x1300 | form));
                Check(Cell(m, 2, 3, 4) == ((form & 2) == 0 ? 0x0e41 : 0x1041));
                Check(Cell(m, 2, 4, 0) == ((form & 2) == 0 ? 0x0e43 : 0x1543));
                Check(m.Memory.ReadWord(0x454) == ((form & 1) == 0 ? 0x0607 : 0x0401) && m.Memory.ReadWord(0x450) == 0 && HardwareCursor(m) == cursor);
                Check(m.Cpu.GetRegister(Register.Bp) == 0x3000 && m.Cpu.GetSegment(Segment.Es) == 0x2000 && m.Cpu.GetRegister(Register.Dx) == 0x0304);
            });
        test("BIOS string input wraps its source offset bytewise, preserves CPU state and updates active CRTC cursor", () =>
        {
            using var m = Boot(); Bios(m, 0x0501);
            m.Memory.WriteByte(0x2ffff, (byte)'X'); m.Memory.WriteByte(0x20000, 0x2c); m.Memory.WriteByte(0x20001, (byte)'Y'); m.Memory.WriteByte(0x20002, 0x3d);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetSegment(Segment.Ds, 0x1234); m.Cpu.SetRegister(Register.Bp, 0xabcdffff);
            m.Cpu.SetRegister(Register.Bx, 0x87650100); m.Cpu.SetRegister(Register.Cx, 0x98760002); m.Cpu.SetRegister(Register.Dx, 0x54320102);
            m.Cpu.SetRegister(Register.Si, 0x6789abcd); m.Cpu.SetRegister(Register.Di, 0x4567abcd); m.Cpu.Eflags = 2 | X86Cpu.Direction | X86Cpu.Carry;
            uint sp = m.Cpu.GetRegister(Register.Sp); Bios(m, 0x1303);
            Check(Cell(m, 1, 1, 2) == 0x2c58 && Cell(m, 1, 1, 3) == 0x3d59 && m.Memory.ReadWord(0x452) == 0x0104 && HardwareCursor(m) == 2048 + 84);
            Check(m.Cpu.GetRegister(Register.Bp) == 0xabcdffff && m.Cpu.GetRegister(Register.Bx) == 0x87650100 && m.Cpu.GetRegister(Register.Cx) == 0x98760002 && m.Cpu.GetRegister(Register.Dx) == 0x54320102);
            Check(m.Cpu.GetRegister(Register.Si) == 0x6789abcd && m.Cpu.GetRegister(Register.Di) == 0x4567abcd && m.Cpu.GetRegister(Register.Sp) == sp);
            Check(m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetSegment(Segment.Es) == 0x2000 && m.Cpu.Eflags == (2 | X86Cpu.Direction | X86Cpu.Carry));
        });
        test("A string scrolls its inactive page and keeps the saved cursor when AL bit zero is clear", () =>
        {
            using var m = Boot(); Cursor(m, 4, 2, 3);
            m.Memory.WriteWord(0xb8000 + 4 * 4096 + 160, 0x0b4b); new byte[] { (byte)'X',10,(byte)'Z' }.CopyTo(m.Memory.Ram, 0x23000);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bp, 0x3000); m.Cpu.SetRegister(Register.Bx, 0x040e);
            m.Cpu.SetRegister(Register.Cx, 3); m.Cpu.SetRegister(Register.Dx, 0x184f); Bios(m, 0x1300);
            Check(Cell(m, 4, 22, 79) == 0x0e58 && Cell(m, 4, 24, 0) == 0x0e5a && m.Memory.ReadWord(0x458) == 0x0203);
            Check(Cell(m, 0, 0, 0) == 0x0720 && m.Memory.ReadByte(0x462) == 0 && HardwareCursor(m) == 0);
        });
        test("Invalid pages, positions, rectangles and string forms are atomic no-ops", () =>
        {
            using var m = Boot(); byte[] original = Text(m), bda = m.Memory.Ram.AsSpan(0x449, 30).ToArray();
            foreach (var (ax,bx,cx,dx) in new[] { (0x0508,0,0,0), (0x0200,0x0800,0,0), (0x0200,0,0,0x1900), (0x0200,0,0,0x0050), (0x0941,0x080e,1,0), (0x0601,0x0700,0x0800,0x0400), (0x0701,0x0700,0x0009,0x0008), (0x0600,0x0700,0x1900,0xffff), (0x0700,0x0700,0x0050,0xffff), (0x1304,0,1,0), (0x1301,0,0,0x0101), (0x1301,0x0800,1,0) })
            {
                m.Cpu.SetRegister(Register.Bx, (uint)bx); m.Cpu.SetRegister(Register.Cx, (uint)cx); m.Cpu.SetRegister(Register.Dx, (uint)dx); Bios(m, (ushort)ax);
                Check(Text(m).SequenceEqual(original) && m.Memory.Ram.AsSpan(0x449, 30).SequenceEqual(bda), $"Invalid {ax:X}/{bx:X} modified text state");
            }
        });
        test("Graphics modes keep page-zero software cursor coordinates and reject text-only string forms/invalid pages", () =>
        {
            using var m = Boot(); foreach (ushort mode in new ushort[] { 0x12, 0x13 })
            {
                Bios(m, mode); Cursor(m, 0, 3, 4); Bios(m, 0x0300);
                Check((m.Cpu.GetRegister(Register.Dx) & 65535) == 0x0304 && Pages(m) == 1);
                Cursor(m, 1, 7, 8); Check(m.Memory.ReadWord(0x450) == 0x0304);
                byte[] before = m.Video.Capture(0).ArgbPixels.SelectMany(BitConverter.GetBytes).ToArray();
                m.Cpu.SetRegister(Register.Bx, 0x0100); m.Cpu.SetRegister(Register.Cx, 1); Bios(m, 0x0a41);
                m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x1302);
                Check(m.Video.Capture(0).ArgbPixels.SelectMany(BitConverter.GetBytes).SequenceEqual(before), "Invalid pages/text-only graphics string forms must not corrupt graphics memory");
            }
        });
        test("VGA cursor uses cell foreground color across eight/nine-dot cells and honors skew, disable and reversed shape", () =>
        {
            using var m = Boot(); m.Memory.WriteWord(0xb8000, 0x0220); m.Memory.WriteWord(0xb8002, 0x0420);
            Reg(m.Video, 10, 14); Reg(m.Video, 11, 15);
            Check(m.Video.Capture(0).ArgbPixels[14 * 640] == 0xff00aa00);
            m.Video.Write(0x3c4, 0x0001, 2); var nine = m.Video.Capture(0);
            Check(nine.Width == 720 && nine.ArgbPixels.AsSpan(14 * 720, 9).ToArray().All(p => p == 0xff00aa00));
            Reg(m.Video, 11, 0x2f); var shifted = m.Video.Capture(0);
            Check(shifted.ArgbPixels[14 * 720] == 0xff000000 && shifted.ArgbPixels[14 * 720 + 9] == 0xffaa0000);
            Reg(m.Video, 10, 0x2e); Check(m.Video.Capture(0).ArgbPixels[14 * 720 + 9] == 0xff000000);
            Reg(m.Video, 10, 15); Reg(m.Video, 11, 0x2e); Check(m.Video.Capture(0).ArgbPixels[14 * 720 + 9] == 0xff000000);
        });
        test("VGA virtual cursor blink changes after sixteen raster frames independently of attribute blink after thirty-two", () =>
        {
            using var m = Boot(); m.Memory.WriteWord(0xb8000, 0x8220); Reg(m.Video, 10, 14); Reg(m.Video, 11, 15);
            const ulong period = 14_285_714;
            Check(m.Video.Capture(16 * period - 1).ArgbPixels[14 * 640] == 0xff00aa00);
            Check(m.Video.Capture(16 * period).ArgbPixels[14 * 640] == 0xff000000);
            Check(m.Video.Capture(32 * period).ArgbPixels[14 * 640] == 0xff00aa00, "Cursor must remain independent of character blink");
            // One solid glyph row in plane 2, with cursor disabled: character blink has a distinct period.
            m.Memory.WriteByte(0x23000, 255); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bp, 0x3000);
            m.Cpu.SetRegister(Register.Bx, 0x0100); m.Cpu.SetRegister(Register.Cx, 1); m.Cpu.SetRegister(Register.Dx, 32); Bios(m, 0x1100); Reg(m.Video, 10, 32);
            Check(m.Video.Capture(32 * period - 1).ArgbPixels[0] == 0xff00aa00 && m.Video.Capture(32 * period).ArgbPixels[0] == 0xff000000);
            Check(m.Video.Capture(64 * period).ArgbPixels[0] == 0xff00aa00);
        });
    }
}
