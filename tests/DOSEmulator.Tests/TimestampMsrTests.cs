// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Actual P54C TSC MSR instructions, all 64 value bits, privilege faults and coordinated clock state.</summary>
internal static class TimestampMsrTests
{
    private static readonly Dictionary<object, string> NoOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, IStateObject> NoObjects = new();
    /// <summary>Requires a guest-observable value, fault or unchanged-clock result.</summary>
    private static void Check(bool value, string message = "TSC MSR contract failed") { if (!value) throw new Exception(message); }
    /// <summary>Encodes selected explicit state without executing a clock or guest operation.</summary>
    private static string Time(DeviceScheduler clock) => JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json);
    /// <summary>Builds an explicitly clock-bound core and enters the requested mode with guest bootstrap instructions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, DeviceScheduler Clock) Rig(byte[] code, int mode)
    {
        var clock = new DeviceScheduler();
        if (mode == 3) { var (c, m) = PagingTests.Create(code, model: X86CpuModel.IntelP54cB5Subset, timestampSource: clock, timestampFrequency: 90_000_000); return (c, m, clock); }
        if (mode != 0) { var (c, m) = ProtectedModeTests.Rig(code, code32: mode == 2, model: X86CpuModel.IntelP54cB5Subset, timestampSource: clock, timestampFrequency: 90_000_000); return (c, m, clock); }
        var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, new(), X86CpuModel.IntelP54cB5Subset, clock, 90_000_000);
        cpu.SetSegment(Segment.Cs, 0); cpu.SetRegister(Register.Sp, 0x9000); cpu.Eip = 0x2000; code.CopyTo(memory.Ram, 0x2000); return (cpu, memory, clock);
    }
    /// <summary>Executes an actual CR4.TSD write before the test instruction.</summary>
    private static void Tsd(X86Cpu cpu, MemoryBus memory, uint value)
    { cpu.SetRegister(Register.Ax, value); PagingTests.At(cpu, memory, [0x0f, 0x22, 0xe0]); cpu.Eip = 0x2000; }
    /// <summary>Checks real-mode no-error-word or protected-mode architectural fault delivery.</summary>
    private static void FaultAt(X86Cpu cpu, MemoryBus memory, int mode, byte vector)
    {
        if (mode != 0) { Fault(cpu, memory, vector, vector == 6 ? null : 0); return; }
        memory.WriteWord((uint)vector * 4, 0x6000); memory.WriteWord((uint)vector * 4 + 2, 0); byte? seen = null; cpu.InterruptEntered += v => seen = v;
        cpu.Step(); Check(seen == vector && cpu.Eip == 0x6000 && cpu.GetRegister(Register.Sp) == 0x8ffa && memory.ReadWord(0x8ffa) == 0x2000);
    }

    /// <summary>Registers precise value, rights, rejection, reset and raw-state tests for the one implemented MSR.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
        foreach (ulong value in new ulong[] { 0, 1, 0xfffffffc, 0xffffffff, 0x100000000, 0x0123456789abcdef, 0x8000000000000000, ulong.MaxValue })
            test($"M4 MSR full TSC mode {mode} prefix {Convert.ToHexString(prefix)} value {value:X16}", () =>
            {
                var (c, m, clock) = Rig([.. prefix, 0x0f, 0x30, .. prefix, 0x0f, 0x32], mode); Tsd(c, m, 4); clock.AdvanceCycles(2001, 90_000_000);
                c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, (uint)value); c.SetRegister(Register.Dx, (uint)(value >> 32)); c.SetRegister(Register.Bx, 0x76543210); c.Eflags = 0x200cd7;
                string before = Time(clock); long tlb = c.TlbInvalidations; uint cycles = c.Step();
                Check(c.TimestampCounter == value && Time(clock) == before && c.GetRegister(Register.Ax) == (uint)value && c.GetRegister(Register.Dx) == (uint)(value >> 32));
                Check(c.Eflags == 0x200cd7 && c.GetRegister(Register.Cx) == 0x10 && c.GetRegister(Register.Bx) == 0x76543210 && c.TlbInvalidations == tlb);
                clock.AdvanceCycles(cycles, 90_000_000); c.SetRegister(Register.Ax, 0xa5a5a5a5); c.SetRegister(Register.Dx, 0x5a5a5a5a); before = Time(clock); c.Step();
                ulong expected = unchecked(value + 4); Check(c.GetRegister(Register.Ax) == (uint)expected && c.GetRegister(Register.Dx) == (uint)(expected >> 32));
                Check(Time(clock) == before && c.Eflags == 0x200cd7 && c.GetRegister(Register.Cx) == 0x10 && c.Cr4 == 4 && c.Eip == 0x2004 + 2 * prefix.Length);
            });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (byte operation in new byte[] { 0x30, 0x32 })
        foreach (uint address in new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0x11, 0x12, 0x13, 0x14, 0x10010, 0x80000010, uint.MaxValue })
            test($"M4 MSR unavailable address {address:X8} mode {mode} operation {operation:X}", () =>
            {
                var (c, m, clock) = Rig([0x0f, operation], mode); clock.AdvanceCycles(137, 90_000_000);
                c.SetRegister(Register.Cx, address); c.SetRegister(Register.Ax, 0x76543210); c.SetRegister(Register.Dx, 0xfedcba98); string before = Time(clock); FaultAt(c, m, mode, 13);
                Check(c.TimestampCounter == 137 && Time(clock) == before && c.GetRegister(Register.Cx) == address && c.GetRegister(Register.Ax) == 0x76543210 && c.GetRegister(Register.Dx) == 0xfedcba98);
            });
        foreach (bool paging in new[] { false, true })
        foreach (int ring in new[] { 1, 2, 3 })
        foreach (byte operation in new byte[] { 0x30, 0x32 })
        foreach (uint tsd in new uint[] { 0, 4 })
            test($"M4 MSR privileged CPL {ring} paging {paging} TSD {tsd} operation {operation:X}", () =>
            {
                var (c, m, clock) = Rig([0x0f, operation], paging ? 3 : 2); Tsd(c, m, tsd); PagingTests.Tss(c, m);
                Descriptor(m, 0x28, 0, 0xfffff, (byte)(0x9a | ring << 5), 12); Descriptor(m, 0x30, 0, 0xfffff, (byte)(0x92 | ring << 5), 12);
                uint[] frame = [0x2000, (uint)(0x28 | ring), 0x202, 0x1b000, (uint)(0x30 | ring)];
                for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]); PagingTests.At(c, m, [0xcf]);
                c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, 0x76543210); c.SetRegister(Register.Dx, 0xfedcba98); m.Ram.AsSpan(0x19f00, 256).Fill(0xa5);
                clock.AdvanceCycles(137, 90_000_000); string before = Time(clock); byte? seen = null; c.InterruptEntered += v => seen = v; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 13 && c.Eip == 0x6000 && m.ReadDword(sp) == 0 && m.ReadDword(sp + 4) == 0x2000 && c.CurrentPrivilegeLevel == 0);
                Check(c.TimestampCounter == 137 && Time(clock) == before && c.GetRegister(Register.Ax) == 0x76543210 && c.GetRegister(Register.Dx) == 0xfedcba98 && c.Cr4 == tsd);
            });
        foreach (bool paging in new[] { false, true })
        foreach (uint tsd in new uint[] { 0, 4 })
        foreach (byte operation in new byte[] { 0x30, 0x32 })
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
            test($"M4 MSR V86 GP paging {paging} TSD {tsd} opcode {operation:X} prefix {Convert.ToHexString(prefix)}", () =>
            {
                var (c, m, clock) = Rig([0xf4], paging ? 3 : 2); Tsd(c, m, tsd); PagingTests.Tss(c, m);
                uint[] frame = [0x100, 0x3000, 0x220202, 0x8000, 0x4000, 0, 0, 0, 0]; for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
                new byte[] { }.Concat(prefix).Concat(new byte[] { 0x0f, operation }).ToArray().CopyTo(m.Ram, 0x30100); PagingTests.At(c, m, [0xcf]);
                c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, 0x76543210); c.SetRegister(Register.Dx, 0xfedcba98); m.Ram.AsSpan(0x19f00, 256).Fill(0xa5);
                clock.AdvanceCycles(137, 90_000_000); string before = Time(clock); byte? seen = null; c.InterruptEntered += v => seen = v; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 13 && !c.Virtual8086 && c.Eip == 0x6000 && m.ReadDword(sp) == 0 && m.ReadDword(sp + 4) == 0x100 && (m.ReadDword(sp + 12) & 0x20000) != 0);
                Check(c.TimestampCounter == 137 && Time(clock) == before && c.GetRegister(Register.Ax) == 0x76543210 && c.GetRegister(Register.Dx) == 0xfedcba98);
            });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (byte operation in new byte[] { 0x30, 0x32 })
            test($"M4 MSR LOCK UD mode {mode} operation {operation:X}", () =>
            {
                var (c, m, clock) = Rig([0xf0, 0x0f, operation], mode); c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, uint.MaxValue); clock.AdvanceCycles(137, 90_000_000);
                string before = Time(clock); FaultAt(c, m, mode, 6); Check(c.TimestampCounter == 137 && Time(clock) == before && c.GetRegister(Register.Ax) == uint.MaxValue);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (byte operation in new byte[] { 0x30, 0x32 })
            test($"M4 MSR absent without clock model {model} operation {operation:X}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f, operation], model: model); c.SetRegister(Register.Cx, 0x10); Fault(c, m, 6, null); Check(c.TimestampCounter == 0);
            });
        foreach (byte operation in new byte[] { 0x30, 0x32 })
            test("M4 MSR TF traps after committed operation " + operation.ToString("X"), () =>
            {
                var (c, m, clock) = Rig([0x0f, operation], 2); clock.AdvanceCycles(137, 90_000_000); c.SetRegister(Register.Cx, 0x10);
                c.SetRegister(Register.Ax, 0x89abcdef); c.SetRegister(Register.Dx, 0x01234567); c.Eflags = 0x302; byte? seen = null; c.InterruptEntered += v => seen = v; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 1 && c.Eip == 0x6000 && m.ReadDword(sp) == 0x2002 && (m.ReadDword(sp + 8) & 0x100) != 0);
                Check(c.TimestampCounter == (operation == 0x30 ? 0x0123456789abcdefUL : 137UL));
                if (operation == 0x32) Check(c.GetRegister(Register.Ax) == 137 && c.GetRegister(Register.Dx) == 0);
            });
        test("M4 MSR reset reanchors a written high counter without rewinding source", () =>
        {
            var (c, _, clock) = Rig([0x0f, 0x30], 0); clock.AdvanceCycles(2001, 90_000_000); c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, 0x89abcdef); c.SetRegister(Register.Dx, 0xfedcba98); c.Step();
            Check(c.TimestampCounter == 0xfedcba9889abcdef); string before = Time(clock); c.Reset(); Check(c.TimestampCounter == 0 && Time(clock) == before); clock.AdvanceCycles(4, 90_000_000); Check(c.TimestampCounter == 4);
        });
        test("M4 MSR written epoch and fractional source restore together", () =>
        {
            var (a, _, ca) = Rig([0x0f, 0x30], 0); ca.AdvanceCycles(137, 90_000_000); a.SetRegister(Register.Cx, 0x10); a.SetRegister(Register.Ax, 0xfffffffc); a.SetRegister(Register.Dx, uint.MaxValue); a.Step();
            var (b, _, cb) = Rig([0x0f, 0x30], 0); b.LoadState(a.SaveState()); cb.LoadState(ca.SaveState(NoOwners), NoObjects);
            for (int i = 0; i < 19; i++) { ca.AdvanceCycles(4, 90_000_000); cb.AdvanceCycles(4, 90_000_000); Check(a.TimestampCounter == b.TimestampCounter && a.TimestampCounter == unchecked(0xfffffffffffffffcUL + (ulong)(i + 1) * 4)); }
            Check(JsonSerializer.Serialize(a.SaveState(), StateValues.Json) == JsonSerializer.Serialize(b.SaveState(), StateValues.Json) && Time(ca) == Time(cb));
        });
    }
}
