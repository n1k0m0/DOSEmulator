// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Complete-state controls for stateless integer execution and its ordinary-Step fallbacks.</summary>
internal static class IntegerBatchTests
{
    /// <summary>Private CPU and RAM, never host endpoints or original guest media.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory);
    /// <summary>Requires an independently observed count, guard, future effect or complete-state equality.</summary>
    private static void Check(bool ok, string reason = "Integer batch invariant")
    { if (!ok) throw new Exception(reason); }

    /// <summary>Builds real, PE16/32, paged PE16/32 or genuine IRET-entered V86 fixtures and warms code through a real NOP.</summary>
    private static Rig Fixture(int mode, byte[] code, bool warm = true)
    {
        X86Cpu cpu; MemoryBus memory;
        bool paging = mode is 3 or 4 or 6;
        var clock = new DeviceScheduler();
        if (mode == 0)
        {
            memory = new(2 * 1024 * 1024);
            cpu = new(memory, new(), X86CpuModel.IntelP54cB5Subset, clock, 100000000);
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0);
            cpu.Eip = 0x2000; memory.Ram[0x2000] = 0x90; code.CopyTo(memory.Ram, 0x2001);
        }
        else
        {
            var pair = paging
                ? PagingTests.Create([], model: X86CpuModel.IntelP54cB5Subset, timestampSource: clock, timestampFrequency: 100000000)
                : ProtectedModeTests.Rig([], code32: mode != 1, model: X86CpuModel.IntelP54cB5Subset,
                    timestampSource: clock, timestampFrequency: 100000000);
            cpu = pair.Cpu; memory = pair.Memory;
            if (mode is 5 or 6)
            {
                PagingTests.Tss(cpu, memory);
                uint stack = cpu.GetRegister(Register.Sp);
                uint[] frame = [0x100, 0x3000, 0x232d7, 0xa5a58000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
                for (int i = 0; i < frame.Length; i++) memory.WriteDword(stack + (uint)i * 4, frame[i]);
                memory.Ram[0x30100] = 0x90; code.CopyTo(memory.Ram, 0x30101);
                PagingTests.At(cpu, memory, [0xcf], 0x2800);
                Check(cpu.Virtual8086 && cpu.Eip == 0x100, "Fixture never entered real V86.");
            }
            else
            {
                memory.Ram[0x2000] = 0x90; code.CopyTo(memory.Ram, 0x2001);
                if (mode == 3) PagingTests.At(cpu, memory, [0xea, 0, 0x20, 0, 0, 0x18, 0], 0x2800);
                else cpu.Eip = 0x2000;
            }
        }
        cpu.Eflags = cpu.Virtual8086 ? 0x232d7u : 0x2d7u;
        if (warm) { Check(cpu.Step() == 4 && !cpu.TripleFaulted, "Warm NOP failed."); }
        else cpu.Eip++;
        for (int i = 0; i < 8; i++) cpu.SetRegister((Register)i, 0x8bad1230u + (uint)i * 0x1010101u);
        cpu.SetRegister(Register.Sp, mode is 5 or 6 ? 0xa5a58000u : mode == 0 ? 0xa5a59000u : 0x18000u);
        return new(cpu, memory);
    }

    /// <summary>Compares every generated state member including scratch, TLB entries/ages/counters and full RAM, without exclusions.</summary>
    private static void Equal(Rig scalar, Rig batched)
    {
        StateBlock left = scalar.Cpu.SaveState(), right = batched.Cpu.SaveState();
        Check(left.Type == right.Type && left.Version == right.Version && left.Fields.Keys.SequenceEqual(right.Fields.Keys), "State shape differs.");
        foreach (string name in left.Fields.Keys)
            Check(left.Fields[name].GetRawText() == right.Fields[name].GetRawText(), "Complete CPU field differs: " + name);
        Check(scalar.Memory.Ram.AsSpan().SequenceEqual(batched.Memory.Ram), "Complete RAM/PTE bytes differ.");
    }

    /// <summary>Executes exactly the returned number of ordinary steps, comparing their entire state to the prototype.</summary>
    private static int Batch(Rig scalar, Rig batched, int budget)
    {
        int count = batched.Cpu.TryRunIntegerBatch(budget);
        Check(count >= 0 && count <= Math.Max(budget, 0), "Returned count exceeds the caller budget.");
        for (int i = 0; i < count; i++) Check(scalar.Cpu.Step() == 4, "Admitted scalar element did not cost four clocks.");
        Equal(scalar, batched);
        return count;
    }

    /// <summary>Builds independent varied operands spanning all admitted families and byte aliases, ending before HLT.</summary>
    private static byte[] Program(bool code32)
    {
        var code = new List<byte>();
        int width = code32 ? 4 : 2;
        // Appends one literal low-endian operand, independent from the implementation's decoder.
        void Immediate(uint value, int size) { for (int i = 0; i < size; i++) code.Add((byte)(value >> (i * 8))); }
        for (int r = 0; r < 8; r++) { code.Add((byte)(0xb0 + r)); code.Add((byte)(0x91 + r * 13)); }
        for (int r = 0; r < 8; r++) { code.Add((byte)(0xb8 + r)); Immediate(0xfedc9081u + (uint)r * 0x12345u, width); }
        for (int operation = 0; operation < 8; operation++)
        {
            for (int form = 0; form < 4; form++)
            {
                code.Add((byte)(operation * 8 + form));
                code.Add((byte)(0xc0 | ((operation + form) & 7) << 3 | ((operation * 3 + form + 1) & 7)));
            }
            code.Add((byte)(operation * 8 + 4)); Immediate((uint)(0x81 + operation * 11), 1);
            code.Add((byte)(operation * 8 + 5)); Immediate(0x81234567u + (uint)operation * 0x11111111u, width);
            foreach (byte opcode in new byte[] { 0x80, 0x81, 0x83 })
            {
                code.Add(opcode); code.Add((byte)(0xc0 | operation << 3 | ((operation + 4) & 7)));
                Immediate(opcode == 0x83 ? 0xf1u : 0x80abcdf1u, opcode == 0x81 ? width : 1);
            }
        }
        for (int r = 0; r < 8; r++) foreach (byte opcode in new byte[] { 0x88, 0x89, 0x8a, 0x8b })
        { code.Add(opcode); code.Add((byte)(0xc0 | r << 3 | ((r + 3) & 7))); }
        for (int r = 0; r < 8; r++) foreach (byte opcode in new byte[] { 0x84, 0x85, 0x86, 0x87 })
        { code.Add(opcode); code.Add((byte)(0xc0 | r << 3 | ((r + 5) & 7))); }
        for (int r = 1; r < 8; r++) code.Add((byte)(0x90 + r));
        code.Add(0xa8); Immediate(0xa5, 1); code.Add(0xa9); Immediate(0x812345a5, width);
        foreach (byte opcode in new byte[] { 0x98, 0x99, 0x9e, 0x9f, 0xf5, 0xf8, 0xf9, 0xfc, 0xfd }) code.Add(opcode);
        for (int r = 0; r < 8; r++)
        {
            code.Add(0xc6); code.Add((byte)(0xc0 | r)); Immediate((uint)(0xa7 + r), 1);
            code.Add(0xc7); code.Add((byte)(0xc0 | r)); Immediate(0xa7654321u + (uint)r, width);
            foreach (byte opcode in new byte[] { 0xb6, 0xb7, 0xbe, 0xbf })
            { code.Add(0x0f); code.Add(opcode); code.Add((byte)(0xc0 | r << 3 | ((r + 1) & 7))); }
            code.Add(0x0f); code.Add((byte)(0xc8 + r));
        }
        for (int condition = 0; condition < 16; condition++)
        { code.Add(0x0f); code.Add((byte)(0x90 + condition)); code.Add((byte)(0xc0 | (condition & 7))); }
        code.AddRange(code32
            ? [0x8d,0x04,0x8d,0x78,0x56,0x34,0x12, 0x8d,0x54,0x24,0xfe,
               0x8d,0x0d,0x78,0x56,0x34,0x12, 0x8d,0xac,0xf3,0x01,0x80,0,0]
            : [0x8d,0x00, 0x8d,0x56,0xfe, 0x8d,0x0e,0x45,0x23, 0x8d,0xa9,0x01,0x80]);
        for (int r = 0; r < 16; r++) code.Add((byte)(0x40 + r));
        code.Add(0x90); code.Add(0xf4);
        return code.ToArray();
    }

    /// <summary>A mapped code/data peer retains actual reads; admission is forbidden to touch it.</summary>
    private sealed class MappedCode : IMemoryDevice
    {
        /// <summary>Real mapped reads only, never speculative eligibility reads.</summary>
        public int Reads;
        /// <summary>Real mapped writes, which these register-only scenarios must not invent.</summary>
        public int Writes;
        /// <summary>Returns an ordinary NOP when the real byte bus eventually performs a fetch.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x90; }
        /// <summary>Records an actual device write.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    /// <summary>Own high fallback decoder, proving the batch never resolves or reads dynamic MMIO.</summary>
    private sealed class DynamicCode : IMemoryDecoder
    {
        /// <summary>Actual decode calls recorded separately from byte reads.</summary>
        public int Queries;
        /// <summary>Actual device fetches reached only by normal Step.</summary>
        public int Reads;
        /// <summary>Recognizes only the own high page outside all installed RAM.</summary>
        public bool Decodes(uint address) { Queries++; return address is >= 0x400000 and < 0x401000; }
        /// <summary>Returns the own NOP without hidden or host effects.</summary>
        public byte ReadDecodedByte(uint address) { Reads++; return 0x90; }
        /// <summary>Rejects an unrelated device write instead of silently accepting it.</summary>
        public void WriteDecodedByte(uint address, byte value) => throw new Exception("Unexpected dynamic write.");
    }

    /// <summary>Changes only explicit private fixture scratch fields using the same full generated state shape.</summary>
    private static void Field<T>(X86Cpu cpu, string name, T value)
    {
        StateBlock state = cpu.SaveState();
        state.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json);
        cpu.LoadState(state);
    }

    /// <summary>Changes only the fixture's already loaded code limit, retaining its selector/base/access/default width.</summary>
    private static void CodeLimit(Rig rig, uint limit)
    {
        SegmentDescriptorCache[] caches = rig.Cpu.SaveState().Fields["_segmentCaches"].Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        caches[(int)Segment.Cs] = caches[(int)Segment.Cs] with { Limit = limit };
        Field(rig.Cpu, "_segmentCaches", caches);
    }

    /// <summary>Independent boolean specification of the sixteen ordinary arithmetic branch conditions.</summary>
    private static bool BranchCondition(int condition, uint flags)
    {
        bool carry = (flags & 1) != 0, parity = (flags & 4) != 0, zero = (flags & 64) != 0;
        bool sign = (flags & 128) != 0, overflow = (flags & 2048) != 0;
        bool[] positive = [overflow, carry, zero, carry || zero, sign, parity, sign != overflow, zero || sign != overflow];
        return positive[condition / 2] != ((condition & 1) != 0);
    }

    /// <summary>Places the actual warmed code translation in one selected way without changing any entry value.</summary>
    private static int CodeWay(Rig rig, int way)
    {
        if (!rig.Cpu.PagingEnabled) return -1;
        uint page = rig.Cpu.LinearInstructionAddress >> 12;
        StateBlock state = rig.Cpu.SaveState();
        JsonElement[] entries = state.Fields["_tlb"].EnumerateArray().ToArray();
        int source = Array.FindIndex(entries, row => row.GetProperty("valid").GetBoolean() &&
            row.GetProperty("page").GetUInt32() == page);
        Check(source >= 0, "Fixture code page is not actually cached.");
        int target = (int)(page & 7) * 4 + way;
        (entries[source], entries[target]) = (entries[target], entries[source]);
        state.Fields["_tlb"] = JsonSerializer.SerializeToElement(entries, StateValues.Json); rig.Cpu.LoadState(state);
        return target;
    }

    /// <summary>Lists independent ordinary-fetch counts, including wide immediates that share exactly one translation.</summary>
    private static (byte[] Code, int Touches)[] DirectPrograms(bool wide)
    {
        byte[] immediate = wide ? [0x78,0x56,0x34,0x92] : [0x78,0x92];
        return
        [
            ([0x90], 1), ([0x40], 1), ([0x4c], 1), ([0x91], 1),
            ([0xb4,0x81], 2), ([0xb8,..immediate], 2),
            ([0x88,0xe0], 2), ([0x8b,0xe4], 2), ([0x00,0xe0], 2), ([0x31,0xc0], 2),
            ([0x04,0x81], 2), ([0x05,..immediate], 2),
            ([0x80,0xe4,0x7f], 3), ([0x81,0xed,..immediate], 3), ([0x83,0xd4,0xff], 3),
            ([0x84,0xe0], 2), ([0x85,0xe4], 2), ([0x86,0xe0], 2), ([0x87,0xe4], 2),
            ([0xa8,0x81], 2), ([0xa9,..immediate], 2)
        ];
    }

    /// <summary>Registers full-state positives and exact no-effect admission/future-fault negatives.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("integer decoder and scalar immediate reads do not announce RAM writes", () =>
        {
            var batch = Fixture(0, [0x40, 0x48, 0xf4]);
            int batchWrites = 0;
            batch.Memory.PhysicalRamWrite += (_, _) => batchWrites++;
            Check(batch.Cpu.TryRunIntegerBatch(2) == 2 && batchWrites == 0,
                "Register-only batch invalidated native RAM translations.");
            var scalar = Fixture(0, [0xb8, 0x34, 0x12, 0xf4]);
            int scalarWrites = 0;
            scalar.Memory.PhysicalRamWrite += (_, _) => scalarWrites++;
            Check(scalar.Cpu.Step() == 4 && (scalar.Cpu.GetRegister(Register.Ax) & 0xffff) == 0x1234 && scalarWrites == 0,
                "Immediate instruction fetch invalidated native RAM translations.");
        });
        for (int value = 0; value < 7; value++)
        foreach (int size in new[] { -1, 0, 1, 2, 3, 7, 31, 1000 })
        {
            int mode = value, budget = size;
            test($"IntegerBatch complete CPU/RAM mode{mode} budget{budget}", () =>
            {
                byte[] code = Program(mode is 2 or 4);
                var a = Fixture(mode, code); var b = Fixture(mode, code);
                Equal(a, b); int count = Batch(a, b, budget);
                Check(budget < 2 ? count == 0 : count > 0, "Positive code was not exercised.");
            });
        }
        for (int value = 0; value < 7; value++)
        {
            int mode = value;
            test($"IntegerBatch changing budgets preserve every last-step field mode{mode}", () =>
            {
                byte[] code = Program(mode is 2 or 4);
                var a = Fixture(mode, code); var b = Fixture(mode, code);
                int total = 0, round = 0;
                int[] budgets = [2, 3, 11, 5, 37];
                while (!b.Cpu.Halted && round < 1000)
                {
                    int count = Batch(a, b, budgets[round++ % budgets.Length]); total += count;
                    if (count == 0) { a.Cpu.Step(); b.Cpu.Step(); Equal(a, b); }
                }
                Check(total > 100 && b.Cpu.Halted, "Mixed-budget register block did not finish through ordinary HLT.");
            });
        }
        byte[][] rejected = [[0x66,0x90], [0x67,0x90], [0xf0,0x90], [0xf3,0x90], [0x2e,0x90],
            [0x8b,0x00], [0x01,0x00], [0x83,0x00,1], [0x50], [0x58], [0xfa], [0xfb], [0xcf],
            [0x8d,0xc0], [0xe8,0,0,0,0], [0xe4,0x60], [0xe6,0x60], [0x63,0xc0], [0x0f,0xa2],
            [0x9b], [0xd9,0xe8], [0xa4], [0xf3,0xa4], [0xf4], [0xc7,0xc8,1,2,3,4]];
        foreach (byte[] code in rejected)
            test("IntegerBatch rejects without fetch " + Convert.ToHexString(code), () =>
            {
                var a = Fixture(4, code); var b = Fixture(4, code);
                Check(Batch(a, b, 128) == 0, "Unsupported code admitted.");
            });
        foreach (uint flag in new[] { X86Cpu.Trap, 0x10000u })
            test($"IntegerBatch does not erase pending flag {flag:X}", () =>
            {
                var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
                a.Cpu.Eflags |= flag; b.Cpu.Eflags |= flag; Check(Batch(a, b, 2) == 0);
            });
        foreach (string name in new[] { "_waitingForNumericError", "_repValid", "_debugDataActive", "_debugStepShadowed",
            "_debugInterruptTransfer", "_debugDataTriggered", "_debugDeferredTrigger", "_debugDeferredData", "_checkpointProtected" })
            test("IntegerBatch preserves pending private boundary " + name, () =>
            {
                var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
                Field(a.Cpu, name, true); Field(b.Cpu, name, true); Check(Batch(a, b, 2) == 0);
            });
        foreach (string name in new[] { "_interruptShadow", "_debugShadow" })
            test("IntegerBatch preserves actual shadow guard " + name, () =>
            {
                var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
                Field(a.Cpu, name, 1); Field(b.Cpu, name, 1); Check(Batch(a, b, 2) == 0);
            });
        foreach (string name in new[] { "_debugDataMatches", "_debugDeferredStatus", "Dr7" })
            test("IntegerBatch preserves enabled/deferred debug " + name, () =>
            {
                var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
                uint value = name == "Dr7" ? 0x401u : 1u;
                Field(a.Cpu, name, value); Field(b.Cpu, name, value); Check(Batch(a, b, 2) == 0);
            });
        foreach (bool interruptObserver in new[] { false, true })
            test($"IntegerBatch keeps observer boundary interrupt={interruptObserver}", () =>
            {
                var a = Fixture(4, [0x90,0x90]); var b = Fixture(4, [0x90,0x90]);
                if (interruptObserver) { a.Cpu.InterruptEntered += _ => { }; b.Cpu.InterruptEntered += _ => { }; }
                else { a.Cpu.InstructionStarting += (_,_) => { }; b.Cpu.InstructionStarting += (_,_) => { }; }
                Check(Batch(a, b, 2) == 0);
            });
        test("IntegerBatch completes before an unbatched memory IMUL and leaves its future Step unchanged", () =>
        {
            byte[] code = [0x40,0x90,0x69,0x05,0,0,3,0,1,0,0,0,0xf4];
            var a = Fixture(2, code); var b = Fixture(2, code);
            a.Memory.WriteDword(0x30000, 0xdecafbad); b.Memory.WriteDword(0x30000, 0xdecafbad);
            Check(Batch(a, b, 32) == 2 && b.Cpu.Eip == 0x2003);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(b.Cpu.GetRegister(Register.Ax) == 0xdecafbad, "Ordinary memory MOV was skipped.");
        });
        test("IntegerBatch returns zero on cold TLB without filling or setting page bits", () =>
        {
            var a = Fixture(4, [0x90]); var b = Fixture(4, [0x90]);
            foreach (Rig r in new[] { a, b }) { r.Memory.Ram.AsSpan(0x3000, 4).Fill(0x90); r.Cpu.Eip = 0x3000; }
            Check(Batch(a, b, 3) == 0);
            Check((b.Memory.ReadDword(0x1d00c) & 0x60) == 0, "Admission changed a cold PTE.");
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(Batch(a, b, 2) == 2, "The actual warmed page was not admitted.");
        });
        test("IntegerBatch page-end admission leaves future ModRM PF and its real frame unchanged", () =>
        {
            var a = Fixture(4, [0x90]); var b = Fixture(4, [0x90]);
            foreach (Rig r in new[] { a, b })
            { r.Cpu.Eip = 0x2fff; r.Memory.Ram[0x2fff] = 0x8b; PagingTests.Pte(r.Memory, 0x3000, 0, 0); }
            Check(Batch(a, b, 8) == 0);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000 && b.Cpu.Eip == 0x6000 && !b.Cpu.TripleFaulted,
                "Future normal ModRM fetch fault was not delivered.");
        });
        test("IntegerBatch exact last scratch is retained when following immediate crosses the page", () =>
        {
            var a = Fixture(4, [0x90]); var b = Fixture(4, [0x90]);
            foreach (Rig r in new[] { a, b })
            { r.Cpu.Eip = 0x2ffe; r.Memory.Ram[0x2ffe] = 0x90; r.Memory.Ram[0x2fff] = 0xb8; }
            Check(Batch(a, b, 8) == 1 && b.Cpu.Eip == 0x2fff);
            Check(b.Cpu.SaveState().Fields["_instructionBytes"].GetInt32() == 1,
                "Rejected future immediate overwrote the accepted element's record.");
        });
        test("IntegerBatch stops at the code page even when following plain RAM exists", () =>
        {
            var a = Fixture(2, [0x90]); var b = Fixture(2, [0x90]);
            foreach (Rig r in new[] { a, b }) { r.Cpu.Eip = 0x2ffe; r.Memory.Ram.AsSpan(0x2ffe, 8).Fill(0x90); }
            Check(Batch(a, b, 8) == 2 && b.Cpu.Eip == 0x3000);
        });
        foreach (bool longImmediate in new[] { false, true })
            test($"IntegerBatch does not cross actual CS16 limit immediate={longImmediate}", () =>
            {
                var a = Fixture(1, [0x90]); var b = Fixture(1, [0x90]);
                foreach (Rig r in new[] { a, b }) { r.Cpu.Eip = 0xffff; r.Memory.Ram[0xffff] = longImmediate ? (byte)0xb8 : (byte)0x90; }
                Check(Batch(a, b, 8) == (longImmediate ? 0 : 1));
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                Check(b.Cpu.Eip == 0x6000 && !b.Cpu.TripleFaulted, "Ordinary future CS-limit fault was lost.");
            });
        test("IntegerBatch honors actual A20 folding while keeping unfolded first-fetch diagnostics", () =>
        {
            var a = Fixture(2, [0x90,0x40,0xf4]); var b = Fixture(2, [0x90,0x40,0xf4]);
            foreach (Rig r in new[] { a, b }) { r.Memory.A20Enabled = false; r.Cpu.Eip = 0x102001; }
            Check(Batch(a, b, 8) == 2 && b.Cpu.Eip == 0x102003);
            Check(b.Cpu.SaveState().Fields["_firstInstructionPhysical"].GetUInt32() == 0x102002);
        });
        foreach (bool hole in new[] { false, true })
            test($"IntegerBatch honors current DRAM decode/hole hole={hole}", () =>
            {
                var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
                foreach (Rig r in new[] { a, b })
                    if (hole) r.Memory.SetRamHole(0x2000, 4096); else r.Memory.SetDecodedRamBytes(0x2000);
                Check(Batch(a, b, 8) == 0);
            });
        test("IntegerBatch reclassifies a newly mapped partial code page without a device peek", () =>
        {
            var a = Fixture(4, [0x90,0x90]); var b = Fixture(4, [0x90,0x90]);
            var da = new MappedCode(); var db = new MappedCode();
            a.Memory.Map(0x2100, 1, da, "new own partial device");
            b.Memory.Map(0x2100, 1, db, "new own partial device");
            Check(Batch(a, b, 8) == 0 && da.Reads == 0 && db.Reads == 0 && da.Writes == 0 && db.Writes == 0);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(da.Reads == 0 && db.Reads == 0, "Nondevice byte called the partial peer.");
        });
        test("IntegerBatch refuses mapped code and lets the next normal Step perform the actual read", () =>
        {
            var a = Fixture(2, [0x90,0x90]); var b = Fixture(2, [0x90,0x90]);
            var da = new MappedCode(); var db = new MappedCode();
            a.Memory.Map(0x2001, 1, da, "own actual code device");
            b.Memory.Map(0x2001, 1, db, "own actual code device");
            Check(Batch(a, b, 8) == 0 && da.Reads == 0 && db.Reads == 0);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(da.Reads == 1 && db.Reads == 1 && da.Writes == 0 && db.Writes == 0);
        });
        test("IntegerBatch never asks the dynamic decoder to classify or peek high MMIO", () =>
        {
            var a = Fixture(2, [0x90]); var b = Fixture(2, [0x90]);
            var da = new DynamicCode(); var db = new DynamicCode();
            a.Memory.MapDynamic(da); b.Memory.MapDynamic(db); a.Cpu.Eip = b.Cpu.Eip = 0x400000;
            Check(Batch(a, b, 8) == 0 && da.Queries == 0 && db.Queries == 0 && da.Reads == 0 && db.Reads == 0);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            Check(da.Queries == 1 && db.Queries == 1 && da.Reads == 1 && db.Reads == 1);
        });
        test("IntegerBatch resets previous decoder/control scratch exactly like the next ordinary Step", () =>
        {
            var a = Fixture(4, [0x90,0x40,0xf4]); var b = Fixture(4, [0x90,0x40,0xf4]);
            foreach (Rig r in new[] { a, b })
            {
                Field(r.Cpu, "_controlCheckpointSaved", true); Field(r.Cpu, "_lockPrefix", true);
                Field(r.Cpu, "_lockedMemory", true); Field(r.Cpu, "_operand32", false);
                Field(r.Cpu, "_address32", false); Field(r.Cpu, "_override", (Segment?)Segment.Gs);
            }
            Check(Batch(a, b, 8) == 2);
        });
        test("IntegerBatch preserves an active REP continuation and its old diagnostic bytes", () =>
        {
            var a = Fixture(2, [0xf3,0xa4,0xf4]); var b = Fixture(2, [0xf3,0xa4,0xf4]);
            foreach (Rig r in new[] { a, b })
            {
                r.Cpu.SetRegister(Register.Cx, 3); r.Cpu.SetRegister(Register.Si, 0x30000); r.Cpu.SetRegister(Register.Di, 0x31000);
                r.Memory.WriteByte(0x30000, 0x91); r.Cpu.Step();
                Check(r.Cpu.HasRepContinuation, "Fixture did not execute/remember the real REP element.");
            }
            Check(Batch(a, b, 8) == 0);
        });
        test("IntegerBatch preserves the real HLT stop without accounting a new element", () =>
        {
            var a = Fixture(2, [0xf4]); var b = Fixture(2, [0xf4]); a.Cpu.Step(); b.Cpu.Step();
            Check(a.Cpu.Halted && b.Cpu.Halted && Batch(a, b, 8) == 0);
        });
        for (int value = 0; value < 7; value++)
        foreach (bool nearValue in new[] { false, true })
        {
            int mode = value; bool near = nearValue;
            test($"IntegerBatch every actual Jcc outcome and last-step record mode{mode} near={near}", () =>
            {
                bool wide = mode is 2 or 4;
                foreach (uint flags in new uint[] { 2, 3, 6, 0x42, 0x82, 0x802, 0x8d7 })
                for (int condition = 0; condition < 16; condition++)
                {
                    var code = new List<byte>();
                    if (near)
                    {
                        code.Add(0x0f); code.Add((byte)(0x80 + condition));
                        code.AddRange(wide ? [2,0,0,0] : [2,0]);
                    }
                    else code.AddRange([(byte)(0x70 + condition), 2]);
                    code.AddRange([0x40,0x90,0x48,0xf4]);
                    var a = Fixture(mode, code.ToArray()); var b = Fixture(mode, code.ToArray());
                    a.Cpu.Eflags = b.Cpu.Eflags = flags | (a.Cpu.Virtual8086 ? 0x23000u : 0u);
                    bool take = BranchCondition(condition, flags);
                    uint before = b.Cpu.GetRegister(Register.Ax);
                    Check(Batch(a, b, 16) == (take ? 2 : 4), "Jcc admission changed the actual taken/not-taken work.");
                    int width = wide ? 4 : 2;
                    uint mask = width == 4 ? uint.MaxValue : 65535;
                    uint expected = take ? (before & ~mask) | (unchecked(before - 1) & mask) : before;
                    Check(b.Cpu.GetRegister(Register.Ax) == expected, "Independent Jcc path/register result differs.");
                    a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                    Check(mode is 5 or 6 ? !b.Cpu.Virtual8086 && !b.Cpu.TripleFaulted : b.Cpu.Halted,
                        "Ordinary terminal HLT or its real V86 GP did not remain outside the integer batch.");
                }
            });
            test($"IntegerBatch backward branch obeys every finite caller budget mode{mode} near={near}", () =>
            {
                bool wide = mode is 2 or 4;
                byte[] code = near ? wide ? [0xe9,0xfb,0xff,0xff,0xff] : [0xe9,0xfd,0xff] : [0xeb,0xfe];
                var a = Fixture(mode, code); var b = Fixture(mode, code);
                uint start = b.Cpu.Eip;
                foreach (int budget in new[] { 2, 3, 31, 257 })
                    Check(Batch(a, b, budget) == budget && b.Cpu.Eip == start, "Backward JMP exceeded or shortened its exact budget.");
            });
        }
        foreach (bool nearValue in new[] { false, true })
        {
            bool near = nearValue;
            test($"IntegerBatch preserves a taken branch's target-page PF on the following ordinary fetch near={near}", () =>
            {
                byte[] code = near ? [0xe9,0xfa,0x0f,0,0] : [0xeb,0x7d];
                var a = Fixture(4, code); var b = Fixture(4, code);
                foreach (Rig rig in new[] { a, b })
                {
                    if (!near) { rig.Cpu.Eip = 0x2f81; code.CopyTo(rig.Memory.Ram, 0x2f81); }
                    PagingTests.Pte(rig.Memory, 0x3000, 0, 0);
                }
                Check(Batch(a, b, 16) == 1 && b.Cpu.Eip == 0x3000, "Branch did not end before target fetch.");
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000 && b.Cpu.Eip == 0x6000 && !b.Cpu.TripleFaulted,
                    "Future target PF/frame was changed or prefetched.");
            });
        }
        foreach (bool takeValue in new[] { false, true })
        {
            bool take = takeValue;
            test($"IntegerBatch Jcc checks only the taken target before admission take={take}", () =>
            {
                var a = Fixture(2, [0x74,0x7f,0x40,0xf4]); var b = Fixture(2, [0x74,0x7f,0x40,0xf4]);
                foreach (Rig rig in new[] { a, b }) { CodeLimit(rig, 0x2004); rig.Cpu.Eflags = take ? 0x42u : 2u; }
                Check(Batch(a, b, 16) == (take ? 0 : 2), "Potential target fault did not choose the original path.");
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                Check(take ? b.Cpu.Eip == 0x6000 && !b.Cpu.TripleFaulted : b.Cpu.Halted,
                    "Ordinary future GP/HLT was not preserved.");
            });
        }
        foreach (byte[] codeValue in new byte[][] { [0xeb], [0xe9,0,0], [0x0f,0x84,0,0], [0x8d,0x84,0x24] })
        {
            byte[] code = codeValue;
            test("IntegerBatch incomplete branch/LEA never fetches the missing next-page code " + Convert.ToHexString(code), () =>
            {
                var a = Fixture(4, [0x90]); var b = Fixture(4, [0x90]);
                foreach (Rig rig in new[] { a, b })
                { rig.Cpu.Eip = 0x3000u - (uint)code.Length; code.CopyTo(rig.Memory.Ram, (int)rig.Cpu.Eip); PagingTests.Pte(rig.Memory, 0x3000, 0, 0); }
                Check(Batch(a, b, 16) == 0);
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000 && !b.Cpu.TripleFaulted,
                    "Future branch/LEA code PF was not delivered through ordinary Step.");
            });
        }
        test("IntegerBatch LEA calculates an inaccessible data address without consulting a mapped peer", () =>
        {
            byte[] code = [0x8d,0x05,0,0,0x40,0,0x90,0xf4];
            var a = Fixture(4, code); var b = Fixture(4, code);
            var da = new DynamicCode(); var db = new DynamicCode();
            a.Memory.MapDynamic(da); b.Memory.MapDynamic(db);
            Check(Batch(a, b, 16) == 2 && b.Cpu.GetRegister(Register.Ax) == 0x400000);
            Check(da.Queries == 0 && db.Queries == 0 && da.Reads == 0 && db.Reads == 0,
                "LEA invented a lookup or read of its data-address expression.");
        });
        foreach ((ulong eventAt, ulong? deadline, int expected) in new (ulong, ulong?, int)[]
            { (70, null, 0), (80, null, 2), (200, 70, 0), (200, 120, 3) })
        {
            ulong due = eventAt; ulong? stop = deadline; int count = expected;
            test($"IntegerBatch deferred owner budget event{due} deadline{stop} count{count}", () =>
            {
                var a = Fixture(4, [0x90,0x40,0x90,0x48,0xf4]); var b = Fixture(4, [0x90,0x40,0x90,0x48,0xf4]);
                var scheduler = new DeviceScheduler(); int callbacks = 0;
                scheduler.ScheduleAfter(due, () => callbacks++);
                int actual = b.Cpu.TryRunIntegerBatch(16, scheduler, 100000000, stop);
                Check(actual == count, "Deferred owner budget differs from the exact 40ns elements.");
                for (int i = 0; i < actual; i++) a.Cpu.Step();
                Equal(a, b);
                Check(scheduler.Now == 0 && scheduler.NextEventTime == due && callbacks == 0,
                    "CPU admission advanced time or delivered a caller-owned event.");
                scheduler.AdvanceTo(due);
                Check(callbacks == 1, "The future callback/token was lost by admission.");
            });
        }
        for (int value = 0; value < 7; value++)
        foreach (int wayValue in new[] { 0, 3 })
        {
            int mode = value, way = wayValue;
            test($"IntegerBatch direct register forms preserve explicit ordinary-fetch counts mode{mode} codeWay{way}", () =>
            {
                foreach ((byte[] code, int touches) in DirectPrograms(mode is 2 or 4))
                {
                    byte[] terminal = [..code,0xf4];
                    var a = Fixture(mode, terminal); var b = Fixture(mode, terminal);
                    int slot = CodeWay(a, way); Check(CodeWay(b, way) == slot);
                    uint first = b.Cpu.PhysicalInstructionAddress, start = b.Cpu.Eip;
                    StateBlock before = b.Cpu.SaveState(); long hits = b.Cpu.TlbHits;
                    long age = before.Fields["_translationAge"].GetInt64();
                    Check(Batch(a, b, 2) == 1, "Direct register instruction failed to finish before the ordinary HLT.");
                    StateBlock after = b.Cpu.SaveState();
                    long expectedTouches = slot < 0 ? 0 : touches;
                    Check(b.Cpu.TlbHits - hits == expectedTouches &&
                        after.Fields["_translationAge"].GetInt64() - age == expectedTouches,
                        "Direct code touches differ from the independently listed ordinary fetches.");
                    Check(after.Fields["_firstInstructionPhysical"].GetUInt32() == first &&
                        after.Fields["_instructionBytes"].GetInt32() == code.Length &&
                        after.Fields["_checkpointIp"].GetUInt32() == start && b.Cpu.Eip == start + code.Length,
                        "Direct code length, first physical address or last checkpoint differs.");
                    if (slot >= 0)
                    {
                        JsonElement entry = after.Fields["_tlb"].EnumerateArray().ElementAt(slot);
                        Check(entry.GetProperty("age").GetInt64() == age + touches,
                            "Direct code age was stored in a different associative way.");
                    }
                }
            });
        }
        for (int value = 0; value < 7; value++)
        {
            int mode = value;
            test($"IntegerBatch interleaved direct registers and ordinary branch decoders retain all code records mode{mode}", () =>
            {
                byte[] immediate = mode is 2 or 4 ? [0x78,0x56,0x34,0x12] : [0x78,0x56];
                byte[] code = [0xb8,..immediate,0x40,0xeb,2,0x48,0x90,0x0f,0xc8,0x83,0xc0,3,0x75,0,0x90,0xf4];
                var a = Fixture(mode, code); var b = Fixture(mode, code);
                CodeWay(a, 3); int slot = CodeWay(b, 3); long hits = b.Cpu.TlbHits;
                Check(Batch(a, b, 128) == 7, "Mixed direct/fallback block followed a different actual branch path.");
                Check(b.Cpu.TlbHits - hits == (slot < 0 ? 0 : 13), "Interleaved code-fetch total differs.");
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            });
        }
        foreach (bool pagedValue in new[] { false, true })
        {
            bool paged = pagedValue;
            test($"IntegerBatch direct register immediate retains the unfolded A20 code address paged={paged}", () =>
            {
                byte[] code = [0xb8,0x78,0x56,0x34,0x92,0xf4];
                var a = Fixture(paged ? 4 : 2, code); var b = Fixture(paged ? 4 : 2, code);
                foreach (Rig rig in new[] { a, b })
                {
                    rig.Memory.A20Enabled = false;
                    if (paged)
                    {
                        // Actual PTE programming, INVLPG and warm fetch establish the high
                        // unfolded frame, whose physical bus bytes fold to the original code.
                        PagingTests.Pte(rig.Memory, 0x2000, 0x102000);
                        rig.Cpu.SetRegister(Register.Ax, 0x2000);
                        PagingTests.At(rig.Cpu, rig.Memory, [0x0f,0x01,0x38], 0x2800);
                        rig.Cpu.Eip = 0x2000; rig.Cpu.Step();
                        Check(rig.Cpu.Eip == 0x2001 && !rig.Cpu.TripleFaulted, "High-frame warm fetch did not execute the real NOP.");
                        CodeWay(rig, 3);
                    }
                    else rig.Cpu.Eip = 0x102001;
                }
                long hits = b.Cpu.TlbHits;
                Check(Batch(a, b, 2) == 1 && b.Cpu.GetRegister(Register.Ax) == 0x92345678,
                    "Direct wide immediate did not use the actual A20-folded backing bytes.");
                Check(b.Cpu.SaveState().Fields["_firstInstructionPhysical"].GetUInt32() == 0x102001 &&
                    b.Cpu.TlbHits - hits == (paged ? 2 : 0), "A20 folded the diagnostic address or changed its code-fetch count.");
            });
        }
    }
}
