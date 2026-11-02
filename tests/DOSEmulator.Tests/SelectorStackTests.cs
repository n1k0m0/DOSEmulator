// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>P54C selector widths and interlevel stack restoration, preserving the separately tested 486 answers.</summary>
internal static class SelectorStackTests
{
    /// <summary>Requires an independently observable pointer, frame byte or architectural error.</summary>
    private static void Check(bool value, string message = "Selector/stack contract failed") { if (!value) throw new Exception(message); }
    /// <summary>Computes the historical return pointer separately from the CPU helper.</summary>
    private static uint Returned(X86CpuModel model, int width, bool outer32, uint old, uint inner, uint adjustment)
    {
        if (!outer32) return (inner & 0xffff0000) | ((old + adjustment) & 0xffff);
        if (width == 4) return old + adjustment;
        return (model == X86CpuModel.Intel486Dx ? 0x00330000u : 0) + (old & 0xffff) + adjustment;
    }
    /// <summary>Reads only the width advertised by a historical gate.</summary>
    private static uint Read(MemoryBus memory, uint address, int width) => width == 4 ? memory.ReadDword(address) : memory.ReadWord(address);
    /// <summary>Creates ordinary P54C stack instructions in real, PE16, PE32, paging or paged V86 mode.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Ordinary(byte[] code, int mode, bool stack32)
    {
        if (mode == 0)
        {
            var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, new(), X86CpuModel.IntelP54cB5Subset);
            cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x2000; cpu.SetRegister(Register.Sp, 0xabcd8000); code.CopyTo(memory.Ram, 0x2000); cpu.Eflags = 0x602; return (cpu, memory);
        }
        var (c, m) = mode >= 3 ? PagingTests.Create(code, model: X86CpuModel.IntelP54cB5Subset)
            : ProtectedModeTests.Rig(code, code32: mode == 2, stack32: stack32, model: X86CpuModel.IntelP54cB5Subset);
        if (mode == 3 && !stack32) { c.SetSegment(Segment.Ss, 32); c.SetRegister(Register.Sp, 0xabcd8000); }
        if (mode == 4)
        {
            PagingTests.Tss(c, m); uint[] frame = [0x100, 0x3000, 0x23202, 0xabcd8000, 0x4000, 0x5000, 0x6000, 0x7000, 0x8000];
            for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]); code.CopyTo(m.Ram, 0x30100); PagingTests.At(c, m, [0xcf]);
        }
        else { c.SetSegment(Segment.Fs, 16); c.SetSegment(Segment.Gs, 16); }
        c.Eflags |= 0x602; return (c, m);
    }

    /// <summary>Registers model-separated gate returns, selector data widths and a wrapped error-frame preflight.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
        foreach (bool outer32 in new[] { false, true })
        foreach (bool inner32 in new[] { false, true })
        foreach (bool tss32 in new[] { false, true })
        foreach (bool paging in new[] { false, true })
        foreach (byte count in new byte[] { 0, 2, 31 })
            test($"M4 Selector RETF model {model} width {width} old32 {outer32} new32 {inner32} TSS32 {tss32} paging {paging} count {count}", () =>
            {
                var (c, m, old, inner) = CallGateTests.Create(width, count, old32: outer32, new32: inner32, tss32: tss32, paging: paging, model: model);
                c.Step(); Check(c.CurrentPrivilegeLevel == 0 && c.Eip == 0x5000 && c.GetSegment(Segment.Ss) == 0x58);
                uint physical = 0x60000 + (c.GetRegister(Register.Sp) & (inner32 ? uint.MaxValue : 0xffff));
                Check(Read(m, physical + (uint)width, width) == 0x2b);
                Check(Read(m, physical + (uint)(3 + count) * (uint)width, width) == 0x33);
                if (width == 4) Check(m.ReadDword(physical + 4) == 0x2b, "P54C selector frame padding differs");
                uint adjustment = (uint)(count * width); new byte[] { 0xca, (byte)adjustment, (byte)(adjustment >> 8) }.CopyTo(m.Ram, 0x5000); c.Step();
                Check(c.CurrentPrivilegeLevel == 3 && c.GetSegment(Segment.Ss) == 0x33 && c.GetSegment(Segment.Cs) == 0x2b && c.Eip == (width == 2 ? 0x2007u : 0x2005u));
                Check(c.GetRegister(Register.Sp) == Returned(model, width, outer32, old, inner, adjustment), "Interlevel RETF pointer differs");
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (byte kind in new byte[] { 6, 7, 14, 15 })
        foreach (bool outer32 in new[] { false, true })
        foreach (bool inner32 in new[] { false, true })
        foreach (bool tss32 in new[] { false, true })
        foreach (bool paging in new[] { false, true })
            test($"M4 Selector IRET model {model} gate {kind} old32 {outer32} new32 {inner32} TSS32 {tss32} paging {paging}", () =>
            {
                int width = kind < 8 ? 2 : 4; var (c, m, old, inner) = CallGateTests.Create(width, 0, old32: outer32, new32: inner32, tss32: tss32, paging: paging, model: model);
                new byte[] { 0xcd, 0x30 }.CopyTo(m.Ram, 0x2000); Gate(m, 0x30, 0x50, 0x5000, (byte)(0xe0 | kind)); c.Eflags |= 0x200;
                c.Step(); Check(c.CurrentPrivilegeLevel == 0 && c.Eip == 0x5000 && c.GetSegment(Segment.Ss) == 0x58);
                Check(((c.Eflags & 0x200) != 0) == (kind is 7 or 15));
                m.WriteByte(0x5000, 0xcf); c.Step();
                Check(c.CurrentPrivilegeLevel == 3 && c.GetSegment(Segment.Ss) == 0x33 && c.Eip == 0x2002 && (c.Eflags & 0x200) != 0);
                Check(c.GetRegister(Register.Sp) == Returned(model, width, outer32, old, inner, 0), "Interlevel IRET pointer differs");
            });
        foreach (int mode in new[] { 0, 1, 2, 3, 4 })
        foreach (bool stack32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
        foreach (Segment segment in Enum.GetValues<Segment>())
        {
            if (mode is 0 or 4 && stack32) continue;
            bool code32 = mode is 2 or 3; byte[] push = segment switch { Segment.Es => [6], Segment.Cs => [14], Segment.Ss => [22], Segment.Ds => [30], Segment.Fs => [15, 160], _ => [15, 168] };
            byte[] prefix = (width == 4) == code32 ? [] : [0x66];
            test($"M4 Selector P54C PUSH {segment} width {width} mode {mode} stack32 {stack32}", () =>
            {
                var (c, m) = Ordinary([.. prefix, 0x67, .. push], mode, stack32); uint old = c.GetRegister(Register.Sp); bool wide = c.GetSegmentCache(Segment.Ss).Default32;
                uint sp = unchecked(old - (uint)width) & (wide ? uint.MaxValue : 0xffff); uint address = c.GetSegmentBase(Segment.Ss) + sp; ushort selector = c.GetSegment(segment);
                m.Ram.AsSpan((int)address - 1, width + 2).Fill(0xa5); uint flags = c.Eflags; c.Step();
                Check(Read(m, address, width) == selector && m.ReadByte(address - 1) == 0xa5 && m.ReadByte(address + (uint)width) == 0xa5 && c.Eflags == flags);
                Check(c.GetRegister(Register.Sp) == (wide ? sp : old & 0xffff0000 | sp));
            });
            if (segment == Segment.Cs) continue;
            byte[] pop = segment switch { Segment.Es => [7], Segment.Ss => [23], Segment.Ds => [31], Segment.Fs => [15, 161], _ => [15, 169] };
            test($"M4 Selector P54C POP {segment} width {width} mode {mode} stack32 {stack32}", () =>
            {
                var (c, m) = Ordinary([.. prefix, 0x67, .. pop], mode, stack32); uint old = c.GetRegister(Register.Sp); bool wide = c.GetSegmentCache(Segment.Ss).Default32;
                uint address = c.GetSegmentBase(Segment.Ss) + (old & (wide ? uint.MaxValue : 0xffff)); ushort selector = c.GetSegment(segment);
                m.WriteDword(address, 0xa5a50000u | selector); uint flags = c.Eflags; c.Step();
                Check(c.GetSegment(segment) == selector && c.GetRegister(Register.Sp) == (wide ? old + (uint)width : old & 0xffff0000 | (old + (uint)width) & 0xffff));
                Check(c.Eflags == flags && m.ReadDword(address) == (0xa5a50000u | selector));
                if (segment == Segment.Ss) Check(!c.CanAcceptMaskableInterrupt, "POP SS lost its existing shadow");
            });
        }
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("M4 Selector wrapped error preflight model " + model, () =>
            {
                var (c, m) = PagingTests.Create([0xfa], model: model); PagingTests.Tss(c, m);
                Descriptor(m, 0x58, 0, 0x1ffff, 0x92); m.WriteDword(0x9004, 0x16); m.WriteWord(0x9008, 0x58);
                PagingTests.Pte(m, 0x10000, 0x10000, 0); c.SetRegister(Register.Ax, c.Cr3); PagingTests.At(c, m, [0x0f, 0x22, 0xd8]);
                PagingTests.User(c, m); m.Ram.AsSpan(0, 32).Fill(0xa5); m.Ram[0x10000] = m.Ram[0x10001] = 0xa5;
                c.Step();
                if (model != X86CpuModel.Intel486Dx)
                    Check(c.TripleFaulted && c.CurrentPrivilegeLevel == 3 && m.Ram.AsSpan(0, 32).ToArray().All(v => v == 0xa5), "P54C wrote an earlier frame before the missing high error word was checked");
                else Check(!c.TripleFaulted && c.CurrentPrivilegeLevel == 0 && c.Eip == 0x6000 && (c.GetRegister(Register.Sp) & 0xffff) == 0xfffe && m.ReadWord(0xfffe) == 0);
                Check(m.Ram[0x10000] == 0xa5 && m.Ram[0x10001] == 0xa5);
            });
    }
}
