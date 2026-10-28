// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Whole CPU/RAM comparisons and independent addressing/effect controls for the admitted ordinary-RAM memory path.</summary>
internal static class IntegerMemoryBatchTests
{
    private static readonly uint[] Registers = [0x3000, 0x4000, 0x5000, 0x7000, 0x18000, 0x8000, 0x100, 0x200];
    private const uint Pattern = 0xa19b7d35;
    /// <summary>Own CPU, RAM and explicit address-width/segment inputs for one independent scalar or batched fixture.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory, int Mode, uint Start, uint Offset, Segment DataSegment);

    /// <summary>Requires an independently specified address, result, guarded byte or complete state equality.</summary>
    private static void Check(bool value, string why = "Integer memory batch invariant failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Identifies the two fixtures whose actual cached CS defaults to 32-bit operands and addresses.</summary>
    private static bool Wide(int mode) => mode is 2 or 4;

    /// <summary>Sets known register inputs without changing segment, paging, TLB or guest instruction history.</summary>
    private static void Seed(Rig r)
    {
        for (int i = 0; i < 8; i++) r.Cpu.SetRegister((Register)i, Registers[i]);
        r.Cpu.Eflags = r.Cpu.Virtual8086 ? 0x23003u : 3u;
    }

    /// <summary>Builds real, PE16/32, paged PE16/32 and actual IRET-entered V86 fixtures with private code and tables.</summary>
    private static Rig Make(int mode, byte[] instruction, uint offset = 0x7000, Segment segment = Segment.Ds,
        bool dirty = true, bool warm = true)
    {
        var clock = new DeviceScheduler(); X86Cpu cpu; MemoryBus memory; uint start;
        if (mode == 0)
        {
            memory = new(2 * 1024 * 1024); cpu = new(memory, new(), X86CpuModel.IntelP54cB5Subset, clock, 100000000);
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0); cpu.SetSegment(Segment.Ds, 0); start = 0x2000;
        }
        else
        {
            var pair = mode is 3 or 4 or 6
                ? PagingTests.Create([], model: X86CpuModel.IntelP54cB5Subset, timestampSource: clock, timestampFrequency: 100000000)
                : ProtectedModeTests.Rig([], code32: mode != 1, model: X86CpuModel.IntelP54cB5Subset,
                    timestampSource: clock, timestampFrequency: 100000000);
            cpu = pair.Cpu; memory = pair.Memory;
            if (mode is 5 or 6)
            {
                PagingTests.Tss(cpu, memory);
                uint stack = cpu.GetRegister(Register.Sp);
                uint[] frame = [0x100, 0x3000, 0x23003, 0x8000, 0x4000, 0x6000, 0x5000, 0x7000, 0x8000];
                for (int i = 0; i < frame.Length; i++) memory.WriteDword(stack + (uint)i * 4, frame[i]);
                PagingTests.At(cpu, memory, [0xcf], 0x2800); start = 0x100;
                Check(cpu.Virtual8086 && cpu.Eip == start, "Fixture did not enter actual V86 through IRET.");
            }
            else
            {
                start = 0x2000;
                if (mode == 3) PagingTests.At(cpu, memory, [0xea, 0, 0x20, 0, 0, 0x18, 0], 0x2800);
                else cpu.Eip = start;
            }
        }
        var rig = new Rig(cpu, memory, mode, start, offset, segment);
        Seed(rig);
        if (warm) WarmData(rig, offset, segment, dirty);
        uint linear = unchecked(cpu.GetSegmentBase(segment) + offset);
        if ((ulong)linear + 8 < (ulong)memory.Ram.Length)
        {
            if (linear >= 4) memory.Ram.AsSpan((int)linear - 4, 12).Fill(0xa5);
            memory.WriteDword(linear, Pattern);
        }
        uint code = unchecked(cpu.GetSegmentBase(Segment.Cs) + start);
        new byte[] { 0x90 }.Concat(instruction).Concat(new byte[] { 0x90, 0xf4 }).ToArray().CopyTo(memory.Ram, code);
        Rewind(rig); return rig;
    }

    /// <summary>Warms one actual data translation using a normal scalar byte instruction; a write also establishes the required dirty PTE.</summary>
    private static void WarmData(Rig r, uint offset, Segment segment, bool write)
    {
        var code = new List<byte>();
        if (segment == Segment.Ss) code.Add(0x36);
        if (!Wide(r.Mode)) code.Add(0x67);
        code.Add(write ? (byte)0xa2 : (byte)0xa0);
        for (int i = 0; i < 4; i++) code.Add((byte)(offset >> (i * 8)));
        uint scratch = r.Start + 0x200;
        code.ToArray().CopyTo(r.Memory.Ram, unchecked(r.Cpu.GetSegmentBase(Segment.Cs) + scratch));
        r.Cpu.Eip = scratch; Check(r.Cpu.Step() == 4 && !r.Cpu.TripleFaulted, "Actual data-translation warmup failed.");
    }

    /// <summary>Executes the original NOP on the current code page, then sets exact shared inputs for the measured instruction.</summary>
    private static void Rewind(Rig r)
    {
        r.Cpu.Eip = r.Start; Seed(r);
        Check(r.Cpu.Step() == 4 && r.Cpu.Eip == r.Start + 1 && !r.Cpu.TripleFaulted, "Code-page warm NOP failed.");
        Seed(r);
    }

    /// <summary>Compares every CPU member, translation record/counter and all RAM/PTE/guard bytes with no exclusions.</summary>
    private static void Equal(Rig a, Rig b)
    {
        var left = a.Cpu.SaveState(); var right = b.Cpu.SaveState();
        Check(left.Type == right.Type && left.Version == right.Version && left.Fields.Keys.SequenceEqual(right.Fields.Keys));
        foreach (var name in left.Fields.Keys)
            Check(left.Fields[name].GetRawText() == right.Fields[name].GetRawText(), "Complete CPU field differs: " + name);
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete physical RAM/PTE/guard bytes differ.");
    }

    /// <summary>Calls the explicitly selected stateless or decoded entry point without any shared mutable engine switch.</summary>
    private static int RunBatch(X86Cpu cpu, bool decoded) => decoded ? cpu.TryRunDecodedBatch(32) : cpu.TryRunIntegerBatch(32);

    /// <summary>Requires the exact scalar work, then repeats decoded execution from identical CPU/RAM input with its derived cache retained.</summary>
    private static void Accepted(Rig scalar, Rig batched, int expected, bool decoded)
    {
        Equal(scalar, batched);
        StateBlock? before = decoded ? batched.Cpu.SaveState() : null;
        byte[]? originalRam = decoded ? (byte[])batched.Memory.Ram.Clone() : null;
        int count = RunBatch(batched.Cpu, decoded);
        Check(count == expected, "Expected admitted memory work, got " + count);
        for (int i = 0; i < count; i++) Check(scalar.Cpu.Step() == 4);
        Equal(scalar, batched);
        if (decoded)
        {
            // Restore every authoritative byte/member, retaining only the CPU-keyed derived encoding cache.
            // The separate clock is untouched: these CPU-only fixtures do not advance it in either path.
            originalRam!.CopyTo(batched.Memory.Ram, 0); batched.Cpu.LoadState(before!);
            Check(JsonSerializer.Serialize(batched.Cpu.SaveState(), StateValues.Json) == JsonSerializer.Serialize(before, StateValues.Json) &&
                originalRam.AsSpan().SequenceEqual(batched.Memory.Ram), "Warm-cache replay did not restore its entire original CPU/RAM input.");
            Check(RunBatch(batched.Cpu, true) == expected, "Warm decoded cache changed the admitted element count.");
            Equal(scalar, batched);
        }
    }

    /// <summary>Requires zero mutation on admission rejection and the exact original subsequent Step or fault result.</summary>
    private static void Rejected(Rig scalar, Rig batched, bool decoded)
    {
        Equal(scalar, batched); Check(RunBatch(batched.Cpu, decoded) == 0, "Unsafe memory operation admitted."); Equal(scalar, batched);
        if (decoded)
        {
            Check(RunBatch(batched.Cpu, true) == 0, "Warm decoded cache bypassed the original memory guard."); Equal(scalar, batched);
        }
        Exception? a = null, b = null;
        try { scalar.Cpu.Step(); } catch (Exception error) { a = error; }
        try { batched.Cpu.Step(); } catch (Exception error) { b = error; }
        Check(a?.GetType() == b?.GetType() && a?.Message == b?.Message, "Ordinary future exception behavior differs.");
        Equal(scalar, batched);
    }

    /// <summary>Encodes one direct DS memory reference from the actual default address width, with independently specified operand register.</summary>
    private static byte[] Absolute(byte opcode, int register, bool wide, uint offset)
    {
        var bytes = new List<byte> { opcode };
        if (opcode is not (>= 0xa0 and <= 0xa3)) bytes.Add((byte)((register << 3) | (wide ? 5 : 6)));
        for (int i = 0; i < (wide ? 4 : 2); i++) bytes.Add((byte)(offset >> (i * 8)));
        return bytes.ToArray();
    }

    /// <summary>Reads an encoded byte/word/dword register from the independent fixed input vector.</summary>
    private static uint InputRegister(int register, int width) => width == 1
        ? register < 4 ? Registers[register] & 255 : (Registers[register - 4] >> 8) & 255
        : width == 2 ? Registers[register] & 65535 : Registers[register];

    /// <summary>Computes only the independent width-masked value of each ALU family with a known carry input of one.</summary>
    private static uint AluValue(int operation, uint a, uint b, int width)
    {
        uint result = operation switch
        {
            0 => unchecked(a + b), 1 => a | b, 2 => unchecked(a + b + 1), 3 => unchecked(a - b - 1),
            4 => a & b, 5 or 7 => unchecked(a - b), 6 => a ^ b, _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return width == 1 ? result & 255 : width == 2 ? result & 65535 : result;
    }

    /// <summary>Reads the actual exact-width target through the fixture's original bus after its guest instruction has completed.</summary>
    private static uint Data(Rig r, int width)
    {
        uint linear = unchecked(r.Cpu.GetSegmentBase(r.DataSegment) + r.Offset);
        return width == 1 ? r.Memory.ReadByte(linear) : width == 2 ? r.Memory.ReadWord(linear) : r.Memory.ReadDword(linear);
    }

    /// <summary>Changes one deliberately specified private test input while retaining the complete generated CPU state shape.</summary>
    private static void Field<T>(Rig r, string field, T value)
    {
        var state = r.Cpu.SaveState(); state.Fields[field] = JsonSerializer.SerializeToElement(value, StateValues.Json); r.Cpu.LoadState(state);
    }

    /// <summary>Changes only an explicitly selected hidden segment input for no-effect admission and real future-fault controls.</summary>
    private static void SegmentInput(Rig r, Segment segment, Func<SegmentDescriptorCache, SegmentDescriptorCache> change)
    {
        var caches = r.Cpu.SaveState().Fields["_segmentCaches"].Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        caches[(int)segment] = change(caches[(int)segment]); Field(r, "_segmentCaches", caches);
    }

    /// <summary>Controlled mapped byte for proving that admission performs no observable MMIO operation.</summary>
    private sealed class Peer : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Returns a controlled mapped byte only after an actual ordinary bus read.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x5a; }
        /// <summary>Counts only actual ordinary mapped writes, without CPU callbacks or host effects.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    /// <summary>Controlled unmapped-space decoder for proving that admission performs no dynamic lookup or read.</summary>
    private sealed class DynamicPeer : IMemoryDecoder
    {
        public int Queries, Reads;
        /// <summary>Recognizes the private high byte without opening a host endpoint.</summary>
        public bool Decodes(uint address) { Queries++; return address == 0x400000; }
        /// <summary>Returns the controlled open-space byte after an actual fallback read.</summary>
        public byte ReadDecodedByte(uint address) { Reads++; return 0x6d; }
        /// <summary>Rejects any unexpected actual write to this read-only private peer.</summary>
        public void WriteDecodedByte(uint address, byte value) => throw new InvalidOperationException("Unexpected own dynamic write.");
    }

    /// <summary>Registers the original stateless memory-path matrix with its unchanged names and independent oracles.</summary>
    public static void AddTo(Action<string, Action> test) => AddCases(test, false);

    /// <summary>Runs the identical complete matrix against cold and warm decoded plans, preserving a separate visible test prefix.</summary>
    public static void AddDecodedTo(Action<string, Action> test)
    {
        AddCases((name, run) => test("Decoded RAM matrix " + name, run), true);
        AddImmediateCases(test);
    }

    /// <summary>Encodes a direct group-1 memory operand and its separate immediate fetch at the actual code-default width.</summary>
    private static byte[] ImmediateMemory(byte opcode, int operation, bool wide, uint offset, uint immediate)
    {
        var code = new List<byte>(Absolute(opcode, operation, wide, offset));
        int count = opcode == 0x81 ? wide ? 4 : 2 : 1;
        for (int i = 0; i < count; i++) code.Add((byte)(immediate >> (i * 8)));
        return code.ToArray();
    }

    /// <summary>Checks immediate-memory ALU effects, live addressing and original fault fallbacks with full cold/warm CPU/RAM equality.</summary>
    private static void AddImmediateCases(Action<string, Action> test)
    {
        for (int modeValue = 0; modeValue < 7; modeValue++)
        {
            int mode = modeValue; bool wide = Wide(mode);
            for (int operationValue = 0; operationValue < 8; operationValue++)
            {
                int operation = operationValue;
                test($"Decoded immediate RAM all encodings and signed boundaries mode{mode} operation{operation}", () =>
                {
                    foreach (byte opcode in new byte[] { 0x80, 0x81, 0x83 })
                    foreach (uint encoded in new uint[] { 0, 0x7f, 0x80, 0xff, 0x87c3e19d })
                    {
                        byte[] code = ImmediateMemory(opcode, operation, wide, 0x7000, encoded);
                        var a = Make(mode, code); var b = Make(mode, code);
                        Accepted(a, b, 2, true);
                        int width = opcode == 0x80 ? 1 : wide ? 4 : 2;
                        uint mask = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
                        uint immediate = opcode == 0x83 ? unchecked((uint)(sbyte)encoded)
                            : encoded & (opcode == 0x80 ? 255u : mask);
                        uint expected = operation == 7 ? Pattern & mask : AluValue(operation, Pattern & mask, immediate, width);
                        Check(Data(b, width) == expected, "Independent immediate/sign/ALU result differs.");
                    }
                });
            }
            test($"Decoded immediate RAM current SS addressing mode{mode}", () =>
            {
                uint offset = wide ? 0x18010u : 0x8081u;
                byte[] code = wide ? [0x83,0x6c,0x24,0x10,0x80] : [0x83,0x6a,0x81,0x80];
                var a = Make(mode, code, offset, Segment.Ss); var b = Make(mode, code, offset, Segment.Ss);
                Accepted(a, b, 2, true);
                Check(Data(b, wide ? 4 : 2) == AluValue(5, Pattern, 0xffffff80, wide ? 4 : 2),
                    "Current BP/SI or ESP/SIB/SS addressing differs.");
            });
        }
        foreach (int mode in new[] { 4, 6 })
        foreach (bool compare in new[] { false, true })
            test($"Decoded immediate RAM readonly PTE mode{mode} compare{compare}", () =>
            {
                byte[] code = ImmediateMemory(0x83, compare ? 7 : 0, Wide(mode), 0x7000, 0xff);
                var a = Make(mode, code, warm: false); var b = Make(mode, code, warm: false);
                foreach (var r in new[] { a, b })
                {
                    uint linear = r.Cpu.GetSegmentBase(Segment.Ds) + 0x7000;
                    PagingTests.Pte(r.Memory, linear, linear, 0x45);
                    WarmData(r, 0x7000, Segment.Ds, false); Rewind(r);
                }
                if (compare) { Accepted(a, b, 2, true); Check(Data(b, Wide(mode) ? 4 : 2) == (Wide(mode) ? Pattern : Pattern & 65535)); }
                else { Rejected(a, b, true); Check(b.Cpu.PageFaults == 1, "Original readonly write PF was not delivered."); }
            });
        foreach (bool compare in new[] { false, true })
            test($"Decoded immediate RAM clean PTE dirty transition compare{compare}", () =>
            {
                byte[] code = ImmediateMemory(0x83, compare ? 7 : 0, true, 0x7000, 0x80);
                var a = Make(4, code, dirty: false); var b = Make(4, code, dirty: false);
                if (compare) Accepted(a, b, 2, true); else Rejected(a, b, true);
                Check(((b.Memory.ReadDword(0x1d01c) & 0x40) != 0) == !compare, "Original immediate read/write dirty behavior differs.");
            });
        foreach (bool compare in new[] { false, true })
            test($"Decoded immediate RAM code alias compare{compare}", () =>
            {
                byte[] code = ImmediateMemory(0x83, compare ? 7 : 0, true, 0x2001, 0x80);
                var a = Make(4, code, 0x2001); var b = Make(4, code, 0x2001);
                if (compare) Accepted(a, b, 2, true); else Rejected(a, b, true);
            });
        test("Decoded immediate RAM MMIO rejection has no speculative bus read", () =>
        {
            byte[] code = ImmediateMemory(0x80, 0, true, 0x7000, 1);
            var a = Make(2, code); var b = Make(2, code); var left = new Peer(); var right = new Peer();
            a.Memory.Map(0x7000, 1, left, "own-immediate-peer"); b.Memory.Map(0x7000, 1, right, "own-immediate-peer");
            Rejected(a, b, true);
            Check(left.Reads == 1 && right.Reads == 1 && left.Writes == 1 && right.Writes == 1,
                "Immediate admission read, wrote or suppressed the original device access.");
        });
        test("Decoded immediate RAM changed immediate invalidates a warm plan", () =>
        {
            byte[] code = ImmediateMemory(0x81, 6, true, 0x7000, 0x12345678);
            var a = Make(4, code); var b = Make(4, code); Accepted(a, b, 2, true);
            foreach (var r in new[] { a, b })
            {
                r.Memory.WriteByte(r.Cpu.GetSegmentBase(Segment.Cs) + r.Start + (uint)code.Length, 0x87);
                r.Memory.WriteDword(r.Cpu.GetSegmentBase(Segment.Ds) + r.Offset, Pattern); Rewind(r);
            }
            Accepted(a, b, 2, true);
            Check(Data(b, 4) == (Pattern ^ 0x87345678), "A warm plan reused the old immediate bytes.");
        });
        test("Decoded immediate RAM missing final immediate page preserves original fetch PF", () =>
        {
            byte[] code = ImmediateMemory(0x81, 0, true, 0x7000, 0x12345678);
            var a = Make(4, code); var b = Make(4, code);
            foreach (var r in new[] { a, b })
            {
                code.CopyTo(r.Memory.Ram, 0x2ff8); PagingTests.Pte(r.Memory, 0x3000, 0x3000, 0); r.Cpu.Eip = 0x2ff8;
            }
            Rejected(a, b, true);
            Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000 && Data(b, 4) == Pattern,
                "Missing immediate fetch was prefetched, skipped or changed the memory target.");
        });
        test("Decoded immediate RAM short code limit retains original operand-free fault", () =>
        {
            byte[] code = ImmediateMemory(0x81, 0, true, 0x7000, 0x12345678);
            var a = Make(4, code); var b = Make(4, code);
            foreach (var r in new[] { a, b }) SegmentInput(r, Segment.Cs, cache => cache with { Limit = r.Start + (uint)code.Length - 1 });
            Rejected(a, b, true); Check(Data(b, 4) == Pattern && b.Cpu.PageFaults == 0, "Code limit fault accessed the memory target.");
        });
        test("Decoded immediate RAM split data page retains original scalar accesses", () =>
        {
            byte[] code = ImmediateMemory(0x81, 2, true, 0x7fff, 0x87654321);
            var a = Make(4, code, 0x7fff); var b = Make(4, code, 0x7fff);
            Rejected(a, b, true);
        });
        test("Decoded immediate RAM unusable live data segment retains original fault", () =>
        {
            byte[] code = ImmediateMemory(0x83, 7, true, 0x7000, 0xff);
            var a = Make(4, code); var b = Make(4, code);
            foreach (var r in new[] { a, b }) SegmentInput(r, Segment.Ds, cache => cache with { Usable = false });
            Rejected(a, b, true); Check(b.Cpu.PageFaults == 0, "Unusable data segment reached paging.");
        });
    }

    /// <summary>Shares every addressing/effect/fault oracle while binding the selected entry point in each registered closure.</summary>
    private static void AddCases(Action<string, Action> test, bool decoded)
    {
        /// <summary>Uses this matrix registration's explicit execution engine for one admitted effect and all authoritative-state comparisons.</summary>
        void Accepted(Rig scalar, Rig batched, int expected = 2) => IntegerMemoryBatchTests.Accepted(scalar, batched, expected, decoded);
        /// <summary>Uses this matrix registration's explicit execution engine for rejection and the original subsequent instruction/fault.</summary>
        void Rejected(Rig scalar, Rig batched) => IntegerMemoryBatchTests.Rejected(scalar, batched, decoded);
        for (int value = 0; value < 7; value++)
        {
            int mode = value; bool wide = Wide(mode);
            foreach (byte opcodeValue in new byte[] { 0x88, 0x89, 0x8a, 0x8b, 0xa0, 0xa1, 0xa2, 0xa3 })
            {
                byte opcode = opcodeValue;
                test($"IntegerMemory complete MOV/guard mode{mode} opcode{opcode:X2}", () =>
                {
                    for (int register = 0; register < (opcode >= 0xa0 ? 1 : 8); register++)
                    {
                        byte[] code = Absolute(opcode, register, wide, 0x7000);
                        var a = Make(mode, code); var b = Make(mode, code); Accepted(a, b);
                        int width = (opcode & 1) == 0 ? 1 : wide ? 4 : 2;
                        bool writes = opcode is 0x88 or 0x89 or 0xa2 or 0xa3;
                        uint mask = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
                        Check(Data(b, width) == (writes ? InputRegister(register, width) : Pattern & mask), "MOV wrote another width, value or address.");
                        uint address = unchecked(b.Cpu.GetSegmentBase(Segment.Ds) + 0x7000);
                        Check(b.Memory.ReadByte(address - 1) == 0xa5 && b.Memory.ReadByte(address + (uint)width) ==
                            (width < 4 ? (byte)(Pattern >> (width * 8)) : (byte)0xa5), "MOV changed a neighboring target byte.");
                    }
                });
            }
            for (int operationValue = 0; operationValue < 8; operationValue++)
            {
                int operation = operationValue;
                test($"IntegerMemory complete ALU/CMP directions mode{mode} operation{operation}", () =>
                {
                    for (int form = 0; form < 4; form++)
                    {
                        byte opcode = (byte)(operation * 8 + form); int register = (operation + form) & 7;
                        int width = (form & 1) == 0 ? 1 : wide ? 4 : 2;
                        byte[] code = Absolute(opcode, register, wide, 0x7000);
                        var a = Make(mode, code); var b = Make(mode, code); Accepted(a, b);
                        uint mask = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
                        uint memory = Pattern & mask, input = InputRegister(register, width);
                        uint expected = AluValue(operation, form < 2 ? memory : input, form < 2 ? input : memory, width);
                        Check(Data(b, width) == (form < 2 && operation != 7 ? expected : memory), "ALU/CMP changed the wrong memory direction or operand.");
                    }
                });
            }
            foreach (byte opcodeValue in new byte[] { 0x84, 0x85 })
            {
                byte opcode = opcodeValue;
                test($"IntegerMemory actual TEST is read-only mode{mode} opcode{opcode:X2}", () =>
                {
                    for (int register = 0; register < 8; register++)
                    {
                        int width = opcode == 0x84 ? 1 : wide ? 4 : 2;
                        byte[] code = Absolute(opcode, register, wide, 0x7000);
                        var a = Make(mode, code); var b = Make(mode, code); Accepted(a, b);
                        uint mask = width == 1 ? 255u : width == 2 ? 65535u : uint.MaxValue;
                        uint result = (Pattern & mask) & InputRegister(register, width);
                        Check(Data(b, width) == (Pattern & mask) && (b.Cpu.Eflags & (X86Cpu.Carry | X86Cpu.Overflow)) == 0 &&
                            ((b.Cpu.Eflags & X86Cpu.Zero) != 0) == (result == 0), "TEST stored data or retained an impossible result flag.");
                    }
                });
            }
            test($"IntegerMemory every ModRM memory addressing family mode{mode}", () =>
            {
                uint[] lowBases = [0x7100, 0x7200, 0x8100, 0x8200, 0x100, 0x200, 0x8000, 0x7000];
                for (int mod = 0; mod < 3; mod++) for (int rm = 0; rm < 8; rm++)
                {
                    var code = new List<byte> { 0x8b, (byte)(mod * 64 + rm) };
                    uint offset; Segment segment;
                    if (!wide)
                    {
                        offset = mod == 0 && rm == 6 ? 0x1234u : lowBases[rm];
                        segment = rm is 2 or 3 or 6 && !(mod == 0 && rm == 6) ? Segment.Ss : Segment.Ds;
                        if (mod == 0 && rm == 6) code.AddRange([0x34, 0x12]);
                        if (mod == 1) { code.Add(0x81); offset = unchecked(offset - 127) & 65535; }
                        if (mod == 2) { code.AddRange([0x01, 0x80]); offset = (offset + 0x8001) & 65535; }
                    }
                    else
                    {
                        if (rm == 4) code.Add(0x24);
                        offset = mod == 0 && rm == 5 ? 0x50000u : Registers[rm];
                        segment = rm is 4 or 5 && !(mod == 0 && rm == 5) ? Segment.Ss : Segment.Ds;
                        if (mod == 0 && rm == 5) code.AddRange([0, 0, 5, 0]);
                        if (mod == 1) { code.Add(0x81); offset = unchecked(offset - 127); }
                        if (mod == 2) { code.AddRange([0xfb, 0xff, 0xff, 0xff]); offset = unchecked(offset - 5); }
                    }
                    var a = Make(mode, code.ToArray(), offset, segment); var b = Make(mode, code.ToArray(), offset, segment); Accepted(a, b);
                    Check((b.Cpu.GetRegister(Register.Ax) & (wide ? uint.MaxValue : 65535u)) == (Pattern & (wide ? uint.MaxValue : 65535u)),
                        "Independent ModRM target was not read through the correct DS/SS and wrapped displacement.");
                }
            });
        }

        foreach (int mode in new[] { 2, 4 })
            test($"IntegerMemory all256 actual SIB values mode{mode}", () =>
            {
                for (int sib = 0; sib < 256; sib++)
                {
                    int index = (sib >> 3) & 7, basis = sib & 7;
                    uint offset = unchecked((basis == 5 ? 0x50000u : Registers[basis]) + (index == 4 ? 0u : Registers[index] * (uint)(1 << (sib >> 6))));
                    Segment segment = basis == 4 ? Segment.Ss : Segment.Ds;
                    byte[] code = basis == 5 ? [0x8b, 4, (byte)sib, 0, 0, 5, 0] : [0x8b, 4, (byte)sib];
                    var a = Make(mode, code, offset, segment); var b = Make(mode, code, offset, segment); Accepted(a, b);
                    Check(b.Cpu.GetRegister(Register.Ax) == Pattern, "SIB scale/index/base/no-base formula read another target.");
                }
            });

        foreach (bool write in new[] { false, true })
        foreach (bool missing in new[] { false, true })
            test($"IntegerMemory cold or missing data page retains real future PF/AD effects write={write} missing={missing}", () =>
            {
                byte[] code = Absolute(write ? (byte)0xa3 : (byte)0xa1, 0, true, 0x7000);
                var a = Make(4, code, warm: false); var b = Make(4, code, warm: false);
                if (missing) { PagingTests.Pte(a.Memory, 0x7000, 0, 0); PagingTests.Pte(b.Memory, 0x7000, 0, 0); }
                Rejected(a, b);
                Check(missing ? b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x7000 : b.Cpu.PageWalks > 0, "Original future data walk/fault did not occur.");
            });

        foreach (bool write in new[] { false, true })
            test($"IntegerMemory cached clean PTE preserves actual read/first-write dirty semantics write={write}", () =>
            {
                byte[] code = Absolute(write ? (byte)0xa3 : (byte)0xa1, 0, true, 0x7000);
                var a = Make(4, code, dirty: false); var b = Make(4, code, dirty: false);
                Check((b.Memory.ReadDword(0x1d01c) & 0x40) == 0, "Fixture did not start with a clean actual PTE.");
                if (write) Rejected(a, b); else Accepted(a, b);
                Check(((b.Memory.ReadDword(0x1d01c) & 0x40) != 0) == write, "First writable fallback lost its actual PTE dirty write.");
            });

        foreach (int mode in new[] { 4, 6 })
            test($"IntegerMemory actual cached readonly PTE rejects write before effects mode{mode}", () =>
            {
                byte[] code = Absolute(0xa3, 0, Wide(mode), 0x7000);
                var a = Make(mode, code, warm: false); var b = Make(mode, code, warm: false);
                foreach (var r in new[] { a, b })
                {
                    uint linear = r.Cpu.GetSegmentBase(Segment.Ds) + 0x7000;
                    PagingTests.Pte(r.Memory, linear, linear, 0x45);
                    WarmData(r, 0x7000, Segment.Ds, false); Rewind(r);
                }
                Rejected(a, b);
                Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == (mode == 6 ? 0x57000u : 0x7000u), "Original writable-present PF was not delivered.");
            });

        foreach (bool store in new[] { false, true })
            test($"IntegerMemory coherent different-PTE code alias retains original read or scalar store store={store}", () =>
            {
                byte[] code = Absolute(store ? (byte)0xa3 : (byte)0xa1, 0, true, 0x7000);
                var a = Make(4, code); var b = Make(4, code);
                foreach (var r in new[] { a, b })
                {
                    PagingTests.Pte(r.Memory, 0x7000, 0x2000, 0x67);
                    PagingTests.At(r.Cpu, r.Memory, [0x0f, 1, 0x3d, 0, 0x70, 0, 0], 0x2200);
                    WarmData(r, 0x7000, Segment.Ds, false); Rewind(r);
                }
                if (store) Rejected(a, b); else Accepted(a, b);
                Check(store ? b.Memory.ReadDword(0x2000) == Registers[0] : b.Cpu.GetRegister(Register.Ax) == 0x7000a190,
                    "The different-PTE alias used a stale or unfolded data target.");
            });

        foreach (bool store in new[] { false, true })
            test($"IntegerMemory newly mapped data MMIO is never speculatively read store={store}", () =>
            {
                byte[] code = Absolute(store ? (byte)0xa2 : (byte)0xa0, 0, true, 0x7000);
                var a = Make(4, code); var b = Make(4, code); var left = new Peer(); var right = new Peer();
                a.Memory.Map(0x7000, 1, left, "Own memory batch MMIO"); b.Memory.Map(0x7000, 1, right, "Own memory batch MMIO");
                Rejected(a, b);
                Check(left.Reads == (store ? 0 : 1) && right.Reads == left.Reads && left.Writes == (store ? 1 : 0) && right.Writes == left.Writes,
                    "Admission invented or suppressed the ordinary mapped byte operation.");
            });

        foreach (uint offset in new uint[] { 0x7fff, 0x1ffffe })
            test($"IntegerMemory crossing page or installed RAM end stays scalar offset={offset:X}", () =>
            {
                byte[] code = Absolute(0xa1, 0, true, offset);
                var a = Make(4, code, offset); var b = Make(4, code, offset); Rejected(a, b);
            });

        foreach (byte[] codeValue in new byte[][] { [0xa1], [0x8b], [0x8b, 0x84, 0x24] })
        {
            byte[] code = codeValue;
            test("IntegerMemory incomplete code does not prefetch a missing operand page " + Convert.ToHexString(code), () =>
            {
                var a = Make(4, [0x90]); var b = Make(4, [0x90]);
                foreach (var r in new[] { a, b })
                {
                    r.Cpu.Eip = 0x3000u - (uint)code.Length;
                    code.CopyTo(r.Memory.Ram, r.Cpu.Eip); PagingTests.Pte(r.Memory, 0x3000, 0, 0);
                }
                Rejected(a, b); Check(b.Cpu.PageFaults == 1 && b.Cpu.Cr2 == 0x3000, "Original incomplete-code PF did not precede data effects.");
            });
        }

        test("IntegerMemory complete code range is checked before any data operand", () =>
        {
            byte[] code = Absolute(0xa1, 0, true, 0x7000);
            var a = Make(4, code); var b = Make(4, code);
            foreach (var r in new[] { a, b }) SegmentInput(r, Segment.Cs, cache => cache with { Limit = 0x2003 });
            Rejected(a, b); Check(b.Cpu.PageFaults == 0 && b.Cpu.Eip == 0x6000, "Original code-limit GP was not retained before data.");
        });

        foreach (string reason in new[] { "unusable", "limit", "readonly", "code-unreadable" })
            test("IntegerMemory segment preflight retains original future fault " + reason, () =>
            {
                bool write = reason == "readonly";
                byte[] code = Absolute(write ? (byte)0xa3 : (byte)0xa1, 0, true, 0x7000);
                var a = Make(4, code); var b = Make(4, code);
                foreach (var r in new[] { a, b }) SegmentInput(r, Segment.Ds, cache => reason switch
                {
                    "unusable" => cache with { Usable = false }, "limit" => cache with { Limit = 0x7001 },
                    "readonly" => cache with { Access = 0x91 }, _ => cache with { Access = 0x99 }
                });
                Rejected(a, b); Check(b.Cpu.Eip == 0x6000 && !b.Cpu.TripleFaulted, "Original segment fault was not delivered.");
            });

        foreach (bool write in new[] { false, true })
            test($"IntegerMemory V86 alignment fault remains before RAM effects write={write}", () =>
            {
                byte[] code = Absolute(write ? (byte)0xa3 : (byte)0xa1, 0, false, 0x7001);
                var a = Make(6, code, 0x7001); var b = Make(6, code, 0x7001);
                foreach (var r in new[] { a, b }) { Field(r, "Cr0", r.Cpu.Cr0 | 0x40000); r.Cpu.Eflags |= 0x40000; }
                Rejected(a, b); Check(b.Cpu.AlignmentFaults == 1 && !b.Cpu.Virtual8086, "Actual future AC did not enter the original monitor frame.");
            });

        foreach (bool write in new[] { false, true })
            test($"IntegerMemory A20 folded data retains exact physical bytes write={write}", () =>
            {
                byte[] code = Absolute(write ? (byte)0xa3 : (byte)0xa1, 0, true, 0x107000);
                var a = Make(4, code, 0x107000); var b = Make(4, code, 0x107000);
                foreach (var r in new[] { a, b }) { r.Memory.A20Enabled = false; r.Memory.WriteDword(0x7000, Pattern ^ uint.MaxValue); }
                Accepted(a, b);
                Check(b.Memory.ReadDword(0x7000) == (write ? Registers[0] : Pattern ^ uint.MaxValue), "A20 folded data was read or stored through the wrong physical alias.");
            });

        foreach (bool folded in new[] { false, true })
            test($"IntegerMemory stores reject every alias of the entire current code page folded={folded}", () =>
            {
                uint offset = folded ? 0x102001u : 0x2001u;
                byte[] code = Absolute(0xa3, 0, true, offset);
                var a = Make(4, code, offset); var b = Make(4, code, offset);
                if (folded) { a.Memory.A20Enabled = false; b.Memory.A20Enabled = false; }
                Rejected(a, b); Check(b.Memory.ReadDword(0x2001) == Registers[0], "Original scalar self-modifying store was not performed.");
            });

        test("IntegerMemory code-data same-slot reads preserve interleaved exact TLB ages", () =>
        {
            byte[] code = Absolute(0xa0, 0, true, 0x2001);
            var a = Make(4, code, 0x2001); var b = Make(4, code, 0x2001); Accepted(a, b);
            Check((b.Cpu.GetRegister(Register.Ax) & 255) == 0xa0, "Actual current opcode was not read as ordinary data.");
        });

        test("IntegerMemory high dynamic fallback does not consult its peer during admission", () =>
        {
            byte[] code = Absolute(0xa0, 0, true, 0x400000);
            var a = Make(2, code, 0x400000, warm: false); var b = Make(2, code, 0x400000, warm: false);
            var left = new DynamicPeer(); var right = new DynamicPeer(); a.Memory.MapDynamic(left); b.Memory.MapDynamic(right);
            Rejected(a, b);
            Check(left.Queries == 1 && right.Queries == 1 && left.Reads == 1 && right.Reads == 1 && (b.Cpu.GetRegister(Register.Ax) & 255) == 0x6d,
                "The original dynamic read was prefetched, queried twice or suppressed.");
        });

        test("IntegerMemory stale decoder scratch is reset before the admitted memory element", () =>
        {
            byte[] code = Absolute(0xa1, 0, true, 0x7000);
            var a = Make(4, code); var b = Make(4, code);
            foreach (var r in new[] { a, b })
            {
                Field(r, "_override", (Segment?)Segment.Es); Field(r, "_operand32", false); Field(r, "_address32", false);
                Field(r, "_lockPrefix", true); Field(r, "_lockedMemory", true); Field(r, "_controlCheckpointSaved", true);
            }
            Accepted(a, b);
        });

        foreach (int mode in new[] { 0, 3 })
            test($"IntegerMemory later operand uses actually modified address register mode{mode}", () =>
            {
                byte[] code = [0x8b, 7, 0x43, 0x8b, 7];
                var a = Make(mode, code); var b = Make(mode, code); Accepted(a, b, 4);
                Check((b.Cpu.GetRegister(Register.Ax) & 65535) == 0x9b7d && b.Cpu.GetRegister(Register.Bx) == 0x7001,
                    "A retained old effective address replaced the next real BX input.");
            });
    }
}
