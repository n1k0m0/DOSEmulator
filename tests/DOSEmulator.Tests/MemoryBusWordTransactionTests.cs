// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent fixed-mapping word transactions, retained byte ordering, physical boundaries and actual CPU stores.</summary>
internal static class MemoryBusWordTransactionTests
{
    /// <summary>Requires actual device, RAM, decoder or instruction effects rather than a helper branch alone.</summary>
    private static void Check(bool condition, string why = "Fixed memory word transaction failed.")
    { if (!condition) throw new InvalidOperationException(why); }

    /// <summary>One actual byte read/write or accepted complete word; declined offers are recorded separately.</summary>
    private sealed record Effect(string Target, char Kind, uint Offset, uint Value);

    /// <summary>Own peripheral storage and an external call ledger; false never changes storage or publishes a write.</summary>
    private class Target(string name = "T", List<Effect>? effects = null) : IMemoryWordWriteDevice
    {
        /// <summary>Whether this synthetic peer accepts the complete offered transaction.</summary>
        public bool Accept = true;
        /// <summary>Optional exact exception used to ensure bus failures never cause duplicate fallback effects.</summary>
        public InvalidOperationException? Failure;
        /// <summary>Private peripheral bytes, independent from the bus RAM array.</summary>
        public readonly Dictionary<uint, byte> Data = [];
        /// <summary>External test observations, including ordered accepted device effects.</summary>
        public readonly List<Effect> Effects = effects ?? [];
        /// <summary>External observations of word offers, distinct from committed effects or device storage.</summary>
        public readonly List<(uint Offset, ushort Value)> Offers = [];
        /// <summary>Returns an explicitly seeded device byte and records only its actual read.</summary>
        public byte ReadByte(uint offset)
        { byte value = Data.GetValueOrDefault(offset, (byte)0x9d); Effects.Add(new(name, 'R', offset, value)); return value; }
        /// <summary>Applies one actual fallback byte, retaining both its relative address and its position in the ledger.</summary>
        public void WriteByte(uint offset, byte value)
        { Effects.Add(new(name, 'B', offset, value)); Data[offset] = value; }
        /// <summary>Accepts one little-endian word or declines without changing any peripheral byte or published effect.</summary>
        public bool TryWriteWord(uint offset, ushort value)
        {
            Offers.Add((offset, value));
            if (Failure is { } failure) throw failure;
            if (!Accept) return false;
            Data[offset] = (byte)value; Data[offset + 1] = (byte)(value >> 8);
            Effects.Add(new(name, 'W', offset, value)); return true;
        }
    }

    /// <summary>Existing byte-only ABI wrapping the same observation peer, with no accidental word capability.</summary>
    private sealed class Legacy(Target target, Action<uint>? afterWrite = null) : IMemoryDevice
    {
        /// <summary>Preserves the original device-relative byte read.</summary>
        public byte ReadByte(uint offset) => target.ReadByte(offset);
        /// <summary>Preserves the original device-relative byte write and its ordering.</summary>
        public void WriteByte(uint offset, byte value) { target.WriteByte(offset, value); afterWrite?.Invoke(offset); }
    }

    /// <summary>A high-memory fallback peer may implement the interface, but registration as dynamic remains byte-only.</summary>
    private sealed class Dynamic(uint start, uint length, List<Effect>? effects = null) : Target("D", effects), IMemoryDecoder
    {
        /// <summary>Tests only the immutable own window, with no target or RAM effects.</summary>
        public bool Decodes(uint address) => address >= start && (ulong)address < (ulong)start + length;
        /// <summary>Translates the absolute dynamic byte into the independently observed private peripheral offset.</summary>
        public byte ReadDecodedByte(uint address) => ReadByte(address - start);
        /// <summary>Retains dynamic byte dispatch even though this test peer also has the optional fixed-word interface.</summary>
        public void WriteDecodedByte(uint address, byte value) => WriteByte(address - start, value);
    }

    /// <summary>Initializes distinct own backing guards without contacting any mapped peripheral.</summary>
    private static MemoryBus Memory(int bytes = 2 * 1048576)
    { var memory = new MemoryBus(bytes); memory.Ram.AsSpan().Fill(0xa6); return memory; }

    /// <summary>Executes an actual protected-mode moffs store or the ordinary public bus operation at the same width.</summary>
    private static void Store(MemoryBus memory, X86Cpu? cpu, uint address, uint value, int width)
    {
        if (cpu is null)
        {
            if (width == 1) memory.WriteByte(address, (byte)value);
            else if (width == 2) memory.WriteWord(address, (ushort)value);
            else memory.WriteDword(address, value);
            return;
        }
        var code = new List<byte> { 0xb8 }; code.AddRange(BitConverter.GetBytes(value));
        if (width == 2) code.Add(0x66);
        code.Add(width == 1 ? (byte)0xa2 : (byte)0xa3); code.AddRange(BitConverter.GetBytes(address));
        code.CopyTo(memory.Ram, 0x2000); cpu.Eip = 0x2000; cpu.Step(); cpu.Step();
        Check(!cpu.TripleFaulted && !cpu.Halted && cpu.GetSegment(Segment.Cs) == 8 && cpu.Eip == 0x2000u + code.Count,
            "Actual CPU memory store left its own caller: " + cpu.Position);
    }

    /// <summary>Checks physical alignment and complete fixed coverage, including odd relative offsets and high/partial pages.</summary>
    private static void Accepted(uint start, uint length, uint address, int bytes)
    {
        var memory = Memory(bytes); var target = new Target(); memory.Map(start, length, target, "Own accepting fixed target");
        byte[] before = memory.Ram.ToArray(); memory.WriteWord(address, 0x6b5a); uint offset = address - start;
        Check(target.Offers.SequenceEqual(new[] { (offset, (ushort)0x6b5a) }) &&
            target.Effects.SequenceEqual(new[] { new Effect("T", 'W', offset, 0x6b5a) }));
        Check(target.Data.Count == 2 && target.Data[offset] == 0x5a && target.Data[offset + 1] == 0x6b && memory.Ram.AsSpan().SequenceEqual(before));
    }

    /// <summary>Requires the actual CPU word store to select only its A20-translated physical target, not the other alias.</summary>
    private static void CpuAlias(bool a20)
    {
        var (cpu, memory) = ProtectedModeTests.Rig([0x90]); memory.A20Enabled = a20;
        var low = new Target("L"); var high = new Target("H");
        memory.Map(0x20000, 4096, low, "Own low alias"); memory.Map(0x120000, 4096, high, "Own high alias");
        memory.Ram[0x20002] = 0xa6; memory.Ram[0x120002] = 0xa6;
        Store(memory, cpu, 0x120002, 0x6b5a, 2); Target used = a20 ? high : low, other = a20 ? low : high;
        Check(used.Offers.SequenceEqual(new[] { (2u, (ushort)0x6b5a) }) && used.Effects.SequenceEqual(new[] { new Effect(a20 ? "H" : "L", 'W', 2, 0x6b5a) }));
        Check(other.Offers.Count == 0 && other.Effects.Count == 0 && memory.Ram[0x20002] == 0xa6 && memory.Ram[0x120002] == 0xa6);
    }

    /// <summary>Requires real CPU byte, unaligned-word, declined-word and doubleword stores to retain their separate paths.</summary>
    private static void CpuFallback(string form)
    {
        var (cpu, memory) = ProtectedModeTests.Rig([0x90]); var target = new Target { Accept = form != "decline" };
        memory.Map(0x3000, 4096, target, "Own CPU fallback target"); memory.Ram.AsSpan(0x3000, 16).Fill(0xa6);
        int width = form == "byte" ? 1 : form == "dword" ? 4 : 2; uint address = form == "unaligned" ? 0x3001u : 0x3002u;
        Store(memory, cpu, address, 0xd13c6b5a, width);
        var expected = Enumerable.Range(0, width).Select(i => new Effect("T", 'B', address - 0x3000 + (uint)i, (byte)(0xd13c6b5au >> (8 * i)))).ToArray();
        Check(target.Effects.SequenceEqual(expected) && target.Offers.Count == (form == "decline" ? 1 : 0));
        Check(memory.Ram.AsSpan(0x3000, 16).IndexOfAnyExcept((byte)0xa6) < 0);
    }

    /// <summary>A one-byte target never consumes an adjacent RAM/hole byte through the word interface.</summary>
    private static void OneByteBoundary(bool targetFirst, bool hole)
    {
        var memory = Memory(); if (hole) memory.SetRamHole(0x4000, 4096);
        var target = new Target(); memory.Map(targetFirst ? 0x4000u : 0x4001u, 1, target, "Own one-byte boundary");
        memory.WriteWord(0x4000, 0x6b5a);
        Check(target.Offers.Count == 0 && target.Effects.SequenceEqual(new[] { new Effect("T", 'B', 0, targetFirst ? 0x5au : 0x6bu) }));
        Check(memory.Ram[0x4000] == (hole || targetFirst ? 0xa6 : 0x5a) && memory.Ram[0x4001] == (hole || !targetFirst ? 0xa6 : 0x6b));
    }

    /// <summary>Two fixed mappings remain distinct even when they share one exact device instance.</summary>
    private static void Adjacent(bool samePeer)
    {
        var memory = Memory(); var events = new List<Effect>(); var left = new Target("L", events); var right = samePeer ? left : new Target("R", events);
        memory.Map(0x4001, 2, left, "Own left mapping"); memory.Map(0x4003, 2, right, "Own right mapping");
        byte[] before = memory.Ram.ToArray(); memory.WriteWord(0x4002, 0x6b5a);
        Check(left.Offers.Count == 0 && right.Offers.Count == 0 && events.SequenceEqual(new[]
        { new Effect("L", 'B', 1, 0x5a), new Effect(samePeer ? "L" : "R", 'B', 0, 0x6b) }));
        Check(memory.Ram.AsSpan().SequenceEqual(before));
    }

    /// <summary>Byte ordering survives both possible bit20 discontinuities, without offering either byte as a word.</summary>
    private static void A20Boundary(uint address)
    {
        var memory = Memory(); memory.A20Enabled = false; var events = new List<Effect>();
        uint first = address & 0xffefffff, second = unchecked(address + 1) & 0xffefffff;
        var left = new Target("L", events); var right = new Target("R", events);
        memory.Map(first, 1, left, "Own A20 low byte"); memory.Map(second, 1, right, "Own A20 next byte");
        byte[] before = memory.Ram.ToArray(); memory.WriteWord(address, 0x6b5a);
        Check(left.Offers.Count == 0 && right.Offers.Count == 0 && events.SequenceEqual(new[]
        { new Effect("L", 'B', 0, 0x5a), new Effect("R", 'B', 0, 0x6b) }) && memory.Ram.AsSpan().SequenceEqual(before));
    }

    /// <summary>Retains the explicit uint-wrap byte path and its separate physical endpoints.</summary>
    private static void Wrap(bool a20)
    {
        var memory = Memory(); memory.A20Enabled = a20; var events = new List<Effect>();
        var last = new Target("L", events); var zero = new Target("Z", events);
        memory.Map(a20 ? uint.MaxValue : 0xffefffff, 1, last, "Own last byte"); memory.Map(0, 1, zero, "Own wrap byte");
        memory.WriteWord(uint.MaxValue, 0x6b5a);
        Check(last.Offers.Count == 0 && zero.Offers.Count == 0 && events.SequenceEqual(new[]
        { new Effect("L", 'B', 0, 0x5a), new Effect("Z", 'B', 0, 0x6b) }) && memory.Ram[0] == 0xa6);
    }

    /// <summary>Fixed targets retain precedence over hidden RAM and a hole, including full-page cache and partial-page classification.</summary>
    private static void HolePriority(bool partial, bool holeFirst)
    {
        var memory = Memory(); if (holeFirst) memory.SetRamHole(0x4000, 4096);
        var target = new Target(); uint start = partial ? 0x4101u : 0x4000u;
        memory.Map(start, partial ? 5u : 4096u, target, "Own target inside RAM hole");
        if (!holeFirst) memory.SetRamHole(0x4000, 4096); memory.SetDecodedRamBytes(0x3000);
        byte[] before = memory.Ram.ToArray(); memory.WriteWord(0x4102, 0x6b5a);
        Check(target.Effects.SequenceEqual(new[] { new Effect("T", 'W', 0x4102 - start, 0x6b5a) }) && target.Offers.Count == 1 && memory.Ram.AsSpan().SequenceEqual(before));
        memory.SetRamHole(0, 0); memory.SetDecodedRamBytes((uint)memory.Ram.Length); memory.WriteWord(0x4102, 0x3c17);
        Check(target.Offers.Count == 2 && target.Effects[^1] == new Effect("T", 'W', 0x4102 - start, 0x3c17) && memory.Ram.AsSpan().SequenceEqual(before));
    }

    /// <summary>Mapping registration invalidates a previously warmed plain page without overwriting its retained physical backing.</summary>
    private static void LateMap()
    {
        var memory = Memory(); memory.WriteWord(0x4000, 0x7391); Check(memory.ReadWord(0x4000) == 0x7391);
        var target = new Target(); memory.Map(0x4000, 4096, target, "Own late fixed page"); memory.WriteWord(0x4000, 0x6b5a);
        Check(target.Effects.SequenceEqual(new[] { new Effect("T", 'W', 0, 0x6b5a) }) && memory.Ram[0x4000] == 0x91 && memory.Ram[0x4001] == 0x73);
    }

    /// <summary>Normal RAM, including unaligned accesses and a conservative partially mapped page, keeps its byte-identical backing path.</summary>
    private static void Plain(int width, bool odd, bool conservative)
    {
        var memory = Memory(); var target = new Target(); if (conservative) memory.Map(0x30f1, 1, target, "Own unrelated partial page");
        byte[] before = memory.Ram.ToArray(); uint address = odd ? 0x3001u : 0x3002u;
        if (width == 2) memory.WriteWord(address, 0x6b5a); else memory.WriteDword(address, 0xd13c6b5a);
        for (int i = 0; i < width; i++) before[address + (uint)i] = (byte)(0xd13c6b5au >> (8 * i));
        Check(memory.Ram.AsSpan().SequenceEqual(before) && target.Offers.Count == 0 && target.Effects.Count == 0);
        Check((width == 2 ? memory.ReadWord(address) : memory.ReadDword(address)) == (width == 2 ? 0x6b5au : 0xd13c6b5au));
    }

    /// <summary>Pure dynamic fallback registration never opts in to fixed-mapping word dispatch.</summary>
    private static void DynamicOnly(int width)
    {
        var memory = Memory(); var target = new Dynamic(0xe0000000, 4096); memory.MapDynamic(target);
        Store(memory, null, 0xe0000002, 0xd13c6b5a, width);
        Check(target.Offers.Count == 0 && target.Effects.SequenceEqual(Enumerable.Range(0, width).Select(i => new Effect("D", 'B', (uint)(2 + i), (byte)(0xd13c6b5au >> (8 * i))))));
    }

    /// <summary>Registers accepting, declining, legacy, CPU and independently bounded mapping/decode controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var row in new (uint Start, uint Length, uint Address, int Ram)[]
        {
            (0x4000,4096,0x4000,2097152), (0x4000,4096,0x4ffe,2097152), (0x4000,8192,0x5000,2097152),
            (0x4101,5,0x4102,2097152), (0x4101,5,0x4104,2097152), (0xe0000001,5,0xe0000002,2097152),
            (0xfffffffe,2,0xfffffffe,2097152), (0x100002,2,0x100002,1048579)
        })
            test($"Core word actual fixed transaction {row.Start:X8}+{row.Length:X} at{row.Address:X8}", () => Accepted(row.Start,row.Length,row.Address,row.Ram));
        foreach (bool a20 in new[] { false, true }) test("Core word actual CPU physical A20 target " + a20, () => CpuAlias(a20));
        foreach (string form in new[] { "byte", "unaligned", "decline", "dword" }) test("Core word actual CPU fallback form " + form, () => CpuFallback(form));
        foreach (bool first in new[] { false, true }) foreach (bool hole in new[] { false, true })
            test($"Core word one-byte fixed boundary targetFirst={first} hole={hole}", () => OneByteBoundary(first,hole));
        foreach (bool same in new[] { false, true }) test("Core word separate mappings same peer=" + same, () => Adjacent(same));
        foreach (uint address in new uint[] { 0xfffff, 0x1fffff }) test("Core word ordered A20 discontinuity " + address.ToString("X8"), () => A20Boundary(address));
        foreach (bool a20 in new[] { false, true }) test("Core word ordered uint-wrap A20=" + a20, () => Wrap(a20));
        foreach (bool partial in new[] { false, true }) foreach (bool holeFirst in new[] { false, true })
            test($"Core word fixed hidden RAM and hole precedence partial={partial} holeFirst={holeFirst}", () => HolePriority(partial,holeFirst));
        test("Core word late full-page mapping invalidates warmed RAM", LateMap);
        foreach (int width in new[] { 2, 4 }) foreach (bool odd in new[] { false, true }) foreach (bool conservative in new[] { false, true })
            test($"Core word ordinary RAM width={width} odd={odd} conservative={conservative}", () => Plain(width,odd,conservative));
        foreach (int width in new[] { 2, 4 }) test("Core word dynamic fallback stays byte-only width=" + width, () => DynamicOnly(width));
        test("Core word fixed target wins over dynamic peer at same high resource", () =>
        {
            var memory = Memory(); var dynamic = new Dynamic(0xe0000000, 4096); var target = new Target();
            memory.MapDynamic(dynamic); memory.Map(0xe0000000,4096,target,"Own fixed priority"); memory.WriteWord(0xe0000002,0x6b5a);
            Check(target.Effects.SequenceEqual(new[] { new Effect("T",'W',2,0x6b5a) }) && dynamic.Offers.Count==0 && dynamic.Effects.Count==0);
        });
        test("Core word legacy fixed ABI retains two ordered byte stores", () =>
        {
            var memory=Memory();var target=new Target();memory.Map(0x4000,4096,new Legacy(target),"Own legacy byte peer");memory.WriteWord(0x4002,0x6b5a);
            Check(target.Offers.Count==0 && target.Effects.SequenceEqual(new[] { new Effect("T",'B',2,0x5a),new Effect("T",'B',3,0x6b) }));
        });
        test("Core word read remains two actual device bytes and never a write offer", () =>
        {
            var memory=Memory();var target=new Target();target.Data[2]=0x17;target.Data[3]=0x3c;memory.Map(0x4000,4096,target,"Own read-only effect control");
            Check(memory.ReadWord(0x4002)==0x3c17 && target.Offers.Count==0 && target.Effects.SequenceEqual(new[] { new Effect("T",'R',2,0x17),new Effect("T",'R',3,0x3c) }));
        });
        test("Core word unmapped hole preserves every hidden backing byte", () =>
        {
            var memory=Memory();memory.SetRamHole(0x4000,4096);byte[] before=memory.Ram.ToArray();memory.WriteWord(0x4ffe,0x6b5a);
            Check(memory.ReadWord(0x4ffe)==0xffff && memory.Ram.AsSpan().SequenceEqual(before));
        });
        test("Core word decoded RAM end retains only its genuinely driven byte", () =>
        {
            var memory=Memory();memory.SetDecodedRamBytes(0x3001);byte[] before=memory.Ram.ToArray();memory.WriteWord(0x3000,0x6b5a);before[0x3000]=0x5a;
            Check(memory.ReadWord(0x3000)==0xff5a && memory.Ram.AsSpan().SequenceEqual(before));
        });
        test("Core word actual CPU odd page crossing remains two ordered byte effects", () =>
        {
            var (cpu,memory)=ProtectedModeTests.Rig([0x90]);var target=new Target();memory.Map(0x4000,8192,target,"Own CPU two-page byte mapping");
            Store(memory,cpu,0x4fff,0x6b5a,2);
            Check(target.Offers.Count==0 && target.Effects.SequenceEqual(new[] {new Effect("T",'B',0xfff,0x5a),new Effect("T",'B',0x1000,0x6b)}));
        });
        test("Core word legacy byte callback preserves A20 change before its following byte", () =>
        {
            var memory=Memory();memory.A20Enabled=false;var events=new List<Effect>();var left=new Target("L",events);var high=new Target("H",events);var zero=new Target("Z",events);
            memory.Map(0xfffff,1,new Legacy(left,_=>memory.A20Enabled=true),"Own A20 byte effect");
            memory.Map(0x100000,1,high,"Own next enabled byte");memory.Map(0,1,zero,"Own old folded alias");memory.WriteWord(0xfffff,0x6b5a);
            Check(memory.A20Enabled && left.Offers.Count==0 && high.Offers.Count==0 && zero.Offers.Count==0 &&
                events.SequenceEqual(new[] {new Effect("L",'B',0,0x5a),new Effect("H",'B',0,0x6b)}) && memory.Ram[0]==0xa6);
        });
        test("Core word accepting peer failure propagates without duplicate byte fallback", () =>
        {
            var memory=Memory();var failure=new InvalidOperationException("Own exact target failure");var target=new Target {Failure=failure};
            memory.Map(0x4000,4096,target,"Own failed word peer");bool caught=false;
            try{memory.WriteWord(0x4002,0x6b5a);}catch(InvalidOperationException error)when(ReferenceEquals(error,failure)){caught=true;}
            Check(caught && target.Offers.Count==1 && target.Effects.Count==0 && target.Data.Count==0 && memory.Ram[0x4002]==0xa6);
        });
    }
}
