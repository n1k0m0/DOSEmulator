// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent set/age controls for real cached operands, page walks and dirty-PTE device effects.</summary>
internal static class TlbHitSelectionTests
{
    private const uint Directory = 0x1c000, Table = 0x1d000;
    /// <summary>Passive value image of all fields of one serialized translation, not the CPU's replacement implementation.</summary>
    private readonly record struct Way(bool Valid, bool User, bool Writable, bool Dirty, uint Page, uint Frame,
        uint PteAddress, long Age);
    /// <summary>Own CPU/RAM together with independently retained initial ways and the actual operand address.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory, Way[] Ways, uint Address, int Target);
    /// <summary>Requires a complete state, real guest effect or fault observation to match its independent expectation.</summary>
    private static void Check(bool ok, string why = "TLB selection invariant") { if (!ok) throw new Exception(why); }
    /// <summary>Decodes all 32 passive translation records through the public generated state boundary.</summary>
    private static Way[] Ways(X86Cpu cpu) => cpu.SaveState().Fields["_tlb"].Deserialize<Way[]>(StateValues.Json)!;
    /// <summary>Changes only reviewed typed fixture fields before running the actual normal guest decoder.</summary>
    private static void Field<T>(StateBlock state, string name, T value) =>
        state.Fields[name] = JsonSerializer.SerializeToElement(value, StateValues.Json);

    /// <summary>Seeds deterministic cache history, two expected code hits and one real 32-bit MOV memory operand.</summary>
    private static Rig Fixture(int target, bool write, bool dirty, int validMask = 15, int agePattern = 0,
        bool miss = false, bool writable = true)
    {
        uint address = miss ? 0x90000u : 0x50000u + (uint)target * 0x8000;
        byte[] code = [(byte)(write ? 0xa3 : 0xa1), (byte)address, (byte)(address >> 8),
            (byte)(address >> 16), (byte)(address >> 24)];
        var (cpu, memory) = PagingTests.Create(code);
        var ways = new Way[32];
        // Code page 2 uses set 2 and cannot compete with the four tested set-0 data ways.
        ways[8] = new(true, true, true, false, 2, 0x2000, Table + 8, 5);
        for (int i = 0; i < 4; i++)
        {
            uint linear = 0x50000u + (uint)i * 0x8000, frame = 0x50000u + (uint)i * 0x1000;
            long age = agePattern == 0 ? 10 + i * 10 : agePattern == 1 ? 40 - i * 10 : 25;
            ways[i] = new((validMask & (1 << i)) != 0, true, writable, dirty, linear >> 12, frame,
                Table + ((linear >> 10) & 0xffc), age);
            PagingTests.Pte(memory, linear, frame, (writable ? 7u : 5u) | (dirty ? 0x40u : 0));
            memory.WriteDword(frame, 0x13579000u + (uint)i);
        }
        if (miss) { PagingTests.Pte(memory, address, 0x90000); memory.WriteDword(0x90000, 0xdecafbad); }
        cpu.SetRegister(Register.Ax, 0x89abcdef); cpu.Eflags = 0x202;
        StateBlock state = cpu.SaveState();
        Field(state, "_tlb", ways); Field(state, "_translationAge", 100L);
        Field(state, "TlbHits", 17L); Field(state, "PageWalks", 23L); Field(state, "PageFaults", 0L);
        cpu.LoadState(state);
        return new(cpu, memory, ways, address, target);
    }

    /// <summary>Checks every field of every way, both cache counters and the unwrapped monotonic age.</summary>
    private static void Cache(Rig rig, Way[] expected, long hits, long walks, long age)
    {
        var actual = Ways(rig.Cpu);
        Check(actual.Length == 32 && actual.SequenceEqual(expected), "Complete TLB record differs.");
        StateBlock state = rig.Cpu.SaveState();
        Check(state.Fields["_translationAge"].GetInt64() == age, "Translation age differs.");
        Check(rig.Cpu.TlbHits == hits && rig.Cpu.PageWalks == walks && rig.Cpu.PageFaults == 0,
            "TLB/page-walk/fault counters differ.");
    }
    /// <summary>Writes one independent expected physical dword without invoking the CPU's memory or translation helpers.</summary>
    private static void ExpectedDword(byte[] ram, uint at, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(ram.AsSpan((int)at, 4), value);
    /// <summary>Reads one independent expected physical dword from the retained complete RAM copy.</summary>
    private static uint ExpectedDword(byte[] ram, uint at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ram.AsSpan((int)at, 4));

    /// <summary>Own byte-only PTE peer, retaining the exact ordered read/write transactions and their CPU observation.</summary>
    private sealed class PtePeer(uint value, Action observe) : IMemoryDevice
    {
        public readonly List<(bool Write, uint Offset, byte Value)> Effects = [];
        private uint _value = value;
        /// <summary>Observes one actual byte read after the existing dirty-hit control checkpoint.</summary>
        public byte ReadByte(uint offset)
        {
            observe(); byte part = (byte)(_value >> ((int)offset * 8)); Effects.Add((false, offset, part)); return part;
        }
        /// <summary>Observes one actual PTE-byte write before the cached Dirty flag is committed.</summary>
        public void WriteByte(uint offset, byte part)
        {
            observe(); Effects.Add((true, offset, part)); int shift = (int)offset * 8;
            _value = (_value & ~(255u << shift)) | ((uint)part << shift);
        }
        /// <summary>Complete owned PTE value after all real byte transactions.</summary>
        public uint Value => _value;
    }

    /// <summary>Registers varied-way hits, independent replacement expectations, dirty callbacks and cached-rights faults.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        for (int index = 0; index < 4; index++)
        foreach (bool write in new[] { false, true })
        foreach (bool dirty in new[] { false, true })
        {
            int target = index;
            test($"TLB hit selection way{target} write={write} dirty={dirty} retains every cache/RAM field", () =>
            {
                var rig = Fixture(target, write, dirty); byte[] expectedRam = (byte[])rig.Memory.Ram.Clone();
                var expected = (Way[])rig.Ways.Clone(); expected[8] = expected[8] with { Age = 102 };
                expected[target] = expected[target] with { Age = 103, Dirty = dirty || write };
                if (write)
                {
                    ExpectedDword(expectedRam, rig.Ways[target].Frame, 0x89abcdef);
                    if (!dirty) ExpectedDword(expectedRam, rig.Ways[target].PteAddress,
                        ExpectedDword(expectedRam, rig.Ways[target].PteAddress) | 0x40);
                }
                Check(rig.Cpu.Step() == 4 && rig.Cpu.Eip == 0x2005 && !rig.Cpu.TripleFaulted);
                Cache(rig, expected, 20, 23, 103);
                Check(rig.Cpu.GetRegister(Register.Ax) == (write ? 0x89abcdefu : 0x13579000u + (uint)target));
                Check(rig.Cpu.Eflags == 0x202 && rig.Memory.Ram.AsSpan().SequenceEqual(expectedRam),
                    "Actual operand or complete RAM/A-D image differs.");
            });
        }
        for (int mask = 0; mask < 16; mask++)
        for (int pattern = 0; pattern < 3; pattern++)
        {
            int validMask = mask, agePattern = pattern;
            test($"TLB miss selection mask{validMask:X} agePattern{agePattern} preserves invalid/LRU/tie policy", () =>
            {
                var rig = Fixture(0, false, false, validMask, agePattern, miss: true);
                // Independent policy: last invalid slot; otherwise first slot among the minimum ages.
                int[] invalid = Enumerable.Range(0, 4).Where(i => !rig.Ways[i].Valid).ToArray();
                int replacement = invalid.Length != 0 ? invalid[^1] :
                    Enumerable.Range(0, 4).OrderBy(i => rig.Ways[i].Age).ThenBy(i => i).First();
                var expected = (Way[])rig.Ways.Clone(); expected[8] = expected[8] with { Age = 102 };
                uint pte = Table + ((rig.Address >> 10) & 0xffc);
                expected[replacement] = new(true, true, true, false, rig.Address >> 12, 0x90000, pte, 103);
                byte[] expectedRam = (byte[])rig.Memory.Ram.Clone();
                ExpectedDword(expectedRam, Directory, ExpectedDword(expectedRam, Directory) | 0x20);
                ExpectedDword(expectedRam, pte, ExpectedDword(expectedRam, pte) | 0x20);
                Check(rig.Cpu.Step() == 4 && rig.Cpu.Eip == 0x2005 && rig.Cpu.GetRegister(Register.Ax) == 0xdecafbad);
                Cache(rig, expected, 19, 24, 103);
                Check(rig.Memory.Ram.AsSpan().SequenceEqual(expectedRam), "Complete walked A/D RAM image differs.");
            });
        }
        for (int index = 0; index < 4; index++)
        {
            int target = index;
            test($"TLB dirty hit way{target} preserves actual PTE device ordering and precommit observation", () =>
            {
                var rig = Fixture(target, true, false); Way original = rig.Ways[target];
                uint pteValue = rig.Memory.ReadDword(original.PteAddress);
                var peer = new PtePeer(pteValue, () =>
                {
                    Check(rig.Cpu.TlbHits == 20 && rig.Cpu.PageWalks == 23);
                    Check(Ways(rig.Cpu)[target] == (original with { Age = 103 }), "Dirty committed before the PTE callback.");
                    StateBlock during = rig.Cpu.SaveState();
                    Check(during.Fields["_controlCheckpointSaved"].GetBoolean(), "PTE device lost the original lazy checkpoint.");
                });
                rig.Memory.Map(original.PteAddress, 4, peer, "own dirty-PTE peer");
                byte[] expectedRam = (byte[])rig.Memory.Ram.Clone(); ExpectedDword(expectedRam, original.Frame, 0x89abcdef);
                var expected = (Way[])rig.Ways.Clone(); expected[8] = expected[8] with { Age = 102 };
                expected[target] = original with { Age = 103, Dirty = true };
                Check(rig.Cpu.Step() == 4 && rig.Cpu.Eip == 0x2005);
                Cache(rig, expected, 20, 23, 103);
                var effects = new List<(bool Write, uint Offset, byte Value)>();
                for (uint b = 0; b < 4; b++) effects.Add((false, b, (byte)(pteValue >> ((int)b * 8))));
                for (uint b = 0; b < 4; b++) effects.Add((true, b, (byte)((pteValue | 0x40) >> ((int)b * 8))));
                Check(peer.Effects.SequenceEqual(effects) && peer.Value == (pteValue | 0x40), "PTE byte effects differ.");
                Check(rig.Memory.Ram.AsSpan().SequenceEqual(expectedRam), "Mapped PTE wrote through or operand data differs.");
            });
            test($"TLB cached readonly way{target} faults before age/dirty/operand commit", () =>
            {
                var rig = Fixture(target, true, false, writable: false);
                // Keep this target newer than two set-0 descriptor pages needed by real exception delivery.
                rig.Ways[target] = rig.Ways[target] with { Age = 90 };
                StateBlock before = rig.Cpu.SaveState(); Field(before, "_tlb", rig.Ways); rig.Cpu.LoadState(before);
                uint original = rig.Memory.ReadDword(rig.Ways[target].Frame);
                ProtectedModeTests.Fault(rig.Cpu, rig.Memory, 14, 3);
                Check(rig.Cpu.Cr2 == rig.Address && rig.Cpu.PageFaults == 1 && rig.Cpu.GetRegister(Register.Ax) == 0x89abcdef);
                Check(Ways(rig.Cpu)[target] == rig.Ways[target], "Denied hit aged, dirtied or replaced its cached mapping.");
                Check(rig.Memory.ReadDword(rig.Ways[target].Frame) == original &&
                    (rig.Memory.ReadDword(rig.Ways[target].PteAddress) & 0x40) == 0, "Denied hit committed data or a dirty PTE.");
            });
        }
    }
}
