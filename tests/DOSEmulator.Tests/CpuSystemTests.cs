using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

internal static class CpuSystemTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("486 system-register invariant failed."); }
    private static (X86Cpu Cpu, MemoryBus Memory) Rig(params byte[] code)
    {
        var memory = new MemoryBus(2 * 1024 * 1024);
        var cpu = new X86Cpu(memory, new());
        cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x1000; cpu.SetRegister(Register.Sp, 0x9000);
        code.CopyTo(memory.Ram, 0x1000);
        return (cpu, memory);
    }
    private static byte[] TableCode(int operation, bool operand32 = false, ushort offset = 0x300)
    {
        var code = new List<byte>();
        if (operand32) code.Add(0x66);
        code.AddRange([0x0f, 0x01, (byte)((operation << 3) | 6), (byte)offset, (byte)(offset >> 8)]);
        return code.ToArray();
    }
    private static void Table(MemoryBus m, uint address, ushort limit, uint basis)
    {
        m.WriteWord(address, limit); m.WriteDword(address + 2, basis);
    }
    private static void Fault(X86Cpu c, MemoryBus m, byte vector)
    {
        m.WriteWord((uint)vector * 4, 0x2000); m.WriteWord((uint)vector * 4 + 2, 0);
        c.Step();
        Check(c.Eip == 0x2000 && c.GetSegment(Segment.Cs) == 0 && c.GetRegister(Register.Sp) == 0x8ffa);
        Check(m.ReadWord(0x8ffa) == 0x1000 && m.ReadWord(0x8ffc) == 0 && c.Instructions == 1);
    }
    private static void Unsupported(X86Cpu c)
    {
        try { c.Step(); } catch (UnsupportedInstructionException) { Check(c.Eip == 0x1000 && c.Instructions == 0); return; }
        throw new Exception("Unimplemented mode/instruction executed instead of stopping.");
    }
    private sealed class AccessProbe : IMemoryDevice
    {
        public int Reads, Writes;
        public byte ReadByte(uint offset) { Reads++; return 0xa5; }
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    public static void AddTo(Action<string, Action> test)
    {
        test("system registers reset to IVT and explicit undefined-state defaults", () =>
        {
            var (c, m) = Rig(TableCode(3, true)); Table(m, 0x300, 0x123, 0x76543210); c.Step();
            c.Eip = 0x1000; TableCode(2, true).CopyTo(m.Ram, 0x1000); c.Step();
            c.Eip = 0x1000; new byte[] { 0x0f, 0x22, 0xd0, 0x0f, 0x22, 0xd8 }.CopyTo(m.Ram, 0x1000);
            c.SetRegister(Register.Ax, 0x12345018); c.Step(); c.Step(); c.Reset();
            Check(c.Idtr == new DescriptorTableRegister(0x3ff, 0) && c.Gdtr == new DescriptorTableRegister(0xffff, 0));
            Check(c.Cr2 == 0 && c.Cr3 == 0 && c.Cr0 == 0x60000010 && c.PhysicalInstructionAddress == 0xfffffff0);
        });
        foreach (int operation in new[] { 2, 3 })
        foreach (bool wide in new[] { false, true })
            test($"{(operation == 2 ? "LGDT" : "LIDT")} loads {(wide ? 32 : 24)}-bit linear base and byte limit", () =>
            {
                var (c, m) = Rig(TableCode(operation, wide)); c.SetSegment(Segment.Ds, 0x100);
                Table(m, 0x1300, 0xabcd, 0x98765432); uint flags = c.Eflags = 0x0cd7;
                c.Step(); var table = operation == 2 ? c.Gdtr : c.Idtr;
                Check(table == new DescriptorTableRegister(0xabcd, wide ? 0x98765432u : 0x765432)); Check(c.Eflags == flags);
            });
        foreach (int operation in new[] { 0, 1 })
        foreach (bool wide in new[] { false, true })
            test($"{(operation == 0 ? "SGDT" : "SIDT")} stores six bytes with {(wide ? "full" : "zero high")} base byte", () =>
            {
                var (c, m) = Rig(TableCode(operation + 2, true).Concat(TableCode(operation, wide, 0x400)).ToArray());
                Table(m, 0x300, 0xabc, 0xfedcba98); m.Ram.AsSpan(0x3ff, 8).Fill(0x5a);
                c.Step(); uint flags = c.Eflags = 0x0cd7; c.Step();
                Check(m.ReadWord(0x400) == 0xabc && m.ReadDword(0x402) == (wide ? 0xfedcba98u : 0xdcba98));
                Check(m.ReadByte(0x3ff) == 0x5a && m.ReadByte(0x406) == 0x5a && c.Eflags == flags);
            });
        test("LGDT address-size SIB and SS override are independent of operand size", () =>
        {
            var (c, m) = Rig(0x67, 0x66, 0x0f, 0x01, 0x54, 0x8d, 0x10);
            c.SetSegment(Segment.Ss, 0x200); c.SetRegister(Register.Bp, 0x100); c.SetRegister(Register.Cx, 3);
            Table(m, 0x211c, 0x77, 0x12345678); c.Step(); Check(c.Gdtr == new DescriptorTableRegister(0x77, 0x12345678));
        });
        test("descriptor operand segment override uses ES instead of DS", () =>
        {
            var (c, m) = Rig(new byte[] { 0x26 }.Concat(TableCode(2, true)).ToArray());
            c.SetSegment(Segment.Es, 0x200); Table(m, 0x2300, 0x88, 0xfedcba98); c.Step();
            Check(c.Gdtr == new DescriptorTableRegister(0x88, 0xfedcba98));
        });
        test("six-byte pseudo-descriptor may end exactly at the segment limit", () =>
        {
            var (c, m) = Rig(TableCode(2, true, 0xfffa)); Table(m, 0xfffa, 7, 0x87654321); c.Step();
            Check(c.Gdtr == new DescriptorTableRegister(7, 0x87654321));
        });
        foreach (int operation in new[] { 0, 1, 2, 3 })
            test($"system table operation /{operation} rejects register with restartable UD", () =>
            {
                var (c, m) = Rig(0x0f, 0x01, (byte)(0xc0 | operation << 3)); c.SetRegister(Register.Ax, 0xabcdef01);
                Fault(c, m, 6); Check(c.GetRegister(Register.Ax) == 0xabcdef01 && c.Idtr.Base == 0 && c.Gdtr.Base == 0);
            });
        foreach (int operation in new[] { 0, 1, 2, 3 })
            test($"system table operation /{operation} faults before any partial operand access", () =>
            {
                var (c, m) = Rig(TableCode(operation, true, 0xfffb)); var probe = new AccessProbe();
                m.Map(0xfffb, 16, probe, "cross-limit operand"); Fault(c, m, 13);
                Check(probe.Reads == 0 && probe.Writes == 0 && c.Idtr.Base == 0 && c.Gdtr.Base == 0);
            });
        test("32-bit system operand offset overflow cannot wrap past limit validation", () =>
        {
            var (c, m) = Rig(0x67, 0x0f, 0x01, 0x10); c.SetRegister(Register.Ax, 0xfffffffd); Fault(c, m, 13);
            Check(c.Gdtr.Base == 0);
        });
        test("relocated real-mode IDT handles software INT and IRET", () =>
        {
            var (c, m) = Rig(TableCode(3, true).Concat(new byte[] { 0xcd, 0x60, 0xf4 }).ToArray());
            Table(m, 0x300, 0x183, 0x20000); m.WriteWord(0x20180, 0x400); m.WriteWord(0x20182, 0x100);
            new byte[] { 0xb8, 0xef, 0xbe, 0xcf }.CopyTo(m.Ram, 0x1400);
            for (int i = 0; i < 5; i++) c.Step();
            Check(c.Halted && c.GetRegister(Register.Ax) == 0xbeef && c.GetRegister(Register.Sp) == 0x9000);
        });
        test("external interrupt uses relocated IDT and clears halt and IF", () =>
        {
            var (c, m) = Rig(TableCode(3, true).Concat(new byte[] { 0xf4 }).ToArray());
            Table(m, 0x300, 0x3ff, 0x20000); m.WriteWord(0x20020, 0x1234); m.WriteWord(0x20022, 0x500);
            c.Step(); c.Eflags = 0x302; c.Step(); c.EnterRealModeInterrupt(8);
            Check(!c.Halted && c.Eip == 0x1234 && c.GetSegment(Segment.Cs) == 0x500 && c.Eflags == 2);
        });
        test("IDT limit diagnostic stops before interrupt frame or notification mutation", () =>
        {
            var (c, m) = Rig(TableCode(3).Concat(new byte[] { 0xcd, 0x61 }).ToArray());
            Table(m, 0x300, 0x183, 0x20000); c.Step(); int events = 0; c.InterruptEntered += _ => events++;
            try { c.Step(); throw new Exception("Invalid IDT access succeeded."); }
            catch (UnsupportedInstructionException) { Check(c.Eip == 0x1005 && c.GetRegister(Register.Sp) == 0x9000 && events == 0); }
        });
        test("LMSW only changes MP EM TS and ignores high bits and operand prefix", () =>
        {
            var (c, _) = Rig(0x66, 0x0f, 0x01, 0xf0); c.SetRegister(Register.Ax, 0xffffffee); c.Step();
            Check(c.Cr0 == 0x6000001e && c.GetRegister(Register.Ax) == 0xffffffee);
        });
        test("LMSW memory source remains sixteen bits with operand prefix", () =>
        {
            var (c, m) = Rig(TableCode(6, true, 0xfffe)); m.WriteWord(0xfffe, 0xfffc); c.Step(); Check(c.Cr0 == 0x6000001c);
        });
        test("LMSW enters protected mode and cannot clear PE", () =>
        {
            var (c, _) = Rig(0x0f, 0x01, 0xf0, 0x0f, 0x01, 0xf0);
            c.SetRegister(Register.Ax, 0xf); c.Step(); Check(c.Cr0 == 0x6000001f && c.ProtectedMode);
            c.SetRegister(Register.Ax, 0); c.Step(); Check(c.Cr0 == 0x60000011 && c.ProtectedMode);
        });
        test("SMSW preserves sixteen-bit destination upper bits and stores only a word to memory", () =>
        {
            var (c, m) = Rig(new byte[] { 0x0f, 0x01, 0xe0 }.Concat(TableCode(4, true, 0xfffe)).ToArray());
            c.SetRegister(Register.Ax, 0xaabb0000); m.WriteWord(0x10000, 0x7788); c.Step(); c.Step();
            Check(c.GetRegister(Register.Ax) == 0xaabb0010 && m.ReadWord(0xfffe) == 0x10 && m.ReadWord(0x10000) == 0x7788);
        });
        test("CLTS clears TS alone without changing general registers or flags", () =>
        {
            var (c, _) = Rig(0x0f, 0x22, 0xc0, 0x0f, 0x06); c.SetRegister(Register.Ax, 0x6005003e); c.Step();
            uint flags = c.Eflags = 0xcd7; c.Step(); Check(c.Cr0 == 0x60050036 && c.Eflags == flags && c.GetRegister(Register.Ax) == 0x6005003e);
        });
        foreach (int control in new[] { 0, 2, 3 })
            test($"MOV CR{control} uses all 32 register bits regardless of prefixes", () =>
            {
                var (c, _) = Rig(0x0f, 0x22, (byte)(0xc0 | control << 3), 0x66, 0x67, 0x0f, 0x20, (byte)(0xc3 | control << 3));
                uint value = control == 0 ? 0x6005001eu : 0x34567018u; c.SetRegister(Register.Ax, value); c.Step(); c.Step();
                Check(c.GetRegister(Register.Bx) == value);
            });
        test("CR0 ET remains hardwired when software writes zero", () =>
        {
            var (c, _) = Rig(0x0f, 0x22, 0xc0); c.SetRegister(Register.Ax, 0); c.Step(); Check(c.Cr0 == 0x10);
        });
        test("PG without PE generates restartable guest GP without changing CR0", () =>
        {
            var (c, m) = Rig(0x0f, 0x22, 0xc0); c.SetRegister(Register.Ax, 0xe0000010); Fault(c, m, 13); Check(c.Cr0 == 0x60000010);
        });
        test("MOV PE and PG entry retain the old CS cache until the next instruction fetch", () =>
        {
            var (c, _) = Rig(0x0f, 0x22, 0xc0, 0x0f, 0x22, 0xc0);
            var cache = c.GetSegmentCache(Segment.Cs); c.SetRegister(Register.Ax, 0x60000011); c.Step();
            Check(c.ProtectedMode && c.Cr0 == 0x60000011 && c.GetSegmentCache(Segment.Cs) == cache);
            c.SetRegister(Register.Ax, 0xe0000011);
            c.Step(); Check(c.Eip == 0x1006 && c.Cr0 == 0xe0000011 && c.PagingEnabled && c.GetSegmentCache(Segment.Cs) == cache);
        });
        test("486 CR1 and CR4 encodings generate guest UD instead of fabricated registers", () =>
        {
            foreach (byte modrm in new byte[] { 0xc8, 0xe0 }) { var (c, m) = Rig(0x0f, 0x20, modrm); Fault(c, m, 6); }
        });
        test("reserved system group generates UD and real-mode INVLPG invalidates without a memory read", () =>
        {
            var (c, m) = Rig(0x0f, 0x01, 0xe8); Fault(c, m, 6);
            var (other, _) = Rig(TableCode(7)); other.Step(); Check(other.Eip == 0x1005 && other.TlbInvalidations == 1);
        });
        test("WAIT NM restarts at the faulting instruction and CLTS handler permits retry", () =>
        {
            var (c, m) = Rig(0x0f, 0x22, 0xc0, 0x9b, 0xf4); c.SetRegister(Register.Ax, 0x6000001a);
            m.WriteWord(28, 0x2000); m.WriteWord(30, 0); new byte[] { 0x0f, 0x06, 0xcf }.CopyTo(m.Ram, 0x2000);
            c.Step(); c.Step(); Check(c.Eip == 0x2000 && m.ReadWord(0x8ffa) == 0x1003);
            c.Step(); c.Step(); c.Step(); c.Step();
            Check(c.Halted && c.Cr0 == 0x60000012 && c.GetRegister(Register.Sp) == 0x9000);
        });
        test("WAIT only checks TS with MP and ignores EM alone", () =>
        {
            foreach (uint bits in new uint[] { 0, 2, 4, 8, 12 })
            {
                var (c, _) = Rig(0x0f, 0x22, 0xc0, 0x9b); c.SetRegister(Register.Ax, 0x60000010 | bits); c.Step(); c.Step();
                Check(c.Eip == 0x1004 && c.GetRegister(Register.Sp) == 0x9000);
            }
        });
    }
}
