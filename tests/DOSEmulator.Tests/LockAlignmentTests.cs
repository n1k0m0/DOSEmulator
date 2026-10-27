// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class LockAlignmentTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string detail = "LOCK/alignment invariant failed")
    { if (!value) throw new Exception(detail); }
    /// <summary>Builds explicit locked or unlocked memory-opcode bytes with the requested width and address.</summary>
    private static byte[] Mem(byte opcode, int group = 0, int width = 4, bool locked = true, uint address = 0x30000, byte? extended = null, byte[]? immediate = null)
    {
        var bytes = new List<byte>();
        if (width == 2) bytes.Add(0x66);
        if (locked) bytes.Add(0xf0);
        bytes.Add(opcode); if (extended is { } next) bytes.Add(next);
        bytes.Add((byte)(5 | group << 3)); bytes.AddRange(BitConverter.GetBytes(address));
        if (immediate is not null) bytes.AddRange(immediate);
        return bytes.ToArray();
    }
    /// <summary>Returns the bit mask for the supported byte, word or doubleword access width.</summary>
    private static uint Mask(int width) => width == 4 ? uint.MaxValue : width == 2 ? 65535u : 255u;
    /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
    private static uint Read(MemoryBus m, int width, uint a = 0x30000) => width == 4 ? m.ReadDword(a) : width == 2 ? m.ReadWord(a) : m.ReadByte(a);
    /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
    private static void Write(MemoryBus m, int width, uint value, uint a = 0x30000)
    { if (width == 4) m.WriteDword(a, value); else if (width == 2) m.WriteWord(a, (ushort)value); else m.WriteByte(a, (byte)value); }
    /// <summary>Builds a controlled user-ring fixture with explicit paging and alignment-check controls.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) User(byte[] code, bool am = true, bool ac = true, int ring = 3, bool paging = true)
    {
        var (c, m) = paging ? PagingTests.Create(code) : ProtectedModeTests.Rig(code);
        c.SetRegister(Register.Ax, am ? c.Cr0 | 0x40000 : c.Cr0 & ~0x40000u);
        PagingTests.At(c, m, [0x0f, 0x22, 0xc0]); PagingTests.Tss(c, m);
        uint flags = ac ? 0x40202u : 0x202;
        if (ring != 0)
        {
            Descriptor(m, 0x28, 0, 0xfffff, (byte)(0x9a | ring << 5), 12);
            Descriptor(m, 0x30, 0, 0xfffff, (byte)(0x92 | ring << 5), 12);
            uint[] frame = [0x2000, (uint)(0x28 | ring), flags, 0x1b000, (uint)(0x30 | ring)];
            for (int i = 0; i < frame.Length; i++) m.WriteDword(0x18000u + (uint)i * 4, frame[i]);
            PagingTests.At(c, m, [0xcf], 0x2100); c.SetSegment(Segment.Ds, (ushort)(0x30 | ring)); c.SetSegment(Segment.Es, (ushort)(0x30 | ring));
        }
        else { c.Eflags = flags; c.Eip = 0x2000; }
        return (c, m);
    }
    /// <summary>Validates the real user-mode exception frame and its restart address.</summary>
    private static uint UserFault(X86Cpu c, MemoryBus m, byte vector, uint? error = 0, uint ip = 0x2000)
    {
        byte? entered = null; c.InterruptEntered += v => entered = v;
        uint originalSp = c.GetRegister(Register.Sp); ushort originalSs = c.GetSegment(Segment.Ss); c.Step(); uint frame = c.GetRegister(Register.Sp);
        Check(entered == vector && c.Eip == 0x6000 && c.CurrentPrivilegeLevel == 0 && !c.TripleFaulted,
            $"Expected #{vector}, got {entered} at {c.Position}, TR={c.Tr:X}");
        Check(frame == 0x1a000 - (error.HasValue ? 24 : 20));
        if (error.HasValue) Check(m.ReadWord(frame) == error, "Wrong error code");
        uint head = frame + (error.HasValue ? 4u : 0);
        Check(m.ReadDword(head) == ip && m.ReadDword(head + 4) == 0x2b && (m.ReadDword(head + 8) & 0x10000) != 0);
        Check(m.ReadDword(head + 12) == originalSp && m.ReadWord(head + 16) == originalSs);
        return frame;
    }
    private sealed class Device : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Returns a controlled mapped-device byte and records the test observation/fault hook.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0x55; }
        /// <summary>Applies the controlled mapped-device write and records its test observation/fault hook.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; Check(value == 0x55); }
    }
    private sealed class Port : IIoPortDevice
    {
        public int Reads, Writes;
        /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
        public uint Read(ushort port, int width) { Reads++; return 0x11223344; }
        /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
        public void Write(ushort port, uint value, int width) { Writes++; }
    }

    /// <summary>Registers the named lock alignment regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int width in new[] { 1, 2, 4 }) foreach (int op in Enumerable.Range(0, 7)) foreach (bool immediate in new[] { false, true })
            test($"LOCK ALU {op} width {width} {(immediate ? "immediate" : "register source")} writes memory", () =>
            {
                uint initial = 0x12345678 & Mask(width), source = 3;
                byte[] code = immediate ? Mem((byte)(width == 1 ? 0x80 : 0x83), op, width, immediate: [3]) : Mem((byte)(op * 8 + (width == 1 ? 0 : 1)), 1, width);
                var (c, m) = ProtectedModeTests.Rig(code); Write(m, width, initial); c.SetRegister(Register.Cx, source); c.Eflags |= 1;
                uint expected = op switch { 0 => initial + source, 1 => initial | source, 2 => initial + source + 1, 3 => initial - source - 1,
                    4 => initial & source, 5 => initial - source, _ => initial ^ source };
                c.Step(); Check(c.Eip == 0x2000 + code.Length && Read(m, width) == (expected & Mask(width)) && c.LockedMemoryInstructions == 1);
                Check(c.InvalidLockPrefixes == 0 && c.GetRegister(Register.Cx) == source);
            });
        foreach (int width in new[] { 1, 2, 4 }) foreach (int operation in new[] { 0, 1, 2, 3 })
            test($"LOCK unary width {width} operation {operation} preserves its contract", () =>
            {
                bool incDec = operation < 2;
                byte opcode = (byte)(incDec ? (width == 1 ? 0xfe : 0xff) : (width == 1 ? 0xf6 : 0xf7));
                var (c, m) = ProtectedModeTests.Rig(Mem(opcode, operation, width)); Write(m, width, 0x23); c.Eflags |= 1;
                uint expected = operation switch { 0 => 0x24, 1 => 0x22, 2 => ~0x23u, _ => unchecked(0u - 0x23) };
                c.Step(); Check(Read(m, width) == (expected & Mask(width)) && c.LockedMemoryInstructions == 1);
                Check((c.Eflags & 1) == 1);
            });
        foreach (int width in new[] { 2, 4 }) foreach (int operation in new[] { 5, 6, 7 }) foreach (bool immediate in new[] { false, true })
            test($"LOCK bit operation {operation} width {width} {(immediate ? "imm" : "reg")}", () =>
            {
                byte next = immediate ? (byte)0xba : operation == 5 ? (byte)0xab : operation == 6 ? (byte)0xb3 : (byte)0xbb;
                byte[] code = Mem(0x0f, immediate ? operation : 1, width, extended: next, immediate: immediate ? [1] : null);
                var (c, m) = ProtectedModeTests.Rig(code); c.SetRegister(Register.Cx, 1); Write(m, width, 2); c.Step();
                Check(Read(m, width) == (operation == 5 ? 2 : 0) && (c.Eflags & 1) != 0 && c.LockedMemoryInstructions == 1);
            });
        foreach (int width in new[] { 1, 2, 4 }) foreach (bool locked in new[] { false, true })
            test($"memory XCHG width {width} implicit lock, explicit={locked}", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(Mem((byte)(width == 1 ? 0x86 : 0x87), 1, width, locked));
                Write(m, width, 0x23); c.SetRegister(Register.Cx, 0x12345678); c.Eflags = 0x207; c.Step();
                Check(Read(m, width) == (0x12345678 & Mask(width)) && c.GetRegister(Register.Cx) == ((0x12345678 & ~Mask(width)) | 0x23));
                Check(c.LockedMemoryInstructions == 1 && c.Eflags == 0x207);
            });
        foreach (int width in new[] { 1, 2, 4 }) foreach (bool matches in new[] { false, true }) foreach (bool locked in new[] { false, true })
            test($"CMPXCHG width {width} equal={matches} lock={locked} sets accumulator and destination", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(Mem(0x0f, 1, width, locked, extended: (byte)(width == 1 ? 0xb0 : 0xb1)));
                Write(m, width, 0x23); c.SetRegister(Register.Ax, 0x98760000 | (matches ? 0x23u : 0x24)); c.SetRegister(Register.Cx, 0x57);
                if (width == 4) c.SetRegister(Register.Ax, matches ? 0x23u : 0x24);
                c.Step(); Check(Read(m, width) == (matches ? 0x57 : 0x23) && (c.GetRegister(Register.Ax) & Mask(width)) == 0x23);
                Check(((c.Eflags & X86Cpu.Zero) != 0) == matches && c.LockedMemoryInstructions == (locked ? 1 : 0));
            });
        foreach (int width in new[] { 1, 2, 4 })
            test($"XADD memory width {width} snapshots the source and old destination", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(Mem(0x0f, 1, width, extended: (byte)(width == 1 ? 0xc0 : 0xc1)));
                Write(m, width, 0x7f); c.SetRegister(Register.Cx, 0x12340001); c.Step();
                uint source = 0x12340001 & Mask(width);
                Check(Read(m, width) == ((0x7f + source) & Mask(width)) && c.GetRegister(Register.Cx) == ((0x12340001 & ~Mask(width)) | 0x7f));
                Check(c.LockedMemoryInstructions == 1); if (width == 1) Check((c.Eflags & 0x8d5) == 0x890);
            });
        foreach (byte[] code in new byte[][] { [0x0f, 0xc1, 0xc0], [0x66, 0x0f, 0xc1, 0xc0], [0x0f, 0xc0, 0xc0], [0x0f, 0xc0, 0xe0] })
            test($"register XADD alias {Convert.ToHexString(code)} keeps the final sum", () =>
            {
                var (c, m) = ProtectedModeTests.Rig(code); c.SetRegister(Register.Ax, 0x12345678); c.Step();
                uint expected = code[^1] == 0xe0 ? 0x123478ceu : code[0] == 0x66 ? 0x1234acf0u : code[1] == 0xc0 ? 0x123456f0u : 0x2468acf0u;
                Check(c.GetRegister(Register.Ax) == expected && c.LockedMemoryInstructions == 0);
            });
        test("CMPXCHG failed comparison still writes all bytes to an MMIO destination", () =>
        {
            var (c, m) = ProtectedModeTests.Rig(Mem(0x0f, 1, extended: 0xb1)); var device = new Device(); m.Map(0x30000, 4096, device, "CMPXCHG proof");
            c.SetRegister(Register.Ax, 0); c.SetRegister(Register.Cx, 0xdeadbeef); c.Step();
            Check(device.Reads == 4 && device.Writes == 4 && c.GetRegister(Register.Ax) == 0x55555555 && (c.Eflags & X86Cpu.Zero) == 0);
        });
        List<byte[]> invalid = [
            [0xf0,0x90], [0xf0,0x91], [0xf0,0xc3], [0xf0,0xf4], [0xf0,0xfa], [0xf0,0xdb,0xe3],
            [0xf0,0x89,0x05,0,0,3,0], [0xf0,0x39,0x05,0,0,3,0], [0xf0,0x85,0x05,0,0,3,0],
            [0xf0,0x83,0x3d,0,0,3,0,1], [0xf0,0xc1,0x25,0,0,3,0,1],
            [0xf0,0xff,0x15,0,0,3,0], [0xf0,0xf7,0x35,0,0,3,0], [0xf0,0xf7,0x05,0,0,3,0,1,0,0,0],
            [0xf0,0x0f,0xa3,0x05,0,0,3,0], [0xf0,0x0f,0xba,0x25,0,0,3,0,1], [0xf0,0x0f,0x01,0x05,0,0,3,0],
            [0xf0,0x0f,0xc8], [0xf0,0x0f,0xa2], [0xf0,0xa5], [0xf0,0x0f,0xb1,0xc8], [0xf0,0x0f,0xc1,0xc8],
            [0xf0,0x87,0xc8], [0xf0,0xfe,0xc0], [0xf0,0xf7,0xd8], [0xf0,0x0f,0xab,0xc8]
        ];
        foreach (int op in Enumerable.Range(0, 7)) invalid.Add([0xf0, (byte)(op * 8 + 1), 0xc8]);
        foreach (byte[] code in invalid)
            test($"invalid LOCK {Convert.ToHexString(code)} gives UD before data faults or side effects", () =>
            {
                var (c, m) = User(code); c.SetSegment(Segment.Ds, 0); c.SetRegister(Register.Ax, 0x12345678); c.SetRegister(Register.Cx, 0x13579bdf);
                var device = new Device(); m.Map(0x30000, 4096, device, "invalid LOCK side effects"); UserFault(c, m, 6, null);
                Check(c.InvalidLockPrefixes == 1 && c.LockedMemoryInstructions == 0 && c.AlignmentFaults == 0 && c.Cr2 == 0);
                Check(device.Reads == 0 && device.Writes == 0 && c.GetRegister(Register.Ax) == 0x12345678 && c.GetRegister(Register.Cx) == 0x13579bdf);
            });
        foreach (int ring in new[] { 0, 1, 2, 3 }) foreach (bool am in new[] { false, true }) foreach (bool ac in new[] { false, true }) foreach (int width in new[] { 2, 4 })
            test($"alignment enable ring {ring} AM={am} AC={ac} width {width}", () =>
            {
                byte[] code = width == 4 ? [0xa1,1,0,3,0] : [0x66,0xa1,1,0,3,0]; var (c, m) = User(code, am, ac, ring);
                Write(m, width, 0x12345678, 0x30001); c.SetRegister(Register.Ax, 0xaabbccdd);
                bool faults = am && ac && ring == 3;
                if (faults) { UserFault(c, m, 17); Check(c.GetRegister(Register.Ax) == 0xaabbccdd && c.AlignmentFaults == 1); }
                else { c.Step(); Check((c.GetRegister(Register.Ax) & Mask(width)) == (0x12345678 & Mask(width)) && c.AlignmentFaults == 0); }
            });
        foreach (bool write in new[] { false, true }) foreach (int width in new[] { 2, 4 })
            test($"unaligned {(write ? "store" : "load")} width {width} in nonpaged protected mode faults without data access", () =>
            {
                byte[] code = [(byte)(write ? 0xa3 : 0xa1), 1, 0, 3, 0]; if (width == 2) code = [0x66, ..code];
                var (c, m) = User(code, paging: false); var device = new Device(); m.Map(0x30000, 4096, device, "alignment side effect proof");
                c.SetRegister(Register.Ax, 0x55555555); UserFault(c, m, 17); Check(device.Reads == device.Writes && device.Reads == 0);
            });
        foreach (bool write in new[] { false, true })
            test($"486 reference AC precedes {(write ? "write-protection" : "missing-page")} PF for unaligned operand", () =>
            {
                var (c, m) = User([(byte)(write ? 0xa3 : 0xa1), 1, 0, 3, 0]); PagingTests.Pte(m, 0x30000, 0x30000, write ? 5u : 0);
                UserFault(c, m, 17); Check(c.AlignmentFaults == 1 && c.PageFaults == 0 && c.Cr2 == 0);
            });
        test("unaligned locked RMW produces AC before translation and preserves input registers", () =>
        {
            var (c, m) = User(Mem(0x0f, 1, address: 0x30001, extended: 0xc1)); PagingTests.Pte(m, 0x30000, 0x30000, 5);
            c.SetRegister(Register.Cx, 0x12345678); UserFault(c, m, 17);
            Check(c.GetRegister(Register.Cx) == 0x12345678 && c.PageFaults == 0 && c.LockedMemoryInstructions == 0);
        });
        foreach (bool write in new[] { false, true })
            test($"aligned {(write ? "store" : "load")} still gets proper PF while AC is enabled", () =>
            {
                var (c, m) = User([(byte)(write ? 0xa3 : 0xa1), 0, 0, 3, 0]); PagingTests.Pte(m, 0x30000, 0, 0);
                UserFault(c, m, 14, write ? 6u : 4u); Check(c.AlignmentFaults == 0 && c.PageFaults == 1 && c.Cr2 == 0x30000);
            });
        foreach (bool stack in new[] { false, true })
            test($"{(stack ? "SS" : "DS")} segment limit wins over AC and PF", () =>
            {
                byte[] code = stack ? [0x36,0xa1,1,0,3,0] : [0xa1,1,0,3,0]; var (c, m) = User(code);
                Descriptor(m, 0x38, 0, 0, 0xf2); c.SetSegment(stack ? Segment.Ss : Segment.Ds, 0x3b);
                UserFault(c, m, stack ? (byte)12 : (byte)13); Check(c.AlignmentFaults == c.PageFaults && c.AlignmentFaults == 0);
            });
        foreach (bool pop in new[] { false, true })
            test($"unaligned user {(pop ? "POP" : "PUSH")} saves old ESP for AC handler", () =>
            {
                var (c, m) = User([(byte)(pop ? 0x58 : 0x50)]); c.SetRegister(Register.Sp, 0x1b001); c.SetRegister(Register.Ax, 0x11223344);
                m.Ram.AsSpan(0x1affd, 8).Fill(0xa5); UserFault(c, m, 17);
                Check(c.GetRegister(Register.Ax) == 0x11223344 && m.Ram.AsSpan(0x1affd, 8).ToArray().All(b => b == 0xa5));
            });
        foreach (uint basis in new uint[] { 0x30000, 0x30001 })
            test($"AC checks linear address including DS base {basis:X}", () =>
            {
                var (c, m) = User([0xa1,0,0,0,0]); Descriptor(m, 0x38, basis, 0xffff, 0xf2); c.SetSegment(Segment.Ds, 0x3b);
                m.WriteDword(basis, 0x12345678);
                if ((basis & 3) != 0) UserFault(c, m, 17); else { c.Step(); Check(c.GetRegister(Register.Ax) == 0x12345678); }
            });
        foreach (bool idt in new[] { false, true }) foreach (uint address in new uint[] { 0x30000, 0x30001, 0x30002 })
            test($"486 {(idt ? "SIDT" : "SGDT")} user pseudo-descriptor at {address:X} needs 2 MOD 4", () =>
            {
                var (c, m) = User(Mem(0x0f, idt ? 1 : 0, locked: false, address: address, extended: 1)); m.Ram.AsSpan((int)address, 6).Fill(0xa5);
                if ((address & 3) != 2) { UserFault(c, m, 17); Check(m.Ram.AsSpan((int)address, 6).ToArray().All(b => b == 0xa5)); }
                else { c.Step(); Check(m.ReadWord(address) == (idt ? 0x7ff : 0xff) && m.ReadDword(address+2) == (idt ? 0x8000 : 0x400)); }
            });
        foreach (bool destination in new[] { false, true })
            test($"REP MOVSD {(destination ? "destination" : "source")} AC leaves count pointers and device untouched", () =>
            {
                var (c, m) = User([0xf3,0xa5]); var device = new Device(); m.Map(0x30000, 4096, device, "MOVSD proof");
                c.SetRegister(Register.Si, destination ? 0x30000u : 0x30001); c.SetRegister(Register.Di, destination ? 0x31001u : 0x31000); c.SetRegister(Register.Cx, 2);
                UserFault(c, m, 17); Check(c.GetRegister(Register.Cx) == 2 && device.Reads == 0 && device.Writes == 0);
            });
        test("INS alignment is checked before any input-port read", () =>
        {
            var port = new Port();
            var io = new IoPortBus(); io.Map(0x300, 1, port);
            var (c, m) = PagingTests.Create([0x6d], io: io); c.SetRegister(Register.Ax, c.Cr0 | 0x40000); PagingTests.At(c, m, [0x0f,0x22,0xc0]);
            PagingTests.Tss(c, m); PagingTests.User(c, m); c.SetSegment(Segment.Es, 0x33); c.Eflags = 0x43002;
            c.SetRegister(Register.Di, 0x30001); c.SetRegister(Register.Dx, 0x300); UserFault(c, m, 17); Check(port.Reads == 0);
        });
        test("AC handler can clear saved AC and retry the original unaligned load", () =>
        {
            var (c, m) = User([0xa1,1,0,3,0,0xf4]); m.WriteDword(0x30001, 0x12345678); uint frame = UserFault(c, m, 17);
            m.WriteDword(frame+12, m.ReadDword(frame+12) & ~0x40000u); c.SetRegister(Register.Sp, frame+4); m.WriteByte(0x6000, 0xcf); c.Step();
            Check(c.Eip == 0x2000 && c.CurrentPrivilegeLevel == 3 && !c.AlignmentCheckEnabled); c.Step();
            Check(c.GetRegister(Register.Ax) == 0x12345678 && c.Eip == 0x2005 && c.AlignmentFaults == 1);
        });
        test("V86 unaligned dword memory operand raises AC into a ring-zero monitor", () =>
        {
            var (c, m) = User([0xf4], ring: 0); uint sp = c.GetRegister(Register.Sp);
            uint[] values = [0x100, 0x3000, 0x63002, 0x8000, 0x4000, 0x5000, 0x6000, 0, 0];
            for (int i = 0; i < values.Length; i++) m.WriteDword(sp+(uint)i*4, values[i]); PagingTests.At(c, m, [0xcf]);
            new byte[] { 0x66,0xa1,1,0 }.CopyTo(m.Ram, 0x30100); c.Step();
            Check(c.Eip == 0x6000 && !c.Virtual8086 && c.CurrentPrivilegeLevel == 0 && c.AlignmentFaults == 1 && c.Virtual8086Exits == 1);
            Check(m.ReadWord(0x19fd8) == 0 && m.ReadDword(0x19fdc) == 0x100 && (m.ReadDword(0x19fe4) & 0x60000) == 0x60000);
        });
        test("sixteen instruction bytes give guest GP instead of a host unsupported stop", () =>
        {
            var code = Enumerable.Repeat((byte)0x3e, 15).Concat(new byte[] { 0x90 }).ToArray(); var (c, m) = ProtectedModeTests.Rig(code); Fault(c, m, 13);
            Check(c.InvalidLockPrefixes == 0);
        });
        test("fifteen-byte prefixed instruction remains executable", () =>
        {
            var code = Enumerable.Repeat((byte)0x3e, 14).Concat(new byte[] { 0x90 }).ToArray(); var (c, m) = ProtectedModeTests.Rig(code); c.Step(); Check(c.Eip == 0x200f);
        });
        test("reset clears LOCK and AC diagnostics", () =>
        {
            var (c, m) = User([0xa1,1,0,3,0]); UserFault(c, m, 17); c.Reset();
            Check(!c.AlignmentCheckEnabled && c.AlignmentFaults == 0 && c.LockedMemoryInstructions == 0 && c.InvalidLockPrefixes == 0);
        });
        test("user LOCK does not depend on IOPL and aligned data works with AC enabled", () =>
        {
            var (c, m) = User(Mem(1, 1)); m.WriteDword(0x30000, 5); c.SetRegister(Register.Cx, 7); c.Step();
            Check(c.CurrentPrivilegeLevel == 3 && (c.Eflags & 0x3000) == 0 && m.ReadDword(0x30000) == 12 && c.LockedMemoryInstructions == 1);
        });
        test("word bit-string alignment uses the 486 four-byte boundary", () =>
        {
            var (c, m) = User(Mem(0x0f, 1, 2, address: 0x30002, extended: 0xab)); c.SetRegister(Register.Cx, 0);
            UserFault(c, m, 17); Check(c.LockedMemoryInstructions == 0);
        });
        test("CMPXCHG mismatch still requires write permission before MMIO read", () =>
        {
            var (c, m) = User(Mem(0x0f, 1, extended: 0xb1)); var device = new Device(); m.Map(0x30000, 4096, device, "permission before compare");
            PagingTests.Pte(m, 0x30000, 0x30000, 5); c.SetRegister(Register.Ax, 0x12345678); c.SetRegister(Register.Cx, 0xdeadbeef);
            UserFault(c, m, 14, 7); Check(device.Reads == device.Writes && device.Reads == 0 && c.GetRegister(Register.Ax) == 0x12345678);
        });
        foreach (int width in new[] { 1, 2, 4 }) foreach (bool matches in new[] { false, true })
            test($"register CMPXCHG width {width} comparison={matches}", () =>
            {
                byte[] code = [0x0f, (byte)(width == 1 ? 0xb0 : 0xb1), 0xd9]; if (width == 2) code = [0x66, ..code];
                var (c, m) = ProtectedModeTests.Rig(code); c.SetRegister(Register.Ax, matches ? 0x23u : 0x24); c.SetRegister(Register.Cx, 0x23); c.SetRegister(Register.Bx, 0x57);
                c.Step(); Check(c.GetRegister(Register.Cx) == (matches ? 0x57u : 0x23) && (c.GetRegister(Register.Ax) & Mask(width)) == 0x23);
                Check(c.GetRegister(Register.Bx) == 0x57 && c.LockedMemoryInstructions == 0);
            });
        test("unaligned GDT and TSS implicit supervisor reads ignore user AC", () =>
        {
            var (c, m) = PagingTests.Create([0xcd,0x60]); Descriptor(m, 0x40, 0x9001, 0x67, 0x89);
            m.WriteDword(0x9005, 0x1a000); m.WriteWord(0x9009, 16); c.SetRegister(Register.Ax, 0x40); PagingTests.At(c, m, [0x0f,0x00,0xd8]);
            Descriptor(m, 0x28, 0, 0xfffff, 0xfa, 12); Descriptor(m, 0x30, 0, 0xfffff, 0xf2, 12);
            m.Ram.AsSpan(0x400, 256).CopyTo(m.Ram.AsSpan(0x401, 256)); m.WriteWord(0x300, 255); m.WriteDword(0x302, 0x401);
            PagingTests.At(c, m, [0x0f,0x01,0x15,0,3,0,0]); c.SetRegister(Register.Ax, c.Cr0 | 0x40000); PagingTests.At(c, m, [0x0f,0x22,0xc0]);
            uint[] frame = [0x2000,0x2b,0x40002,0x1b000,0x33]; for (int i = 0; i < frame.Length; i++) m.WriteDword(0x18000u+(uint)i*4, frame[i]);
            PagingTests.At(c, m, [0xcf], 0x2100); Gate(m, 0x60, access: 0xee); c.Step();
            Check(c.Eip == 0x6000 && c.CurrentPrivilegeLevel == 0 && c.AlignmentFaults == 0 && c.TrCache.Base == 0x9001 && c.PrivilegeStackSwitches == 1);
        });
        test("real mode executes an unaligned locked word operation with AM and AC set", () =>
        {
            var m = new MemoryBus(2*1024*1024); var c = new X86Cpu(m, new()); c.SetSegment(Segment.Cs, 0); c.SetSegment(Segment.Ds, 0); c.SetSegment(Segment.Ss, 0);
            c.SetRegister(Register.Sp, 0x9000); c.SetRegister(Register.Ax, c.Cr0 | 0x40000);
            PagingTests.At(c, m, [0x0f,0x22,0xc0], 0x1000); c.Eflags = 0x40002; c.SetRegister(Register.Cx, 3); m.WriteWord(0x3001, 5);
            PagingTests.At(c, m, [0xf0,0x01,0x0e,1,0x30], 0x1100);
            Check(m.ReadWord(0x3001) == 8 && !c.AlignmentCheckEnabled && c.AlignmentFaults == 0 && c.LockedMemoryInstructions == 1);
        });
    }
}
