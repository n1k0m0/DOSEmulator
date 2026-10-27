// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class VbeTests
{
    /// <summary>Require an independent firmware result rather than accepting an AX success marker alone.</summary>
    private static void Check(bool ok, string message = "VBE firmware contract") { if (!ok) throw new Exception(message); }
    /// <summary>Boot the actual own firmware on a private demo disk with an explicit adapter choice.</summary>
    private static PcMachine Boot(bool et = true)
    {
        var m = PcMachine.CreateDemo(new MachineProfile { VideoCard = et ? "et4000ax-isa-1m-development" : DOSEIdentity.VgaModel });
        m.RunSteps(200000); Check(m.Fault is null && m.Cpu.Halted); return m;
    }
    /// <summary>Execute INT10 and require the exact HLT return point and preserved caller stack.</summary>
    private static ushort Call(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4);
        uint sp = m.Cpu.GetRegister(Register.Sp);
        m.Cpu.SetRegister(Register.Ax, 0xabcd0000u | ax); m.Cpu.EnterRealModeInterrupt(0x10); m.RunSteps(2000000);
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001001", m.Fault ?? m.Cpu.Position);
        Check(m.Cpu.GetRegister(Register.Sp) == sp && m.Cpu.GetRegister(Register.Ax) >> 16 == 0xabcd);
        return (ushort)m.Cpu.GetRegister(Register.Ax);
    }
    /// <summary>Read a packed little-endian information-field word from the explicitly supplied buffer.</summary>
    private static ushort Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    /// <summary>Read a packed far pointer without normalizing or inventing its ROM segment.</summary>
    private static uint Dword(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    /// <summary>Read a bounded immutable OEM string from the actual mapped ROM.</summary>
    private static string String(PcMachine m, uint far)
    {
        uint address = (far >> 16) * 16 + (far & 65535); var bytes = new List<byte>();
        for (int i = 0; i < 128; i++) { byte b = m.Memory.ReadByte(address + (uint)i); if (b == 0) return System.Text.Encoding.ASCII.GetString(bytes.ToArray()); bytes.Add(b); }
        throw new Exception("Unterminated VBE OEM string");
    }
    /// <summary>Retrieve one real mode record for subsequent independently constructed field assertions.</summary>
    private static byte[] Info(PcMachine m, ushort mode)
    {
        m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x1000); m.Cpu.SetRegister(Register.Cx, mode);
        Check(Call(m, 0x4f01) == 0x004f); return m.Memory.Ram.AsSpan(0x21000, 256).ToArray();
    }
    /// <summary>Register positive, rejected, buffer-boundary and actual far-call VBE cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool et in new[] { false, true })
        foreach (ushort offset in new ushort[] { 0, 1, 0xff00, 0xff01, 0xffff })
        foreach (bool carry in new[] { false, true })
            test($"VBE controller card={et}, buffer={offset:X4}, CF={carry}", () =>
            {
                using var m = Boot(et); int address = 0x20000 + offset;
                m.Memory.Ram.AsSpan(address - 16, 544).Fill(0xa7); m.Memory.WriteDword((uint)address, 0x32454256); // VBE2 request must not overrun a 1.x record.
                byte[] before = m.Memory.Ram.AsSpan(address, 512).ToArray();
                m.Cpu.SetSegment(Segment.Ds, 0x1234); m.Cpu.SetSegment(Segment.Es, 0x2000);
                m.Cpu.SetRegister(Register.Di, 0x56780000u | offset); m.Cpu.SetRegister(Register.Si, 0x12348765);
                m.Cpu.SetRegister(Register.Bx, 0x24681357); m.Cpu.SetRegister(Register.Cx, 0x789a3456); m.Cpu.SetRegister(Register.Dx, 0x13572468);
                m.Cpu.Eflags = X86Cpu.Direction | 2u | (carry ? 1u : 0u);
                ushort status = Call(m, 0x4f00); bool valid = et && offset <= 0xff00;
                Check(status == (valid ? 0x004f : 0x014f));
                if (valid)
                {
                    byte[] info = m.Memory.Ram.AsSpan(address, 256).ToArray();
                    Check(System.Text.Encoding.ASCII.GetString(info, 0, 4) == "VESA" && Word(info, 4) == 0x0102 && Dword(info, 10) == 0 && Word(info, 18) == 16);
                    Check(String(m, Dword(info, 6)).Contains("DOSEBios") && String(m, Dword(info, 6)).Contains("banked subset"));
                    uint pointer = Dword(info, 14); Check(pointer >> 16 == 0xf000);
                    uint list = (pointer >> 16) * 16 + (pointer & 65535);
                    Check(Enumerable.Range(0, 4).Select(i => m.Memory.ReadWord(list + (uint)i * 2)).SequenceEqual(new ushort[] { 0x101, 0x103, 0x105, 0xffff }));
                    Check(info.Skip(20).All(x => x == 0) && m.Memory.Ram.AsSpan(address + 256, 256).SequenceEqual(before.AsSpan(256)));
                }
                else Check(m.Memory.Ram.AsSpan(address, 512).SequenceEqual(before));
                Check(m.Memory.Ram.AsSpan(address - 16, 16).ToArray().All(x => x == 0xa7));
                Check(m.Cpu.GetRegister(Register.Di) == (0x56780000u | offset) && m.Cpu.GetRegister(Register.Si) == 0x12348765 &&
                    m.Cpu.GetRegister(Register.Bx) == 0x24681357 && m.Cpu.GetRegister(Register.Cx) == 0x789a3456 && m.Cpu.GetRegister(Register.Dx) == 0x13572468);
                Check(m.Cpu.GetSegment(Segment.Ds) == 0x1234 && m.Cpu.GetSegment(Segment.Es) == 0x2000 && (m.Cpu.Eflags & 0x401) == (0x400u | (carry ? 1u : 0u)));
            });
        foreach (var mode in new[] { (Id: 0x101, Legacy: 0x2e, W: 640, H: 480, Pages: 2), (Id: 0x103, Legacy: 0x30, W: 800, H: 600, Pages: 1), (Id: 0x105, Legacy: 0x38, W: 1024, H: 768, Pages: 0) })
        {
            test($"VBE mode {mode.Id:X3} reports all 256 bytes and real 1-MiB bank geometry", () =>
            {
                using var m = Boot(); byte[] b = Info(m, (ushort)mode.Id);
                Check(Word(b, 0) == 0x1b && b[2] == 7 && b[3] == 0 && Word(b, 4) == 64 && Word(b, 6) == 64 && Word(b, 8) == 0xa000 && Word(b, 10) == 0);
                Check(Dword(b, 12) >> 16 == 0xf000 && Word(b, 16) == mode.W && Word(b, 18) == mode.W && Word(b, 20) == mode.H);
                Check(b.AsSpan(22, 9).SequenceEqual(new byte[] { 8, 16, 1, 8, 1, 4, 0, (byte)mode.Pages, 1 }) && b.Skip(31).All(x => x == 0));
                Check(m.Video.Width == 640 && !m.Video.GraphicsMode && m.Video.SegmentSelect == 0);
            });
            foreach (bool preserve in new[] { false, true })
                test($"VBE set {mode.Id:X3}, preserve={preserve}, clears or retains all sixteen banks", () =>
                {
                    using var m = Boot(); m.Cpu.SetRegister(Register.Bx, (uint)mode.Id); Check(Call(m, 0x4f02) == 0x4f);
                    for (int i = 0; i < 16; i++) { m.Ports.Write(0x3cd, (uint)(i * 17), 1); m.Memory.WriteDword(0xa0000, (uint)(0x12345600 + i)); m.Memory.WriteDword(0xafffc, (uint)(0xabcdef00 + i)); }
                    m.Cpu.SetRegister(Register.Bx, (uint)mode.Id | (preserve ? 0x8000u : 0)); Check(Call(m, 0x4f02) == 0x4f);
                    Check(m.Video.Width == mode.W && m.Video.Height == mode.H && m.Memory.ReadByte(0x449) == mode.Legacy && m.Video.SegmentSelect == 0);
                    for (int i = 0; i < 16; i++) { m.Ports.Write(0x3cd, (uint)(i * 17), 1); Check(m.Memory.ReadDword(0xa0000) == (preserve ? (uint)(0x12345600 + i) : 0) && m.Memory.ReadDword(0xafffc) == (preserve ? (uint)(0xabcdef00 + i) : 0)); }
                    Check(Call(m, 0x4f03) == 0x4f && m.Cpu.GetRegister(Register.Bx) == mode.Id); // VBE 1.x deliberately omits D15.
                    Check(Call(m, 3) != 0x014f); Check(Call(m, 0x4f03) == 0x4f && m.Cpu.GetRegister(Register.Bx) == 3);
                });
            test($"VBE {mode.Id:X3} bank set/get executes all sixteen coupled hardware nibbles", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, (uint)mode.Id); Check(Call(m, 0x4f02) == 0x4f);
                for (int i = 0; i < 16; i++)
                {
                    m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, (uint)i); Check(Call(m, 0x4f05) == 0x4f && m.Video.SegmentSelect == i * 17);
                    m.Memory.WriteByte(0xa0000, (byte)(i + 1)); m.Memory.WriteByte(0xaffff, (byte)(i + 31));
                    m.Cpu.SetRegister(Register.Bx, 0x100); m.Cpu.SetRegister(Register.Dx, 0xdeadbeef); Check(Call(m, 0x4f05) == 0x4f && m.Cpu.GetRegister(Register.Dx) == (0xdead0000u | (uint)i));
                }
                for (int i = 0; i < 16; i++) { m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, (uint)i); Call(m, 0x4f05); Check(m.Memory.ReadByte(0xa0000) == i + 1 && m.Memory.ReadByte(0xaffff) == i + 31); }
                m.Ports.Write(0x3cd, 0x53, 1); m.Cpu.SetRegister(Register.Bx, 0x100); m.Cpu.SetRegister(Register.Dx, 0xcafebabe);
                Check(Call(m, 0x4f05) == 0x014f && m.Cpu.GetRegister(Register.Dx) == 0xcafebabe && m.Video.SegmentSelect == 0x53);
            });
            test($"VBE {mode.Id:X3} real far WinFuncPtr ignores AX and preserves flags/registers", () =>
            {
                using var m = Boot(); byte[] info = Info(m, (ushort)mode.Id); uint callback = Dword(info, 12);
                m.Cpu.SetRegister(Register.Bx, (uint)mode.Id); Call(m, 0x4f02);
                byte[] code = [0x9a, (byte)callback, (byte)(callback >> 8), (byte)(callback >> 16), (byte)(callback >> 24), 0xcf];
                code.CopyTo(m.Memory.Ram, 0x1000); m.Memory.WriteWord(0x180, 0x1000); m.Memory.WriteWord(0x182, 0); m.Memory.WriteByte(0x1100, 0xf4);
                for (int i = 0; i < 16; i++)
                {
                    m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1100; m.Cpu.Eflags = 0x403;
                    m.Cpu.SetRegister(Register.Ax, 0x12349876); m.Cpu.SetRegister(Register.Bx, 0x56780000); m.Cpu.SetRegister(Register.Dx, 0x789a0000u | (uint)i);
                    uint sp = m.Cpu.GetRegister(Register.Sp); m.Cpu.EnterRealModeInterrupt(0x60); m.RunSteps(10000);
                    Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001101");
                    Check(m.Video.SegmentSelect == i * 17 && m.Cpu.GetRegister(Register.Ax) == 0x1234004f && m.Cpu.GetRegister(Register.Bx) == 0x56780000 && m.Cpu.GetRegister(Register.Dx) == (0x789a0000u | (uint)i));
                    Check((m.Cpu.Eflags & 0x403) == 0x403 && m.Cpu.GetRegister(Register.Sp) == sp);
                }
            });
        }
        foreach (ushort mode in new ushort[] { 0, 3, 0x100, 0x102, 0x104, 0x106, 0x111, 0x181, 0x201, 0x4101, 0x8101, 0xffff })
            test($"VBE rejected mode information {mode:X4} keeps the entire caller buffer", () =>
            {
                using var m = Boot(); m.Memory.Ram.AsSpan(0x23000, 288).Fill(0xa7); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x3000); m.Cpu.SetRegister(Register.Cx, mode);
                Check(Call(m, 0x4f01) == 0x014f && m.Memory.Ram.AsSpan(0x23000, 288).ToArray().All(x => x == 0xa7));
            });
        foreach (ushort mode in new ushort[] { 0, 3, 0x100, 0x181, 0x201, 0x2101, 0x4101, 0xffff })
            test($"VBE rejected mode set {mode:X4} has no device or memory effect", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0x101); Call(m, 0x4f02); m.Ports.Write(0x3cd, 0x33, 1); m.Memory.WriteByte(0xa0000, 29);
                var picture = m.CaptureFrame(); long banks = m.Video.BankSwitchWrites; m.Cpu.SetRegister(Register.Bx, mode);
                Check(Call(m, 0x4f02) == 0x014f && m.Video.SegmentSelect == 0x33 && m.Video.BankSwitchWrites == banks && m.Memory.ReadByte(0xa0000) == 29);
                Check(m.Memory.ReadByte(0x449) == 0x2e && m.CaptureFrame().ArgbPixels.SequenceEqual(picture.ArgbPixels));
            });
        foreach (ushort bx in new ushort[] { 1, 0x101, 0x200, 0xff00 })
            test($"VBE invalid bank subfunction/window {bx:X4} leaves bank untouched", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0x101); Call(m, 0x4f02); m.Ports.Write(0x3cd, 0x77, 1);
                long writes = m.Video.BankSwitchWrites; m.Cpu.SetRegister(Register.Bx, bx); m.Cpu.SetRegister(Register.Dx, 0xbeef1234);
                Check(Call(m, 0x4f05) == 0x014f && m.Video.SegmentSelect == 0x77 && m.Video.BankSwitchWrites == writes && m.Cpu.GetRegister(Register.Dx) == 0xbeef1234);
            });
        foreach (ushort bank in new ushort[] { 16, 255, 256, 65535 })
            test($"VBE rejects noninstalled bank {bank} before hardware writes", () =>
            {
                using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0x101); Call(m, 0x4f02); m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, bank);
                long writes = m.Video.BankSwitchWrites; Check(Call(m, 0x4f05) == 0x014f && m.Video.BankSwitchWrites == writes && m.Video.SegmentSelect == 0);
            });
        test("VBE bank service follows the active mono decoder without replacing Misc-Out", () =>
        {
            using var m = Boot(); m.Cpu.SetRegister(Register.Bx, 0x101); Call(m, 0x4f02);
            m.Ports.Write(0x3c2, 0x66, 1); m.Ports.Write(0x3b8, 0, 1);
            m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, 9);
            Check(Call(m, 0x4f05) == 0x4f && m.Video.ExtendedRegistersUnlocked && m.Video.SegmentSelect == 0x99);
            Check(m.Ports.Read(0x3cc, 1) == 0x66);
            m.Cpu.SetRegister(Register.Bx, 0x100); Check(Call(m, 0x4f05) == 0x4f && (ushort)m.Cpu.GetRegister(Register.Dx) == 9);
        });
        test("VBE far window get and invalid-window returns retain the real far caller contract", () =>
        {
            using var m = Boot(); uint callback = Dword(Info(m, 0x101), 12); m.Cpu.SetRegister(Register.Bx, 0x101); Call(m, 0x4f02); m.Ports.Write(0x3cd, 0x77, 1);
            byte[] code = [0x9a, (byte)callback, (byte)(callback >> 8), (byte)(callback >> 16), (byte)(callback >> 24), 0xcf];
            code.CopyTo(m.Memory.Ram, 0x1000); m.Memory.WriteWord(0x180, 0x1000); m.Memory.WriteWord(0x182, 0); m.Memory.WriteByte(0x1100, 0xf4);
            foreach (bool valid in new[] { true, false })
            {
                m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1100; m.Cpu.Eflags = 0x403;
                m.Cpu.SetRegister(Register.Ax, 0xabcd9876); m.Cpu.SetRegister(Register.Bx, valid ? 0x56780100u : 0x56780101u); m.Cpu.SetRegister(Register.Dx, 0xcafebabe);
                uint sp = m.Cpu.GetRegister(Register.Sp); m.Cpu.EnterRealModeInterrupt(0x60); m.RunSteps(10000);
                Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001101" && m.Cpu.GetRegister(Register.Sp) == sp);
                Check(m.Cpu.GetRegister(Register.Ax) == (valid ? 0xabcd004fu : 0xabcd014fu) && m.Cpu.GetRegister(Register.Dx) == (valid ? 0xcafe0007u : 0xcafebabeu));
                Check(m.Cpu.GetRegister(Register.Bx) == (valid ? 0x56780100u : 0x56780101u) && m.Video.SegmentSelect == 0x77 && (m.Cpu.Eflags & 0x403) == 0x403);
            }
        });
        test("VBE unsupported functions and plain VGA never fabricate success or output buffers", () =>
        {
            foreach (bool et in new[] { false, true })
            {
                using var m = Boot(et); m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Di, 0x1000); m.Memory.Ram.AsSpan(0x21000, 512).Fill(0x71);
                foreach (ushort fn in et ? new ushort[] { 4, 6, 7, 8, 9, 10, 15, 255 } : new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7, 255 })
                    Check(Call(m, (ushort)(0x4f00 | fn)) == 0x014f && m.Memory.Ram.AsSpan(0x21000, 512).ToArray().All(x => x == 0x71));
                m.Cpu.SetRegister(Register.Bx, 0); m.Cpu.SetRegister(Register.Dx, 1); Check(Call(m, 0x4f05) == 0x014f);
            }
        });
    }
}
