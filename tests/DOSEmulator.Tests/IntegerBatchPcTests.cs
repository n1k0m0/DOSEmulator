// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Complete-PC scalar/batched controls with independent clock, IRQ, reset and guest-effect oracles.</summary>
internal static class IntegerBatchPcTests
{
    private const uint Seed = 0x1234f100;
    private const string DefinitionId = "f4b67d3e2a5d4b96a8f304c8de682a19";

    /// <summary>Requires an independently specified count, guest byte or complete continuation result.</summary>
    private static void Check(bool value, string why = "Integer batch PC invariant failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Selects four actual fixed profile/CPU/frequency/resource tuples without changing their clock contract.</summary>
    private static MachineProfile Profile(int hz) => hz switch
    {
        33_000_000 => new() { RamMiB = 4 },
        90_000_000 => new() { Id = MachineProfile.P54cProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = hz },
        100_000_000 => new() { Id = MachineProfile.PiixResetProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = hz, RamMiB = 32 },
        133_000_000 => new() { Id = MachineProfile.Windows95Profile133Id, Cpu = MachineProfile.P54cCpuId,
            CpuHz = hz, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId },
        _ => throw new ArgumentOutOfRangeException(nameof(hz))
    };

    /// <summary>Places an own real-mode RAM program and warms through a real scalar NOP before seeding known register data.</summary>
    private static void Prepare(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0);
        m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0);
        m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 3;
        m.Memory.Ram.AsSpan(0x1000, 0x6000).Fill(0x40); m.Memory.Ram[0x1000] = 0x90;
        Check(m.RunSteps(1, null, false) == 1 && m.Cpu.Eip == 0x1001 && m.Fault is null, "Own warm NOP did not complete.");
        m.Cpu.SetRegister(Register.Ax, Seed);
    }

    /// <summary>Binds both independent own PCs to the same explicit definition before any guest execution, retaining every saved identity field.</summary>
    private static PcMachine Factory(MachineProfile profile) => MachineFactory.FromDefinition(new()
    {
        Id = DefinitionId, Name = "Own integer batch comparison", Hardware = profile, BuiltInTestDisk = true
    });

    /// <summary>Creates only an own factory PC and private in-memory test medium, never licensed or personal media.</summary>
    private static PcMachine Machine(int hz)
    {
        var m = Factory(Profile(hz)); Prepare(m); return m;
    }

    /// <summary>Builds equally identified PE32 PCs through actual LGDT, CR0, far jump, CR3 and paging instructions.</summary>
    private static PcMachine PagedMachine(byte[] code)
    {
        var m = Factory(Profile(33_000_000) with { RamMiB = 8 }); var cpu = m.Cpu; var memory = m.Memory;
        ProtectedModeTests.Descriptor(memory, 8, 0, 0xfffff, 0x9a, 12);
        ProtectedModeTests.Descriptor(memory, 16, 0, 0xfffff, 0x92, 12);
        memory.WriteWord(0x300, 0x17); memory.WriteDword(0x302, 0x400);
        byte[] bootstrap = [0x0f, 1, 0x16, 0, 3, 0x66, 0xb8, 0x11, 0, 0, 0x60,
            0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0];
        bootstrap.CopyTo(memory.Ram, 0x1000); code.CopyTo(memory.Ram, 0x2000);
        cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x1000;
        Check(m.RunSteps(4, null, false) == 4 && cpu.ProtectedMode && cpu.Eip == 0x2000, "Own PE32 bootstrap failed.");
        cpu.SetSegment(Segment.Ds, 16); cpu.SetSegment(Segment.Es, 16); cpu.SetSegment(Segment.Ss, 16);
        cpu.SetRegister(Register.Sp, 0x18000); cpu.Eflags = 2;
        memory.WriteDword(0x1c000, 0x1d007);
        for (uint i = 0; i < 1024; i++) memory.WriteDword(0x1d000 + i * 4, i * 4096 | 7);
        new byte[] { 0x0f, 0x22, 0xd8 }.CopyTo(memory.Ram, 0x2200); cpu.SetRegister(Register.Ax, 0x1c000); cpu.Eip = 0x2200;
        Check(m.RunSteps(1, null, false) == 1);
        new byte[] { 0x0f, 0x22, 0xc0 }.CopyTo(memory.Ram, 0x2200); cpu.SetRegister(Register.Ax, cpu.Cr0 | 0x80010000); cpu.Eip = 0x2200;
        Check(m.RunSteps(1, null, false) == 1 && cpu.PagingEnabled && m.Fault is null, "Own identity paging bootstrap failed.");
        cpu.Eip = 0x2000; return m;
    }

    /// <summary>Includes every registered component member and the entire scheduler conversion cache and event queue.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);

    /// <summary>Names the first actual differing component/member/array index without omitting any state or rewriting its values.</summary>
    private static string? Difference(JsonElement left, JsonElement right, string path)
    {
        if (left.GetRawText() == right.GetRawText()) return null;
        if (left.ValueKind != right.ValueKind) return path + ": JSON kinds " + left.ValueKind + " / " + right.ValueKind;
        if (left.ValueKind == JsonValueKind.Object)
        {
            string[] first = left.EnumerateObject().Select(p => p.Name).ToArray(), second = right.EnumerateObject().Select(p => p.Name).ToArray();
            if (!first.SequenceEqual(second)) return path + ": property names or order differ";
            foreach (var property in left.EnumerateObject())
                if (Difference(property.Value, right.GetProperty(property.Name), path + "/" + property.Name) is { } difference) return difference;
        }
        else if (left.ValueKind == JsonValueKind.Array)
        {
            if (left.GetArrayLength() != right.GetArrayLength()) return path + ": array lengths " + left.GetArrayLength() + " / " + right.GetArrayLength();
            for (int i = 0; i < left.GetArrayLength(); i++)
                if (Difference(left[i], right[i], path + "[" + i + "]") is { } difference) return difference;
        }
        else
        {
            string first = left.GetRawText(), second = right.GetRawText();
            return path + ": scalar=" + first[..Math.Min(180, first.Length)] + "; batched=" + second[..Math.Min(180, second.Length)];
        }
        return path + ": serialized representation differs";
    }

    /// <summary>Requires every saved hardware field and every physical RAM byte to match, with no diagnostic exclusions.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        string scalar = State(a), batched = State(b);
        if (scalar != batched)
        {
            using var left = JsonDocument.Parse(scalar); using var right = JsonDocument.Parse(batched);
            throw new Exception("Complete state differs at " + Difference(left.RootElement, right.RootElement, "hardware"));
        }
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete physical RAM differs.");
    }

    /// <summary>Computes elapsed nanoseconds with independent wide integer arithmetic from an exact starting phase.</summary>
    private static ulong Delta(int steps, uint hz, ulong remainder) =>
        (ulong)(((UInt128)(uint)steps * 4_000_000_000UL + remainder) / hz);

    /// <summary>Computes the first scalar element whose completed time reaches the absolute caller deadline.</summary>
    private static int DeadlineCount(ulong nanoseconds, uint hz, ulong remainder)
    {
        if (nanoseconds == 0) return 0;
        UInt128 target = (UInt128)nanoseconds * hz;
        if (target <= remainder) return 0;
        return (int)((target - remainder + 3_999_999_999UL) / 4_000_000_000UL);
    }

    /// <summary>Checks the independent INC-AX result, retirement count, instruction pointer and exact fractional virtual time.</summary>
    private static void Increments(PcMachine m, int count, ulong started, ulong remainder, long retired)
    {
        uint expected = (Seed & 0xffff0000) | ((Seed + (uint)count) & 0xffff);
        Check(m.Fault is null && !m.Cpu.Halted && m.Cpu.GetRegister(Register.Ax) == expected, "INC-AX result or terminal state differs.");
        Check(m.Cpu.Eip == 0x1001u + (uint)count && m.Cpu.Instructions == retired + count, "Count or exact executed position differs.");
        Check(m.Scheduler.Now == started + Delta(count, (uint)m.Profile.CpuHz, remainder), "Independent rational clock oracle differs.");
        Check((m.Cpu.Eflags & X86Cpu.Carry) != 0, "INC changed its preserved carry input.");
    }

    /// <summary>Captures the actual complete CPU record observed by a controlled virtual device callback.</summary>
    private static string Observation(PcMachine m) => m.Scheduler.Now + ":" + JsonSerializer.Serialize(m.Cpu.SaveState(), StateValues.Json);

    /// <summary>Programs the two real PIC banks and a private real-mode IRQ0 handler that counts, EOIs and returns with IRET.</summary>
    private static void Irq(PcMachine m)
    {
        m.Pic.Write(0x20, 0x11, 1); m.Pic.Write(0xa0, 0x11, 1);
        m.Pic.Write(0x21, 0x20, 1); m.Pic.Write(0xa1, 0x28, 1);
        m.Pic.Write(0x21, 4, 1); m.Pic.Write(0xa1, 2, 1);
        m.Pic.Write(0x21, 1, 1); m.Pic.Write(0xa1, 1, 1);
        m.Pic.Write(0x21, 0xfe, 1); m.Pic.Write(0xa1, 0xff, 1);
        m.Memory.WriteWord(0x80, 0x8000); m.Memory.WriteWord(0x82, 0);
        new byte[] { 0x50, 0xff, 6, 0, 0x70, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf }.CopyTo(m.Memory.Ram, 0x8000);
        m.Cpu.Eflags |= X86Cpu.InterruptEnable;
    }

    /// <summary>Pulses the actual board IRQ0 source while retaining the selected bridge-to-PIC wiring.</summary>
    private static void Pulse(PcMachine m)
    {
        if (m.PiixIrq is { } bridge) { bridge.SetIsaLine(0, true); bridge.SetIsaLine(0, false); }
        else { m.Pic.SetLine(0, true); m.Pic.SetLine(0, false); }
    }

    /// <summary>Registers boundary cases whose names distinguish profile, deadline, caller budget and true future effects.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int hz in new[] { 33_000_000, 90_000_000, 100_000_000, 133_000_000 })
        {
            foreach (int maximum in new[] { 0, 1, 2, 3, 7, 31, 4096, 4097 })
                test($"IntegerBatch PC complete exact caller budget hz={hz} maximum={maximum}", () =>
                {
                    using var a = Machine(hz); using var b = Machine(hz);
                    foreach (var m in new[] { a, b }) m.Scheduler.AdvanceCycles(1, (uint)hz);
                    ulong start = a.Scheduler.Now, remainder = a.CaptureHardwareState().Scheduler.Remainder; long retired = a.Cpu.Instructions;
                    Check(a.RunSteps(maximum, null, false) == maximum && b.RunSteps(maximum, null, true) == maximum);
                    Increments(a, maximum, start, remainder, retired); Increments(b, maximum, start, remainder, retired); Same(a, b);
                    Check(a.CaptureHardwareState().Scheduler.CachedCycles == (maximum == 0 ? 1u : 4u), "Final scalar conversion cache was not retained.");
                });

            foreach (ulong delay in new ulong[] { 0, 1, 30, 31, 39, 40, 79, 80, 121, 999, 10_001 })
                test($"IntegerBatch PC complete absolute deadline hz={hz} delay={delay}", () =>
                {
                    using var a = Machine(hz); using var b = Machine(hz);
                    foreach (var m in new[] { a, b }) m.Scheduler.AdvanceCycles(1, (uint)hz);
                    ulong start = a.Scheduler.Now, remainder = a.CaptureHardwareState().Scheduler.Remainder; long retired = a.Cpu.Instructions;
                    int expected = DeadlineCount(delay, (uint)hz, remainder);
                    Check(expected < 1024 && a.RunSteps(1024, start + delay, false) == expected && b.RunSteps(1024, start + delay, true) == expected,
                        "Deadline did not stop after the independently expected scalar element.");
                    Increments(a, expected, start, remainder, retired); Increments(b, expected, start, remainder, retired); Same(a, b);
                });

            test($"IntegerBatch PC clock overflow retains the first overflowing scalar instruction hz={hz}", () =>
            {
                using var a = Machine(hz); using var b = Machine(hz);
                ulong remainder = a.CaptureHardwareState().Scheduler.Remainder;
                long retired = a.Cpu.Instructions;
                // These constructor-only devices have no timer work; do not dispatch a long guest timeline.
                foreach (var m in new[] { a, b })
                {
                    Check(m.Scheduler.NextEventTime is null, "Overflow fixture unexpectedly contains a live device deadline.");
                    m.Scheduler.AdvanceTo(ulong.MaxValue - Delta(2, (uint)hz, remainder));
                }
                foreach (var (m, batched) in new[] { (a, false), (b, true) })
                {
                    bool overflow = false;
                    try { m.RunSteps(16, null, batched); }
                    catch (OverflowException) { overflow = true; }
                    Check(overflow && m.Scheduler.Now == ulong.MaxValue && m.Cpu.Instructions == retired + 3 && m.Cpu.Eip == 0x1004,
                        "Clock overflow did not retain exactly the third completed CPU instruction.");
                    Check(m.Cpu.GetRegister(Register.Ax) == ((Seed & 0xffff0000) | ((Seed + 3u) & 0xffff)) && m.Fault is null,
                        "Overflow skipped or added a register effect or changed the existing exception policy.");
                }
                Same(a, b);
            });

            test($"IntegerBatch PC actual IRQ on last element preserves pending PIC and original IRET frame hz={hz}", () =>
            {
                using var a = Machine(hz); using var b = Machine(hz);
                var acknowledgements = new List<string>[] { [], [] };
                ulong start = a.Scheduler.Now, remainder = a.CaptureHardwareState().Scheduler.Remainder;
                ulong delay = Delta(3, (uint)hz, remainder); long retired = a.Cpu.Instructions;
                foreach (var (m, index) in new[] { (a, 0), (b, 1) })
                {
                    Irq(m);
                    m.Pic.InterruptAcknowledged += (irq, vector, spurious) =>
                        acknowledgements[index].Add($"{irq}:{vector}:{spurious}:{m.Scheduler.Now}:{m.Cpu.Eip}");
                    m.Scheduler.ScheduleAfter(delay, () => Pulse(m));
                }
                Check(a.RunSteps(3, null, false) == 3 && b.RunSteps(3, null, true) == 3);
                Increments(a, 3, start, remainder, retired); Increments(b, 3, start, remainder, retired);
                Check(a.Pic.HasPending && b.Pic.HasPending && acknowledgements.All(x => x.Count == 0), "IRQ was delivered inside the completed element or lost.");
                uint returnFlags = a.Cpu.Eflags, value = a.Cpu.GetRegister(Register.Ax); Same(a, b);
                Check(a.RunSteps(6, null, false) == 6 && b.RunSteps(6, null, true) == 6);
                Check(acknowledgements[0].SequenceEqual(acknowledgements[1]) && acknowledgements[0].Count == 1,
                    "Actual physical PIC acknowledgements differ.");
                Check(acknowledgements[0][0] == $"0:32:False:{start + delay}:4100", "IRQ did not acknowledge at the original completed third instruction.");
                foreach (var m in new[] { a, b })
                {
                    Check(m.Cpu.Eip == 0x1004 && m.Cpu.GetRegister(Register.Sp) == 0x9000 && m.Cpu.GetRegister(Register.Ax) == value && m.Cpu.Eflags == returnFlags);
                    Check(m.Memory.ReadWord(0x7000) == 1 && m.Memory.ReadWord(0x8ffa) == 0x1004 && m.Memory.ReadWord(0x8ffc) == 0 && m.Memory.ReadWord(0x8ffe) == (ushort)returnFlags,
                        "Original IRQ frame, actual handler count or IRET result differs.");
                    m.Pic.Write(0x20, 0x0b, 1); Check(m.Pic.Read(0x20, 1) == 0 && !m.Pic.HasPending, "Actual handler EOI did not clear IRQ0 service.");
                }
                Same(a, b);
            });

            test($"IntegerBatch PC port92 reset is consumed before the next guest instruction hz={hz}", () =>
            {
                using var a = Machine(hz); using var b = Machine(hz);
                var observed = new List<string>[] { [], [] };
                ulong start = a.Scheduler.Now, remainder = a.CaptureHardwareState().Scheduler.Remainder;
                ulong delay = Delta(3, (uint)hz, remainder);
                foreach (var (m, index) in new[] { (a, 0), (b, 1) })
                    m.Scheduler.ScheduleAfter(delay, () => { observed[index].Add(Observation(m)); m.Ports.Write(0x92, 3, 1); });
                Check(a.RunSteps(4, null, false) == 4 && b.RunSteps(4, null, true) == 4);
                Check(observed[0].SequenceEqual(observed[1]) && observed[0].Count == 1, "Reset callback observed another CPU boundary.");
                foreach (var m in new[] { a, b })
                    Check(m.HardwareResetCount == 1 && m.LastResetSource == "port92" && m.LastResetTime == start + delay && m.Cpu.Instructions == 1 && m.Fault is null,
                        "The pending reset did not precede exactly one real reset-vector instruction.");
                Same(a, b);
            });

            test($"IntegerBatch PC real OUT and IN remain ordered scalar effects hz={hz}", () =>
            {
                using var a = Machine(hz); using var b = Machine(hz);
                foreach (var m in new[] { a, b })
                {
                    m.Cpu.SetRegister(Register.Ax, 0);
                    new byte[] { 0x40, 0x40, 0xb0, 0x5a, 0xe6, 0xe9, 0x40, 0xe4, 0x92, 0xf4 }.CopyTo(m.Memory.Ram, 0x1001);
                }
                Check(a.RunSteps(32, null, false) == 7 && b.RunSteps(32, null, true) == 7);
                foreach (var m in new[] { a, b })
                    Check(m.Cpu.Halted && m.Cpu.Eip == 0x100b && m.Cpu.GetRegister(Register.Ax) == 2 && m.Board.DebugText == "Z" && m.Fault is null,
                        "The original OUT E9, IN92 result or HLT boundary was skipped.");
                Same(a, b);
            });

            test($"IntegerBatch PC strict SnapshotStore future retains complete phase and state hz={hz}", () =>
            {
                using var fixture = new SnapshotTests.Fixture(Profile(hz), start: false); var original = fixture.Machine; Prepare(original);
                Check(original.RunSteps(511, null, true) == 511);
                using var restored = fixture.Restore(); Same(original, restored);
                ulong start = original.Scheduler.Now, remainder = original.CaptureHardwareState().Scheduler.Remainder;
                long retired = original.Cpu.Instructions; int later = 0;
                foreach (int count in new[] { 1, 2, 7, 31, 257, 1024 })
                {
                    Check(original.RunSteps(count, null, false) == count && restored.RunSteps(count, null, true) == count);
                    later += count; Same(original, restored);
                    Check(original.Cpu.GetRegister(Register.Ax) == ((Seed & 0xffff0000) | ((Seed + 511u + (uint)later) & 0xffff)), "Restored guest did not execute all known increments.");
                    Check(original.Cpu.Instructions == retired + later && original.Scheduler.Now == start + Delta(later, (uint)hz, remainder), "Restored future lost retirement or fractional clocks.");
                }
            });
        }

        test("IntegerBatch PC nested same-time callbacks and later code mutation observe exact completed CPU records", () =>
        {
            using var a = Machine(100_000_000); using var b = Machine(100_000_000);
            var observations = new List<string>[] { [], [] }; var counts = new List<long>[] { [], [] };
            ulong start = a.Scheduler.Now; long retired = a.Cpu.Instructions;
            foreach (var (m, index) in new[] { (a, 0), (b, 1) })
                m.Scheduler.ScheduleAfter(120, () =>
                {
                    observations[index].Add(Observation(m)); counts[index].Add(m.Cpu.Instructions - retired);
                    m.Memory.WriteByte(0x1004, 0x48);
                    m.Scheduler.ScheduleAfter(0, () => { observations[index].Add(Observation(m)); counts[index].Add(m.Cpu.Instructions - retired); });
                    m.Scheduler.ScheduleAfter(60, () =>
                    {
                        observations[index].Add(Observation(m)); counts[index].Add(m.Cpu.Instructions - retired);
                        m.Memory.WriteByte(0x3f000, 0xa6);
                    });
                });
            Check(a.RunSteps(16, null, false) == 16 && b.RunSteps(16, null, true) == 16);
            Check(observations[0].SequenceEqual(observations[1]) && counts[0].SequenceEqual(new long[] { 3, 3, 5 }),
                "Same-time ordering, late event CPU record or exact callback boundary differs.");
            foreach (var m in new[] { a, b })
                Check(m.Scheduler.Now == start + 640 && m.Cpu.GetRegister(Register.Ax) == ((Seed & 0xffff0000) | ((Seed + 14u) & 0xffff)) && m.Memory.Ram[0x3f000] == 0xa6,
                    "Code mutation or future publication did not affect the subsequent real instruction stream.");
            Same(a, b);
        });

        foreach (bool write in new[] { false, true })
            test($"IntegerBatch PC actual paged memory fallback retains PF frame and next handler instruction write={write}", () =>
            {
                byte[] code = [0x90, 0x40, 0x41, (byte)(write ? 0x89 : 0x8b), 5, 0, 0, 3, 0, 0xf4];
                using var a = PagedMachine(code); using var b = PagedMachine(code);
                foreach (var m in new[] { a, b })
                {
                    m.Memory.WriteWord(0x330, 0x7ff); m.Memory.WriteDword(0x332, 0x8000);
                    ProtectedModeTests.Gate(m.Memory, 14); m.Memory.Ram[0x6000] = 0xf4;
                    new byte[] { 0x0f, 1, 0x1d, 0x30, 3, 0, 0 }.CopyTo(m.Memory.Ram, 0x2200);
                    m.Cpu.Eip = 0x2200; Check(m.RunSteps(1, null, false) == 1);
                    PagingTests.Pte(m.Memory, 0x30000, 0, 0);
                    m.Cpu.Eip = 0x2000; m.Cpu.Eflags = 2; Check(m.RunSteps(1, null, false) == 1);
                    m.Cpu.SetRegister(Register.Ax, Seed); m.Cpu.SetRegister(Register.Cx, 0x5678);
                }
                uint stack = a.Cpu.GetRegister(Register.Sp); long retired = a.Cpu.Instructions;
                Check(a.RunSteps(3, null, false) == 3 && b.RunSteps(3, null, true) == 3);
                foreach (var m in new[] { a, b })
                {
                    Check(m.Cpu.PageFaults == 1 && m.Cpu.Cr2 == 0x30000 && m.Cpu.Eip == 0x6000 && !m.Cpu.Halted && !m.Cpu.TripleFaulted,
                        "The memory fallback did not enter the actual PF handler.");
                    Check(m.Cpu.Instructions == retired + 3 && m.Cpu.GetRegister(Register.Ax) == Seed + 1 && m.Cpu.GetRegister(Register.Cx) == 0x5679);
                    Check(m.Cpu.GetRegister(Register.Sp) == stack - 16 && m.Memory.ReadDword(stack - 16) == (write ? 2u : 0u) &&
                        m.Memory.ReadDword(stack - 12) == 0x2003 && m.Memory.ReadDword(stack - 8) == 8 &&
                        (m.Memory.ReadDword(stack - 4) & 0x10000) != 0, "Original error/IP/CS/RF frame differs.");
                }
                Same(a, b);
                Check(a.RunSteps(1, null, false) == 1 && b.RunSteps(1, null, true) == 1 && a.Cpu.Halted && b.Cpu.Halted);
                Same(a, b);
            });
    }
}
