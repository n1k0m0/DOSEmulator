// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;
using static ProtectedModeTests;

/// <summary>Independent instruction, fault, bus-effect and raw-state checks of the first P54C core subset.</summary>
internal static class P54cTests
{
    private const X86CpuModel Model = X86CpuModel.IntelP54cB5Subset;
    private const uint Low = 0x89abcdef, High = 0x76543210, NewLow = 0x13579bdf, NewHigh = 0x2468ace0;

    /// <summary>Requires an independently specified result instead of reusing the CPU implementation.</summary>
    private static void Check(bool value, string message = "P54C core contract failed")
    { if (!value) throw new Exception(message); }

    /// <summary>Builds the fixed-width CX8 operand with explicit operand/address prefixes.</summary>
    private static byte[] Code(bool code32, bool locked, uint address, bool operandPrefix = false, bool addressPrefix = false, byte? segment = null)
    {
        var bytes = new List<byte>();
        if (operandPrefix) bytes.Add(0x66);
        if (addressPrefix) bytes.Add(0x67);
        if (segment is { } prefix) bytes.Add(prefix);
        if (locked) bytes.Add(0xf0);
        bytes.AddRange([0x0f, 0xc7]);
        bool address32 = code32 != addressPrefix;
        bytes.Add(address32 ? (byte)0x0d : (byte)0x0e);
        bytes.AddRange(address32 ? BitConverter.GetBytes(address) : BitConverter.GetBytes((ushort)address));
        return bytes.ToArray();
    }

    /// <summary>Constructs the requested execution mode using real guest descriptor/control-register operations.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Rig(byte[] code, int mode, X86CpuModel model = Model)
    {
        if (mode == 3) return PagingTests.Create(code, model: model);
        if (mode != 0) return ProtectedModeTests.Rig(code, code32: mode == 2, model: model);
        var memory = new MemoryBus(2 * 1024 * 1024);
        var cpu = new X86Cpu(memory, new(), model);
        cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x2000; cpu.SetRegister(Register.Sp, 0x9000);
        code.CopyTo(memory.Ram, 0x2000);
        return (cpu, memory);
    }

    /// <summary>Loads independent register and memory inputs, with defined non-result flags as guards.</summary>
    private static void Inputs(X86Cpu cpu, MemoryBus memory, uint address, bool equal)
    {
        memory.WriteDword(address, Low); memory.WriteDword(address + 4, High);
        memory.WriteByte(address - 1, 0xa7); memory.WriteByte(address + 8, 0x5c);
        cpu.SetRegister(Register.Ax, equal ? Low : Low ^ 1); cpu.SetRegister(Register.Dx, High);
        cpu.SetRegister(Register.Bx, NewLow); cpu.SetRegister(Register.Cx, NewHigh);
        cpu.SetRegister(Register.Si, 0xabcddcba); cpu.SetRegister(Register.Di, 0x12344321);
        cpu.Eflags = 0x200c97;
    }

    private sealed class Device : IMemoryDevice
    {
        public readonly byte[] Data = BitConverter.GetBytes(((ulong)High << 32) | Low);
        public int Reads, Writes;
        /// <summary>Records each actual mapped byte read in its original bus order.</summary>
        public byte ReadByte(uint offset) { Reads++; return Data[(int)offset & 7]; }
        /// <summary>Records writeback on both comparison outcomes, including unchanged destination bytes.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; Data[(int)offset & 7] = value; }
    }

    /// <summary>Encodes explicit CPU state, including raw FPU, restart, descriptor, debug and TLB fields.</summary>
    private static string State(X86Cpu cpu) => JsonSerializer.Serialize(cpu.SaveState(), StateValues.Json);

    /// <summary>Registers isolated P54C subset cases without enabling a new selectable PC profile.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("M4 P54C reset identity and fixed subset " + model, () =>
            {
                var cpu = new X86Cpu(new(2 * 1024 * 1024), new(), model);
                Check(cpu.GetRegister(Register.Dx) == (model == X86CpuModel.IntelP55cMmxSubset ? 0x543u : model == Model ? 0x524u : 0x400u));
                Check(cpu.Model == model && cpu.Eflags == 2 && cpu.Cr0 == 0x60000010 && cpu.PhysicalInstructionAddress == 0xfffffff0);
                cpu.SetRegister(Register.Ax, 0xdeadbeef); cpu.Reset(); Check(cpu.Model == model && cpu.GetRegister(Register.Ax) == 0);
            });
        test("M4 P54C retains the original two-argument constructor ABI", () =>
        {
            Check(typeof(X86Cpu).GetConstructor([typeof(MemoryBus), typeof(IoPortBus)]) is not null);
            var cpu = new X86Cpu(new(2 * 1024 * 1024), new());
            Check(cpu.Model == X86CpuModel.Intel486Dx && cpu.GetRegister(Register.Dx) == 0x400);
        });
        test("M4 P54C invalid model rejected before construction", () =>
        {
            bool rejected = false;
            try { _ = new X86Cpu(new(2 * 1024 * 1024), new(), (X86CpuModel)42); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected);
        });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (uint leaf in new uint[] { 0, 1, 2, 0x80000000, uint.MaxValue })
        foreach (bool prefix in new[] { false, true })
            test($"M4 P54C CPUID mode {mode} leaf {leaf:X} prefix {prefix}", () =>
            {
                byte[] code = prefix ? [0x66, 0x0f, 0xa2] : [0x0f, 0xa2];
                var (c, m) = Rig(code, mode); c.SetRegister(Register.Ax, leaf);
                c.SetRegister(Register.Bx, 0xdeadbeef); c.SetRegister(Register.Cx, 0x12345678); c.SetRegister(Register.Dx, 0x87654321);
                c.SetRegister(Register.Si, 0xa5a55a5a); c.Eflags = 0x200cd7;
                long instructions = c.Instructions; c.Step();
                Check(c.Eip == 0x2000 + code.Length && c.Instructions == instructions + 1);
                Check(c.GetRegister(Register.Ax) == (leaf == 0 ? 1u : leaf == 1 ? 0x524u : 0));
                Check(c.GetRegister(Register.Bx) == (leaf == 0 ? 0x756e6547u : 0));
                Check(c.GetRegister(Register.Cx) == (leaf == 0 ? 0x6c65746eu : 0));
                Check(c.GetRegister(Register.Dx) == (leaf == 0 ? 0x49656e69u : leaf == 1 ? 0x101u : 0));
                Check(c.GetRegister(Register.Si) == 0xa5a55a5a && c.Eflags == 0x200cd7 && !c.TripleFaulted);
            });
        foreach (int mode in new[] { 0, 1, 2, 3 })
        foreach (bool locked in new[] { false, true })
        foreach (bool equal in new[] { false, true })
        foreach (bool operandPrefix in new[] { false, true })
        foreach (bool addressPrefix in new[] { false, true })
            test($"M4 P54C CX8 mode {mode} LOCK {locked} equal {equal} op66 {operandPrefix} addr67 {addressPrefix}", () =>
            {
                byte[] code = Code(mode >= 2, locked, 0x7000, operandPrefix, addressPrefix);
                var (c, m) = Rig(code, mode); Inputs(c, m, 0x7000, equal); long count = c.LockedMemoryInstructions;
                c.Step(); Check(c.Eip == 0x2000 + code.Length && !c.TripleFaulted);
                Check(m.ReadDword(0x7000) == (equal ? NewLow : Low) && m.ReadDword(0x7004) == (equal ? NewHigh : High));
                Check(c.GetRegister(Register.Ax) == (equal ? Low : Low) && c.GetRegister(Register.Dx) == High);
                Check(c.GetRegister(Register.Bx) == NewLow && c.GetRegister(Register.Cx) == NewHigh);
                Check(c.GetRegister(Register.Si) == 0xabcddcba && c.GetRegister(Register.Di) == 0x12344321);
                Check(c.Eflags == ((0x200c97u & ~X86Cpu.Zero) | (equal ? X86Cpu.Zero : 0)));
                Check(m.ReadByte(0x6fff) == 0xa7 && m.ReadByte(0x7008) == 0x5c && c.LockedMemoryInstructions == count + (locked ? 1 : 0));
            });
        foreach (bool equal in new[] { false, true })
        foreach (bool locked in new[] { false, true })
            test($"M4 P54C CX8 actual MMIO writeback equal {equal} LOCK {locked}", () =>
            {
                var (c, m) = Rig(Code(true, locked, 0x30000), 2); Inputs(c, m, 0x30000, equal);
                var device = new Device(); m.Map(0x30000, 8, device, "Own CX8 bus-effect fixture"); c.Step();
                Check(device.Reads == 8 && device.Writes == 8);
                Check(BitConverter.ToUInt32(device.Data) == (equal ? NewLow : Low));
                Check(BitConverter.ToUInt32(device.Data, 4) == (equal ? NewHigh : High));
            });
        foreach (uint address in new uint[] { 0x30ff9, 0x30ffc, 0x30ffd, 0x30fff })
        foreach (bool equal in new[] { false, true })
            test($"M4 P54C CX8 nonidentity cross-page {address:X} equal {equal}", () =>
            {
                var (c, m) = Rig(Code(true, true, address), 3);
                PagingTests.Pte(m, 0x30000, 0x50000); PagingTests.Pte(m, 0x31000, 0x72000);
                Inputs(c, m, address, equal);
                for (uint i = 0; i < 8; i++) m.WriteByte(i + address < 0x31000 ? 0x50000 + ((address + i) & 4095) : 0x72000 + ((address + i) & 4095), BitConverter.GetBytes(((ulong)High << 32) | Low)[i]);
                c.Step(); Check(c.Eip == 0x2000 + Code(true, true, address).Length);
                byte[] expected = BitConverter.GetBytes(equal ? ((ulong)NewHigh << 32) | NewLow : ((ulong)High << 32) | Low);
                for (uint i = 0; i < 8; i++) Check(m.ReadByte(i + address < 0x31000 ? 0x50000 + ((address + i) & 4095) : 0x72000 + ((address + i) & 4095)) == expected[i]);
            });
        foreach (bool missing in new[] { false, true })
        foreach (bool equal in new[] { false, true })
            test($"M4 P54C CX8 upper-page {(missing ? "absent" : "read only")} preflights without MMIO equal {equal}", () =>
            {
                var (c, m) = Rig(Code(true, true, 0x30ffc), 3); Inputs(c, m, 0x30ffc, equal);
                var device = new Device(); m.Map(0x30000, 4096, device, "Own no-read-on-fault fixture");
                PagingTests.Pte(m, 0x31000, 0x31000, missing ? 0u : 5u);
                uint a = c.GetRegister(Register.Ax), d = c.GetRegister(Register.Dx);
                Fault(c, m, 14, missing ? 2u : 3u);
                Check(device.Reads == 0 && device.Writes == 0 && c.Cr2 == 0x31000);
                Check(c.GetRegister(Register.Ax) == a && c.GetRegister(Register.Dx) == d && c.LockedMemoryInstructions == 0);
            });
        foreach (int group in new[] { 0, 2, 3, 4, 5, 6, 7 })
        foreach (bool locked in new[] { false, true })
            test($"M4 P54C CX8 reserved group {group} LOCK {locked} before data", () =>
            {
                var (c, m) = Rig([(byte)(locked ? 0xf0 : 0x3e), 0x0f, 0xc7, (byte)(5 | group << 3), 0, 0, 3, 0], 2);
                var device = new Device(); m.Map(0x30000, 8, device, "Own invalid-opcode fixture");
                Fault(c, m, 6, null); Check(device.Reads == 0 && device.Writes == 0);
            });
        foreach (byte modrm in new byte[] { 0xc8, 0xc9, 0xcb, 0xcf })
            test($"M4 P54C CX8 rejects register form {modrm:X}", () =>
            {
                var (c, m) = Rig([0xf0, 0x0f, 0xc7, modrm], 2); c.SetRegister(Register.Ax, 0x12345678);
                Fault(c, m, 6, null); Check(c.GetRegister(Register.Ax) == 0x12345678);
            });
        foreach (uint address in new uint[] { 0x7001, 0x7002, 0x7003, 0x7004, 0x7005, 0x7006, 0x7007 })
            test($"M4 P54C CX8 user alignment {address:X} checks all eight bytes", () =>
            {
                var (c, m) = Rig(Code(true, true, address), 3);
                c.SetRegister(Register.Ax, c.Cr0 | 0x40000); PagingTests.At(c, m, [0x0f, 0x22, 0xc0]);
                PagingTests.Tss(c, m); PagingTests.User(c, m); c.SetSegment(Segment.Ds, 0x33);
                Inputs(c, m, address, true); c.Eflags |= 0x40000; byte? entered = null; c.InterruptEntered += v => entered = v;
                c.Step(); Check(entered == 17 && c.AlignmentFaults == 1 && c.Eip == 0x6000 && !c.TripleFaulted);
                Check(m.ReadDword(address) == Low && m.ReadDword(address + 4) == High);
            });
        foreach (byte[] code in new byte[][] { [0x0f, 0xa2], [0x0f, 0xc7, 0x0d, 0, 0, 3, 0], [0xf0, 0x0f, 0xc7, 0x0d, 0, 0, 3, 0] })
            test("M4 P54C instruction unavailable on unchanged 486 " + Convert.ToHexString(code), () =>
            {
                var (c, m) = Rig(code, 2, X86CpuModel.Intel486Dx); Inputs(c, m, 0x30000, true);
                Fault(c, m, 6, null); Check(m.ReadDword(0x30000) == Low && c.GetRegister(Register.Bx) == NewLow);
                Check((c.Eflags & 0x200000) == 0);
            });
        foreach (bool p54c in new[] { false, true })
            test("M4 P54C EFLAGS ID follows selected model through actual POPFD " + p54c, () =>
            {
                var (c, m) = Rig([0x9d, 0x9c, 0x58], 2, p54c ? Model : X86CpuModel.Intel486Dx);
                c.Push(0x200002, 4); c.Step(); c.Step(); c.Step();
                Check(c.GetRegister(Register.Ax) == (p54c ? 0x200002u : 2u));
                c.Push(2, 4); PagingTests.At(c, m, [0x9d]); Check(c.Eflags == 2);
            });
        foreach (bool equal in new[] { false, true })
            test("M4 P54C CPU-only raw-state continuation " + equal, () =>
            {
                byte[] program = [0xdb, 0xe3, 0xd9, 0xeb, 0x0f, 0xa2, 0xf0, 0x0f, 0xc7, 0x0e, 0, 0x70, 0xf4];
                var (a, memoryA) = Rig(program, 0); a.Step(); a.Step(); a.Step(); Inputs(a, memoryA, 0x7000, equal);
                var memoryB = new MemoryBus(memoryA.Ram.Length); memoryA.Ram.CopyTo(memoryB.Ram, 0);
                var b = new X86Cpu(memoryB, new(), Model);
                StateBlock saved = JsonSerializer.Deserialize<StateBlock>(State(a), StateValues.Json)!; b.LoadState(saved);
                Check(State(a) == State(b)); a.Step(); b.Step();
                Check(State(a) == State(b) && memoryA.Ram.SequenceEqual(memoryB.Ram)); a.Step(); b.Step();
                Check(a.Halted && b.Halted && State(a) == State(b));
            });
        foreach (bool p54cTo486 in new[] { false, true })
            test("M4 P54C CPU-only state rejects model mismatch before field mutation " + p54cTo486, () =>
            {
                var (a, _) = Rig([0x90], 0, p54cTo486 ? Model : X86CpuModel.Intel486Dx);
                var (b, _) = Rig([0x90], 0, p54cTo486 ? X86CpuModel.Intel486Dx : Model);
                a.SetRegister(Register.Ax, 0xdeadbeef); string before = State(b); bool rejected = false;
                try { b.LoadState(a.SaveState()); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && State(b) == before); b.Step(); Check(b.Eip == 0x2001);
            });

        foreach (int mode in new[] { 0, 1 })
        foreach (ushort offset in new ushort[] { 0xfff9, 0xfffc, 0xffff })
            test($"M4 P54C CX8 segment preflight mode {mode} offset {offset:X}", () =>
            {
                var (c, m) = Rig(Code(false, true, offset), mode);
                if (mode == 1) c.SetSegment(Segment.Ds, 32);
                var device = new Device(); m.Map(0xfff8, 16, device, "Own segment-boundary fixture");
                if (mode == 0)
                {
                    m.WriteWord(13 * 4, 0x6000); m.WriteWord(13 * 4 + 2, 0);
                    byte? entered = null; c.InterruptEntered += v => entered = v; c.Step();
                    Check(entered == 13 && c.Eip == 0x6000 && m.ReadWord(c.GetRegister(Register.Sp)) == 0x2000);
                }
                else Fault(c, m, 13);
                Check(device.Reads == 0 && device.Writes == 0);
            });
        foreach (bool equal in new[] { false, true })
        foreach (int addressRegister in new[] { 0, 1, 2, 3 })
            test($"M4 P54C CX8 source/address alias r{addressRegister} equal {equal}", () =>
            {
                var (c, m) = Rig([0xf0, 0x0f, 0xc7, (byte)(8 | addressRegister)], 2);
                c.SetRegister(Register.Ax, Low); c.SetRegister(Register.Dx, High);
                c.SetRegister(Register.Bx, NewLow); c.SetRegister(Register.Cx, NewHigh);
                c.SetRegister((Register)addressRegister, 0x30000);
                uint a = c.GetRegister(Register.Ax), d = c.GetRegister(Register.Dx), b = c.GetRegister(Register.Bx), x = c.GetRegister(Register.Cx);
                uint low = equal ? a : a ^ 1, high = d;
                m.WriteDword(0x30000, low); m.WriteDword(0x30004, high); c.Step();
                Check(c.Eip == 0x2004 && m.ReadDword(0x30000) == (equal ? b : low) && m.ReadDword(0x30004) == (equal ? x : high));
                Check(c.GetRegister(Register.Ax) == (equal ? a : low) && c.GetRegister(Register.Dx) == d);
            });
        foreach (bool paging in new[] { false, true })
        foreach (bool cpuid in new[] { false, true })
        foreach (bool locked in new[] { false, true })
            test($"M4 P54C V86 paging {paging} CPUID {cpuid} LOCK {locked}", () =>
            {
                byte[] code = cpuid ? [0x0f, 0xa2] : Code(false, locked, 0x7000);
                var (c, m) = PagingTests.Create([0xf4], enable: false, model: Model);
                PagingTests.Tss(c, m); if (paging) PagingTests.Enable(c, m);
                uint[] frame = [0x100, 0x3000, 0x220002, 0x8000, 0x4000, 0, 0, 0, 0];
                for (uint i = 0; i < 9; i++) m.WriteDword(0x18000 + i * 4, frame[i]);
                code.CopyTo(m.Ram, 0x30100); PagingTests.At(c, m, [0xcf]);
                Check(c.Virtual8086 && c.Eip == 0x100);
                if (cpuid) c.SetRegister(Register.Ax, 1); else Inputs(c, m, 0x7000, true);
                c.Eflags |= 0x20000; c.Step();
                Check(c.Virtual8086 && c.Eip == 0x100 + code.Length && !c.TripleFaulted);
                if (cpuid) Check(c.GetRegister(Register.Ax) == 0x524 && c.GetRegister(Register.Dx) == 0x101);
                else Check(m.ReadDword(0x7000) == NewLow && m.ReadDword(0x7004) == NewHigh);
            });
        test("M4 P54C LOCK CPUID rejects before changing identification registers", () =>
        {
            var (c, m) = Rig([0xf0, 0x0f, 0xa2], 2); c.SetRegister(Register.Ax, 1); c.SetRegister(Register.Bx, NewLow);
            Fault(c, m, 6, null); Check(c.GetRegister(Register.Ax) == 1 && c.GetRegister(Register.Bx) == NewLow);
        });

        test("M4 P54C core is not advertised as selectable platform", () =>
        {
            using var m = PcMachine.CreateDemo(); Check(m.Cpu.Model == X86CpuModel.Intel486Dx);
            bool rejected = false;
            try { (new MachineProfile { Cpu = "p54c-b5-development" }).Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected && m.Ports.Read(0xcf8, 4) == uint.MaxValue);
        });
    }
}
