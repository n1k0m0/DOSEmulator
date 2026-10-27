// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

/// <summary>Independent post-pop ESP addresses, source/destination effects and precise POP r/m fault restarts.</summary>
internal static class PopMemoryTests
{
    private const uint StackBase = 0x30000, DataBase = 0x50000, Value = 0x91b2c3d4;
    private sealed class StackDevice : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Counts actual source-stack reads without treating a memory preflight as a read.</summary>
        public byte ReadByte(uint offset) { Reads++; return (byte)(Value >> (int)(offset * 8)); }
        /// <summary>Records any unexpected mapped source-stack write.</summary>
        public void WriteByte(uint offset, byte value) => Writes++;
    }

    /// <summary>Requires a concrete independent address, byte guard, pointer or exception frame.</summary>
    private static void Check(bool value, string message = "POP memory contract failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Encodes independent operand/address overrides around one explicit ModR/M operand.</summary>
    private static byte[] Code(byte[] operand, bool code32 = true, int width = 4, bool address32 = true, byte? segment = null)
    {
        return [.. (segment.HasValue ? new byte[] { segment.Value } : []),
            .. ((width == 4) != code32 ? new byte[] { 0x66 } : []), .. (address32 != code32 ? new byte[] { 0x67 } : []), 0x8f, .. operand];
    }
    /// <summary>Creates private PE16/PE32 or paged CPU memory with distinct SS/DS bases and an independently sized stack.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Rig(X86CpuModel model, byte[] code, bool code32 = true, bool stack32 = true, bool paging = false)
    {
        var (cpu, memory) = paging ? PagingTests.Create(code, model: model) : ProtectedModeTests.Rig(code, code32: code32, stack32: stack32, model: model);
        Descriptor(memory, 0x58, StackBase, 0x1ffff, 0x92, stack32 ? (byte)4 : (byte)0); cpu.SetSegment(Segment.Ss, 0x58);
        Descriptor(memory, 0x60, DataBase, 0x1ffff, 0x92, 4); cpu.SetSegment(Segment.Ds, 0x60);
        cpu.SetRegister(Register.Sp, stack32 ? 0x8000u : 0x18000u); cpu.Eip = 0x2000; return (cpu, memory);
    }
    /// <summary>Reads the independently specified architectural operand width from raw private RAM.</summary>
    private static uint Read(MemoryBus memory, uint address, int width) => width == 2 ? memory.ReadWord(address) : memory.ReadDword(address);
    /// <summary>Writes only the independent source width and initializes destination guards before execution.</summary>
    private static void Prepare(MemoryBus memory, uint source, uint destination, int width)
    {
        memory.Ram.AsSpan((int)destination - 1, width + 2).Fill(0xa5);
        if (width == 2) memory.WriteWord(source, unchecked((ushort)Value)); else memory.WriteDword(source, Value);
    }
    /// <summary>Checks the destination and its adjacent bytes after an actual guest POP.</summary>
    private static void Destination(MemoryBus memory, uint address, int width, uint source = 0x38000)
    {
        Check(Read(memory, address, width) == (width == 2 ? Value & 0xffff : Value), "Incorrect POP destination value");
        byte left = address - 1 >= source && address - 1 < source + width ? (byte)(Value >> ((int)(address - 1 - source) * 8)) : (byte)0xa5;
        Check(memory.ReadByte(address - 1) == left && memory.ReadByte(address + (uint)width) == 0xa5, "POP changed destination guards");
    }
    /// <summary>Checks a protected exception on the original stack after rollback of a failed POP.</summary>
    private static void Fault(X86Cpu cpu, MemoryBus memory, byte vector, uint? error, uint oldEsp, uint ip = 0x2000)
    {
        byte? entered = null; cpu.InterruptEntered += v => entered = v; cpu.Step();
        uint pointer = (oldEsp - (error.HasValue ? 16u : 12u)) & (cpu.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff);
        uint expected = cpu.GetSegmentCache(Segment.Ss).Default32 ? pointer : oldEsp & 0xffff0000 | pointer;
        uint physical = StackBase + pointer;
        Check(entered == vector && cpu.Eip == 0x6000 && !cpu.TripleFaulted, $"Expected fault {vector}, got {entered} at {cpu.Position}");
        if (error.HasValue) { Check(memory.ReadWord(physical) == error.Value); physical += 4; }
        Check(cpu.GetRegister(Register.Sp) == expected && memory.ReadDword(physical) == ip, "POP fault did not preserve the original pointer/EIP");
        Check((memory.ReadDword(physical + 8) & 0x10000) != 0);
    }
    /// <summary>Executes a real handler's error removal and IRETD back to the faulting instruction.</summary>
    private static void Retry(X86Cpu cpu, MemoryBus memory)
    {
        new byte[] { 0x83, 0xc4, 4, 0xcf }.CopyTo(memory.Ram, 0x6000); cpu.Eip = 0x6000; cpu.Step(); cpu.Step();
        Check(cpu.Eip == 0x2000); cpu.Step();
    }

    /// <summary>Registers compact model-separated address/effect and fault/restart checks for POP memory.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool code32 in new[] { false, true })
        foreach (bool stack32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
            test($"CPU POP memory post ESP model {model} code32 {code32} SS32 {stack32} width {width}", () =>
            {
                byte[] code = Code([4, 0x24], code32, width); var (c, m) = Rig(model, code, code32, stack32);
                uint old = c.GetRegister(Register.Sp), source = 0x38000, destination = stack32 ? 0x38000u + (uint)width : 0x48000u + (uint)width;
                Prepare(m, source, destination, width); m.WriteDword(DataBase + 0x8000 + (uint)width, 0x11223344);
                c.Eflags = 0xcd7; c.SetRegister(Register.Bx, 0x12345678); c.Step();
                Destination(m, destination, width); Check(Read(m, source, width) == (width == 2 ? Value & 0xffff : Value));
                Check(c.GetRegister(Register.Sp) == old + width && c.Eip == 0x2000 + code.Length && c.Eflags == 0xcd7 && c.GetRegister(Register.Bx) == 0x12345678);
                Check(m.ReadDword(DataBase + 0x8000 + (uint)width) == 0x11223344, "ESP base incorrectly selected DS");
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
            test($"CPU POP memory real address32 ESP model {model} width {width}", () =>
            {
                byte[] code = Code([4, 0x24], false, width); var m = new MemoryBus(2 * 1024 * 1024); var c = new X86Cpu(m, new(), model);
                c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0x3000); c.SetRegister(Register.Sp, 0x8000); c.Eip = 0x2000; code.CopyTo(m.Ram, 0x2000);
                Prepare(m, 0x38000, 0x38000u + (uint)width, width); c.Eflags = 0x602; c.Step(); Destination(m, 0x38000u + (uint)width, width);
                Check(c.GetRegister(Register.Sp) == 0x8000 + width && c.Eip == 0x2000 + code.Length && c.Eflags == 0x602);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach ((byte[] operand, uint destination) in new (byte[], uint)[]
        {
            ([0x44, 0x24, 0x20], 0x38024), ([0x44, 0x24, 0xf0], 0x37ff4),
            ([0x84, 0x24, 0x00, 0x12, 0, 0], 0x39204), ([0x84, 0x24, 0, 0xf0, 0xff, 0xff], 0x37004),
            ([4, 0x8c], 0x38010), ([4, 0xf4], 0x3801c)
        })
            test($"CPU POP memory independent SIB displacement model {model} bytes {Convert.ToHexString(operand)}", () =>
            {
                byte[] code = Code(operand); var (c, m) = Rig(model, code); c.SetRegister(Register.Cx, 3); c.SetRegister(Register.Si, 3);
                Prepare(m, 0x38000, destination, 4); c.Eflags = 0x602; c.Step(); Destination(m, destination, 4);
                Check(c.GetRegister(Register.Sp) == 0x8004 && c.GetRegister(Register.Cx) == 3 && c.GetRegister(Register.Si) == 3 && c.Eflags == 0x602 && c.Eip == 0x2000 + code.Length);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory SIB without ESP base retains independent EBX address " + model, () =>
            {
                var (c, m) = Rig(model, Code([4, 0x8b])); c.SetRegister(Register.Bx, 0x1000); c.SetRegister(Register.Cx, 3);
                Prepare(m, 0x38000, 0x5100c, 4); c.Step(); Destination(m, 0x5100c, 4); Check(c.GetRegister(Register.Sp) == 0x8004 && c.GetRegister(Register.Bx) == 0x1000);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool code32 in new[] { false, true })
        foreach (int width in new[] { 2, 4 })
            test($"CPU POP memory address16 BP SI has no ESP substitution model {model} code32 {code32} width {width}", () =>
            {
                byte[] code = Code([0x42, 0xf0], code32, width, false); var (c, m) = Rig(model, code, code32);
                c.SetRegister(Register.Bp, 0xaaaa4000); c.SetRegister(Register.Si, 0xbbbb0020); Prepare(m, 0x38000, 0x34010, width); c.Step(); Destination(m, 0x34010, width);
                Check(c.GetRegister(Register.Sp) == 0x8000 + width && c.GetRegister(Register.Bp) == 0xaaaa4000 && c.GetRegister(Register.Si) == 0xbbbb0020);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach ((Segment segment, byte prefix, uint basis) in new (Segment, byte, uint)[] { (Segment.Ds, 0x3e, 0x60000), (Segment.Es, 0x26, 0x70000), (Segment.Fs, 0x64, 0x80000) })
            test($"CPU POP memory segment override {segment} model {model}", () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24], segment: prefix)); Descriptor(m, 0x68, basis, 0x1ffff, 0x92, 4); c.SetSegment(segment, 0x68);
                Prepare(m, 0x38000, basis + 0x8004, 4); m.WriteDword(0x38004, 0x11223344); c.Step();
                Destination(m, basis + 0x8004, 4); Check(m.ReadDword(0x38004) == 0x11223344 && c.GetRegister(Register.Sp) == 0x8004);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
        foreach (bool modrm in new[] { false, true })
            test($"CPU POP memory register ESP control model {model} width {width} modrm {modrm}", () =>
            {
                byte[] code = modrm ? Code([0xc4], width: width) : [.. (width == 2 ? new byte[] { 0x66 } : []), 0x5c];
                var (c, m) = Rig(model, code); c.SetRegister(Register.Sp, 0x18000); m.WriteDword(0x48000, Value); c.Eflags = 0x602; c.Step();
                Check(c.GetRegister(Register.Sp) == (width == 2 ? 0x1c3d4u : Value) && c.Eflags == 0x602 && m.ReadDword(0x48000) == Value && c.Eip == 0x2000 + code.Length);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int width in new[] { 2, 4 })
            test($"CPU POP memory SS16 wrap preserves high ESP for address32 model {model} width {width}", () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24], width: width), stack32: false); c.SetRegister(Register.Sp, 0x20000u - (uint)width);
                Prepare(m, 0x40000u - (uint)width, 0x40000, width); c.Step(); Destination(m, 0x40000, width, 0x40000u - (uint)width); Check(c.GetRegister(Register.Sp) == 0x10000);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory destination SS limit faults after source without consuming SP " + model, () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24])); Descriptor(m, 0x58, StackBase, 0x8003, 0x92, 4); c.SetSegment(Segment.Ss, 0x58);
                m.WriteDword(0x38000, Value); m.WriteDword(0x38004, 0xa5a5a5a5); Fault(c, m, 12, 0, 0x8000);
                Check(m.ReadDword(0x38000) == Value && m.ReadDword(0x38004) == 0xa5a5a5a5);
                Descriptor(m, 0x58, StackBase, 0x8007, 0x92, 4); c.SetSegment(Segment.Ss, 0x58); Retry(c, m);
                Check(c.GetRegister(Register.Sp) == 0x8004 && m.ReadDword(0x38004) == Value && c.Eip == 0x2003);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory source SS fault precedes null DS destination " + model, () =>
            {
                var (c, m) = Rig(model, Code([3])); Descriptor(m, 0x58, StackBase, 0x7fff, 0x92, 4); c.SetSegment(Segment.Ss, 0x58); c.SetSegment(Segment.Ds, 0);
                c.SetRegister(Register.Bx, 0x1000); m.WriteDword(0x38000, Value); Fault(c, m, 12, 0, 0x8000); Check(m.ReadDword(0x38000) == Value && c.GetRegister(Register.Bx) == 0x1000);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory null DS fault preserves pointer and source " + model, () =>
            {
                var (c, m) = Rig(model, Code([3])); c.SetSegment(Segment.Ds, 0); c.SetRegister(Register.Bx, 0x1000); m.WriteDword(0x38000, Value);
                Fault(c, m, 13, 0, 0x8000); Check(m.ReadDword(0x38000) == Value && c.GetRegister(Register.Bx) == 0x1000);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (int fault in new[] { 0, 1, 2 })
            test($"CPU POP memory paging source missing destination missing readonly model {model} case {fault}", () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24]), paging: true); uint old = fault == 0 ? 0x8000u : 0xfffc;
                c.SetRegister(Register.Sp, old); uint source = StackBase + old, destination = source + 4; m.WriteDword(source, Value); m.WriteDword(destination, 0xa5a5a5a5);
                uint missing = fault == 0 ? 0x38000u : 0x40000; PagingTests.Pte(m, missing, missing, fault == 2 ? 5u : 0);
                Fault(c, m, 14, fault == 0 ? 0u : fault == 1 ? 2u : 3u, old); Check(c.Cr2 == missing && m.ReadDword(source) == Value && m.ReadDword(destination) == 0xa5a5a5a5);
                PagingTests.Pte(m, missing, missing); PagingTests.At(c, m, [0x2e, 0x0f, 0x01, 0x3d, .. BitConverter.GetBytes(missing)], 0x2200); Retry(c, m);
                Check(c.GetRegister(Register.Sp) == old + 4 && m.ReadDword(destination) == Value && c.Eip == 0x2003);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory split destination PF performs no earlier partial write " + model, () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24]), paging: true); c.SetRegister(Register.Sp, 0x10ffa); m.WriteDword(0x40ffa, Value); m.WriteDword(0x40ffe, 0xa5a5a5a5);
                PagingTests.Pte(m, 0x41000, 0, 0); Fault(c, m, 14, 2, 0x10ffa);
                Check(c.Cr2 == 0x41000 && m.ReadDword(0x40ffa) == Value && m.ReadDword(0x40ffe) == 0xa5a5a5a5);
                PagingTests.Pte(m, 0x41000, 0x41000); Retry(c, m); Check(c.GetRegister(Register.Sp) == 0x10ffe && m.ReadDword(0x40ffe) == Value);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory instruction-tail PF precedes mapped source read " + model, () =>
            {
                var (c, m) = Rig(model, [0x90], paging: true); var device = new StackDevice(); m.Map(0x38000, 4, device, "POP private stack device");
                new byte[] { 0x8f, 0x84, 0x24 }.CopyTo(m.Ram, 0x2ffd); PagingTests.Pte(m, 0x3000, 0, 0); c.Eip = 0x2ffd;
                Fault(c, m, 14, 0, 0x8000, 0x2ffd); Check(c.Cr2 == 0x3000 && device.Reads == 0 && device.Writes == 0);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (bool fs in new[] { false, true })
            test($"CPU POP memory real destination limit retains unconsumed SP model {model} FS {fs}", () =>
            {
                var m = new MemoryBus(2 * 1024 * 1024); var c = new X86Cpu(m, new(), model); c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ss, 0x3000); c.SetSegment(Segment.Fs, 0x5000);
                byte[] code = Code([4, 0x24], false, 4, true, fs ? (byte)0x64 : null); code.CopyTo(m.Ram, 0x2000); c.Eip = 0x2000;
                uint old = fs ? 0x18000u : 0x1fffc, source = fs ? 0x38000u : 0x3fffc; c.SetRegister(Register.Sp, old); m.WriteDword(source, Value);
                byte vector = fs ? (byte)13 : (byte)12; m.WriteWord((uint)vector * 4, 0x6000); m.WriteWord((uint)vector * 4 + 2, 0); byte? entered = null; c.InterruptEntered += v => entered = v; c.Step();
                uint expected = old & 0xffff0000 | (old - 6) & 0xffff, frame = StackBase + (expected & 0xffff);
                Check(entered == vector && c.Eip == 0x6000 && c.GetRegister(Register.Sp) == expected && m.ReadWord(frame) == 0x2000 && m.ReadDword(source) == Value);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
            test("CPU POP memory combined read write debug trap observes committed post ESP " + model, () =>
            {
                var (c, m) = Rig(model, Code([4, 0x24])); m.WriteDword(0x38000, Value);
                c.SetRegister(Register.Ax, 0x38000); PagingTests.At(c, m, [0x0f, 0x23, 0xc0]);
                c.SetRegister(Register.Ax, 0x38004); PagingTests.At(c, m, [0x0f, 0x23, 0xc8]);
                c.SetRegister(Register.Ax, 0xdf0005); PagingTests.At(c, m, [0x0f, 0x23, 0xf8]); c.Eip = 0x2000;
                byte? entered = null; c.InterruptEntered += v => entered = v; c.Step();
                Check(entered == 1 && c.GetRegister(Register.Sp) == 0x7ff8 && (c.Dr6 & 15) == 3 && c.DataBreakpointTraps == 1 && m.ReadDword(0x38004) == Value);
                Check(m.ReadDword(0x37ff8) == 0x2003);
            });
        foreach (X86CpuModel model in Enum.GetValues<X86CpuModel>())
        foreach (byte[] code in new byte[][] { [0xf0, 0x8f, 4, 0x24], [0x8f, 0x0c, 0x24] })
            test($"CPU POP memory invalid form rejects before stack device read model {model} code {Convert.ToHexString(code)}", () =>
            {
                var (c, m) = Rig(model, code); var device = new StackDevice(); m.Map(0x38000, 4, device, "POP private stack device");
                if (code[0] == 0xf0) Fault(c, m, 6, null, 0x8000);
                else
                {
                    bool rejected = false; try { c.Step(); } catch (UnsupportedInstructionException) { rejected = true; }
                    Check(rejected && c.Eip == 0x2000 && c.GetRegister(Register.Sp) == 0x8000, "Reserved POP group changed its explicit unsupported-instruction policy");
                }
                Check(device.Reads == 0 && device.Writes == 0);
            });
    }
}
