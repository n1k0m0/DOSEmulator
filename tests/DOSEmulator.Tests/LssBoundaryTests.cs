// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

internal static class LssBoundaryTests
{
    /// <summary>Requires the independently specified Intel486 boundary or committed register invariant.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    /// <summary>Encodes the requested LSS operand/address sizes without relying on a product decoder helper.</summary>
    private static byte[] Instruction(bool default32, bool operand32, bool address32, int register)
    {
        var bytes = new List<byte>();
        if (default32 != operand32) bytes.Add(0x66);
        if (default32 != address32) bytes.Add(0x67);
        bytes.AddRange([0x0f, 0xb2, (byte)((register << 3) | (address32 ? 5 : 6))]);
        bytes.AddRange(address32 ? [0x00, 0x30, 0, 0] : [0x00, 0x30]);
        return bytes.ToArray();
    }
    /// <summary>Builds real, protected 16/32-bit and paged V86 contexts through actual guest transitions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, ushort Selector) Rig(int mode, byte[] code)
    {
        if (mode == 0)
        {
            var m = new MemoryBus(2097152); var c = new X86Cpu(m, new());
            c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ds, 0); c.SetSegment(Segment.Ss, 0);
            c.Eip = 0x2000; c.SetRegister(Register.Sp, 0x9000); code.CopyTo(m.Ram, 0x2000);
            return (c, m, 0x1000);
        }
        if (mode is 1 or 2)
        {
            var (c, m) = ProtectedModeTests.Rig(code, code32: mode == 2);
            ProtectedModeTests.Descriptor(m, 40, 0x10000, 0xffff, 0x92, 4);
            return (c, m, 40);
        }
        var (cpu, memory) = PagingTests.Create([0x90]);
        PagingTests.Tss(cpu, memory);
        uint[] frame = [0x2000, 0x1000, 0x23202, 0x9000, 0, 0, 0, 0, 0];
        for (uint i = 0; i < frame.Length; i++) memory.WriteDword(0x18000 + i * 4, frame[i]);
        code.CopyTo(memory.Ram, 0x12000); cpu.SetRegister(Register.Sp, 0x18000);
        PagingTests.At(cpu, memory, [0xcf]);
        Check(cpu.Virtual8086 && cpu.PagingEnabled, "Actual paged V86 entry");
        return (cpu, memory, 0x1000);
    }
    /// <summary>Registers LSS commit/boundary checks and contrasts them with the preserved MOV/POP SS contract.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int mode in Enumerable.Range(0, 4))
        foreach (bool wide in new[] { false, true })
        foreach (bool address32 in new[] { false, true })
        foreach (int register in new[] { 0, 4, 7 })
            test($"LSS boundary mode{mode} operand{(wide ? 32 : 16)} address{(address32 ? 32 : 16)} r{register} accepts IRQ immediately", () =>
            {
                byte[] code = Instruction(mode == 2, wide, address32, register);
                var (c, m, selector) = Rig(mode, [..code, 0x90]);
                c.SetRegister((Register)register, 0xabcd9000);
                if (wide) m.WriteDword(0x3000, 0x7000); else m.WriteWord(0x3000, 0x7000);
                m.WriteWord(wide ? 0x3004u : 0x3002, selector);
                c.Eflags |= 0x200;
                uint flags = c.Eflags; c.Step();
                Check(c.Eip == 0x2000 + code.Length && c.GetSegment(Segment.Ss) == selector, "Complete LSS instruction");
                Check(c.GetRegister((Register)register) == (wide ? 0x7000u : 0xabcd7000), "Width-specific pointer commit");
                Check(c.GetSegmentBase(Segment.Ss) == 0x10000 && c.Eflags == flags, "Committed new SS and unchanged flags");
                Check(c.CanAcceptMaskableInterrupt, "LSS must not create a following-instruction IRQ shadow");
            });
        foreach (bool wide in new[] { false, true })
            test($"LSS boundary STI followed by LSS{(wide ? 32 : 16)} consumes only STI shadow", () =>
            {
                byte[] lss = Instruction(true, wide, true, 4); var (c, m, _) = Rig(2, [0xfb, ..lss, 0x90]);
                c.SetRegister(Register.Sp, 0x9000); m.WriteDword(0x3000, 0x7000); m.WriteWord(wide ? 0x3004u : 0x3002, 40); c.Eflags = 2;
                c.Step(); Check(!c.CanAcceptMaskableInterrupt, "STI shadow must cover LSS");
                c.Step(); Check(c.CanAcceptMaskableInterrupt, "LSS must not extend STI shadow");
                uint next = c.Eip; c.EnterHardwareInterrupt(0x20);
                uint sp = c.GetRegister(Register.Sp);
                Check(m.ReadDword(0x10000 + sp) == next && m.ReadDword(0x10000 + sp + 4) == 8, "Interrupt uses fully committed new stack and next IP");
            });
        foreach (byte[] shadow in new byte[][] { [0x8e, 0xd0], [0x17] })
            test($"LSS boundary preserves {Convert.ToHexString(shadow)} SS shadow before following FNOP", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([..shadow, 0xd9, 0xd0, 0x90]);
                c.SetRegister(Register.Ax, 16); m.WriteDword(0x18000, 16); c.Eflags = 0x202;
                c.Step(); Check(!c.CanAcceptMaskableInterrupt, "MOV/POP SS retain their distinct shadow");
                c.Step(); Check(c.CanAcceptMaskableInterrupt && c.X87Instructions == 1, "Following x87 completes before interrupt is eligible");
            });
        foreach (bool badSelector in new[] { false, true })
            test($"LSS boundary fault preserves old SS and ESP ({(badSelector ? "selector" : "page")})", () =>
            {
                byte[] code = Instruction(true, true, true, 4); var (c, m) = PagingTests.Create(code);
                m.WriteDword(0x3000, 0x7000); m.WriteWord(0x3004, badSelector ? (ushort)0 : (ushort)40);
                ProtectedModeTests.Descriptor(m, 40, 0x10000, 0xffff, 0x92, 4);
                if (!badSelector) PagingTests.Pte(m, 0x3000, 0x3000, 0);
                c.Eflags = 0x202; ProtectedModeTests.Fault(c, m, badSelector ? (byte)13 : (byte)14, 0);
                Check(c.GetSegment(Segment.Ss) == 16 && c.GetRegister(Register.Sp) == 0x17ff0, "Only fault frame changes the old stack");
            });
        foreach (bool wide in new[] { false, true })
        foreach (bool ignore in new[] { false, true })
            test($"LSS boundary IRQ precedes pending x87 MF at FNOP width{(wide ? 32 : 16)} IGNNE={ignore}", () =>
            {
                byte[] code = Instruction(true, wide, true, 4); var (c, m, _) = Rig(2, [..code, 0xd9, 0xd0]);
                c.SetRegister(Register.Sp, 0x9000); m.WriteDword(0x3000, 0x7000); m.WriteWord(wide ? 0x3004u : 0x3002, 40);
                c.SetRegister(Register.Ax, c.Cr0 | 0x20); PagingTests.At(c, m, [0x0f, 0x22, 0xc0]);
                m.WriteWord(0x4000, 0x037e); m.WriteWord(0x4004, 0x8081); m.WriteWord(0x4008, 0xffff);
                PagingTests.At(c, m, [0xdd, 0x25, 0x00, 0x40, 0, 0]);
                c.Eip = 0x2000; c.Eflags = 0x202; c.IgnoreNumericError = ignore;
                c.Step(); Check(c.CanAcceptMaskableInterrupt && c.FpuErrorAsserted && c.NumericErrorFaults == 0, "LSS does not execute or mask the following FNOP");
                uint next = c.Eip; c.EnterHardwareInterrupt(0x20);
                Check(m.ReadDword(0x10000 + c.GetRegister(Register.Sp)) == next, "IRQ saves next x87 address on new stack");
                m.WriteByte(0x6000, 0xcf); c.Step();
                Check(c.Eip == next && c.FpuErrorAsserted, "IRETD preserves pending FERR");
                c.Step(); Check(c.Eip == 0x6000 && c.NumericErrorFaults == 1 && c.FpuErrorAsserted, "Next FNOP delivers MF independently of IGNNE at NE=1");
            });
        foreach (bool wide in new[] { false, true })
            test($"LSS boundary immediate TF trap uses committed stack width{(wide ? 32 : 16)}", () =>
            {
                byte[] code = Instruction(true, wide, true, 4); var (c, m, _) = Rig(2, code);
                c.SetRegister(Register.Sp, 0x9000); m.WriteDword(0x3000, 0x7000); m.WriteWord(wide ? 0x3004u : 0x3002, 40);
                c.Eflags = 0x302; c.Step();
                Check(c.SingleStepTraps == 1 && c.Eip == 0x6000, "LSS does not start a debug shadow");
                Check(m.ReadDword(0x10000 + c.GetRegister(Register.Sp)) == 0x2000 + code.Length, "TF trap has next IP on new stack");
            });
        foreach (bool wide in new[] { false, true })
            test($"LSS boundary source data breakpoint traps after both commits width{(wide ? 32 : 16)}", () =>
            {
                byte[] code = Instruction(true, wide, true, 4); var (c, m, _) = Rig(2, code);
                c.SetRegister(Register.Sp, 0x9000); m.WriteDword(0x3000, 0x7000); m.WriteWord(wide ? 0x3004u : 0x3002, 40);
                c.SetRegister(Register.Ax, 0x3000); PagingTests.At(c, m, [0x0f, 0x23, 0xc0]);
                c.SetRegister(Register.Ax, 0xf0001); PagingTests.At(c, m, [0x0f, 0x23, 0xf8]);
                c.Eip = 0x2000; c.Eflags = 0x202; c.Step();
                Check(c.DataBreakpointTraps == 1 && c.Eip == 0x6000 && c.GetSegment(Segment.Ss) == 40, "LSS data trap follows selector/pointer commit");
                Check(m.ReadDword(0x10000 + c.GetRegister(Register.Sp)) == 0x2000 + code.Length, "Data trap has next IP on new stack");
            });
    }
}
