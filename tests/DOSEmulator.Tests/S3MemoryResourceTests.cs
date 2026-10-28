// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent physical-bus, paging and restart effects of constructor-bound high-memory fallback resources.</summary>
internal static class S3MemoryResourceTests
{
    private const uint High = 0xe0000000;
    /// <summary>Requires an observed byte, fault, event order or unchanged backing result.</summary>
    private static void Check(bool condition, string reason = "S3 physical fallback invariant") { if (!condition) throw new Exception(reason); }
    /// <summary>Accepts only the explicitly specified atomic registration failure.</summary>
    private static void Invalid(Action action)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Invalid fallback registration accepted"); }
    /// <summary>Observes every real byte transfer and pure address query, without using RAM as peripheral backing.</summary>
    private sealed class Decoder(MemoryBus memory, uint start, uint length, bool enforceWholeWindow = true) : IMemoryDecoder
    {
        public uint Start = start, Length = length;
        public bool Enabled = true;
        public readonly List<uint> Queries = [];
        public readonly List<(bool Write, uint Address, byte Value)> Effects = [];
        private readonly Dictionary<uint, byte> _bytes = [];
        /// <summary>Records only diagnostic queries; resource selection itself reads no peripheral storage or architectural latch.</summary>
        public bool Decodes(uint address)
        { Queries.Add(address); return Enabled && address >= Start && (ulong)address < (ulong)Start + Length && (!enforceWholeWindow || memory.IsFixedWindowAvailable(Start, Length)); }
        /// <summary>Returns exactly the independently seeded peripheral byte and records its absolute physical address.</summary>
        public byte ReadDecodedByte(uint address) { byte value = _bytes.GetValueOrDefault(address, (byte)0xff); Effects.Add((false, address, value)); return value; }
        /// <summary>Records one actual byte write to the peripheral, independently from installed RAM.</summary>
        public void WriteDecodedByte(uint address, byte value) { Effects.Add((true, address, value)); _bytes[address] = value; }
        /// <summary>Seeds only this own synthetic peripheral's bytes before the operation being checked.</summary>
        public void Seed(uint address, params byte[] bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[unchecked(address + (uint)i)] = bytes[i]; }
        /// <summary>Clears diagnostic observations without changing any peripheral byte or selected resource.</summary>
        public void Clear() { Queries.Clear(); Effects.Clear(); }
    }
    private sealed class Fixed : IMemoryDevice
    {
        public readonly List<(bool Write, uint Offset, byte Value)> Effects = [];
        /// <summary>Returns a fixed target byte through the ordinary device-relative mapping.</summary>
        public byte ReadByte(uint offset) { byte value = (byte)(0x30 + offset); Effects.Add((false, offset, value)); return value; }
        /// <summary>Records an ordinary fixed target write without passing it to a dynamic peer.</summary>
        public void WriteByte(uint offset, byte value) => Effects.Add((true, offset, value));
    }
    /// <summary>Encodes a real 32-bit moffs instruction without obtaining expected values from the decoder.</summary>
    private static byte[] Moffs(uint address, int width, bool write)
    {
        byte opcode = (byte)(write ? width == 1 ? 0xa2 : 0xa3 : width == 1 ? 0xa0 : 0xa1);
        byte[] body = [opcode, (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24)];
        return width == 2 ? [0x66, .. body] : body;
    }
    /// <summary>Registers meaningful bus, resource, CPU-translation and rollback boundaries for the new fallback transport.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach ((uint start, uint length, bool allowed) in new (uint, uint, bool)[]
        {
            (0, 0, false), (0, 1, false), (0x1fffff, 1, false), (0x1fffff, 2, false),
            (0x200000, 1, true), (0x200000, 0x1000, true), (High, 0x100, true),
            (High + 0x100, 1, false), (High + 0xff, 2, false), (High + 0x102, 2, false),
            (High + 0x103, 1, true), (0xfffff000, 0x1000, true), (0xffffffff, 1, true), (0xffffffff, 2, false)
        })
            test($"M4 S3 resource complete window {start:X8}+{length:X8}", () =>
            {
                var memory = new MemoryBus(2 * 1048576); var fixedTarget = new Fixed(); memory.Map(High + 0x100, 3, fixedTarget, "Own unaligned fixed target");
                Check(memory.IsFixedWindowAvailable(start, length) == allowed && fixedTarget.Effects.Count == 0);
            });
        test("M4 S3 dynamic registration remains fixed identity and rejects null or duplicate peers atomically", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var first = new Decoder(memory, High, 4096); var second = new Decoder(memory, High + 4096, 4096);
            Invalid(() => memory.MapDynamic(null!)); Check(memory.DynamicDecoderCount == 0);
            memory.MapDynamic(first); Check(memory.HasOnlyDynamicDecoder(first) && !memory.HasOnlyDynamicDecoder(second));
            string before = memory.StateMap(item => ReferenceEquals(item, first) ? "own-first" : throw new Exception("Unexpected owner"));
            Invalid(() => memory.MapDynamic(first)); Check(memory.DynamicDecoderCount == 1 && memory.HasOnlyDynamicDecoder(first));
            first.Start += 8192; first.Enabled = false; Check(memory.StateMap(item => "own-first") == before, "Moving BAR data must not replace the constructor graph");
            memory.MapDynamic(second); Check(memory.DynamicDecoderCount == 2 && !memory.HasOnlyDynamicDecoder(first));
        });
        test("M4 S3 fallback cannot query installed RAM including hidden rows holes and a partial final page", () =>
        {
            var memory = new MemoryBus(2 * 1048576 + 17); var decoder = new Decoder(memory, 0, uint.MaxValue, false); memory.MapDynamic(decoder);
            memory.Ram[0x100] = 0x12; memory.Ram[0x180000] = 0x34; memory.Ram[^1] = 0x56;
            memory.SetDecodedRamBytes(1048576); memory.SetRamHole(0x180000, 4096);
            Check(memory.ReadByte(0x100) == 0x12 && memory.ReadByte(0x180000) == 255 && memory.ReadByte((uint)memory.Ram.Length - 1) == 255);
            memory.WriteByte(0x100, 0xa7); memory.WriteByte(0x180000, 0xb8); memory.WriteByte((uint)memory.Ram.Length - 1, 0xc9);
            Check(memory.Ram[0x100] == 0xa7 && memory.Ram[0x180000] == 0x34 && memory.Ram[^1] == 0x56 && decoder.Queries.Count == 0 && decoder.Effects.Count == 0);
            memory.SetRamHole(0, 0); memory.SetDecodedRamBytes((uint)memory.Ram.Length);
            Check(memory.ReadByte(0x180000) == 0x34 && memory.ReadByte((uint)memory.Ram.Length - 1) == 0x56 && decoder.Queries.Count == 0);
        });
        test("M4 S3 fixed byte maps and immutable ROM always win without fallback peeks", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, High, 0x10000000, false); memory.MapDynamic(decoder);
            var target = new Fixed(); memory.Map(High + 0x100, 3, target, "Own fixed target"); memory.Map(High + 0x1000, 4, new RomDevice([0x13, 0x57, 0x9b, 0xdf]), "Own immutable ROM");
            Check(memory.ReadByte(High + 0x101) == 0x31 && memory.ReadDword(High + 0x1000) == 0xdf9b5713);
            memory.WriteByte(High + 0x102, 0x7d); memory.WriteDword(High + 0x1000, 0x12345678);
            Check(decoder.Queries.Count == 0 && decoder.Effects.Count == 0 && target.Effects.SequenceEqual(new[] { (false, 1u, (byte)0x31), (true, 2u, (byte)0x7d) }));
            Check(memory.ReadDword(High + 0x1000) == 0xdf9b5713);
        });
        foreach (uint collision in new uint[] { 0, 0x100000, 0x180000, 0xa0000, 0xc0000, 0xf0000, 0xffef0000, 0xffff0000 })
            test("M4 S3 entire conflicting window is inert without storage/latch effects " + collision.ToString("X8"), () =>
            {
                var memory = new MemoryBus(2 * 1048576); memory.SetDecodedRamBytes(1048576); memory.SetRamHole(0x180000, 4096);
                memory.Map(0xa0000, 0x20000, new OpenBusMemory(), "Own legacy aperture"); memory.Map(0xc0000, 0x40000, new OpenBusMemory(), "Own PAM window");
                memory.Map(0xffef0000, 65536, new RomDevice(new byte[65536]), "Own masked ROM"); memory.Map(0xffff0000, 65536, new RomDevice(new byte[65536]), "Own top ROM");
                var decoder = new Decoder(memory, collision, 65536); memory.MapDynamic(decoder); decoder.Seed(collision, 0x5a); byte[] before = memory.Ram.ToArray();
                Check(!memory.IsFixedWindowAvailable(collision, 65536)); memory.ReadByte(collision); memory.WriteByte(collision, 0x69);
                Check(decoder.Effects.Count == 0, "An inert resource must not read a latch or touch VRAM");
                if (collision < 1048576 && collision is not (0xa0000 or 0xc0000 or 0xf0000)) Check(memory.Ram[collision] == 0x69);
                else Check(memory.Ram.AsSpan().SequenceEqual(before));
            });
        test("M4 S3 diagnostic peek refuses the target and every RAM/code/physical span remains query-free", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder);
            Check(!memory.TryPeekByte(High, out _) && decoder.Queries.SequenceEqual(new[] { High }) && decoder.Effects.Count == 0);
            decoder.Clear(); Check(!memory.TryReadRamByte(High, out _) && !memory.IsPlainRamByte(High));
            Check(!memory.TryGetRamSpan(High, 4, out _) && !memory.TryGetCodeSpan(High, 4, out _) && !memory.TryGetPhysicalRamSpan(High, 4, null, out _));
            Check(decoder.Queries.Count == 0 && decoder.Effects.Count == 0);
        });
        test("M4 S3 overlapping fallback peers resolve one first registered target without probing later storage", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var a = new Decoder(memory, High, 4096); var b = new Decoder(memory, High, 4096); a.Seed(High, 0x13); b.Seed(High, 0x79);
            memory.MapDynamic(a); memory.MapDynamic(b); Check(memory.ReadByte(High) == 0x13); memory.WriteByte(High, 0x35);
            Check(a.Effects.Count == 2 && b.Queries.Count == 0 && b.Effects.Count == 0);
        });
        foreach (int width in new[] { 2, 4 })
            test("M4 S3 byte-exact wide transfers cross the final fallback byte width " + width, () =>
            {
                var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); decoder.Seed(High + 4094, 0x13, 0x57);
                uint value = width == 2 ? memory.ReadWord(High + 4094) : memory.ReadDword(High + 4094);
                Check(value == (width == 2 ? 0x5713u : 0xffff5713u));
                if (width == 2) memory.WriteWord(High + 4095, 0x9bdf); else memory.WriteDword(High + 4095, 0x13579bdf);
                Check(decoder.Effects.SequenceEqual(new[] { (false, High + 4094, (byte)0x13), (false, High + 4095, (byte)0x57), (true, High + 4095, (byte)0xdf) }));
            });
        test("M4 S3 byte bus preserves uint wrapping from fallback to low installed RAM", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, 0xfffff000, 4096); memory.MapDynamic(decoder); decoder.Seed(uint.MaxValue, 0xdd);
            memory.Ram[0] = 0xaa; memory.Ram[1] = 0xbb; memory.Ram[2] = 0xcc; memory.Ram[3] = 0xee;
            Check(memory.ReadDword(uint.MaxValue) == 0xccbbaadd); memory.WriteDword(uint.MaxValue, 0x13579bdf);
            Check(memory.Ram.AsSpan(0, 4).SequenceEqual(new byte[] { 0x9b, 0x57, 0x13, 0xee }) && decoder.Effects.SequenceEqual(new[] { (false, uint.MaxValue, (byte)0xdd), (true, uint.MaxValue, (byte)0xdf) }));
        });
        test("M4 S3 CPU A20 folding precedes high physical fallback selection", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, High, 0x400000); memory.MapDynamic(decoder);
            decoder.Seed(High, 0x13, 0x57, 0x9b, 0xdf); decoder.Seed(High + 0x100000, 1, 2, 3, 4);
            Check(memory.ReadDword(High + 0x100000) == 0x04030201); decoder.Clear(); memory.A20Enabled = false;
            Check(memory.ReadDword(High + 0x100000) == 0xdf9b5713); memory.WriteByte(High + 0x100002, 0x69);
            Check(decoder.Effects.Select(e => e.Address).SequenceEqual(new[] { High, High + 1, High + 2, High + 3, High + 2 }) && decoder.Effects[^1] == (true, High + 2, (byte)0x69));
        });
        test("M4 S3 resource relocation and disable leave neither a stale target nor a storage read", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); decoder.Seed(High, 0x13); decoder.Seed(High + 8192, 0x57);
            Check(memory.ReadByte(High) == 0x13); decoder.Start = High + 8192; Check(memory.ReadByte(High) == 255 && memory.ReadByte(High + 8192) == 0x57);
            decoder.Enabled = false; Check(memory.ReadByte(High + 8192) == 255); memory.WriteByte(High + 8192, 0xa5);
            Check(decoder.Effects.SequenceEqual(new[] { (false, High, (byte)0x13), (false, High + 8192, (byte)0x57) }));
        });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>()) foreach (int width in new[] { 1, 2, 4 }) foreach (bool write in new[] { false, true })
            test($"M4 S3 actual CPU fallback operand model {model} width {width} write {write}", () =>
            {
                var (cpu, memory) = ProtectedModeTests.Rig(Moffs(High + 0x100, width, write), model: model); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder);
                decoder.Seed(High + 0x100, 0x13, 0x57, 0x9b, 0xdf); cpu.SetRegister(Register.Ax, 0x13579bdf); cpu.Eflags = 0xcd7; byte[] before = memory.Ram.ToArray(); cpu.Step();
                uint expected = write ? 0x13579bdfu : width == 1 ? 0x13579b13u : width == 2 ? 0x13575713u : 0xdf9b5713u;
                Check(cpu.GetRegister(Register.Ax) == expected && cpu.Eflags == 0xcd7 && memory.Ram.AsSpan().SequenceEqual(before) && !cpu.TripleFaulted);
                Check(decoder.Effects.Count == width && decoder.Effects.All(e => e.Write == write));
                for (int i = 0; i < width; i++) Check(decoder.Effects[i].Address == High + 0x100 + (uint)i && decoder.Effects[i].Value == (byte)((write ? 0x13579bdfu : 0xdf9b5713u) >> (8 * i)));
            });
        foreach (bool write in new[] { false, true })
            test("M4 S3 actual paged nonidentity CPU transaction reaches physical fallback write " + write, () =>
            {
                var (cpu, memory) = PagingTests.Create(Moffs(0x30000, 4, write)); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); decoder.Seed(High, 0x13, 0x57, 0x9b, 0xdf);
                PagingTests.Pte(memory, 0x30000, High); memory.WriteDword(0x30000, 0x76543210); cpu.SetRegister(Register.Ax, 0x13579bdf); cpu.Step();
                Check(cpu.GetRegister(Register.Ax) == (write ? 0x13579bdf : 0xdf9b5713) && memory.ReadDword(0x30000) == 0x76543210 && decoder.Effects.Count == 4 && decoder.Effects[0].Address == High);
                Check((memory.ReadDword(0x1d0c0) & 0x60) == (write ? 0x60u : 0x20u));
            });
        foreach ((bool user, bool write, uint flags, uint error) in new (bool, bool, uint, uint)[]
        { (false, false, 0, 0), (false, true, 0, 2), (false, true, 5, 3), (true, false, 0, 4), (true, true, 0, 6), (true, false, 3, 5), (true, true, 3, 7), (true, true, 5, 7) })
            test($"M4 S3 paging rejects before any fallback effect user {user} write {write} PTE {flags}", () =>
            {
                var (cpu, memory) = PagingTests.Create(Moffs(0x30000, 4, write)); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); PagingTests.Pte(memory, 0x30000, High, flags);
                if (user) { PagingTests.Tss(cpu, memory); PagingTests.User(cpu, memory, true); cpu.SetSegment(Segment.Ds, 0x33); }
                cpu.SetRegister(Register.Ax, 0x13579bdf); byte? vector = null; cpu.InterruptEntered += v => vector = v; cpu.Step();
                uint stack = cpu.GetRegister(Register.Sp); Check(vector == 14 && cpu.Eip == 0x6000 && memory.ReadDword(stack) == error && cpu.Cr2 == 0x30000 && cpu.GetRegister(Register.Ax) == 0x13579bdf);
                Check(decoder.Queries.Count == 0 && decoder.Effects.Count == 0);
            });
        foreach (bool write in new[] { false, true })
            test("M4 S3 cross-page operand preflight has zero partial effects then retries exactly once write " + write, () =>
            {
                var (cpu, memory) = PagingTests.Create(Moffs(0x30ffe, 4, write)); var decoder = new Decoder(memory, High, 8192); memory.MapDynamic(decoder);
                decoder.Seed(High + 4094, 0x13, 0x57, 0x9b, 0xdf); PagingTests.Pte(memory, 0x30000, High); PagingTests.Pte(memory, 0x31000, 0, 0); cpu.SetRegister(Register.Ax, 0x13579bdf);
                ProtectedModeTests.Fault(cpu, memory, 14, write ? 2u : 0); Check(cpu.Cr2 == 0x31000 && decoder.Queries.Count == 0 && decoder.Effects.Count == 0);
                PagingTests.Pte(memory, 0x31000, High + 4096); new byte[] { 0x83, 0xc4, 4, 0xcf }.CopyTo(memory.Ram, 0x6000); cpu.Step(); cpu.Step(); Check(cpu.Eip == 0x2000); cpu.Step();
                Check(decoder.Effects.Count == 4 && decoder.Effects.All(e => e.Write == write) && decoder.Effects.Select(e => e.Address).SequenceEqual(new[] { High + 4094, High + 4095, High + 4096, High + 4097 }));
                Check(cpu.GetRegister(Register.Ax) == (write ? 0x13579bdf : 0xdf9b5713));
            });
        test("M4 S3 RMW checks page write rights before a side-effecting target read", () =>
        {
            var (cpu, memory) = PagingTests.Create([0xff, 0x05, 0, 0, 3, 0]); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); PagingTests.Pte(memory, 0x30000, High, 5);
            ProtectedModeTests.Fault(cpu, memory, 14, 3); Check(cpu.Cr2 == 0x30000 && decoder.Queries.Count == 0 && decoder.Effects.Count == 0);
        });
        test("M4 S3 CPU code fetch and immediates use real ordered fallback reads without speculative RAM", () =>
        {
            var (cpu, memory) = PagingTests.Create([0x90]); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); decoder.Seed(High, 0xb8, 0x78, 0x56, 0x34, 0x12, 0xf4);
            cpu.SetRegister(Register.Ax, cpu.Cr3); PagingTests.At(cpu, memory, [0x0f, 0x22, 0xd8]); PagingTests.Pte(memory, 0x2000, High); cpu.Eip = 0x2000;
            Check(cpu.MappedInstructionAddress == High && decoder.Queries.Count == 0); cpu.Step(); Check(cpu.GetRegister(Register.Ax) == 0x12345678 && decoder.Effects.Count == 5);
            cpu.Step(); Check(cpu.Halted && decoder.Effects.Count == 6 && decoder.Effects.Select(e => e.Address).SequenceEqual(Enumerable.Range(0, 6).Select(i => High + (uint)i)));
        });
        test("M4 S3 REP fallback source cannot batch or consume a faulting iteration before destination rights", () =>
        {
            var (cpu, memory) = PagingTests.Create([0xf3, 0xa4]); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); decoder.Seed(High, 0x13, 0x57, 0x9b);
            PagingTests.Pte(memory, 0x30000, High); PagingTests.Pte(memory, 0x41000, 0, 0); cpu.SetRegister(Register.Si, 0x30000); cpu.SetRegister(Register.Di, 0x40ffe); cpu.SetRegister(Register.Cx, 3);
            cpu.Step(); int effects = decoder.Effects.Count; Check(cpu.HasRepContinuation && cpu.TryRunRepBatch(10) == 0 && decoder.Effects.Count == effects); cpu.Step(); ProtectedModeTests.Fault(cpu, memory, 14, 2);
            Check(cpu.Cr2 == 0x41000 && cpu.GetRegister(Register.Si) == 0x30002 && cpu.GetRegister(Register.Di) == 0x41000 && cpu.GetRegister(Register.Cx) == 1 && memory.Ram[0x40ffe] == 0x13 && memory.Ram[0x40fff] == 0x57 && decoder.Effects.Count == 2);
            PagingTests.Pte(memory, 0x41000, 0x50000); new byte[] { 0x83, 0xc4, 4, 0xcf }.CopyTo(memory.Ram, 0x6000); cpu.Step(); cpu.Step(); cpu.Step();
            Check(cpu.Eip == 0x2002 && cpu.GetRegister(Register.Cx) == 0 && memory.Ram[0x50000] == 0x9b && decoder.Effects.Select(e => e.Address).SequenceEqual(new[] { High, High + 1, High + 2 }));
        });
        test("M4 S3 REP destination remains ordered byte MMIO and never a direct RAM span", () =>
        {
            var (cpu, memory) = PagingTests.Create([0xf3, 0xa4]); var decoder = new Decoder(memory, High, 4096); memory.MapDynamic(decoder); PagingTests.Pte(memory, 0x31000, High);
            new byte[] { 0x13, 0x57, 0x9b, 0xdf }.CopyTo(memory.Ram, 0x30000); cpu.SetRegister(Register.Si, 0x30000); cpu.SetRegister(Register.Di, 0x31000); cpu.SetRegister(Register.Cx, 4);
            cpu.Step(); int effects = decoder.Effects.Count; Check(cpu.TryRunRepBatch(10) == 0 && decoder.Effects.Count == effects); for (int i = 0; i < 3; i++) cpu.Step();
            Check(cpu.Eip == 0x2002 && cpu.GetRegister(Register.Si) == 0x30004 && cpu.GetRegister(Register.Di) == 0x31004 && cpu.GetRegister(Register.Cx) == 0 && decoder.Effects.SequenceEqual(new[] { (true, High, (byte)0x13), (true, High + 1, (byte)0x57), (true, High + 2, (byte)0x9b), (true, High + 3, (byte)0xdf) }));
        });
    }
}
