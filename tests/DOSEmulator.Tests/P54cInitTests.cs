// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Historical P54C INIT/CPURST state classes, real fetches, x87 pins and virtual-clock boundaries.</summary>
internal static class P54cInitTests
{
    private const X86CpuModel Model = X86CpuModel.IntelP54cB5Subset;
    private static readonly Dictionary<object, string> NoOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, IStateObject> NoObjects = new();
    private static readonly string[] Diagnostics = ["Instructions", "X87Instructions", "NumericErrorWaits", "NumericErrorFaults", "PageFaults", "PageWalks", "TlbHits", "RepBatchedElements", "ControlCheckpointsSaved", "InstructionBreakpointFaults", "DataBreakpointTraps", "SingleStepTraps", "GeneralDetectFaults", "HardwareTaskSwitches", "HardwareTaskReturns", "TaskGateTransfers", "TaskDebugTraps", "LockedMemoryInstructions", "InvalidLockPrefixes", "AlignmentFaults", "ProtectedModeEntries", "ProtectedModeExits", "ProtectedInstructions", "PrivilegeStackSwitches", "OuterPrivilegeReturns", "Virtual8086Entries", "Virtual8086Exits", "Virtual8086Instructions", "CallGateTransfers"];
    /// <summary>Requires an independent expected architectural value or unchanged-state result.</summary>
    private static void Check(bool condition, string message = "P54C INIT/CPURST invariant") { if (!condition) throw new Exception(message); }
    /// <summary>Serializes all explicit raw CPU fields without reading a virtual clock.</summary>
    private static string State(X86Cpu cpu) => JsonSerializer.Serialize(cpu.SaveState(), StateValues.Json);
    /// <summary>Captures the scheduler phase including its integer conversion remainder.</summary>
    private static string Time(DeviceScheduler clock) => JsonSerializer.Serialize(clock.SaveState(NoOwners), StateValues.Json);
    /// <summary>Changes one typed field of a controlled raw fixture, never the live component.</summary>
    private static void Put<T>(StateBlock state, string name, T value) => state.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json);
    /// <summary>Reads one field from explicitly captured state.</summary>
    private static T Read<T>(X86Cpu cpu, string name) => StateValues.Read<T>(cpu.SaveState().Fields[name]);
    /// <summary>Compares raw x87 values, tags, pointers, control/status and the published external error level.</summary>
    private static string Fpu(X86Cpu cpu) => string.Join("|", cpu.SaveState().Fields.Where(p => p.Key.StartsWith("_fpu", StringComparison.Ordinal) || p.Key is "FpuControlWord" or "FpuStatusWord" or "_publishedFpuError" or "IgnoreNumericError").OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value.GetRawText()));
    /// <summary>Builds real/PE16/PE32/paged/V86 contexts through existing guest bootstrap instructions.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory, DeviceScheduler Clock) Rig(int mode = 0, uint hz = 90_000_000, bool bound = true)
    {
        var clock = new DeviceScheduler(); DeviceScheduler? source = bound ? clock : null; uint frequency = bound ? hz : 0;
        if (mode is 3 or 4)
        {
            var (cpu, memory) = PagingTests.Create([0x90], model: Model, timestampSource: source, timestampFrequency: frequency);
            if (mode == 4)
            {
                PagingTests.Tss(cpu, memory);
                uint[] frame = [0x100, 0x3000, 0x232202, 0x8000, 0x4000, 0, 0, 0, 0];
                for (uint i = 0; i < frame.Length; i++) memory.WriteDword(0x18000 + i * 4, frame[i]);
                memory.WriteByte(0x30100, 0x90); PagingTests.At(cpu, memory, [0xcf]);
                Check(cpu.Virtual8086 && cpu.CurrentPrivilegeLevel == 3, "V86 fixture must really enter V86");
            }
            return (cpu, memory, clock);
        }
        if (mode is 1 or 2)
        {
            var (cpu, memory) = ProtectedModeTests.Rig([0x90], code32: mode == 2, model: Model, timestampSource: source, timestampFrequency: frequency);
            return (cpu, memory, clock);
        }
        var m = new MemoryBus(2 * 1024 * 1024); var c = new X86Cpu(m, new(), Model, source, frequency);
        c.SetSegment(Segment.Cs, 0); c.SetRegister(Register.Sp, 0x9000); c.Eip = 0x2000; m.WriteByte(0x2000, 0x90);
        return (c, m, clock);
    }
    /// <summary>Seeds all eight raw physical registers and nontrivial retained x87 metadata, including unsupported encodings.</summary>
    private static void SeedFpu(X86Cpu cpu, bool pending, bool ignore)
    {
        var state = cpu.SaveState();
        Float80Value[] raw = [new(0, 0x8000), Float80Value.One, new(1, 0), new(0x7fffffffffffffff, 0x4000), new(0x8000000000000000, 0x7fff), new(0xc123456789abcdef, 0xffff), new(0xfedcba9876543210, 0xbfff), new(0x0123456789abcdef, 0x1234)];
        Put(state, "_fpuRegisters", raw); Put(state, "_fpuEmpty", (byte)0x24);
        Put(state, "FpuControlWord", (ushort)(pending ? 0x0b7e : 0x0b7f));
        Put(state, "FpuStatusWord", (ushort)(pending ? 0xa9c1 : 0x2940));
        Put(state, "_publishedFpuError", pending); Put(state, "IgnoreNumericError", ignore);
        Put(state, "_fpuIp", 0x87654321u); Put(state, "_fpuData", 0xfedcba98u);
        Put(state, "_fpuRealIp", 0xabcdeu); Put(state, "_fpuRealData", 0x54321u);
        Put(state, "_fpuCs", (ushort)0x1234); Put(state, "_fpuDs", (ushort)0xabcd); Put(state, "_fpuOpcode", (ushort)0x7fd);
        cpu.LoadState(state); Check(cpu.FpuErrorAsserted == pending);
    }
    /// <summary>Checks constants specified independently in Intel 241428-004 table 4-2, including hidden startup bytes.</summary>
    private static void Startup(X86Cpu cpu, uint cr0)
    {
        foreach (Register r in Enum.GetValues<Register>()) Check(cpu.GetRegister(r) == (r == Register.Dx ? 0x524u : 0), "GPR " + r);
        Check(cpu.Eip == 0xfff0 && cpu.Eflags == 2 && cpu.Cr0 == cr0 && cpu.Cr2 == 0 && cpu.Cr3 == 0 && cpu.Cr4 == 0);
        Check(!cpu.ProtectedMode && !cpu.PagingEnabled && !cpu.Virtual8086 && cpu.CurrentPrivilegeLevel == 0 && !cpu.Halted && !cpu.TripleFaulted);
        foreach (Segment segment in Enum.GetValues<Segment>())
        {
            var cache = cpu.GetSegmentCache(segment); bool cs = segment == Segment.Cs;
            Check(cpu.GetSegment(segment) == (cs ? 0xf000 : 0) && cache.Base == (cs ? 0xffff0000u : 0) && cache.Limit == 0xffff && cache.Access == 0x93 && cache.Flags == 0 && cache.Usable, "Segment " + segment);
        }
        Check(cpu.Gdtr == new DescriptorTableRegister(0xffff, 0) && cpu.Idtr == new DescriptorTableRegister(0xffff, 0));
        Check(cpu.Ldtr == 0 && cpu.Tr == 0 && cpu.LdtrCache == new SystemDescriptorCache(0, 0xffff, 0x82, 0, false) && cpu.TrCache == new SystemDescriptorCache(0, 0xffff, 0x82, 0, false));
        Check(cpu.Dr0 == 0 && cpu.Dr1 == 0 && cpu.Dr2 == 0 && cpu.Dr3 == 0 && cpu.Dr6 == 0xffff0ff0 && cpu.Dr7 == 0x400);
        Check(!cpu.WaitingForNumericError && !cpu.HasRepContinuation);
    }
    /// <summary>Checks physical power-on x87 values rather than the different FNINIT result.</summary>
    private static void PowerFpu(X86Cpu cpu)
    {
        Check(cpu.FpuControlWord == 0x0040 && cpu.FpuStatusWord == 0 && cpu.FpuTop == 0 && cpu.FpuTagWord == 0x5555 && !cpu.FpuErrorAsserted);
        Check(cpu.FpuInstructionPointer == 0 && cpu.FpuDataPointer == 0 && cpu.FpuCodeSelector == 0 && cpu.FpuDataSelector == 0 && cpu.FpuOpcode == 0);
        Check(Read<uint>(cpu, "_fpuRealIp") == 0 && Read<uint>(cpu, "_fpuRealData") == 0);
        for (int i = 0; i < 8; i++) Check(cpu.GetFpuRegister(i) == Float80Value.Zero && !cpu.IsFpuRegisterEmpty(i));
    }
    /// <summary>Runs one public signal, including the narrower deassert operation.</summary>
    private static void Signal(X86Cpu cpu, int kind) { if (kind == 0) cpu.Init(); else if (kind == 1) cpu.ResetSignal(); else cpu.ResetSignalReleased(); }
    /// <summary>Requires a model/owner rejection without swallowing an unrelated failure.</summary>
    private static void Rejected(Action operation) { bool seen = false; try { operation(); } catch (InvalidOperationException) { seen = true; } Check(seen, "Expected atomic invalid-operation rejection"); }
    private sealed class VectorRom : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Supplies a real far jump from the CPU's high physical reset vector.</summary>
        public byte ReadByte(uint offset) { Reads++; return offset switch { 0 => 0xea, 1 => 0x00, 2 => 0x30, 3 => 0, 4 => 0, _ => 0xff }; }
        /// <summary>Counts unexpected writes to the immutable private reset vector.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }
    /// <summary>Registers signal separation, architectural state, continuation and failure-boundary cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int mode in new[] { 0, 1, 2, 3, 4 }) foreach (uint cache in new uint[] { 0, 0x20000000, 0x40000000, 0x60000000 })
            test($"M4 P54C INIT integer restart mode {mode} CDNW {cache:X8}", () =>
            {
                var (c, m, clock) = Rig(mode); var state = c.SaveState();
                Put(state, "Cr0", (c.Cr0 & ~0x60000000u) | cache); Put(state, "Cr2", 0xdeadbeefu); Put(state, "Cr4", 12u);
                Put(state, "_debugAddresses", new uint[] { 0x1234, 0x5678, 0x9abc, 0xdef0 }); Put(state, "Dr6", 0xffffefffu); Put(state, "Dr7", 0xf1232405u);
                Put(state, "TripleFaulted", true); c.LoadState(state);
                for (int i = 0; i < 8; i++) c.SetRegister((Register)i, 0xfedc0000u + (uint)i);
                byte[] before = (byte[])m.Ram.Clone(); string time = Time(clock); c.Init(); Startup(c, cache | 0x10);
                Check(m.Ram.AsSpan().SequenceEqual(before) && Time(clock) == time, "INIT must not write RAM or advance/reset the scheduler");
            });
        foreach (int mode in new[] { 0, 1, 2, 3, 4 }) foreach (bool pending in new[] { false, true }) foreach (bool ignore in new[] { false, true })
            test($"M4 P54C INIT full raw x87/FERR retention mode {mode} pending {pending} IGNNE {ignore}", () =>
            {
                var (c, _, _) = Rig(mode); SeedFpu(c, pending, ignore); string before = Fpu(c); int edges = 0; c.FpuErrorChanged += _ => edges++;
                c.Init(); Check(Fpu(c) == before && c.FpuErrorAsserted == pending && c.IgnoreNumericError == ignore && edges == 0);
            });
        foreach (int mode in new[] { 0, 1, 2, 3, 4 }) foreach (bool ignore in new[] { false, true })
            test($"M4 P54C CPURST physical x87 values mode {mode} IGNNE {ignore}", () =>
            {
                var (c, _, clock) = Rig(mode); SeedFpu(c, true, ignore); var edges = new List<bool>(); c.FpuErrorChanged += edges.Add; clock.AdvanceCycles(137, 90_000_000);
                string before = Time(clock); c.ResetSignal(); Startup(c, 0x60000010); PowerFpu(c);
                Check(c.TimestampCounter == 0 && Time(clock) == before && c.IgnoreNumericError == ignore && edges.SequenceEqual(new[] { false }));
                c.ResetSignal(); Check(edges.Count == 1, "A stable low FERR must not produce another edge");
            });
        foreach (int kind in new[] { 0, 1, 2 })
            test("M4 signal API rejects 486 without any mutation kind " + kind, () =>
            {
                var m = new MemoryBus(2 * 1024 * 1024); var c = new X86Cpu(m, new()); SeedFpu(c, true, true); c.SetRegister(Register.Bx, 0xabcdef01);
                string before = State(c); byte[] ram = (byte[])m.Ram.Clone(); int edges = 0; c.FpuErrorChanged += _ => edges++;
                Rejected(() => Signal(c, kind)); Check(State(c) == before && m.Ram.AsSpan().SequenceEqual(ram) && edges == 0);
            });
        foreach (int kind in new[] { 0, 1, 2 })
            test("M4 P54C signal rejects active scheduler callback atomically kind " + kind, () =>
            {
                var (c, m, clock) = Rig(); SeedFpu(c, true, false); string before = State(c); byte[] ram = (byte[])m.Ram.Clone(); int calls = 0, edges = 0;
                c.FpuErrorChanged += _ => edges++; clock.ScheduleAfter(10, () => { calls++; Rejected(() => Signal(c, kind)); Check(State(c) == before); });
                clock.AdvanceTo(10); Check(calls == 1 && !clock.IsDispatching && State(c) == before && m.Ram.AsSpan().SequenceEqual(ram) && edges == 0);
            });
        foreach (uint hz in new uint[] { 33_000_000, 90_000_000, 100_000_000 }) foreach (ulong value in new ulong[] { 0, 0xffffffff, 0x0123456789abcdef, 0xfffffffffffffffc })
            test($"M4 P54C INIT retains full written TSC {hz} {value:X16}", () =>
            {
                var (c, m, clock) = Rig(hz: hz); clock.AdvanceCycles(137, hz); c.SetRegister(Register.Cx, 0x10); c.SetRegister(Register.Ax, (uint)value); c.SetRegister(Register.Dx, (uint)(value >> 32));
                PagingTests.At(c, m, [0x0f, 0x30]); string before = Time(clock); c.Init(); Check(c.TimestampCounter == value && Time(clock) == before);
                clock.AdvanceCycles(19, hz); c.SetSegment(Segment.Cs, 0); PagingTests.At(c, m, [0x0f, 0x31]); ulong expected = unchecked(value + 19);
                Check(c.GetRegister(Register.Ax) == (uint)expected && c.GetRegister(Register.Dx) == (uint)(expected >> 32));
            });
        foreach (uint hz in new uint[] { 33_000_000, 90_000_000, 100_000_000 })
            test("M4 P54C RESET deassert only reanchors TSC " + hz, () =>
            {
                var (c, _, clock) = Rig(hz: hz); SeedFpu(c, true, false); c.ResetSignal(); clock.AdvanceTo(clock.Now + 2_000_000);
                var before = c.SaveState(); string time = Time(clock); c.ResetSignalReleased(); Check(c.TimestampCounter == 0 && Time(clock) == time);
                var after = c.SaveState(); foreach (var field in before.Fields) if (field.Key != "_tscEpochTicks") Check(field.Value.GetRawText() == after.Fields[field.Key].GetRawText(), "Deassert changed " + field.Key);
                clock.AdvanceCycles(7, hz); Check(c.TimestampCounter == 7);
            });
        test("M4 P54C INIT preserves software diagnostics while invalidating translations once", () =>
        {
            var (c, m, _) = Rig(3); PagingTests.Pte(m, 0x30000, 0x40000); m.WriteDword(0x40000, 0x11223344);
            PagingTests.At(c, m, [0xa1, 0, 0, 3, 0]); Check(c.GetRegister(Register.Ax) == 0x11223344);
            Check(c.SaveState().Fields["_tlb"].EnumerateArray().Any(e => e.GetProperty("valid").GetBoolean()));
            var seeded = c.SaveState(); for (int i = 0; i < Diagnostics.Length; i++) Put(seeded, Diagnostics[i], 101L + i);
            Put(seeded, "_taskSwitchEpoch", 321L); c.LoadState(seeded); var before = c.SaveState(); long invalidations = c.TlbInvalidations;
            c.Init(); var after = c.SaveState(); foreach (string key in Diagnostics) Check(before.Fields[key].GetRawText() == after.Fields[key].GetRawText(), "Diagnostic changed " + key);
            Check(c.TlbInvalidations == invalidations + 1 && Read<long>(c, "_taskSwitchEpoch") == 321);
            Check(after.Fields["_tlb"].EnumerateArray().All(e => !e.GetProperty("valid").GetBoolean()));
        });
        foreach (int kind in new[] { 0, 1 })
            test("M4 P54C signal discards suspended real REP without another memory effect kind " + kind, () =>
            {
                var (c, m, _) = Rig(); new byte[] { 0xf3, 0xa4 }.CopyTo(m.Ram, 0x2000);
                c.SetRegister(Register.Cx, 3); c.SetRegister(Register.Si, 0x4000); c.SetRegister(Register.Di, 0x5000);
                m.Ram[0x4000] = 0x12; m.Ram[0x4001] = 0x34; m.Ram[0x4002] = 0x56; m.Ram.AsSpan(0x5000, 3).Fill(0xa5);
                c.Step(); Check(c.HasRepContinuation && c.Eip == 0x2000 && c.GetRegister(Register.Cx) == 2 && m.Ram[0x5000] == 0x12 && m.Ram[0x5001] == 0xa5);
                byte[] ram = (byte[])m.Ram.Clone(); Signal(c, kind); Check(!c.HasRepContinuation && c.TryRunRepBatch(100) == 0 && m.Ram.AsSpan().SequenceEqual(ram));
            });
        foreach (int kind in new[] { 0, 1 })
            test("M4 P54C signal clears a real numeric wait with separate FERR policy kind " + kind, () =>
            {
                var (c, m, _) = Rig(2); var edges = new List<bool>(); c.FpuErrorChanged += edges.Add;
                m.WriteWord(0x30000, 0x37e); PagingTests.At(c, m, [0xd9, 0x2d, 0, 0, 3, 0]); PagingTests.At(c, m, [0xd8, 0xc0]);
                Check(c.FpuErrorAsserted && edges.SequenceEqual(new[] { true })); PagingTests.At(c, m, [0x9b]); Check(c.WaitingForNumericError);
                string fpu = Fpu(c); Signal(c, kind); Check(!c.WaitingForNumericError && !c.Halted);
                if (kind == 0) Check(c.FpuErrorAsserted && Fpu(c) == fpu && edges.Count == 1);
                else { PowerFpu(c); Check(edges.SequenceEqual(new[] { true, false })); }
            });
        foreach (int kind in new[] { 0, 1 })
            test("M4 P54C signal wakes HLT and clears interrupt/debug/decode shadows kind " + kind, () =>
            {
                var (c, m, _) = Rig(); m.Ram[0x2000] = 0xf4; c.Step(); Check(c.Halted);
                var state = c.SaveState(); Put(state, "_interruptShadow", 1); Put(state, "_debugShadow", 1); Put(state, "_debugDeferredStatus", 0x4001u);
                Put(state, "_debugDeferredTrigger", true); Put(state, "_debugDeferredData", true); Put(state, "_debugDataActive", true); Put(state, "_debugStepShadowed", true);
                Put(state, "_debugInterruptTransfer", true); Put(state, "_debugDataMatches", 15u); Put(state, "_debugDataTriggered", true); Put(state, "_lockPrefix", true);
                Put(state, "_lockedMemory", true); Put(state, "_checkpointProtected", true); Put(state, "_controlCheckpointSaved", true); c.LoadState(state);
                Signal(c, kind); Check(!c.Halted); c.Eflags |= X86Cpu.InterruptEnable; Check(c.CanAcceptMaskableInterrupt);
                foreach (string name in new[] { "_debugDeferredTrigger", "_debugDeferredData", "_debugDataActive", "_debugStepShadowed", "_debugInterruptTransfer", "_debugDataTriggered", "_lockPrefix", "_lockedMemory", "_checkpointProtected", "_controlCheckpointSaved" }) Check(!Read<bool>(c, name), name);
                Check(Read<int>(c, "_debugShadow") == 0 && Read<uint>(c, "_debugDeferredStatus") == 0 && Read<uint>(c, "_debugDataMatches") == 0);
            });
        foreach (int kind in new[] { 0, 1 })
            test("M4 P54C actual high reset-vector far jump then register/HLT fetch kind " + kind, () =>
            {
                var (c, m, clock) = Rig(); var rom = new VectorRom(); m.Map(0xfffffff0, 16, rom, "Own reset-vector bytes");
                new byte[] { 0x66, 0xb8, 0x44, 0x33, 0x22, 0x11, 0xf4 }.CopyTo(m.Ram, 0x3000); Signal(c, kind);
                Check(c.LinearInstructionAddress == 0xfffffff0); uint cycles = c.Step(); clock.AdvanceCycles(cycles, 90_000_000);
                Check(c.GetSegment(Segment.Cs) == 0 && c.Eip == 0x3000 && rom.Reads == 5 && rom.Writes == 0);
                c.Step(); Check(c.GetRegister(Register.Ax) == 0x11223344 && c.Eip == 0x3006); c.Step(); Check(c.Halted && !c.TripleFaulted);
            });
        foreach (int kind in new[] { 0, 1 }) foreach (uint hz in new uint[] { 33_000_000, 90_000_000 })
            test($"M4 P54C raw post-signal continuation kind {kind} frequency {hz}", () =>
            {
                var (a, ma, ca) = Rig(hz: hz); SeedFpu(a, true, true); ca.AdvanceCycles(137, hz); Signal(a, kind);
                var (b, mb, cb) = Rig(hz: hz); a.SetSegment(Segment.Cs, 0); a.Eip = 0x2000;
                new byte[] { 0x0f, 0x31, 0x90, 0x66, 0x40, 0xf4 }.CopyTo(ma.Ram, 0x2000); ma.Ram.CopyTo(mb.Ram, 0);
                b.LoadState(a.SaveState()); cb.LoadState(ca.SaveState(NoOwners), NoObjects); int edgesA = 0, edgesB = 0; a.FpuErrorChanged += _ => edgesA++; b.FpuErrorChanged += _ => edgesB++;
                for (int i = 0; i < 4; i++)
                {
                    uint ac = a.Step(), bc = b.Step(); Check(ac == bc); ca.AdvanceCycles(ac, hz); cb.AdvanceCycles(bc, hz);
                    Check(State(a) == State(b) && ma.Ram.AsSpan().SequenceEqual(mb.Ram) && Time(ca) == Time(cb) && a.TimestampCounter == b.TimestampCounter);
                }
                Check(a.Halted && b.Halted && edgesA == 0 && edgesB == 0);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("M4 legacy Reset retains its existing raw x87 contract model " + model, () =>
            {
                var m = new MemoryBus(2 * 1024 * 1024); var c = new X86Cpu(m, new(), model); SeedFpu(c, true, true); string fpu = Fpu(c); int edges = 0; c.FpuErrorChanged += _ => edges++;
                c.Reset(); Check(Fpu(c) == fpu && c.FpuErrorAsserted && edges == 0 && c.Idtr.Limit == 0x3ff && c.GetSegmentCache(Segment.Cs).Access == 0x9b);
            });
        foreach (int kind in new[] { 0, 1, 2 })
            test("M4 clockless P54C signal preserves constructor feature boundary kind " + kind, () =>
            {
                var (c, m, _) = Rig(bound: false); SeedFpu(c, true, true); string fpu = Fpu(c); Signal(c, kind);
                if (kind == 1) PowerFpu(c); else Check(Fpu(c) == fpu);
                Check(!c.HasTimeStampCounter && c.TimestampFrequency == 0 && c.TimestampCounter == 0);
                c.SetSegment(Segment.Cs, 0); c.SetRegister(Register.Ax, 1); PagingTests.At(c, m, [0x0f, 0xa2]);
                Check(c.GetRegister(Register.Dx) == 0x0101 && c.GetRegister(Register.Ax) == 0x0524, "Signals must not invent clock-dependent feature bits");
            });
        test("M4 P54C physical RESET and later FNINIT have distinct x87 tag/control values", () =>
        {
            var (c, m, _) = Rig(); c.ResetSignal(); PowerFpu(c); c.SetSegment(Segment.Cs, 0); PagingTests.At(c, m, [0xdb, 0xe3]);
            Check(c.FpuControlWord == 0x037f && c.FpuTagWord == 0xffff && c.FpuStatusWord == 0);
            for (int i = 0; i < 8; i++) Check(c.GetFpuRegister(i) == Float80Value.Zero && c.IsFpuRegisterEmpty(i));
        });
    }
}
