// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Independent stack bytes/SP/target/TLB accounting and complete scalar versus cold/warm decoded continuations.</summary>
internal static class DecodedStackBatchTests
{
    private static readonly uint[] Inputs = [0x89abcdef, 0x10203040, 0xa5a55a5a, 0x76543210, 0, 0x13579bdf, 0xfedc9876, 0x55aa33cc];
    private static readonly uint[] Words = [0x11223344, 0x55667788, 0x99aabbcc, 0xdeadbeef, 0x3456789a, 0x789abcde, 0xbcdef012, 0xfedcba98];
    /// <summary>Own CPU/RAM plus explicit operand and stack widths, never original guest media.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory, int Mode, int Width, bool Stack32, bool Paging, uint Start, uint InitialSp);

    /// <summary>Requires independently specified stack bytes, control targets, translation counts or complete-state equality.</summary>
    private static void Check(bool value, string why = "Decoded stack invariant failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Resets only the controlled GPR/flag inputs, preserving all actual segment, instruction and TLB history.</summary>
    private static void Seed(Rig r)
    {
        for (int i = 0; i < 8; i++) r.Cpu.SetRegister((Register)i, i == 4 ? r.InitialSp : Inputs[i]);
        r.Cpu.Eflags = r.Cpu.Virtual8086 ? 0x23003u : 3u;
    }

    /// <summary>Returns the independent SS.B-selected effective offset rather than treating the full ESP as a 16-bit stack address.</summary>
    private static uint Pointer(Rig r) => r.Cpu.GetRegister(Register.Sp) & (r.Stack32 ? uint.MaxValue : 65535u);

    /// <summary>Combines a known effective new pointer with the original raw high half for SS.B16.</summary>
    private static uint RawPointer(Rig r, uint value) => r.Stack32 ? value : (r.InitialSp & 0xffff0000) | (value & 65535);

    /// <summary>Reads one exact operand-sized stack value through the fixture's physical bus.</summary>
    private static uint Read(Rig r, uint pointer) => r.Width == 2
        ? r.Memory.ReadWord(unchecked(r.Cpu.GetSegmentBase(Segment.Ss) + pointer))
        : r.Memory.ReadDword(unchecked(r.Cpu.GetSegmentBase(Segment.Ss) + pointer));

    /// <summary>Sets only an own stack test operand, retaining the actual hardware A20 mapping.</summary>
    private static void Write(Rig r, uint pointer, uint value)
    {
        uint address = unchecked(r.Cpu.GetSegmentBase(Segment.Ss) + pointer);
        if (r.Width == 2) r.Memory.WriteWord(address, (ushort)value); else r.Memory.WriteDword(address, value);
    }

    /// <summary>Warms current SS translations using actual byte reads/writes; code size and stack-address size remain independent.</summary>
    private static void Warm(Rig r, uint pointer, bool write)
    {
        var code = new List<byte> { 0x36 };
        if (r.Width == 2) code.Add(0x67);
        code.Add(write ? (byte)0xa2 : (byte)0xa0);
        for (int i = 0; i < 4; i++) code.Add((byte)(pointer >> (i * 8)));
        uint scratch = r.Start + 0x200;
        code.ToArray().CopyTo(r.Memory.Ram, unchecked(r.Cpu.GetSegmentBase(Segment.Cs) + scratch));
        r.Cpu.Eip = scratch; Check(r.Cpu.Step() == 4 && !r.Cpu.TripleFaulted, "Actual SS translation warmup failed.");
    }

    /// <summary>Executes a real NOP at the test entry before restoring shared input registers.</summary>
    private static void Rewind(Rig r)
    {
        r.Cpu.Eip = r.Start; Seed(r); Check(r.Cpu.Step() == 4 && r.Cpu.Eip == r.Start + 1); Seed(r);
    }

    /// <summary>Creates real16, four PE CS/SS width pairs, four paged pairs and two genuine IRET-entered V86 fixtures.</summary>
    private static Rig Make(int mode, byte[] code, bool warm = true, bool dirty = true)
    {
        bool wide = mode is 3 or 4 or 7 or 8, stack32 = mode is 2 or 4 or 6 or 8;
        bool paging = mode is >= 5 and <= 8 or 10, vm = mode is 9 or 10;
        var clock = new DeviceScheduler(); X86Cpu cpu; MemoryBus memory; uint start;
        if (mode == 0)
        {
            memory = new(2 * 1024 * 1024); cpu = new(memory, new(), X86CpuModel.IntelP54cB5Subset, clock, 100000000);
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0); cpu.SetSegment(Segment.Ds, 0); start = 0x2000;
        }
        else
        {
            var pair = ProtectedModeTests.Rig([], code32: vm || wide, stack32: vm || stack32,
                model: X86CpuModel.IntelP54cB5Subset, timestampSource: clock, timestampFrequency: 100000000);
            cpu = pair.Cpu; memory = pair.Memory;
            if (paging)
            {
                memory.WriteDword(0x1c000, 0x1d007);
                for (uint i = 0; i < 1024; i++) memory.WriteDword(0x1d000 + i * 4, i * 4096 | 7);
                PagingTests.Enable(cpu, memory);
            }
            if (vm)
            {
                PagingTests.Tss(cpu, memory); uint stack = cpu.GetRegister(Register.Sp);
                uint[] frame = [0x100, 0x3000, 0x23003, 0x9000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
                for (int i = 0; i < frame.Length; i++) memory.WriteDword(stack + (uint)i * 4, frame[i]);
                PagingTests.At(cpu, memory, [0xcf], 0x2800); start = 0x100;
                Check(cpu.Virtual8086 && cpu.Eip == start, "Fixture did not enter V86 through actual IRET.");
            }
            else start = 0x2000;
        }
        var r = new Rig(cpu, memory, mode, wide ? 4 : 2, stack32, paging, start, stack32 ? 0x19000u : 0x5a5a9000u);
        Seed(r); uint pointer = Pointer(r);
        if (warm) foreach (uint at in new[] { pointer - 64, pointer, pointer + 64 }) Warm(r, at, dirty);
        uint physical = unchecked(cpu.GetSegmentBase(Segment.Ss) + pointer);
        memory.Ram.AsSpan((int)physical - 128, 256).Fill(0xa5);
        for (int i = 0; i < 8; i++) Write(r, pointer + (uint)(i * r.Width), Words[i]);
        new byte[] { 0x90 }.Concat(code).Concat(new byte[] { 0x90, 0xf4 }).ToArray()
            .CopyTo(memory.Ram, unchecked(cpu.GetSegmentBase(Segment.Cs) + start));
        Rewind(r); return r;
    }

    /// <summary>Compares every actual CPU member/TLB/scratch/counter and every physical RAM byte, without excluding diagnostics.</summary>
    private static void Equal(Rig a, Rig b)
    {
        var left = a.Cpu.SaveState(); var right = b.Cpu.SaveState();
        Check(left.Type == right.Type && left.Version == right.Version && left.Fields.Keys.SequenceEqual(right.Fields.Keys));
        foreach (var name in left.Fields.Keys)
            Check(left.Fields[name].GetRawText() == right.Fields[name].GetRawText(), "Complete CPU field differs: " + name);
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete stack/RAM/PTE bytes differ.");
    }

    /// <summary>Checks complete cold and warm-cache results against the same scalar future and independent exact translation counts.</summary>
    private static void Accepted(Rig scalar, Rig decoded, int expected, int codeTouches, int stackTouches)
    {
        Equal(scalar, decoded); var before = decoded.Cpu.SaveState(); byte[] ram = (byte[])decoded.Memory.Ram.Clone();
        long hits = decoded.Cpu.TlbHits;
        Check(decoded.Cpu.TryRunDecodedBatch(32) == expected, "Actual decoded stack element count differs.");
        for (int i = 0; i < expected; i++) Check(scalar.Cpu.Step() == 4);
        Equal(scalar, decoded);
        Check(decoded.Cpu.TlbHits - hits == (decoded.Paging ? codeTouches + stackTouches : 0), "Independent code/probe/transfer TLB hit count differs.");
        ram.CopyTo(decoded.Memory.Ram, 0); decoded.Cpu.LoadState(before);
        Check(decoded.Cpu.TryRunDecodedBatch(32) == expected, "Warm stack plan changed its exact execution count."); Equal(scalar, decoded);
    }

    /// <summary>Requires cold/warm rejection without effects, then compares the actual original next instruction or exception.</summary>
    private static void Rejected(Rig a, Rig b)
    {
        Equal(a, b);
        Check(b.Cpu.TryRunDecodedBatch(32) == 0 && b.Cpu.TryRunDecodedBatch(32) == 0, "Unsafe stack range or target admitted."); Equal(a, b);
        Exception? first = null, second = null;
        try { a.Cpu.Step(); } catch (Exception error) { first = error; }
        try { b.Cpu.Step(); } catch (Exception error) { second = error; }
        Check(first?.GetType() == second?.GetType() && first?.Message == second?.Message, "Original stack fallback exception differs."); Equal(a, b);
    }

    /// <summary>Creates a literal near call whose independently selected target is relative to the post-instruction return address.</summary>
    private static byte[] Call(int width, uint start, uint target)
    {
        uint relative = unchecked(target - (start + 1u + 1u + (uint)width));
        return [0xe8, ..Enumerable.Range(0, width).Select(i => (byte)(relative >> (i * 8)))];
    }

    /// <summary>Changes one controlled test-image field while keeping the entire generated CPU shape.</summary>
    private static void Field<T>(Rig r, string name, T value)
    {
        var state = r.Cpu.SaveState(); state.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json); r.Cpu.LoadState(state);
    }

    /// <summary>Changes only one explicitly selected cached segment input for future-fault controls.</summary>
    private static void SegmentInput(Rig r, Segment segment, Func<SegmentDescriptorCache, SegmentDescriptorCache> change)
    {
        var caches = r.Cpu.SaveState().Fields["_segmentCaches"].Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        caches[(int)segment] = change(caches[(int)segment]); Field(r, "_segmentCaches", caches);
    }

    /// <summary>One controlled mapped byte proves rejected admission never reads or writes a stack MMIO peer.</summary>
    private sealed class Peer : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Counts one actual byte read and returns the fixed private value.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x6d; }
        /// <summary>Counts one actual byte write without CPU callbacks or host effects.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    /// <summary>Registers real stack operands, original SP special cases, preflight counts, target faults and complete saved futures.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int value = 0; value < 11; value++)
        {
            int mode = value;
            test($"DecodedStack PUSH all registers including original SP mode{mode}", () =>
            {
                for (int register = 0; register < 8; register++)
                {
                    var a = Make(mode, [(byte)(0x50 + register)]); var b = Make(mode, [(byte)(0x50 + register)]);
                    uint pointer = Pointer(b), input = register == 4 ? b.InitialSp : Inputs[register];
                    Accepted(a, b, 2, 2, 1);
                    Check(b.Cpu.GetRegister(Register.Sp) == RawPointer(b, pointer - (uint)b.Width) &&
                        Read(b, pointer - (uint)b.Width) == (b.Width == 2 ? input & 65535 : input), "PUSH used its decremented SP or another source width.");
                }
            });
            test($"DecodedStack POP all registers including post-advance SP mode{mode}", () =>
            {
                for (int register = 0; register < 8; register++)
                {
                    var a = Make(mode, [(byte)(0x58 + register)]); var b = Make(mode, [(byte)(0x58 + register)]);
                    uint pointer = Pointer(b), advanced = RawPointer(b, pointer + (uint)b.Width);
                    Accepted(a, b, 2, 2, 1);
                    uint expected = b.Width == 4 ? Words[0] : ((register == 4 ? advanced : Inputs[register]) & 0xffff0000) | (Words[0] & 65535);
                    Check(b.Cpu.GetRegister((Register)register) == expected && (register == 4 || b.Cpu.GetRegister(Register.Sp) == advanced),
                        "POP destination did not follow its real post-pop stack commit.");
                }
            });
            test($"DecodedStack PUSHA original raw SP and eight probes plus eight writes mode{mode}", () =>
            {
                var a = Make(mode, [0x60]); var b = Make(mode, [0x60]); uint pointer = Pointer(b);
                Accepted(a, b, 2, 2, 16);
                Check(b.Cpu.GetRegister(Register.Sp) == RawPointer(b, pointer - (uint)(b.Width * 8)));
                for (int register = 0; register < 8; register++)
                {
                    uint input = register == 4 ? b.InitialSp : Inputs[register];
                    Check(Read(b, pointer - (uint)((register + 1) * b.Width)) == (b.Width == 2 ? input & 65535 : input), "PUSHA stored another register/order or a changing SP.");
                }
            });
            test($"DecodedStack POPA reads all slots while discarding saved SP mode{mode}", () =>
            {
                var a = Make(mode, [0x61]); var b = Make(mode, [0x61]); uint pointer = Pointer(b);
                Accepted(a, b, 2, 2, 9);
                for (int register = 0; register < 8; register++)
                {
                    uint expected = register == 4 ? RawPointer(b, pointer + (uint)(b.Width * 8)) : b.Width == 4 ? Words[7 - register]
                        : (Inputs[register] & 0xffff0000) | (Words[7 - register] & 65535);
                    Check(b.Cpu.GetRegister((Register)register) == expected, "POPA adopted discarded SP, changed ordering or lost upper register bits.");
                }
            });
            test($"DecodedStack near CALL stores actual return and stops before target fetch mode{mode}", () =>
            {
                int width = mode is 3 or 4 or 7 or 8 ? 4 : 2; uint start = mode is 9 or 10 ? 0x100u : 0x2000u;
                var a = Make(mode, Call(width, start, start + 0x80)); var b = Make(mode, Call(width, start, start + 0x80)); uint pointer = Pointer(b);
                Accepted(a, b, 1, 2, 1);
                Check(b.Cpu.Eip == start + 0x80 && Read(b, pointer - (uint)width) == start + 2u + (uint)width &&
                    b.Cpu.GetRegister(Register.Sp) == RawPointer(b, pointer - (uint)width), "CALL return address, target or SP is wrong.");
            });
            foreach (bool adjustValue in new[] { false, true })
            {
                bool adjust = adjustValue;
                test($"DecodedStack near RET and optional skipped arguments mode{mode} adjust={adjust}", () =>
                {
                    var a = Make(mode, adjust ? [0xc2, 0x17, 0] : [0xc3]); var b = Make(mode, adjust ? [0xc2, 0x17, 0] : [0xc3]);
                    uint pointer = Pointer(b), target = b.Start + 0x80;
                    Write(a, pointer, target); Write(b, pointer, target);
                    Accepted(a, b, 1, adjust ? 2 : 1, 1);
                    Check(b.Cpu.Eip == target && b.Cpu.GetRegister(Register.Sp) == RawPointer(b, pointer + (uint)b.Width + (adjust ? 23u : 0)),
                        "RET used another current target or incorrectly accessed its argument adjustment.");
                });
            }
        }

        test("DecodedStack warm RET rereads the current saved target instead of retaining previous stack data", () =>
        {
            var a = Make(8, [0xc3]); var b = Make(8, [0xc3]); uint pointer = Pointer(b);
            Write(a, pointer, 0x2400); Write(b, pointer, 0x2400);
            var state = b.Cpu.SaveState(); byte[] ram = (byte[])b.Memory.Ram.Clone();
            Check(b.Cpu.TryRunDecodedBatch(32) == 1); ram.CopyTo(b.Memory.Ram, 0); b.Cpu.LoadState(state);
            Write(a, pointer, 0x2500); Write(b, pointer, 0x2500); Accepted(a, b, 1, 1, 1);
            Check(b.Cpu.Eip == 0x2500, "Warm RET retained the old peeked return target.");
        });
        foreach (bool callValue in new[] { false, true })
        {
            bool call = callValue;
            test($"DecodedStack future target PF is delivered only by the following original fetch call={call}", () =>
            {
                var a = Make(8, call ? Call(4, 0x2000, 0x3000) : [0xc3]); var b = Make(8, call ? Call(4, 0x2000, 0x3000) : [0xc3]);
                if (!call) { Write(a, Pointer(a), 0x3000); Write(b, Pointer(b), 0x3000); }
                PagingTests.Pte(a.Memory, 0x3000, 0, 0); PagingTests.Pte(b.Memory, 0x3000, 0, 0);
                Accepted(a, b, 1, call ? 2 : 1, 1); Check(b.Cpu.Eip == 0x3000 && b.Cpu.PageFaults == 0);
                a.Cpu.Step(); b.Cpu.Step(); Equal(a, b);
                Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000 && b.Cpu.Eip == 0x6000, "Target PF was prefetched, suppressed or reported at the wrong byte.");
            });
        }
        foreach (bool pushValue in new[] { false, true })
        {
            bool push = pushValue;
            test($"DecodedStack actual SS address wrap stays on the original scalar path push={push}", () =>
            {
                var a = Make(3, [push ? (byte)0x50 : (byte)0x58]); var b = Make(3, [push ? (byte)0x50 : (byte)0x58]);
                foreach (var r in new[] { a, b }) r.Cpu.SetRegister(Register.Sp, push ? 0x5a5a0001u : 0x5a5afffcu);
                Rejected(a, b);
            });
        }
        test("DecodedStack 32-bit post-pop wrap is retained by ordinary Step", () =>
        {
            var a = Make(4, [0x58]); var b = Make(4, [0x58]);
            a.Cpu.SetRegister(Register.Sp, 0xfffffffc); b.Cpu.SetRegister(Register.Sp, 0xfffffffc); Rejected(a, b);
            Check(b.Cpu.GetRegister(Register.Sp) == 0, "Original 32-bit stack wrap did not occur.");
        });
        foreach (bool pushValue in new[] { false, true })
        {
            bool push = pushValue;
            test($"DecodedStack multiword page boundary retains complete original preflight push={push}", () =>
            {
                var a = Make(8, [push ? (byte)0x60 : (byte)0x61]); var b = Make(8, [push ? (byte)0x60 : (byte)0x61]);
                a.Cpu.SetRegister(Register.Sp, push ? 0x19008u : 0x18ff0u); b.Cpu.SetRegister(Register.Sp, push ? 0x19008u : 0x18ff0u); Rejected(a, b);
            });
        }
        foreach (bool dirtyValue in new[] { false, true })
        {
            bool dirty = dirtyValue;
            test($"DecodedStack cold or clean writable stack retains first dirty/walk effects cold={dirty}", () =>
            {
                var a = Make(8, [0x50], warm: !dirty, dirty: false); var b = Make(8, [0x50], warm: !dirty, dirty: false); Rejected(a, b);
                Check(Read(b, Pointer(b)) == Inputs[0], "Original fallback push did not write its real operand.");
            });
        }
        foreach (bool pushValue in new[] { false, true })
        {
            bool push = pushValue;
            test($"DecodedStack mapped stack byte is never speculatively accessed push={push}", () =>
            {
                var a = Make(8, [push ? (byte)0x50 : (byte)0x58]); var b = Make(8, [push ? (byte)0x50 : (byte)0x58]);
                uint pointer = Pointer(a) - (push ? 4u : 0); var left = new Peer(); var right = new Peer();
                a.Memory.Map(pointer, 1, left, "Own decoded stack MMIO"); b.Memory.Map(pointer, 1, right, "Own decoded stack MMIO"); Rejected(a, b);
                Check(left.Reads == (push ? 0 : 1) && right.Reads == left.Reads && left.Writes == (push ? 1 : 0) && right.Writes == left.Writes,
                    "Stack admission performed or suppressed an actual mapped byte transfer.");
            });
        }
        test("DecodedStack readonly V86 push keeps the actual present/user/write PF and original monitor frame", () =>
        {
            var a = Make(10, [0x50], warm: false); var b = Make(10, [0x50], warm: false);
            foreach (var r in new[] { a, b })
            {
                PagingTests.Pte(r.Memory, 0x48000, 0x48000, 0x45);
                Warm(r, 0x8fc0, false); Rewind(r);
            }
            Rejected(a, b); Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x48ffe && !b.Cpu.Virtual8086,
                "Original readonly user-stack PF was not delivered at the pushed word.");
        });
        test("DecodedStack readonly stack remains valid for an actual POP", () =>
        {
            var a = Make(8, [0x58], warm: false); var b = Make(8, [0x58], warm: false);
            foreach (var r in new[] { a, b })
            {
                PagingTests.Pte(r.Memory, 0x19000, 0x19000, 0x45);
                Warm(r, 0x19000, false); Rewind(r);
            }
            Accepted(a, b, 2, 2, 1); Check(b.Cpu.GetRegister(Register.Ax) == Words[0]);
        });
        foreach (byte opcode in new byte[] { 0xe8, 0xc2 })
            test($"DecodedStack incomplete code keeps next-page PF before any stack access opcode={opcode:X2}", () =>
            {
                var a = Make(8, [0x90]); var b = Make(8, [0x90]);
                foreach (var r in new[] { a, b })
                { r.Cpu.Eip = 0x2fff; r.Memory.Ram[0x2fff] = opcode; PagingTests.Pte(r.Memory, 0x3000, 0, 0); }
                Rejected(a, b); Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000, "CALL/RET immediate was prefetched or its code PF suppressed.");
            });
        foreach (string reason in new[] { "unusable", "limit", "readonly", "expand-down" })
            test("DecodedStack current SS fault remains scalar " + reason, () =>
            {
                var a = Make(8, [0x50]); var b = Make(8, [0x50]);
                foreach (var r in new[] { a, b }) SegmentInput(r, Segment.Ss, cache => reason switch
                {
                    "unusable" => cache with { Usable = false }, "limit" => cache with { Limit = 0x18ffd },
                    "readonly" => cache with { Access = 0x91 }, _ => cache with { Access = 0x97, Limit = 0x18ffc }
                });
                Rejected(a, b);
            });
        test("DecodedStack genuine V86 AC inputs retain the original misaligned stack fault", () =>
        {
            var a = Make(10, [0x58]); var b = Make(10, [0x58]);
            foreach (var r in new[] { a, b }) { Field(r, "Cr0", r.Cpu.Cr0 | 0x40000); r.Cpu.Eflags |= 0x40000; r.Cpu.SetRegister(Register.Sp, 0x5a5a9001); }
            Rejected(a, b); Check(b.Cpu.AlignmentFaults == 1 && !b.Cpu.Virtual8086, "Actual stack AC was not delivered through the original V86 frame.");
        });
        test("DecodedStack RET adjustment does not validate unaccessed argument bytes", () =>
        {
            var a = Make(8, [0xc2, 0x40, 0]); var b = Make(8, [0xc2, 0x40, 0]);
            foreach (var r in new[] { a, b }) { Write(r, Pointer(r), 0x2400); SegmentInput(r, Segment.Ss, cache => cache with { Limit = 0x19003 }); }
            Accepted(a, b, 1, 2, 1); Check(b.Cpu.Eip == 0x2400 && b.Cpu.GetRegister(Register.Sp) == 0x19044, "RET probed or rejected skipped arguments.");
        });
        test("DecodedStack taken RET target CS fault retains the original prior stack-read effects", () =>
        {
            var a = Make(8, [0xc3]); var b = Make(8, [0xc3]);
            foreach (var r in new[] { a, b }) { Write(r, Pointer(r), 0x2400); SegmentInput(r, Segment.Cs, cache => cache with { Limit = 0x2300 }); }
            Rejected(a, b);
        });
        test("DecodedStack A20 folded POP retains full unfolded SP and actual physical data", () =>
        {
            var a = Make(4, [0x58]); var b = Make(4, [0x58]);
            foreach (var r in new[] { a, b }) { r.Memory.A20Enabled = false; r.Cpu.SetRegister(Register.Sp, 0x109000); r.Memory.WriteDword(0x9000, 0xcafebabe); }
            Accepted(a, b, 2, 2, 1); Check(b.Cpu.GetRegister(Register.Ax) == 0xcafebabe && b.Cpu.GetRegister(Register.Sp) == 0x109004);
        });
        test("DecodedStack PUSH rejects a physical A20 alias of its entire current code page", () =>
        {
            var a = Make(4, [0x50]); var b = Make(4, [0x50]);
            foreach (var r in new[] { a, b }) { r.Memory.A20Enabled = false; r.Cpu.SetRegister(Register.Sp, 0x102005); }
            Rejected(a, b); Check(b.Memory.ReadDword(0x2001) == Inputs[0], "Original folded self-modifying PUSH did not occur.");
        });
        test("DecodedStack prefixed V86 32-bit PUSH remains an actual ordinary instruction", () =>
        {
            var a = Make(10, [0x66, 0x50]); var b = Make(10, [0x66, 0x50]); uint pointer = Pointer(b);
            Rejected(a, b); Check(b.Memory.ReadDword(b.Cpu.GetSegmentBase(Segment.Ss) + pointer - 4) == Inputs[0], "Scalar V86 operand prefix did not store the full register.");
        });
        test("DecodedStack strict SnapshotStore resumes an actual saved CALL and original RET future", () =>
        {
            using var fixture = new SnapshotTests.Fixture(start: false); var original = fixture.Machine;
            original.Cpu.SetSegment(Segment.Cs, 0); original.Cpu.SetSegment(Segment.Ss, 0); original.Cpu.SetSegment(Segment.Ds, 0);
            original.Cpu.Eip = 0x1000; original.Cpu.SetRegister(Register.Sp, 0x9000); original.Cpu.Eflags = 2;
            original.Memory.Ram.AsSpan(0x1000, 0x20).Fill(0x90);
            new byte[] { 0xb8, 0x34, 0x12, 0x50, 0x5b, 0xe8, 8, 0, 0x90, 0xf4 }.CopyTo(original.Memory.Ram, 0x1000);
            new byte[] { 0x43, 0xc3 }.CopyTo(original.Memory.Ram, 0x1010);
            Check(original.Cpu.TryRunDecodedBatch(64) == 4 && original.Cpu.Eip == 0x1010 && original.Cpu.GetRegister(Register.Sp) == 0x8ffe);
            for (int i = 0; i < 4; i++) original.Scheduler.AdvanceCycles(4, 33_000_000);
            original.Audio.AdvanceTo(original.Scheduler.Now);
            using var restored = fixture.Restore();
            Check(original.RunSteps(2, null, false) == 2 && restored.Cpu.TryRunDecodedBatch(64) == 2);
            for (int i = 0; i < 2; i++) restored.Scheduler.AdvanceCycles(4, 33_000_000);
            restored.Audio.AdvanceTo(restored.Scheduler.Now);
            Check(JsonSerializer.Serialize(original.CaptureHardwareState(), StateValues.Json) == JsonSerializer.Serialize(restored.CaptureHardwareState(), StateValues.Json) &&
                original.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram), "Strict saved CALL/RET future differs in complete hardware, clock or RAM.");
            Check(restored.Cpu.Eip == 0x1008 && restored.Cpu.GetRegister(Register.Sp) == 0x9000 && restored.Cpu.GetRegister(Register.Bx) == 0x1235);
        });
    }
}
