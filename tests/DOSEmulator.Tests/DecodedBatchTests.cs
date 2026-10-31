// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections;
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Whole-state cold/warm decoded-cache controls, including code mutation and current memory guard changes.</summary>
internal static class DecodedBatchTests
{
    /// <summary>Private CPU/RAM fixture with its actual code entry and mode.</summary>
    internal sealed record Rig(X86Cpu Cpu, MemoryBus Memory, uint Start, int Mode);
    /// <summary>Requires an independent effect, cache bound or complete serialized CPU/RAM equality.</summary>
    private static void Check(bool value, string message = "Decoded batch invariant")
    { if (!value) throw new Exception(message); }

    /// <summary>Builds private real/PE/paged/V86 code and data, warming translations through actual scalar instructions.</summary>
    internal static Rig Make(int mode, byte[] program, X86CpuModel model = X86CpuModel.IntelP54cB5Subset)
    {
        var clock = new DeviceScheduler(); X86Cpu cpu; MemoryBus memory; uint start;
        if (mode == 0)
        {
            memory = new(2 * 1024 * 1024); cpu = new(memory, new(), model,
                model == X86CpuModel.IntelP54cB5Subset ? clock : null, model == X86CpuModel.IntelP54cB5Subset ? 100000000u : 0);
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0); cpu.SetSegment(Segment.Ds, 0); start = 0x2000;
        }
        else
        {
            var pair = mode is 3 or 4 or 6
                ? PagingTests.Create([], model: model, timestampSource: model == X86CpuModel.IntelP54cB5Subset ? clock : null,
                    timestampFrequency: model == X86CpuModel.IntelP54cB5Subset ? 100000000u : 0)
                : ProtectedModeTests.Rig([], code32: mode != 1, model: model,
                    timestampSource: model == X86CpuModel.IntelP54cB5Subset ? clock : null,
                    timestampFrequency: model == X86CpuModel.IntelP54cB5Subset ? 100000000u : 0);
            cpu = pair.Cpu; memory = pair.Memory;
            if (mode is 5 or 6)
            {
                PagingTests.Tss(cpu, memory); uint stack = cpu.GetRegister(Register.Sp);
                uint[] frame = [0x100,0x3000,0x23003,0x8000,0x4000,0x6000,0x5000,0x7000,0x8000];
                for (int i = 0; i < frame.Length; i++) memory.WriteDword(stack + (uint)i * 4, frame[i]);
                PagingTests.At(cpu, memory, [0xcf], 0x2800); start = 0x100;
                Check(cpu.Virtual8086 && cpu.Eip == start, "Fixture did not enter genuine V86.");
            }
            else
            {
                start = 0x2000;
                if (mode == 3) PagingTests.At(cpu, memory, [0xea,0,0x20,0,0,0x18,0], 0x2800);
            }
        }
        bool wide = mode is 2 or 4;
        uint linearData = unchecked(cpu.GetSegmentBase(Segment.Ds) + 0x7000);
        byte[] warm = wide ? [0xa2,0,0x70,0,0] : [0xa2,0,0x70];
        uint scratch = unchecked(cpu.GetSegmentBase(Segment.Cs) + start + 0x200);
        warm.CopyTo(memory.Ram, (int)scratch); cpu.Eip = start + 0x200; cpu.Step();
        memory.WriteDword(linearData, 0xa19b7d35);
        uint code = unchecked(cpu.GetSegmentBase(Segment.Cs) + start);
        new byte[] { 0x90 }.Concat(program).Concat(new byte[] { 0xf4 }).ToArray().CopyTo(memory.Ram, (int)code);
        cpu.Eip = start; cpu.Step();
        for (int i = 0; i < 8; i++) cpu.SetRegister((Register)i, 0x80003000u + (uint)i * 0x101);
        cpu.SetRegister(Register.Sp, mode is 5 or 6 ? 0x8000u : mode == 0 ? 0x9000u : 0x18000u);
        cpu.Eflags = cpu.Virtual8086 ? 0x23003u : 3u;
        return new(cpu, memory, start + 1, mode);
    }

    /// <summary>Compares every CPU member, TLB diagnostic/history field and all physical RAM without exclusions.</summary>
    internal static void Equal(Rig a, Rig b)
    {
        StateBlock left = a.Cpu.SaveState(), right = b.Cpu.SaveState();
        Check(left.Type == right.Type && left.Version == right.Version && left.Fields.Keys.SequenceEqual(right.Fields.Keys));
        foreach (string field in left.Fields.Keys)
            Check(left.Fields[field].GetRawText() == right.Fields[field].GetRawText(), "Complete CPU field differs: " + field);
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete RAM/PTE/guard image differs.");
    }

    /// <summary>Runs the exact returned number of ordinary steps and compares their complete results.</summary>
    private static int Batch(Rig scalar, Rig decoded, int maximum, int? expected = null)
    {
        int count = decoded.Cpu.TryRunDecodedBatch(maximum);
        Check(count >= 0 && count <= Math.Max(0, maximum), "Decoder exceeded its caller budget.");
        if (expected is int value) Check(count == value, "Expected " + value + " decoded elements, got " + count);
        for (int i = 0; i < count; i++) Check(scalar.Cpu.Step() == 4);
        Equal(scalar, decoded); return count;
    }

    /// <summary>Primes only derived cache entries, then restores every CPU and RAM byte before the actual comparison.</summary>
    private static void Prime(Rig rig)
    {
        StateBlock state = rig.Cpu.SaveState(); byte[] ram = (byte[])rig.Memory.Ram.Clone();
        rig.Cpu.TryRunDecodedBatch(64); ram.CopyTo(rig.Memory.Ram, 0); rig.Cpu.LoadState(state);
    }

    /// <summary>Changes one explicit generated fixture field without invoking instruction or device shortcuts.</summary>
    private static void Field<T>(X86Cpu cpu, string name, T value)
    {
        StateBlock state = cpu.SaveState(); state.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json); cpu.LoadState(state);
    }

    /// <summary>Generates varied pure-register forms and real DS reads/writes without assuming the decoder's own plan format.</summary>
    private static byte[] Program(bool wide)
    {
        byte[] immediate = wide ? [0x78,0x56,0x34,0x92] : [0x78,0x92];
        byte[] address = wide ? [0,0x70,0,0] : [0,0x70];
        var code = new List<byte>();
        for (int operation = 0; operation < 8; operation++)
        {
            code.AddRange([(byte)(operation * 8 + 4),0x91]);
            code.AddRange([(byte)(operation * 8 + 5),..immediate]);
            code.AddRange([(byte)(operation * 8),0xe0]);
            code.AddRange([0x83,(byte)(0xc0 | operation << 3),0xf1]);
        }
        code.AddRange([0xb8,..immediate,0xb4,0x81,0x86,0xe0,0x91,0x40,0x48,0x84,0xe0,0xa8,0x9d,0xa9,..immediate]);
        code.AddRange([0xa1,..address,0xa3,..address,0x8b,0x0e,..address]);
        // The last ModRM form is address16 direct only; replace it with the real address32 encoding when wide.
        if (wide) { code.RemoveRange(code.Count - 6, 6); code.AddRange([0x8b,0x0d,..address]); }
        code.AddRange([0x98,0x99,0x9e,0x9f,0xf5,0xf8,0xf9,0xfc,0xfd]);
        foreach (byte opcode in new byte[] { 0xb6,0xb7,0xbe,0xbf }) code.AddRange([0x0f,opcode,0xc4]);
        code.AddRange([0x0f,0xc8,0x0f,0x94,0xc4,0xc6,0xc4,0x85,0xc7,0xc1,..immediate,0x90]);
        return code.ToArray();
    }

    /// <summary>Reads only the disposable derived cache's cardinality, proving its bound without making it guest state.</summary>
    private static int CacheCount(X86Cpu cpu)
    {
        object table = typeof(X86Cpu).GetField("DecodedBlocks", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        MethodInfo lookup = table.GetType().GetMethod("TryGetValue")!;
        object?[] arguments = [cpu, null];
        if (!(bool)lookup.Invoke(table, arguments)!) return 0;
        object cache = arguments[1]!;
        return ((IDictionary)cache.GetType().GetField("_blocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cache)!).Count;
    }

    /// <summary>Programs an actual private DMA channel-1 byte grant into the selected own code byte.</summary>
    private static void DmaByte(MemoryBus memory, uint address, byte value)
    {
        var dma = new Dma8237(memory);
        dma.Write(0x0c, 0, 1); dma.Write(2, address & 255, 1); dma.Write(2, (address >> 8) & 255, 1);
        dma.Write(3, 0, 1); dma.Write(3, 0, 1); dma.Write(0x83, address >> 16, 1);
        dma.Write(0x0b, 0x45, 1); dma.Write(0x0a, 1, 1);
        Check(dma.TryWriteByte(1, value) == DmaReadResult.Transferred && dma.IsTerminalCount(1), "Actual DMA code grant was not completed.");
    }

    /// <summary>Own newly mapped code peer, proving that a retained byte copy cannot bypass current bus classification.</summary>
    private sealed class CodePeer : IMemoryDevice
    {
        public int Reads;
        /// <summary>Returns one real NOP only when ordinary Step performs an actual mapped byte read.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x90; }
        /// <summary>Rejects an unintended mapped-code write.</summary>
        public void WriteByte(uint offset, byte value) => throw new Exception("Unexpected code-peer write.");
    }

    /// <summary>Builds a LEA encoding and independent unsigned address expression from retained literal register inputs.</summary>
    private static (byte[] Bytes, uint Offset) Lea(bool wide, byte modrm, byte sib, uint[] registers)
    {
        int mod = modrm >> 6, rm = modrm & 7;
        var bytes = new List<byte> { 0x8d, modrm };
        uint offset; int displacementSize = 0; uint displacement = 0;
        if (!wide)
        {
            uint bx = registers[3] & 65535, bp = registers[5] & 65535;
            uint si = registers[6] & 65535, di = registers[7] & 65535;
            offset = rm switch { 0 => bx + si, 1 => bx + di, 2 => bp + si, 3 => bp + di,
                4 => si, 5 => di, 6 => bp, _ => bx };
            if (mod == 0 && rm == 6) { offset = 0; displacementSize = 2; displacement = 0xfffe; }
            else if (mod == 1) { displacementSize = 1; displacement = 0xed; }
            else if (mod == 2) { displacementSize = 2; displacement = 0xe123; }
        }
        else
        {
            int basis = rm, index = 4, shift = 0;
            if (rm == 4) { bytes.Add(sib); basis = sib & 7; index = (sib >> 3) & 7; shift = sib >> 6; }
            offset = mod == 0 && basis == 5 ? 0 : registers[basis];
            if (index != 4) offset = unchecked(offset + (uint)((ulong)registers[index] * (1u << shift)));
            if (mod == 0 && basis == 5) { displacementSize = 4; displacement = 0xdeadbeef; }
            else if (mod == 1) { displacementSize = 1; displacement = 0xed; }
            else if (mod == 2) { displacementSize = 4; displacement = 0x8abcde01; }
        }
        for (int i = 0; i < displacementSize; i++) bytes.Add((byte)(displacement >> (8 * i)));
        offset = unchecked(offset + (displacementSize == 1 ? (uint)(sbyte)displacement : displacement));
        return (bytes.ToArray(), wide ? offset : offset & 65535);
    }

    /// <summary>Checks one LEA against actual scalar execution after cache priming with deliberately different register inputs.</summary>
    private static void LeaCase(bool wide, byte modrm, byte sib)
    {
        uint[] inputs = [0xfedc0001,0xa987fffe,0x87657fff,0x7654ffff,0x65430003,0x5432fffd,0x43218001,0x3210fff8];
        var (instruction, offset) = Lea(wide, modrm, sib, inputs);
        var a = Make(wide ? 4 : 3, [..instruction,0x90]); var b = Make(wide ? 4 : 3, [..instruction,0x90]);
        Prime(b);
        foreach (Rig r in new[] { a, b })
        {
            for (int i = 0; i < 8; i++) r.Cpu.SetRegister((Register)i, inputs[i]);
            r.Cpu.SetSegment(Segment.Ds, 0); // Real loaded null DS must not turn LEA into a data access.
        }
        uint flags = b.Cpu.Eflags; long hits = b.Cpu.TlbHits, walks = b.Cpu.PageWalks;
        Batch(a, b, 64, 2);
        int destination = (modrm >> 3) & 7;
        uint expected = wide ? offset : (inputs[destination] & 0xffff0000) | offset;
        Check(b.Cpu.GetRegister((Register)destination) == expected && b.Cpu.Eflags == flags,
            "Independent LEA offset/register preservation differs.");
        int mod = modrm >> 6, rm = modrm & 7;
        bool hasSib = wide && rm == 4;
        bool displacement = mod != 0 || (wide ? (hasSib ? sib & 7 : rm) == 5 : rm == 6);
        Check(b.Cpu.TlbHits - hits == 3 + (hasSib ? 1 : 0) + (displacement ? 1 : 0) && b.Cpu.PageWalks == walks,
            "LEA consulted data paging or changed ordinary code-fetch counts.");
    }

    /// <summary>Registers complete-state cache-order positives and unchanged future-fault/byte-guard negatives.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int modeValue = 0; modeValue < 7; modeValue++)
        foreach (bool warmValue in new[] { false, true })
        foreach (int maximumValue in new[] { 1,2,3,17,64,128 })
        {
            int mode = modeValue, maximum = maximumValue; bool warm = warmValue;
            test($"DecodedBatch full CPU/RAM mode{mode} cacheWarm={warm} budget{maximum}", () =>
            {
                var a = Make(mode, Program(mode is 2 or 4)); var b = Make(mode, Program(mode is 2 or 4));
                if (warm) Prime(b);
                Equal(a, b); Batch(a, b, maximum, maximum == 1 ? 0 : null);
            });
        }
        for (int modeValue = 0; modeValue < 7; modeValue++)
        {
            int mode = modeValue;
            test($"DecodedBatch mixed budgets complete original scalar path mode{mode}", () =>
            {
                var a = Make(mode, Program(mode is 2 or 4)); var b = Make(mode, Program(mode is 2 or 4)); Prime(b);
                int total = 0, round = 0;
                while (!b.Cpu.Halted && round < 1000)
                {
                    int count = Batch(a, b, round++ % 2 == 0 ? 3 : 19); total += count;
                    if (count == 0) { a.Cpu.Step(); b.Cpu.Step(); Equal(a, b); }
                }
                Check(total > 50 && b.Cpu.Halted, "Decoded mixed-budget block did not complete through the original HLT/monitor path.");
            });
            test($"DecodedBatch branch ends a cached block with exact current outcome mode{mode}", () =>
            {
                var a = Make(mode, [0x40,0x74,2,0x48,0x90,0x90]); var b = Make(mode, [0x40,0x74,2,0x48,0x90,0x90]);
                b.Cpu.SetRegister(Register.Ax, mode is 2 or 4 ? uint.MaxValue : 65535);
                Prime(b); b.Cpu.SetRegister(Register.Ax, a.Cpu.GetRegister(Register.Ax));
                Batch(a, b, 128, 2);
                Check(b.Cpu.Eip == b.Start + 3, "Current JZ outcome was frozen by cache priming.");
                Batch(a, b, 128);
            });
        }
        foreach (bool dmaValue in new[] { false, true })
        foreach (int positionValue in new[] { 0,1,3 })
        {
            bool dma = dmaValue; int position = positionValue;
            test($"DecodedBatch full-byte guard detects warm code mutation dma={dma} at{position}", () =>
            {
                byte[] code = [0xb8,0x78,0x56,0x34,0x12,0x40,0x90];
                var a = Make(2, code); var b = Make(2, code); Prime(b);
                byte changed = position == 0 ? (byte)0xb9 : position == 1 ? (byte)0x81 : (byte)0x93;
                foreach (Rig r in new[] { a, b })
                { if (dma) DmaByte(r.Memory, r.Start + (uint)position, changed); else r.Memory.WriteByte(r.Start + (uint)position, changed); }
                Batch(a, b, 64, 3);
                uint value = b.Cpu.GetRegister(position == 0 ? Register.Cx : Register.Ax);
                Check(value == (position == 0 ? 0x12345678u : position == 1 ? 0x12345682u : 0x12935679u), "Mutated immediate/register effect was stale.");
            });
        }
        test("DecodedBatch cache capacity and eviction history do not enter restored CPU/RAM state", () =>
        {
            var a = Make(2, [0x90,0x40,0x90]); var b = Make(2, [0x90,0x40,0x90]);
            StateBlock original = b.Cpu.SaveState(); byte[] ram = (byte[])b.Memory.Ram.Clone();
            for (int i = 0; i < 4300; i++)
            {
                uint at = 0x10000u + (uint)i * 8;
                new byte[] { 0x90,0x90,0xf4 }.CopyTo(b.Memory.Ram, (int)at); b.Cpu.Eip = at;
                Check(b.Cpu.TryRunDecodedBatch(2) == 2);
            }
            Check(CacheCount(b.Cpu) == 4096, "Derived cache grew beyond its fixed block bound.");
            ram.CopyTo(b.Memory.Ram, 0); b.Cpu.LoadState(original); Equal(a, b); Batch(a, b, 64, 3);
        });
        test("DecodedBatch new CPU LoadState starts with an empty derivative and identical future state", () =>
        {
            var a = Make(4, [0x90,0x40,0x90]); Prime(a); Check(CacheCount(a.Cpu) > 0);
            var b = Make(4, [0x90,0x40,0x90]); b.Cpu.LoadState(a.Cpu.SaveState()); a.Memory.Ram.CopyTo(b.Memory.Ram, 0);
            Check(CacheCount(b.Cpu) == 0, "New CPU unexpectedly inherited another CPU's cache.");
            Batch(a, b, 64, 3);
        });
        foreach (string guard in new[] { "segment", "readonly", "alignment", "code-alias", "cold-page" })
            test("DecodedBatch warm encoding retains current memory guard " + guard, () =>
            {
                byte[] code = [0xa3,0,0x70,0,0];
                var a = Make(4, code); var b = Make(4, code); Prime(b);
                foreach (Rig r in new[] { a, b })
                {
                    if (guard == "segment") r.Cpu.SetSegment(Segment.Ds, 0);
                    else if (guard == "readonly")
                    {
                        var entries = r.Cpu.SaveState().Fields["_tlb"].EnumerateArray().Select(v => System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText())!).ToArray();
                        foreach (var entry in entries) if ((uint)entry["page"]! == 7 && (bool)entry["valid"]!) entry["writable"] = false;
                        Field(r.Cpu, "_tlb", entries);
                    }
                    else if (guard == "alignment")
                    {
                        // Genuine user access is covered by the existing AC fixtures; this
                        // explicit AM/AC/CPL image isolates unchanged warm-cache admission.
                        Field(r.Cpu, "Cr0", r.Cpu.Cr0 | 0x40000u); Field(r.Cpu, "_cpl", 3); r.Cpu.Eflags |= 0x40000;
                        r.Memory.WriteByte(r.Start + 1, 1);
                    }
                    else if (guard == "code-alias")
                    {
                        r.Memory.WriteDword(r.Start + 1, r.Start);
                        // A coherent already-dirty code mapping reaches the physical overlap
                        // guard; a clean code PTE would reject earlier for its missing dirty bit.
                        var entries = r.Cpu.SaveState().Fields["_tlb"].EnumerateArray().Select(v => System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText())!).ToArray();
                        foreach (var entry in entries) if ((uint)entry["page"]! == 2 && (bool)entry["valid"]!) entry["dirty"] = true;
                        Field(r.Cpu, "_tlb", entries);
                        r.Memory.WriteDword(0x1d008, r.Memory.ReadDword(0x1d008) | 0x40);
                    }
                    else
                    {
                        r.Memory.WriteDword(r.Start + 1, 0x9000); PagingTests.Pte(r.Memory, 0x9000, 0, 0);
                    }
                }
                Check(b.Cpu.TryRunDecodedBatch(64) == 0, "Changed current guard was ignored by warm decoding."); Equal(a, b);
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            });
        test("DecodedBatch actual CS32-to-CS16 far transfer cannot reuse the previous width plan", () =>
        {
            byte[] code = [0xb8,0x78,0x56,0x34,0x12,0x90];
            var a = Make(2, code); var b = Make(2, code); Prime(b);
            foreach (Rig r in new[] { a, b })
                PagingTests.At(r.Cpu, r.Memory, [0xea,(byte)r.Start,(byte)(r.Start >> 8),0,0,0x18,0], 0x2800);
            Check(!b.Cpu.GetSegmentCache(Segment.Cs).Default32 && b.Cpu.Eip == b.Start, "Fixture did not actually load CS16.");
            Batch(a, b, 64, 3);
            Check((b.Cpu.GetRegister(Register.Ax) & 65535) == 0x566a, "CS16 immediate and following XOR were decoded with stale width.");
        });
        test("DecodedBatch current DS base and cold data page remain outside cached encoding facts", () =>
        {
            byte[] code = [0xa1,0,0x70,0,0,0x90];
            var a = Make(4, code); var b = Make(4, code); Prime(b);
            foreach (Rig r in new[] { a, b })
            {
                ProtectedModeTests.Descriptor(r.Memory, 0x50, 0x10000, 0xfffff, 0x92, 12);
                r.Cpu.SetSegment(Segment.Ds, 0x50); r.Memory.WriteDword(0x17000, 0xdecafbad);
            }
            Check(b.Cpu.TryRunDecodedBatch(64) == 0, "A new cold data mapping was assumed cached."); Equal(a, b);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
            foreach (Rig r in new[] { a, b }) r.Cpu.Eip = r.Start;
            Batch(a, b, 64, 2);
            Check(b.Cpu.GetRegister(Register.Ax) == 0xdecafbad, "Retained plan reused the old DS-derived data address.");
        });
        test("DecodedBatch warm byte cache cannot bypass a newly mapped code page", () =>
        {
            var a = Make(4, [0x90,0x40,0x90]); var b = Make(4, [0x90,0x40,0x90]); Prime(b);
            var left = new CodePeer(); var right = new CodePeer();
            a.Memory.Map(a.Start, 1, left, "own current code peer"); b.Memory.Map(b.Start, 1, right, "own current code peer");
            Check(b.Cpu.TryRunDecodedBatch(64) == 0 && right.Reads == 0, "Warm decode consulted or bypassed a newly mapped device."); Equal(a, b);
            a.Cpu.Step(); b.Cpu.Step(); Equal(a, b); Check(left.Reads == 1 && right.Reads == 1);
        });
        foreach ((ulong dueValue, int expectedValue) in new (ulong, int)[] { (70,0),(80,2),(120,3) })
        {
            ulong due = dueValue; int expected = expectedValue;
            test($"DecodedBatch scheduler budget and future callback due{due}", () =>
            {
                var a = Make(4, [0x90,0x40,0x90]); var b = Make(4, [0x90,0x40,0x90]); Prime(b);
                var scheduler = new DeviceScheduler(); int callbacks = 0; scheduler.ScheduleAfter(due, () => callbacks++);
                int actual = b.Cpu.TryRunDecodedBatch(64, scheduler, 100000000);
                Check(actual == expected && scheduler.Now == 0 && callbacks == 0);
                for (int i = 0; i < actual; i++) a.Cpu.Step(); Equal(a, b);
                scheduler.AdvanceTo(due); Check(callbacks == 1);
            });
        }
        foreach (bool wideValue in new[] { false, true })
        {
            bool wide = wideValue;
            test($"DecodedBatch LEA every memory ModRM and current register input wide={wide}", () =>
            {
                for (int modrm = 0; modrm < 0xc0; modrm++) LeaCase(wide, (byte)modrm, 0x25);
            });
        }
        for (int modValue = 0; modValue < 3; modValue++)
        {
            int mod = modValue;
            test($"DecodedBatch LEA all 256 SIB scale/index/base bytes mod={mod}", () =>
            {
                for (int sib = 0; sib < 256; sib++)
                    LeaCase(true, (byte)((mod << 6) | (((sib >> 5) & 7) << 3) | 4), (byte)sib);
            });
        }
        test("DecodedBatch full source guard observes different A20-folded backing after priming", () =>
        {
            var a = Make(2, [0xb8,1,2,3,4,0x90]); var b = Make(2, [0xb8,1,2,3,4,0x90]);
            foreach (Rig r in new[] { a, b })
            {
                new byte[] { 0xb8,0x78,0x56,0x34,0x12,0x90,0xf4 }.CopyTo(r.Memory.Ram, 0x102001);
                r.Cpu.Eip = 0x102001;
            }
            Prime(b);
            foreach (Rig r in new[] { a, b }) r.Memory.A20Enabled = false;
            Batch(a, b, 64, 2);
            Check(b.Cpu.GetRegister(Register.Ax) == 0x04030201 &&
                b.Cpu.SaveState().Fields["_firstInstructionPhysical"].GetUInt32() == 0x102006,
                "A20 reused the old high backing or folded the diagnostic physical address.");
        });
        test("DecodedBatch current physical code frame follows an actual PTE and INVLPG change", () =>
        {
            var a = Make(4, [0xb8,1,2,3,4,0x90]); var b = Make(4, [0xb8,1,2,3,4,0x90]); Prime(b);
            foreach (Rig r in new[] { a, b })
            {
                new byte[] { 0x90,0xb8,0x21,0x43,0x65,0x87,0x90,0xf4 }.CopyTo(r.Memory.Ram, 0x40000);
                PagingTests.Pte(r.Memory, 0x2000, 0x40000);
                r.Cpu.SetRegister(Register.Ax, 0x2000);
                PagingTests.At(r.Cpu, r.Memory, [0x0f,0x01,0x38], 0x2800);
                r.Cpu.Eip = 0x2000; Check(r.Cpu.Step() == 4 && r.Cpu.Eip == r.Start);
            }
            Batch(a, b, 64, 2);
            Check(b.Cpu.GetRegister(Register.Ax) == 0x87654321 &&
                b.Cpu.SaveState().Fields["_firstInstructionPhysical"].GetUInt32() == 0x40006,
                "The new physical code translation reused an old decoded source.");
        });
        test("DecodedBatch actual CPU reset preserves no cached PE32 operand-size assumption", () =>
        {
            var a = Make(2, [0xb8,0x78,0x56,0x34,0x12,0x90]); var b = Make(2, [0xb8,0x78,0x56,0x34,0x12,0x90]); Prime(b);
            foreach (Rig r in new[] { a, b })
            {
                r.Cpu.Reset(); r.Cpu.SetSegment(Segment.Cs, 0); r.Cpu.SetSegment(Segment.Ss, 0); r.Cpu.SetSegment(Segment.Ds, 0);
                r.Cpu.Eip = r.Start;
            }
            Check(!b.Cpu.ProtectedMode && !b.Cpu.GetSegmentCache(Segment.Cs).Default32);
            Batch(a, b, 64, 3);
            Check(b.Cpu.GetRegister(Register.Ax) == 0x566a, "Reset reused a former PE32 immediate width.");
        });
        foreach ((uint hzValue, ulong availableValue, int countValue) in new (uint, ulong, int)[]
            { (100000000,79,0),(100000000,80,2),(33000000,242,0),(33000000,243,2) })
        {
            uint hz = hzValue; ulong available = availableValue; int count = countValue;
            test($"DecodedBatch warm source stops before scheduler time overflow hz{hz} available{available}", () =>
            {
                var a = Make(4, [0x90,0x40,0x90]); var b = Make(4, [0x90,0x40,0x90]); Prime(b);
                var scheduler = new DeviceScheduler(); scheduler.AdvanceTo(ulong.MaxValue - available);
                ulong before = scheduler.Now; int actual = b.Cpu.TryRunDecodedBatch(64, scheduler, hz);
                Check(actual == count && scheduler.Now == before, "Decoder exceeded its conservative near-overflow owner budget.");
                for (int i = 0; i < actual; i++) { a.Cpu.Step(); scheduler.AdvanceCycles(4, hz); }
                Equal(a, b); Check(scheduler.Now >= before, "Representable future time wrapped.");
            });
        }
        foreach (byte[] unsupportedValue in new byte[][]
            { [0xe2,0xfe], [0x8f,0xc0], [0xca,0,0], [0xff,0xd8], [0xfa], [0xfb], [0xd9,0xe8], [0xf0,0x90],
              [0xf3,0xa4], [0x0f,0xa2], [0xc7,0xc8,1,2,3,4], [0x86,5,0,0x70,0,0], [0x8d,0xc0], [0x0f,0xaf,5,0,0x70,0,0] })
        {
            byte[] unsupported = unsupportedValue;
            test("DecodedBatch entry gate leaves unsupported code uncached and observes later supported bytes " + Convert.ToHexString(unsupported), () =>
            {
                byte[] program = unsupported.Concat(Enumerable.Repeat((byte)0x90, 15)).ToArray();
                var a = Make(4, program); var b = Make(4, program);
                for (int repeat = 0; repeat < 2; repeat++)
                { Check(b.Cpu.TryRunDecodedBatch(64) == 0); Equal(a, b); }
                Check(CacheCount(b.Cpu) == 0, "Unsupported entry created a persistent decode lookup result.");
                foreach (Rig r in new[] { a, b })
                    new byte[] { 0xb8,0x78,0x56,0x34,0x12,0x90,0xf4 }.CopyTo(r.Memory.Ram, (int)r.Start);
                Batch(a, b, 64, 2);
                Check(b.Cpu.GetRegister(Register.Ax) == 0x12345678 && CacheCount(b.Cpu) > 0,
                    "The earlier unsupported byte pattern hid a new supported instruction.");
            });
        }
        test("DecodedBatch entry gate rechecks the current second 0F opcode around a retained warm block", () =>
        {
            var a = Make(4, [0x0f,0xc8,0x90]); var b = Make(4, [0x0f,0xc8,0x90]); Prime(b);
            foreach (Rig r in new[] { a, b }) r.Memory.WriteByte(r.Start + 1, 0xa2);
            Check(b.Cpu.TryRunDecodedBatch(64) == 0, "Current unsupported CPUID entered the cached former BSWAP path."); Equal(a, b);
            foreach (Rig r in new[] { a, b }) r.Memory.WriteByte(r.Start + 1, 0xc9);
            Batch(a, b, 64, 2);
            Check(b.Cpu.GetRegister(Register.Cx) == 0x01310080 && b.Cpu.GetRegister(Register.Ax) == 0x80003000,
                "Changed second-byte register selection reused a stale BSWAP plan.");
        });
        test("DecodedBatch warm MOVZX rechecks the actual cold SIB memory source before returning to a register source", () =>
        {
            var a = Make(4, [0x0f,0xb6,0xc4,0x90]); var b = Make(4, [0x0f,0xb6,0xc4,0x90]); Prime(b);
            foreach (Rig r in new[] { a, b }) r.Memory.WriteByte(r.Start + 2, 4);
            Check(b.Cpu.TryRunDecodedBatch(64) == 0, "Current MOVZX memory operand bypassed its current data guard."); Equal(a, b);
            foreach (Rig r in new[] { a, b }) r.Memory.WriteByte(r.Start + 2, 0xc5);
            Batch(a, b, 64, 2);
            Check(b.Cpu.GetRegister(Register.Ax) == 0x31, "Current MOVZX source-register alias used a stale plan.");
        });
        for (int modeValue = 0; modeValue < 7; modeValue++)
        {
            int mode = modeValue;
            test($"DecodedBatch entry gate preserves the existing full-width BSWAP effect mode{mode}", () =>
            {
                var a = Make(mode, [0x0f,0xc8,0x90]); var b = Make(mode, [0x0f,0xc8,0x90]); Prime(b);
                Batch(a, b, 64, 2);
                Check(b.Cpu.GetRegister(Register.Ax) == 0x00300080, "Code-default width changed the existing BSWAP result.");
            });
        }
    }
}
