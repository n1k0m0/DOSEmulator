// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class CursorEmulationTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "Cursor emulation assertion failed")
    { if (!condition) throw new Exception(message); }
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot()
    {
        var m = PcMachine.CreateDemo(); m.RunSteps(100000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "POST did not complete"); return m;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(200000);
        Check(m.Fault is null && m.Cpu.Halted, m.Fault ?? "INT10 did not return");
    }
    /// <summary>Reads an actual indexed CRTC register while preserving the caller's index selection.</summary>
    private static byte Crtc(PcMachine m, int index)
    {
        int old = (int)m.Video.Read(0x3d4, 1); m.Video.Write(0x3d4, (uint)index, 1);
        byte value = (byte)m.Video.Read(0x3d5, 1); m.Video.Write(0x3d4, (uint)old, 1); return value;
    }
    /// <summary>Sets BIOS cursor-emulation policy through the real guest INT10 service.</summary>
    private static void Emulation(PcMachine m, bool enabled)
    {
        m.Cpu.SetRegister(Register.Bx, 0x34); Bios(m, (ushort)(enabled ? 0x1200 : 0x1201));
        Check((m.Cpu.GetRegister(Register.Ax) & 255) == 0x12);
    }
    /// <summary>Requests a cursor shape through the real guest BIOS rather than patching VGA state directly.</summary>
    private static void Shape(PcMachine m, ushort request)
    { m.Cpu.SetRegister(Register.Cx, request); Bios(m, 0x0100); }
    /// <summary>Selects the test's guest character height through the appropriate actual BIOS service.</summary>
    private static void Height(PcMachine m, int height)
    {
        m.Cpu.SetRegister(Register.Bx, 0);
        if (height is 8 or 14 or 16) Bios(m, (ushort)(height == 8 ? 0x1112 : height == 14 ? 0x1111 : 0x1114));
        else
        {
            m.Cpu.SetRegister(Register.Bx, (uint)(height << 8)); m.Cpu.SetRegister(Register.Cx, 1); m.Cpu.SetRegister(Register.Dx, 32);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bp, 0); Bios(m, 0x1110);
        }
        Check(m.Memory.ReadWord(0x485) == height);
    }
    /// <summary>Captures the fixture's actual BIOS/device state for the following independent assertions.</summary>
    private static uint State(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Bx, 0);
        Bios(m, 0x1b00); return 0x23000;
    }
    /// <summary>Registers the named cursor emulation regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("POST default underline occupies bottom two rows of a 16-high cell without AH01", () =>
        {
            using var m = Boot();
            Check(m.Memory.ReadWord(0x460) == 0x0e0f && Crtc(m, 10) == 14 && Crtc(m, 11) == 15,
                "POST must not leave the eight-high 06/07 cursor in a sixteen-high cell");
            m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, 0); Bios(m, 0x0200);
            m.Memory.WriteWord(0xb8000, 0x2c20);
            var frame = m.Video.Capture(0);
            for (int y = 0; y < 16; y++) for (int x = 0; x < 8; x++)
                Check(frame.ArgbPixels[y * frame.Width + x] == (y >= 14 ? 0xffff5555u : 0xff00aa00u),
                    $"POST underline pixel {x}/{y} is not at the cell bottom");
            Bios(m, 0x0300); Check((ushort)m.Cpu.GetRegister(Register.Cx) == 0x0e0f);
        });
        foreach (var (selection, height, start, end) in new[] { (0,8,6,7), (1,14,12,13), (2,16,14,15) })
        foreach (bool enabled in new[] { false, true })
            test($"Mode 03/83 resets the native underline to {start}/{end} at height {height}, scaling={enabled}", () =>
            {
                using var m = Boot(); Emulation(m, enabled);
                m.Cpu.SetRegister(Register.Bx, 0x30); Bios(m, (ushort)(0x1200 | selection));
                foreach (ushort mode in new ushort[] { 3, 0x83 })
                {
                    Shape(m, 0x2000); // Mode set must also replace a previously hidden cursor.
                    Bios(m, mode);
                    Check(m.Memory.ReadWord(0x485) == height && m.Memory.ReadWord(0x460) == ((start << 8) | end));
                    Check(Crtc(m, 10) == start && Crtc(m, 11) == end);
                    m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, 0); Bios(m, 0x0200);
                    m.Memory.WriteWord(0xb8000, 0x2c20);
                    var frame = m.Video.Capture(0);
                    for (int y = 0; y < height; y++) for (int x = 0; x < 8; x++)
                        Check(frame.ArgbPixels[y * frame.Width + x] == (y >= start ? 0xffff5555u : 0xff00aa00u));
                    Bios(m, 0x0300); Check((ushort)m.Cpu.GetRegister(Register.Cx) == ((start << 8) | end));
                    Check(((m.Memory.ReadByte(0x487) & 1) == 0) == enabled);
                    Check(m.Memory.ReadWord(0x44c) == (height == 16 ? 4096 : 4000), "Cursor fix must retain mode-set page spacing");
                }
            });
        // Independent expected single-eighth endpoints; test every ordered range.
        var matrices = new (int Height, int[] Start, int[] End)[] {
            (8, [0,1,2,3,4,5,6,7], [0,1,2,3,4,5,6,7]),
            (14,[0,1,3,5,7,8,10,12],[0,2,4,6,7,9,11,13]),
            (16,[0,2,4,6,8,10,12,14],[1,3,5,7,9,11,13,15]),
            (24,[0,3,6,9,12,15,18,21],[2,5,8,11,14,17,20,23]),
            (32,[0,4,8,12,16,20,24,28],[3,7,11,15,19,23,27,31]) };
        foreach (var matrix in matrices)
            test($"BIOS cursor maps every legacy eight-line interval onto {matrix.Height} physical scanlines without changing AH03", () =>
            {
                using var m = Boot(); Height(m, matrix.Height); Emulation(m, true);
                m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, 0); Bios(m, 0x0200);
                m.Memory.WriteWord(0xb8000, 0x2c20);
                for (int start = 0; start < 8; start++) for (int end = start; end < 8; end++)
                {
                    ushort request = (ushort)(start << 8 | end); Shape(m, request);
                    Check(m.Memory.ReadWord(0x460) == request && Crtc(m, 10) == matrix.Start[start] && Crtc(m, 11) == matrix.End[end], $"{request:X4}: BDA={m.Memory.ReadWord(0x460):X4}, CRTC={Crtc(m, 10)}/{Crtc(m, 11)}");
                    m.Cpu.SetRegister(Register.Bx, 0); Bios(m, 0x0300);
                    Check((ushort)m.Cpu.GetRegister(Register.Cx) == request);
                    var frame = m.Video.Capture(0);
                    for (int y = 0; y < matrix.Height; y++) for (int x = 0; x < 8; x++)
                        Check(frame.ArgbPixels[y * frame.Width + x] == (y >= matrix.Start[start] && y <= matrix.End[end] ? 0xffff5555u : 0xff00aa00u), $"{request:X4}: pixel {x}/{y} = {frame.ArgbPixels[y * frame.Width + x]:X8}");
                }
            });
        foreach (int height in new[] { 8, 14, 16 })
            test($"Disabled cursor scaling uses raw coordinates at {height} rows and survives font reload", () =>
            {
                using var m = Boot(); Emulation(m, false); Height(m, height); Shape(m, 0x0206);
                Check(Crtc(m, 10) == 2 && Crtc(m, 11) == 6 && (m.Memory.ReadByte(0x487) & 1) == 1);
                Emulation(m, true); Check(Crtc(m, 10) == 2 && Crtc(m, 11) == 6, "Policy switch must not retroactively rewrite the cursor");
                Shape(m, 0x0607); Check(Crtc(m, 10) == (height == 8 ? 6 : height == 14 ? 10 : 12) && Crtc(m, 11) == height - 1);
            });
        test("Cursor native, reversed and control/skew-bit requests pass through and retain the CRTC index", () =>
        {
            using var m = Boot();
            foreach (ushort request in new ushort[] { 0x0e0f,0x0008,0x080f,0x0706,0x2000,0x6007,0x0047,0x811f,0x1f1f })
            {
                m.Video.Write(0x3d4, 24, 1); Shape(m, request);
                Check(m.Memory.ReadWord(0x460) == request && Crtc(m, 10) == request >> 8 && Crtc(m, 11) == (request & 255));
                Check(m.Video.Read(0x3d4, 1) == 24);
            }
        });
        test("Cursor policy uses inverted INFO bit, preserves other flags and exposes real AH1B state/capability", () =>
        {
            using var m = Boot(); Check((m.Memory.ReadByte(0x487) & 1) == 0);
            foreach (byte flags in new byte[] { 0,0xfe,0xa6,0x61 })
            {
                m.Memory.WriteByte(0x487, flags); Emulation(m, false); Check(m.Memory.ReadByte(0x487) == (flags | 1));
                uint state = State(m); Check((m.Memory.ReadByte(state + 0x2d) & 16) == 0);
                uint table = (uint)m.Memory.ReadWord(state + 2) * 16 + m.Memory.ReadWord(state);
                Check((m.Memory.ReadByte(table + 10) & 16) != 0);
                Emulation(m, true); Check(m.Memory.ReadByte(0x487) == (flags & 254));
                Check((m.Memory.ReadByte(State(m) + 0x2d) & 16) != 0);
            }
        });
        test("Cursor policy and AH01 preserve caller CPU state and invalid policy requests are atomic", () =>
        {
            using var m = Boot(); m.Cpu.SetSegment(Segment.Ds, 0x1234); m.Cpu.SetSegment(Segment.Es, 0x2345);
            m.Cpu.SetRegister(Register.Bx, 0xabcd5634); m.Cpu.SetRegister(Register.Cx, 0x34560206); m.Cpu.SetRegister(Register.Dx, 0x456789ab);
            m.Cpu.SetRegister(Register.Si, 0x56789abc); m.Cpu.SetRegister(Register.Di, 0x6789abcd); m.Cpu.SetRegister(Register.Bp, 0x789abcde);
            uint sp = m.Cpu.GetRegister(Register.Sp); m.Cpu.Eflags = 2 | X86Cpu.Direction | X86Cpu.Carry;
            Bios(m, 0x1201); Check((ushort)m.Cpu.GetRegister(Register.Ax) == 0x1212);
            byte flags = m.Memory.ReadByte(0x487); Bios(m, 0x1202); Check((ushort)m.Cpu.GetRegister(Register.Ax) == 0x1202 && m.Memory.ReadByte(0x487) == flags);
            Bios(m, 0x0100);
            Check(m.Cpu.GetRegister(Register.Bx) == 0xabcd5634 && m.Cpu.GetRegister(Register.Cx) == 0x34560206 && m.Cpu.GetRegister(Register.Dx) == 0x456789ab);
            Check(m.Cpu.GetRegister(Register.Si) == 0x56789abc && m.Cpu.GetRegister(Register.Di) == 0x6789abcd && m.Cpu.GetRegister(Register.Bp) == 0x789abcde && m.Cpu.GetRegister(Register.Sp) == sp);
            Check(m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetSegment(Segment.Es) == 0x2345 && m.Cpu.Eflags == (2 | X86Cpu.Direction | X86Cpu.Carry));
        });
        test("Cursor emulation policy persists across mode changes but never exposes a graphics hardware cursor", () =>
        {
            using var m = Boot();
            foreach (bool enabled in new[] { false, true }) foreach (ushort mode in new ushort[] { 0x12,0x13,3,0x83 })
            {
                Emulation(m, enabled); Bios(m, mode); Check(((m.Memory.ReadByte(0x487) & 1) == 0) == enabled);
                Shape(m, 0x0007); Check(m.Memory.ReadWord(0x460) == 7);
                if ((mode & 0x7f) != 3) Check((Crtc(m, 10) & 32) != 0 && Crtc(m, 11) == 7);
            }
        });
        test("Legacy direct INFO-bit writes take effect without a hidden host cursor-policy state", () =>
        {
            using var m = Boot(); m.Memory.WriteByte(0x487, 0x61); Shape(m, 0x0607); Check(Crtc(m, 10) == 6 && Crtc(m, 11) == 7);
            m.Memory.WriteByte(0x487, 0x60); Shape(m, 0x0607); Check(Crtc(m, 10) == 12 && Crtc(m, 11) == 15);
        });
    }
}
