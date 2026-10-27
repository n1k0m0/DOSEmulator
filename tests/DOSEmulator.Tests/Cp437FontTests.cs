// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class Cp437FontTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "CP437 assertion failed")
    { if (!condition) throw new Exception(message); }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(200000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return");
    }
    /// <summary>Registers the named cp437 font regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int height in new[] { 8, 14, 16 })
        {
            test($"Own CP437 {height}-row font has all video glyphs, lowercase and only three intentional blanks", () =>
            {
                var font = Cp437Font.Table(height);
                Check(font.Length == 256 * height);
                for (int code = 0; code < 256; code++)
                {
                    bool blank = true;
                    for (int row = 0; row < height; row++) blank &= font[code * height + row] == 0;
                    Check(blank == (code is 0 or 32 or 255), $"{code:X2}: missing or unexpected blank");
                }
                for (int code = 'a'; code <= 'z'; code++)
                    Check(!font.Slice(code * height, height).SequenceEqual(font.Slice((code - 32) * height, height)), $"{(char)code}: capital alias");
                foreach (int code in new[] { 0x24,0x26,0x5e,0x60,0x7b,0x7d,0x7e,0x80,0x84,0x8e,0xe0,0xec,0xf4,0xfd })
                    Check(!font.Slice(code * height, height).SequenceEqual(font.Slice(63 * height, height)), $"{code:X2}: question-mark fallback");
            });
            test($"CP437 {height}-row box repertoire has the correct single/double strokes at all four cell edges", () =>
            {
                // U/D/L/R repertoire, independent character semantics from CP437.
                string[] arms = ["1100","1110","1120","2210","0210","0120","2220","2200","0220","2020","2010","1020","0110",
                    "1001","1011","0111","1101","0011","1111","1102","2201","2002","0202","2022","0222","2202","0022","2222",
                    "1022","2011","0122","0211","2001","1002","0102","0201","2211","1122","1010","0101"];
                var font = Cp437Font.Table(height);
                for (int i = 0; i < arms.Length; i++)
                {
                    int code = 0xb3 + i;
                    // Selects the independent line-art style bits used by the font topology oracle.
                    int Rail(char style) => style switch { '0' => 0, '1' => 0x10, _ => 0x24 };
                    Check(font[code * height] == Rail(arms[i][0]) && font[code * height + height - 1] == Rail(arms[i][1]), $"{code:X2}: vertical boundary");
                    for (int y = 0; y < height; y++)
                    {
                        // Tests whether the independent font oracle's horizontal rail reaches this scanline.
                        bool Reaches(char style) => style == '1' ? y == height / 2 : style == '2' && Math.Abs(y - height / 2) == 1;
                        Check(((font[code * height + y] & 128) != 0) == Reaches(arms[i][2]), $"{code:X2}: left boundary {y}");
                        Check(((font[code * height + y] & 1) != 0) == Reaches(arms[i][3]), $"{code:X2}: right boundary {y}");
                    }
                }
            });
            test($"CP437 {height}-row shades retain density and block halves tile without holes or overlap", () =>
            {
                var font = Cp437Font.Table(height);
                for (int shade = 0; shade < 3; shade++)
                {
                    int set = 0;
                    for (int y = 0; y < height; y++) set += BitOperations.PopCount((uint)font[(0xb0 + shade) * height + y]);
                    Check(set == (shade + 1) * 2 * height);
                }
                for (int y = 0; y < height; y++)
                {
                    Check(font[0xdb * height + y] == 255);
                    byte lower = font[0xdc * height + y], upper = font[0xdf * height + y];
                    Check((lower | upper) == 255 && (lower & upper) == 0 && (lower == 255) == (y >= height / 2));
                    Check(font[0xdd * height + y] == 240 && font[0xde * height + y] == 15);
                }
            });
            test($"BIOS own CP437 {height}-row ROM font reaches real plane-two scanout for every code", () =>
            {
                using var m = PcMachine.CreateDemo(); m.RunSteps(100000);
                m.Cpu.SetRegister(Register.Bx, 0);
                Bios(m, (ushort)(height == 8 ? 0x1112 : height == 14 ? 0x1111 : 0x1114));
                m.Video.Write(0x3d4, 0x200a, 2); // Hide hardware cursor.
                for (int code = 0; code < 256; code++) m.Memory.WriteWord((uint)(0xb8000 + code * 2), (ushort)(0x0700 | code));
                var frame = m.Video.Capture(0);
                var font = Cp437Font.Table(height);
                for (int code = 0; code < 256; code++)
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            int offset = ((code / 80 * height + y) * frame.Width) + code % 80 * 8 + x;
                            uint expected = (font[code * height + y] & (128 >> x)) == 0 ? 0xff000000u : 0xffaaaaaau;
                            Check(frame.ArgbPixels[offset] == expected, $"{code:X2}: pixel {x}/{y}");
                        }
            });
            test($"Own CP437 {height}-row rightward rails and blocks meet the real ninth VGA column", () =>
            {
                using var m = PcMachine.CreateDemo(); m.RunSteps(100000); m.Cpu.SetRegister(Register.Bx, 0);
                Bios(m, (ushort)(height == 8 ? 0x1112 : height == 14 ? 0x1111 : 0x1114));
                m.Video.Write(0x3d4, 0x200a, 2); m.Video.Write(0x3c4, 0x0001, 2);
                for (int code = 0xc0; code <= 0xdf; code++) m.Memory.WriteWord((uint)(0xb8000 + (code - 0xc0) * 2), (ushort)(0x0700 | code));
                var frame = m.Video.Capture(0); Check(frame.Width == 720);
                var font = Cp437Font.Table(height);
                for (int code = 0xc0; code <= 0xdf; code++)
                    for (int y = 0; y < height; y++)
                    {
                        int offset = y * frame.Width + (code - 0xc0) * 9 + 8;
                        Check(frame.ArgbPixels[offset] == ((font[code * height + y] & 1) != 0 ? 0xffaaaaaau : 0xff000000u));
                    }
            });
        }
    }
}
