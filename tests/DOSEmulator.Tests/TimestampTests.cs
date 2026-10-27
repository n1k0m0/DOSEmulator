// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Historical P54C RDTSC/TSD rules and exact, host-independent virtual clock/state checks.</summary>
internal static class TimestampTests
{
    private const X86CpuModel Model = X86CpuModel.IntelP54cB5Subset;
    private static readonly Dictionary<object, string> NoOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, IStateObject> NoObjects = new();

    /// <summary>Requires an independently observable clock, register or fault result.</summary>
    private static void Check(bool value, string message = "Timestamp contract failed")
    { if (!value) throw new Exception(message); }

    /// <summary>Encodes explicit mutable state without host delegates or private object activation.</summary>
    private static string State(X86Cpu cpu) => JsonSerializer.Serialize(cpu.SaveState(), StateValues.Json);

    /// <summary>Creates a clock-bound CPU and enters real, PE16, PE32 or paged mode through guest instructions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, DeviceScheduler Clock) Rig(byte[] code, int mode, uint frequency = 90_000_000)
    {
        var clock = new DeviceScheduler();
        if (mode == 3)
        {
            var (c, m) = PagingTests.Create(code, model: Model, timestampSource: clock, timestampFrequency: frequency);
            return (c, m, clock);
        }
        if (mode != 0)
        {
            var (c, m) = ProtectedModeTests.Rig(code, code32: mode == 2, model: Model, timestampSource: clock, timestampFrequency: frequency);
            return (c, m, clock);
        }
        var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, new(), Model, clock, frequency);
        cpu.SetSegment(Segment.Cs, 0); cpu.SetRegister(Register.Sp, 0x9000); cpu.Eip = 0x2000; code.CopyTo(memory.Ram, 0x2000);
        return (cpu, memory, clock);
    }

    /// <summary>Exercises the actual guest MOV CR4 form before returning to the instruction under test.</summary>
    private static void Tsd(X86Cpu cpu, MemoryBus memory, uint value)
    { cpu.SetRegister(Register.Ax, value); PagingTests.At(cpu, memory, [0x0f, 0x22, 0xe0]); cpu.Eip = 0x2000; }

    /// <summary>Checks a real-mode exception frame or the fixture's protected-mode fault frame.</summary>
    private static void FaultAt(X86Cpu cpu, MemoryBus memory, int mode, byte vector, uint? error = 0)
    {
        if (mode != 0) { Fault(cpu, memory, vector, error); return; }
        memory.WriteWord((uint)vector * 4, 0x6000); memory.WriteWord((uint)vector * 4 + 2, 0);
        byte? entered = null; cpu.InterruptEntered += v => entered = v; cpu.Step();
        Check(entered == vector && cpu.Eip == 0x6000 && memory.ReadWord(cpu.GetRegister(Register.Sp)) == 0x2000);
    }

    /// <summary>Registers all explicit clock and historical privilege-boundary cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (uint frequency in new uint[] { 1, 3, 33_000_000, 90_000_000, 200_000_000, uint.MaxValue })
            test($"M4 TSC exact cycle phase and pure samples {frequency}", () =>
            {
                var clock = new DeviceScheduler(); ulong cycles = 0;
                for (int i = 0; i < 97; i++)
                {
                    uint amount = i % 4 == 0 ? 4096u : (uint)(i % 9); clock.AdvanceCycles(amount, frequency); cycles += amount;
                    string before = JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json);
                    Check(clock.ClockTicks(frequency) == cycles, $"Clock lost fractional cycles at {frequency}/{cycles}.");
                    Check(before == JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json));
                }
            });
        foreach (uint frequency in new uint[] { 33_000_000, 90_000_000, 200_000_000 })
            test($"M4 TSC idle and mixed source frequencies retain rational phase {frequency}", () =>
            {
                var clock = new DeviceScheduler(); clock.AdvanceCycles(4, frequency);
                ulong whole = 4_000_000_000UL / frequency, remainder = 4_000_000_000UL % frequency;
                Check(clock.Now == whole); clock.AdvanceTo(clock.Now + 1234567);
                ulong expected = (ulong)(((UInt128)(whole + 1234567) * frequency + remainder) / 1_000_000_000);
                Check(clock.ClockTicks(frequency) == expected);
                uint other = 123_456_789;
                ulong otherExpected = (ulong)(((UInt128)clock.Now * other * frequency + (UInt128)remainder * other) / frequency / 1_000_000_000);
                Check(clock.ClockTicks(other) == otherExpected);
            });
        test("M4 TSC zero frequency rejected without changing source", () =>
        {
            var clock = new DeviceScheduler(); string before = JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json); bool rejected = false;
            try { clock.ClockTicks(0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && before == JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json));
        });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (uint frequency in new uint[] { 33_000_000, 90_000_000 })
        foreach (uint tsd in new uint[] { 0, 4 })
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
            test($"M4 TSC RDTSC full registers mode {mode} frequency {frequency} TSD {tsd} prefix {Convert.ToHexString(prefix)}", () =>
            {
                byte[] code = [.. prefix, 0x0f, 0x31]; var (c, m, clock) = Rig(code, mode, frequency); Tsd(c, m, tsd);
                clock.AdvanceCycles(2001, frequency); c.SetRegister(Register.Bx, 0x12345678); c.SetRegister(Register.Cx, 0xdeadbeef);
                c.Eflags = 0x200cd7; string time = JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json); c.Step();
                Check(c.Eip == 0x2000 + code.Length && c.GetRegister(Register.Ax) == 2001 && c.GetRegister(Register.Dx) == 0);
                Check(c.GetRegister(Register.Bx) == 0x12345678 && c.GetRegister(Register.Cx) == 0xdeadbeef && c.Eflags == 0x200cd7);
                Check(c.TimestampCounter == 2001 && time == JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json));
            });
        foreach (int ring in new[] { 1, 2, 3 })
        foreach (bool paging in new[] { false, true })
        foreach (uint tsd in new uint[] { 0, 4 })
            test($"M4 TSC protected CPL {ring} paging {paging} TSD {tsd}", () =>
            {
                var (c, m, clock) = Rig([0x0f, 0x31], paging ? 3 : 2); Tsd(c, m, tsd); PagingTests.Tss(c, m);
                Descriptor(m, 0x28, 0, 0xfffff, (byte)(0x9a | ring << 5), 12);
                Descriptor(m, 0x30, 0, 0xfffff, (byte)(0x92 | ring << 5), 12);
                uint[] frame = [0x2000, (uint)(0x28 | ring), 0x202, 0x1b000, (uint)(0x30 | ring)];
                for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
                PagingTests.At(c, m, [0xcf]); clock.AdvanceCycles(137, 90_000_000);
                c.SetRegister(Register.Ax, 0xabcdef01); c.SetRegister(Register.Dx, 0x12345678); m.Ram.AsSpan(0x19f00, 256).Fill(0xa5); byte? seen = null; c.InterruptEntered += v => seen = v;
                c.Step();
                if (tsd == 0) Check(seen is null && c.CurrentPrivilegeLevel == ring && c.GetRegister(Register.Ax) == 137 && c.GetRegister(Register.Dx) == 0);
                else
                {
                    uint sp = c.GetRegister(Register.Sp);
                    Check(seen == 13 && c.CurrentPrivilegeLevel == 0 && c.Eip == 0x6000 && m.ReadDword(sp) == 0 && m.ReadDword(sp + 4) == 0x2000);
                    Check(c.GetRegister(Register.Ax) == 0xabcdef01 && c.GetRegister(Register.Dx) == 0x12345678);
                }
            });
        foreach (bool paging in new[] { false, true })
        foreach (uint tsd in new uint[] { 0, 4 })
        foreach (byte[] prefix in new byte[][] { [], [0x66], [0x67], [0x66, 0x67] })
            test($"M4 TSC P54C V86 always GP paging {paging} TSD {tsd} prefix {Convert.ToHexString(prefix)}", () =>
            {
                var (c, m, clock) = Rig([0xf4], paging ? 3 : 2); Tsd(c, m, tsd); PagingTests.Tss(c, m);
                uint[] frame = [0x100, 0x3000, 0x220202, 0x8000, 0x4000, 0, 0, 0, 0];
                for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
                new byte[] { }.Concat(prefix).Concat(new byte[] { 0x0f, 0x31 }).ToArray().CopyTo(m.Ram, 0x30100);
                PagingTests.At(c, m, [0xcf]); c.SetRegister(Register.Ax, 0xabcdef01); c.SetRegister(Register.Dx, 0x12345678);
                byte? seen = null; c.InterruptEntered += v => seen = v; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 13 && !c.Virtual8086 && c.Eip == 0x6000 && m.ReadDword(sp) == 0 && m.ReadDword(sp + 4) == 0x100);
                Check((m.ReadDword(sp + 12) & 0x20000) != 0 && c.GetRegister(Register.Ax) == 0xabcdef01 && c.GetRegister(Register.Dx) == 0x12345678);
            });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (int register in new[] { 0, 1, 3, 7 })
        foreach (uint value in new uint[] { 0, 4 })
            test($"M4 TSC MOV CR4 full width mode {mode} register {register} value {value}", () =>
            {
                var (c, m, clock) = Rig([0xf4], mode); c.SetRegister((Register)register, value);
                PagingTests.At(c, m, [0x66, 0x0f, 0x22, (byte)(0xe0 | register)]); Check(c.Cr4 == value && c.Eip == 0x2204);
                c.SetRegister((Register)register, uint.MaxValue); PagingTests.At(c, m, [0x66, 0x0f, 0x20, (byte)(0xe0 | register)]);
                Check(c.GetRegister((Register)register) == value && c.Cr4 == value && clock.Now == 0);
            });
        foreach (int mode in new[] { 0, 2, 3 })
        foreach (uint invalid in new uint[] { 1, 2, 32, 16, 64, 0x80000000, uint.MaxValue })
            test($"M4 TSC rejects unimplemented CR4 bits mode {mode} value {invalid:X}", () =>
            {
                var (c, m, clock) = Rig([0x0f, 0x22, 0xe0], mode); Tsd(c, m, 4); c.SetRegister(Register.Ax, invalid);
                long invalidations = c.TlbInvalidations; FaultAt(c, m, mode, 13);
                Check(c.Cr4 == 4 && c.GetRegister(Register.Ax) == invalid && c.TlbInvalidations == invalidations && clock.Now == 0);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (byte[] code in new byte[][] { [0x0f, 0x31], [0x0f, 0x20, 0xe0], [0x0f, 0x22, 0xe0] })
            test("M4 TSC absent without explicit clock " + model + "/" + Convert.ToHexString(code), () =>
            {
                var (c, m) = ProtectedModeTests.Rig(code, model: model); Fault(c, m, 6, null);
                Check(!c.HasTimeStampCounter && c.TimestampFrequency == 0 && c.TimestampCounter == 0 && c.Cr4 == 0);
            });
        foreach (byte[] code in new byte[][] { [0xf0, 0x0f, 0x31], [0xf0, 0x0f, 0x20, 0xe0], [0xf0, 0x0f, 0x22, 0xe0] })
            test("M4 TSC LOCK rejected before time/register effects " + Convert.ToHexString(code), () =>
            {
                var (c, m, clock) = Rig(code, 2); c.SetRegister(Register.Ax, 0x12345678); Fault(c, m, 6, null);
                Check(c.GetRegister(Register.Ax) == 0x12345678 && clock.Now == 0 && c.Cr4 == 0);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("M4 TSC historical fault error padding " + model, () =>
            {
                var (c, m) = ProtectedModeTests.Rig([0x0f, 0x22, 0xc0], model: model);
                m.Ram.AsSpan(0x17f00, 256).Fill(0xa5); c.SetRegister(Register.Ax, c.Cr0 & ~1u | 0x80000000);
                byte? seen = null; c.InterruptEntered += v => seen = v; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 13 && c.Eip == 0x6000 && m.ReadDword(sp) == (model == X86CpuModel.Intel486Dx ? 0xa5a50000u : 0u));
                Check(m.ReadDword(sp + 4) == 0x2000 && m.ReadByte(sp - 1) == 0xa5);
            });
        foreach (int invalid in new[] { 0, 1, 2 })
            test("M4 TSC constructor rejects invalid clock pair/model " + invalid, () =>
            {
                bool rejected = false;
                try { _ = new X86Cpu(new MemoryBus(2 * 1024 * 1024), new(), invalid == 2 ? X86CpuModel.Intel486Dx : Model,
                    invalid == 0 ? null : new DeviceScheduler(), invalid == 1 ? 0u : 90_000_000u); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected);
            });
        foreach (bool v86 in new[] { false, true })
        foreach (byte operation in new byte[] { 0x20, 0x22 })
            test($"M4 TSC MOV CR4 privilege V86 {v86} operation {operation:X}", () =>
            {
                var (c, m, clock) = Rig([0xf4], 3); Tsd(c, m, 4); PagingTests.Tss(c, m);
                uint[] frame;
                if (v86) frame = [0x100, 0x3000, 0x220202, 0x8000, 0x4000, 0, 0, 0, 0];
                else
                {
                    Descriptor(m, 0x28, 0, 0xfffff, 0xfa, 12); Descriptor(m, 0x30, 0, 0xfffff, 0xf2, 12);
                    frame = [0x2000, 0x2b, 0x202, 0x1b000, 0x33];
                }
                for (uint i = 0; i < frame.Length; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
                new byte[] { 0x0f, operation, 0xe0 }.CopyTo(m.Ram, v86 ? 0x30100 : 0x2000);
                PagingTests.At(c, m, [0xcf]); c.SetRegister(Register.Ax, 0xdeadbeef); m.Ram.AsSpan(0x19f00, 256).Fill(0xa5);
                byte? seen = null; c.InterruptEntered += value => seen = value; c.Step(); uint sp = c.GetRegister(Register.Sp);
                Check(seen == 13 && c.Eip == 0x6000 && c.Cr4 == 4 && c.GetRegister(Register.Ax) == 0xdeadbeef && clock.Now == 0);
                Check(m.ReadDword(sp) == 0 && m.ReadDword(sp + 4) == (v86 ? 0x100u : 0x2000u));
            });
        test("M4 TSC clock-bound CPUID advertises only FPU TSC MSR CX8", () =>
        {
            var (c, m, clock) = Rig([0x0f, 0xa2], 0); c.SetRegister(Register.Ax, 1); c.Step();
            Check(c.GetRegister(Register.Dx) == 0x135 && c.GetRegister(Register.Ax) == 0x524 && clock.Now == 0);
        });
        test("M4 TSC reset reanchors live time and preserves fixed clock binding", () =>
        {
            var (c, m, clock) = Rig([0xf4], 0); clock.AdvanceCycles(2001, 90_000_000); Tsd(c, m, 4); c.Reset();
            Check(c.TimestampCounter == 0 && c.Cr4 == 0 && c.HasTimeStampCounter && c.TimestampFrequency == 90_000_000);
            clock.AdvanceCycles(12345, 90_000_000); Check(c.TimestampCounter == 12345);
        });
        test("M4 TSC continues through HLT and has no hidden host-time reads", () =>
        {
            var (c, _, clock) = Rig([0xf4], 0); uint cycles = c.Step(); clock.AdvanceCycles(cycles, 90_000_000);
            Check(c.Halted && c.TimestampCounter == 4); clock.AdvanceTo(clock.Now + 1_000_000);
            for (int i = 0; i < 100; i++) Check(c.TimestampCounter == 90004);
        });
        test("M4 TSC 64-bit counter wraps with UInt128 intermediates", () =>
        {
            var (c, _, clock) = Rig([0x0f, 0x31], 0, 4_000_000_000);
            clock.AdvanceTo((1UL << 62) - 1); c.Step();
            Check(c.GetRegister(Register.Ax) == 0xfffffffc && c.GetRegister(Register.Dx) == uint.MaxValue);
            clock.AdvanceTo(1UL << 62); Check(c.TimestampCounter == 0);
        });
        test("M4 TSC REP batch and scalar owner clocks remain equal", () =>
        {
            var (a, ma, ca) = Rig([0xf3, 0xa4], 0); var (b, mb, cb) = Rig([0xf3, 0xa4], 0);
            foreach (var item in new[] { (a, ma), (b, mb) })
            {
                item.Item1.SetRegister(Register.Cx, 256); item.Item1.SetRegister(Register.Si, 0x6000); item.Item1.SetRegister(Register.Di, 0x7000);
                for (int i = 0; i < 256; i++) item.Item2.Ram[0x6000 + i] = (byte)(i ^ 0xa5);
            }
            ca.AdvanceCycles(a.Step(), 90_000_000); cb.AdvanceCycles(b.Step(), 90_000_000);
            int amount = a.TryRunRepBatch(255); Check(amount == 255); ca.AdvanceCycles((uint)amount * 4, 90_000_000);
            for (int i = 0; i < 255; i++) cb.AdvanceCycles(b.Step(), 90_000_000);
            Check(a.TimestampCounter == 1024 && b.TimestampCounter == 1024 && ma.Ram.SequenceEqual(mb.Ram));
            Check(JsonSerializer.Serialize(ca.SaveState(NoOwners), StateValues.Json) != "{}" && ca.Now == cb.Now);
        });
        test("M4 TSC source callback sampling/reset rejected before CPU mutation", () =>
        {
            var (c, _, clock) = Rig([0x90], 0); c.SetRegister(Register.Ax, 0xabcdef01); string saved = State(c); int rejected = 0;
            clock.ScheduleAfter(1, () =>
            {
                try { _ = c.TimestampCounter; } catch (InvalidOperationException) { rejected++; }
                try { c.Reset(); } catch (InvalidOperationException) { rejected++; }
            });
            clock.AdvanceTo(1); Check(rejected == 2 && State(c) == saved);
        });
        test("M4 TSC coordinated CPU/source continuation retains phase and TSD", () =>
        {
            var (a, ma, ca) = Rig([0x0f, 0x31], 0); Tsd(a, ma, 4); ca.AdvanceCycles(137, 90_000_000);
            var (b, mb, cb) = Rig([0x0f, 0x31], 0); b.LoadState(a.SaveState()); cb.LoadState(ca.SaveState(NoOwners), NoObjects);
            Check(State(a) == State(b) && a.TimestampCounter == b.TimestampCounter);
            for (int i = 0; i < 19; i++)
            {
                a.Eip = b.Eip = 0x2000; ca.AdvanceCycles(a.Step(), 90_000_000); cb.AdvanceCycles(b.Step(), 90_000_000);
                Check(State(a) == State(b) && a.TimestampCounter == b.TimestampCounter);
            }
        });
        test("M4 TSC mismatched clock binding rejected before CPU field mutation", () =>
        {
            var (a, _, ca) = Rig([0x90], 0); var (b, _, cb) = Rig([0x90], 0, 33_000_000);
            a.SetRegister(Register.Ax, 0xdeadbeef); string saved = State(b); bool rejected = false;
            try { b.LoadState(a.SaveState()); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && State(b) == saved && ca.Now == 0 && cb.Now == 0);
        });
    }
}
