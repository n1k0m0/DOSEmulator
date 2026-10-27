// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class VgaTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "VGA assertion failed.") { if (!value) throw new Exception(message); }
    /// <summary>Writes an actual indexed VGA register for the controlled fixture setup.</summary>
    private static void Reg(VgaAdapter v, ushort port, int index, int value) => v.Write(port, (uint)(index | value << 8), 2);
    /// <summary>Reads an actual indexed device register for the surrounding independent assertions.</summary>
    private static byte Get(VgaAdapter v, ushort port, int index) { v.Write(port, (uint)index, 1); return (byte)v.Read((ushort)(port + 1), 1); }
    /// <summary>Resets the actual VGA attribute flip-flop and writes the selected index/data value.</summary>
    private static void Attribute(VgaAdapter v, int index, int value)
    {
        v.Read(0x3da, 1); v.Write(0x3c0, (uint)index, 1); v.Write(0x3c0, (uint)value, 1); v.Write(0x3c0, 0x20, 1);
    }
    /// <summary>Builds the original project's planar VGA palette table.</summary>
    private static VgaAdapter Planar()
    {
        var v = new VgaAdapter(); Reg(v, 0x3c4, 4, 6); Reg(v, 0x3c4, 2, 15); Reg(v, 0x3ce, 5, 0); Reg(v, 0x3ce, 6, 5); return v;
    }
    /// <summary>Reads one actual VGA plane while preserving the fixture's original access registers.</summary>
    private static void Plane(VgaAdapter v, int plane, int address, byte value)
    {
        Reg(v, 0x3c4, 2, 1 << plane); v.WriteByte((uint)address, value);
    }
    /// <summary>Reads one actual VGA plane while preserving the fixture's original access registers.</summary>
    private static byte Plane(VgaAdapter v, int plane, int address)
    {
        Reg(v, 0x3ce, 4, plane); return v.ReadByte((uint)address);
    }
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot()
    {
        var m = PcMachine.CreateDemo(); m.RunSteps(100000); Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not finish."); return m;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(100000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return.");
    }
    /// <summary>Invokes the real guest pixel BIOS operation at the requested coordinates.</summary>
    private static void Pixel(PcMachine m, int x, int y, ushort ax)
    {
        m.Cpu.SetRegister(Register.Cx, (uint)x); m.Cpu.SetRegister(Register.Dx, (uint)y); Bios(m, ax);
    }
    /// <summary>Registers the named vga regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("VGA aperture decode keeps display holes separate from RAM", () =>
        {
            using var m = Boot(); m.Memory.Ram[0xa0000] = 0x55; m.Memory.WriteByte(0xa0000, 0xaa);
            Check(m.Memory.ReadByte(0xa0000) == 0xff && m.Memory.Ram[0xa0000] == 0x55);
            Reg(m.Video, 0x3ce, 6, 5); Reg(m.Video, 0x3ce, 5, 0); Reg(m.Video, 0x3c4, 4, 6);
            Reg(m.Video, 0x3c4, 2, 1); m.Memory.WriteByte(0xa0000, 0x3c);
            Check(m.Memory.ReadByte(0xa0000) == 0x3c && m.Memory.ReadByte(0xb8000) == 255);
            m.Video.Write(0x3c2, 0x65, 1); Check(m.Memory.ReadByte(0xa0000) == 255);
            m.Video.Write(0x3c2, 0x67, 1); Check(m.Memory.ReadByte(0xa0000) == 0x3c);
        });
        test("VGA planar maps and read-map selection have four independent 64-KiB planes", () =>
        {
            var v = Planar(); for (int p = 0; p < 4; p++) { Plane(v, p, 0, (byte)(p + 1)); Plane(v, p, 65535, (byte)(0xa0 + p)); }
            for (int p = 0; p < 4; p++) Check(Plane(v, p, 0) == p + 1 && Plane(v, p, 65535) == 0xa0 + p);
            Reg(v, 0x3c4, 2, 0); v.WriteByte(0, 99); Check(Plane(v, 3, 0) == 4);
        });
        test("VGA write mode zero uses rotated CPU data and previous read latches", () =>
        {
            var v = Planar(); Plane(v, 0, 0, 0x0f); Plane(v, 0, 1, 0xaa); v.ReadByte(0);
            Reg(v, 0x3ce, 3, 0x19); Reg(v, 0x3ce, 8, 0xf0); v.WriteByte(1, 3);
            Check(Plane(v, 0, 1) == 0x8f, "Rotate/XOR/mask must merge the source latch, not the destination byte.");
        });
        test("VGA set reset expands colors independently of the CPU byte", () =>
        {
            var v = Planar(); Reg(v, 0x3ce, 0, 4); Reg(v, 0x3ce, 1, 15); v.WriteByte(0, 0xa5);
            for (int p = 0; p < 4; p++) Check(Plane(v, p, 0) == (p == 2 ? 255 : 0));
        });
        test("VGA logical AND and OR combine CPU data with read latches", () =>
        {
            var v = Planar(); Plane(v, 0, 0, 0x3c); v.ReadByte(0); Reg(v, 0x3ce, 3, 8); v.WriteByte(1, 0x5a);
            Check(Plane(v, 0, 1) == 0x18); v.ReadByte(0); Reg(v, 0x3ce, 3, 16); v.WriteByte(2, 0x5a); Check(Plane(v, 0, 2) == 0x7e);
        });
        test("VGA write mode one copies latches with map mask and ignores bit mask", () =>
        {
            var v = Planar(); byte[] values = [0x12, 0x34, 0x56, 0x78];
            for (int p = 0; p < 4; p++) Plane(v, p, 0, values[p]);
            v.ReadByte(0); Reg(v, 0x3ce, 5, 1); Reg(v, 0x3ce, 3, 31); Reg(v, 0x3ce, 8, 0); Reg(v, 0x3c4, 2, 5); v.WriteByte(1, 255);
            for (int p = 0; p < 4; p++) Check(Plane(v, p, 1) == (p % 2 == 0 ? values[p] : 0));
        });
        test("VGA write modes two and three expand color with latch masks and logic", () =>
        {
            foreach (int mode in new[] { 2, 3 })
            {
                var v = Planar(); byte[] old = [0xaa, 0x55, 0xf0, 0x0f];
                for (int p = 0; p < 4; p++) Plane(v, p, 0, old[p]);
                v.ReadByte(0); Reg(v, 0x3c4, 2, 15); Reg(v, 0x3ce, 0, 5); Reg(v, 0x3ce, 1, 0);
                Reg(v, 0x3ce, 5, mode); Reg(v, 0x3ce, 3, mode == 2 ? 0 : 0x19); Reg(v, 0x3ce, 8, mode == 2 ? 15 : 0xf0);
                v.WriteByte(1, mode == 2 ? (byte)6 : (byte)3);
                byte[] expected = mode == 2 ? [0xa0, 0x5f, 0xff, 0] : [0x2a, 0x55, 0x70, 0x0f];
                for (int p = 0; p < 4; p++) Check(Plane(v, p, 1) == expected[p], $"Write mode {mode}, plane {p}.");
            }
        });
        test("VGA read mode one compares colors and excludes don't-care planes", () =>
        {
            var v = Planar(); byte[] bits = [0xaa, 0xcc, 0xf0, 0]; for (int p = 0; p < 4; p++) Plane(v, p, 0, bits[p]);
            Reg(v, 0x3ce, 2, 5); Reg(v, 0x3ce, 5, 8); Reg(v, 0x3ce, 7, 15); Check(v.ReadByte(0) == 0x20);
            Reg(v, 0x3ce, 7, 5); Check(v.ReadByte(0) == 0xa0); Reg(v, 0x3ce, 7, 0); Check(v.ReadByte(0) == 255);
        });
        test("VGA odd-even text and chain-four graphics route addresses to planes", () =>
        {
            var text = new VgaAdapter(); text.WriteByte(0x18000, (byte)'T'); text.WriteByte(0x18001, 14);
            Check(text.ReadByte(0x18000) == 'T' && text.ReadByte(0x18001) == 14 && text.ReadText().StartsWith("T"));
            Reg(text, 0x3ce, 6, 5); Reg(text, 0x3ce, 5, 0); Reg(text, 0x3c4, 4, 6);
            Check(Plane(text, 0, 0) == 'T' && Plane(text, 1, 0) == 14);
            var v = Planar(); Reg(v, 0x3c4, 4, 14); Reg(v, 0x3ce, 5, 0x40);
            for (uint i = 0; i < 8; i++) v.WriteByte(i, (byte)(i + 0x10));
            for (uint i = 0; i < 8; i++) Check(v.ReadByte(i) == i + 0x10);
            Reg(v, 0x3c4, 2, 1); v.WriteByte(1, 99); Check(v.ReadByte(1) == 0x11);
            Reg(v, 0x3c4, 4, 6); for (int p = 0; p < 4; p++) Check(Plane(v, p, 1) == p + 0x14);
        });
        test("VGA DAC masks six-bit components and wraps palette indices", () =>
        {
            var v = Planar(); v.Write(0x3c8, 255, 1); foreach (byte b in new byte[] { 0xff, 0x80, 0x7f, 1, 2, 3 }) v.Write(0x3c9, b, 1);
            Check(v.Read(0x3c8, 1) == 1 && v.Read(0x3c7, 1) == 0); v.Write(0x3c7, 255, 1); Check(v.Read(0x3c7, 1) == 3);
            foreach (byte b in new byte[] { 63, 0, 63, 1, 2, 3 }) Check(v.Read(0x3c9, 1) == b);
        });
        test("VGA attribute flip-flop resets on status read and gates scanout", () =>
        {
            var v = Planar(); Reg(v, 0x3c4, 2, 1); v.WriteByte(0, 0x80); Attribute(v, 1, 0x3f);
            Check(v.Capture(0).ArgbPixels[0] == 0xffffffff); v.Read(0x3da, 1); v.Write(0x3c0, 1, 1);
            Check(v.Capture(0).ArgbPixels[0] == 0xff000000); v.Read(0x3da, 1); v.Write(0x3c0, 0x20, 1);
            v.Write(0x3c6, 0, 1); Check(v.Capture(0).ArgbPixels[0] == 0xff000000);
        });
        test("VGA CRTC protection and monochrome-port selection follow guest registers", () =>
        {
            var v = new VgaAdapter(); Reg(v, 0x3d4, 0x11, 128); Reg(v, 0x3d4, 1, 39); Check(Get(v, 0x3d4, 1) == 79);
            Reg(v, 0x3d4, 7, 0xff); Check(Get(v, 0x3d4, 7) == 0x12);
            v.Write(0x3c2, 0x66, 1); Check(v.Read(0x3d4, 1) == 255 && v.Read(0x3da, 1) == 255);
            Reg(v, 0x3b4, 0x11, 0); Reg(v, 0x3b4, 1, 39); Check(v.Width == 320 && Get(v, 0x3b4, 1) == 39);
        });
        test("VGA retrace polling follows virtual time and not host wall time", () =>
        {
            var clock = new DeviceScheduler(); var v = new VgaAdapter(clock); Check(v.Read(0x3da, 1) == 0);
            clock.AdvanceTo(14_000_000); Check((v.Read(0x3da, 1) & 8) != 0); Check((v.Read(0x3da, 1) & 8) != 0);
            clock.AdvanceTo(14_285_714); Check(v.Read(0x3da, 1) == 0);
        });
        test("guest INT10 mode changes publish correct dimensions and preserve or clear VRAM", () =>
        {
            using var m = Boot(); int generation = m.Video.ModeGeneration; Bios(m, 0x13);
            var indexed = m.CaptureFrame(); Check(indexed.Width == 320 && indexed.Height == 200 && m.Video.ModeGeneration > generation);
            m.Memory.WriteByte(0xa0000, 201); Bios(m, 0x93); Check(m.Memory.ReadByte(0xa0000) == 201); Bios(m, 0x13); Check(m.Memory.ReadByte(0xa0000) == 0);
            Bios(m, 0x0f00); Check((m.Cpu.GetRegister(Register.Ax) & 65535) == 0x2813);
            Bios(m, 0x12); var planar = m.CaptureFrame(); Check(planar.Width == 640 && planar.Height == 480 && m.Video.ReadText() == "");
            Bios(m, 3); Bios(m, 0x0e41); Check(m.Video.ReadText() == "A"); var text = m.CaptureFrame();
            Check(text.Width == 640 && text.Height == 400 && text.ArgbPixels.Take(640 * 16).Distinct().Count() > 1);
            Check(indexed.Width == 320 && indexed.ArgbPixels.Length == 64000, "Published frames must remain immutable after a mode change.");
        });
        test("guest INT10 pixels read back indexed colors and planar XOR without register loss", () =>
        {
            using var m = Boot(); Bios(m, 0x13); Pixel(m, 319, 199, 0x0cc9); Pixel(m, 319, 199, 0x0d00);
            Check((m.Cpu.GetRegister(Register.Ax) & 255) == 201 && m.Memory.ReadByte(0xaf9ff) == 201);
            Bios(m, 0x12); Reg(m.Video, 0x3ce, 4, 2); Reg(m.Video, 0x3c4, 2, 5);
            Pixel(m, 639, 479, 0x0c0b); Check(Get(m.Video, 0x3c4, 2) == 5 && Get(m.Video, 0x3ce, 4) == 2);
            Pixel(m, 639, 479, 0x0d00); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 11);
            Pixel(m, 639, 479, 0x0c85); Pixel(m, 639, 479, 0x0d00); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 14);
            Pixel(m, 640, 479, 0x0c0f); Pixel(m, 639, 479, 0x0d00); Check((m.Cpu.GetRegister(Register.Ax) & 255) == 14);
        });
        test("guest INT10 DAC single and block services transfer palette through VGA ports", () =>
        {
            using var m = Boot(); Bios(m, 0x13); m.Cpu.SetRegister(Register.Bx, 42); m.Cpu.SetRegister(Register.Cx, 0x3f0c); m.Cpu.SetRegister(Register.Dx, 0x1b00); Bios(m, 0x1010);
            m.Cpu.SetRegister(Register.Cx, 0); m.Cpu.SetRegister(Register.Dx, 0); Bios(m, 0x1015);
            Check(m.Cpu.GetRegister(Register.Cx) == 0x3f0c && m.Cpu.GetRegister(Register.Dx) == 0x1b00);
            m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetRegister(Register.Dx, 0x3000); m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Bx, 254);
            new byte[] { 63, 0, 1, 2, 3, 4 }.CopyTo(m.Memory.Ram, 0x3000); Bios(m, 0x1012);
            m.Cpu.SetRegister(Register.Dx, 0x4000); Bios(m, 0x1017); Check(m.Memory.Ram.AsSpan(0x3000, 6).SequenceEqual(m.Memory.Ram.AsSpan(0x4000, 6)));
            m.Cpu.SetRegister(Register.Dx, 0xfffa); Bios(m, 0x1017); Check(m.Memory.Ram.AsSpan(0xfffa, 6).SequenceEqual(m.Memory.Ram.AsSpan(0x3000, 6)));
            m.Cpu.SetRegister(Register.Bx, 65535); m.Cpu.SetRegister(Register.Dx, 0x5000); m.Cpu.SetRegister(Register.Cx, 2); Bios(m, 0x1017);
            Check(m.Memory.ReadDword(0x5000) == 0, "Overflowing palette range must not access a caller buffer.");
        });
    }
}
