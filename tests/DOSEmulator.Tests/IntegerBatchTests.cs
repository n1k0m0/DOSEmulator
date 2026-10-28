// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Complete-state controls for the unconnected stateless integer-batch prototype.</summary>
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

    /// <summary>Registers full-state positives and exact no-effect admission/future-fault negatives.</summary>
    public static void AddTo(Action<string, Action> test)
    {
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
            [0xeb,0], [0xe8,0,0,0,0], [0xe4,0x60], [0xe6,0x60], [0x63,0xc0], [0x0f,0xa2],
            [0x9b], [0xd9,0xe8], [0xa4], [0xf3,0xa4], [0xf4], [0x91]];
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
        test("IntegerBatch completes before an actual memory operand and leaves its future Step unchanged", () =>
        {
            byte[] code = [0x40,0x90,0x8b,0x05,0,0,3,0,0xf4];
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
    }
}
