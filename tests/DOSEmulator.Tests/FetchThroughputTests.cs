// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class FetchThroughputTests
{
    /// <summary>Requires the architectural or device observation to match its independent expectation.</summary>
    private static void Check(bool ok, string text = "instruction fetch regression") { if (!ok) throw new Exception(text); }
    /// <summary>Snapshots architectural state while excluding optimization diagnostics such as translation call counts.</summary>
    private static string State(X86Cpu c) => string.Join("/", c.Eip, c.Eflags, c.Cr0, c.Cr2, c.Cr3, c.Dr6, c.Dr7,
        string.Join(",", Enum.GetValues<Register>().Select(c.GetRegister)),
        string.Join(",", Enum.GetValues<Segment>().Select(s => $"{c.GetSegment(s)}:{c.GetSegmentCache(s)}")),
        c.PageFaults, c.PageWalks, c.Instructions, c.TripleFaulted, c.Halted);
    /// <summary>Creates identical fixtures, with a no-op observer forcing the original sequential immediate-fetch path.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Fixture(byte[] code, bool paging, bool observe)
    {
        var pair = paging ? PagingTests.Create(code) : ProtectedModeTests.Rig(code);
        if (observe) pair.Cpu.InstructionStarting += (_, _) => { };
        return pair;
    }
    private sealed class CodeDevice(byte[] bytes, Action<int>? read = null) : IMemoryDevice
    {
        public readonly List<uint> Reads = [];
        /// <summary>Records each ordered instruction-byte access and invokes the case's explicit side-effect hook.</summary>
        public byte ReadByte(uint offset) { Reads.Add(offset); read?.Invoke(Reads.Count); return bytes[(int)offset % bytes.Length]; }
        /// <summary>Rejects accidental instruction-memory writes in this controlled read-only device fixture.</summary>
        public void WriteByte(uint offset, byte value) => throw new Exception("Unexpected code-device write");
    }
    /// <summary>Registers same-page, boundary, fault, callback and self-modifying-code cases for the decoder optimization.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool paging in new[] { false, true })
        foreach (int width in new[] { 1, 2, 4 })
        foreach (int address in new[] { 0x2000, 0x2ffc, 0x2ffe, 0x2fff })
            test($"immediate RAM fetch matches ordered bytes width {width}, paging={paging}, address={address:X}", () =>
            {
                byte[] code = width == 1 ? [0xb0, 0x85] : width == 2 ? [0x66, 0xb8, 0x85, 0x93] : [0xb8, 0x85, 0x93, 0xa7, 0xf1];
                var a = Fixture([], paging, false); var b = Fixture([], paging, true);
                code.CopyTo(a.Memory.Ram, address); code.CopyTo(b.Memory.Ram, address); a.Cpu.Eip = b.Cpu.Eip = (uint)address;
                a.Cpu.SetRegister(Register.Ax, 0x76543210); b.Cpu.SetRegister(Register.Ax, 0x76543210);
                a.Cpu.Step(); b.Cpu.Step(); Check(State(a.Cpu) == State(b.Cpu));
                Check(a.Memory.Ram.SequenceEqual(b.Memory.Ram), "Fetch changed RAM/page accessed bits");
                uint expected = width == 1 ? 0x76543285u : width == 2 ? 0x76549385u : 0xf1a79385;
                Check(a.Cpu.GetRegister(Register.Ax) == expected);
            });
        foreach (bool paging in new[] { false, true })
        foreach (int limit in new[] { 0x2001, 0x2002, 0x2003, 0x2004 })
            test($"immediate fetch preserves partial-CS fault/PTE order, limit={limit:X}, paging={paging}", () =>
            {
                var a = Fixture([0xb8, 1, 2, 3, 4], paging, false); var b = Fixture([0xb8, 1, 2, 3, 4], paging, true);
                foreach (var pair in new[] { a, b })
                {
                    // Reload a smaller CS through an actual far jump before the probe.
                    Descriptor(pair.Memory, 0x38, 0, (uint)limit, 0x9a, 4);
                    pair.Memory.Ram.AsSpan(0x2200, 7).Clear();
                    new byte[] { 0xea, 0, 0x20, 0, 0, 0x38, 0 }.CopyTo(pair.Memory.Ram, 0x2200);
                    pair.Cpu.Eip = 0x2200; pair.Cpu.Step(); pair.Cpu.SetRegister(Register.Ax, 0x12345678);
                    pair.Cpu.Step();
                }
                Check(State(a.Cpu) == State(b.Cpu) && a.Memory.Ram.SequenceEqual(b.Memory.Ram));
                Check(a.Cpu.GetRegister(Register.Ax) == (limit < 0x2004 ? 0x12345678u : 0x04030201u));
            });
        foreach (int address in new[] { 0x2ffc, 0x2ffd, 0x2ffe, 0x2fff })
            test($"immediate fetch page fault restarts before any destination commit, address={address:X}", () =>
            {
                var a = Fixture([], true, false); var b = Fixture([], true, true);
                foreach (var (c, m) in new[] { a, b })
                {
                    new byte[] { 0xb8, 1, 2, 3, 4 }.CopyTo(m.Ram, address);
                    PagingTests.Pte(m, 0x3000, 0, 0); c.Eip = (uint)address; c.SetRegister(Register.Ax, 0xaabbccdd); c.Step();
                    Check(c.Cr2 == 0x3000 && c.PageFaults == 1 && c.GetRegister(Register.Ax) == 0xaabbccdd && c.Eip == 0x6000);
                }
                Check(State(a.Cpu) == State(b.Cpu) && a.Memory.Ram.SequenceEqual(b.Memory.Ram));
            });
        foreach (bool vm in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
            test($"immediate fetch keeps real/V86 default operand size, VM={vm}, width={width}", () =>
            {
                X86Cpu c; MemoryBus m;
                if (vm) { (c, m) = Fixture([], true, false); c.Eflags |= 0x20000; }
                else { m = new MemoryBus(2 * 1048576); c = new(m, new()); }
                c.SetSegment(Segment.Cs, 0); c.Eip = 0x2200;
                c.SetRegister(Register.Ax, 0x76540000);
                byte[] code = width == 2 ? [0xb8, 0x34, 0x12] : [0x66, 0xb8, 0x34, 0x12, 0x78, 0x56];
                code.CopyTo(m.Ram, 0x2200); c.Step();
                Check(c.GetRegister(Register.Ax) == (width == 2 ? 0x76541234u : 0x56781234u) && c.Eip == 0x2200 + code.Length);
            });
        foreach (int prefixCount in new[] { 10, 11, 12, 13 })
            test($"immediate fetch retains fifteen-byte length priority with {prefixCount} prefixes", () =>
            {
                byte[] code = Enumerable.Repeat((byte)0x67, prefixCount).Concat(new byte[] { 0xb8, 1, 2, 3, 4 }).ToArray();
                var a = Fixture(code, false, false); var b = Fixture(code, false, true);
                a.Cpu.Step(); b.Cpu.Step(); Check(State(a.Cpu) == State(b.Cpu));
                Check(a.Memory.Ram.SequenceEqual(b.Memory.Ram));
            });
        test("same-page immediate RAM/device boundary keeps ordered callbacks and full restart checkpoint", () =>
        {
            var results = new List<string>(); var counts = new List<int>();
            foreach (bool observe in new[] { false, true })
            {
                var (c, m) = Fixture([0xb8, 1, 2, 3, 4], false, observe);
                Descriptor(m, 0x38, 0x10000, 0xffff, 0x92);
                var old = c.GetSegmentCache(Segment.Ds);
                var device = new CodeDevice([2, 3], n => { if (n == 1) { c.SetSegment(Segment.Ds, 0x38); c.SetRegister(Register.Sp, 0x13000); } });
                m.Map(0x2002, 2, device, "partial code MMIO"); c.Step();
                Check(c.GetRegister(Register.Ax) == 0x04030201 && c.GetSegmentCache(Segment.Ds) != old);
                Check(device.Reads.SequenceEqual(new uint[] { 0, 1 })); results.Add(State(c)); counts.Add(device.Reads.Count);
            }
            Check(results[0] == results[1] && counts[0] == counts[1]);
        });
        test("immediate fetch reads self-modified bytes on the next execution instead of retaining a decoded value", () =>
        {
            var (c, m) = Fixture([0xb8, 1, 2, 3, 4], false, false); c.Step();
            m.WriteDword(0x2001, 0x98765432); c.Eip = 0x2000; c.Step(); Check(c.GetRegister(Register.Ax) == 0x98765432);
        });
        test("immediate fetch preserves A20 folding for both bytes and wide reads", () =>
        {
            var a = Fixture([], false, false); var b = Fixture([], false, true);
            foreach (var (c, m) in new[] { a, b })
            {
                m.A20Enabled = false; new byte[] { 0xb8, 0x78, 0x56, 0x34, 0x12 }.CopyTo(m.Ram, 0x2400); c.Eip = 0x102400; c.Step();
                Check(c.GetRegister(Register.Ax) == 0x12345678);
            }
            Check(State(a.Cpu) == State(b.Cpu));
        });
    }
}
