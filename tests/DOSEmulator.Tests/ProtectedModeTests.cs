// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class ProtectedModeTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string detail = "Protected-mode invariant failed") { if (!condition) throw new Exception(detail); }
    /// <summary>Writes the test's explicit descriptor bytes into its private guest-memory fixture.</summary>
    internal static void Descriptor(MemoryBus memory, ushort selector, uint basis, uint rawLimit, byte access, byte flags = 0)
    {
        uint address = 0x400u + ((uint)selector & 0xfff8);
        memory.WriteDword(address, (rawLimit & 0xffff) | (basis << 16));
        memory.WriteDword(address + 4, ((basis >> 16) & 0xff) | ((uint)access << 8) | (rawLimit & 0xf0000) | ((uint)flags << 20) | (basis & 0xff000000));
    }
    /// <summary>Writes an explicit interrupt/trap gate into the fixture's private IDT.</summary>
    internal static void Gate(MemoryBus memory, byte vector, ushort selector = 8, uint target = 0x6000, byte access = 0x8e)
    {
        uint address = 0x8000u + (uint)vector * 8;
        memory.WriteDword(address, (target & 0xffff) | ((uint)selector << 16));
        memory.WriteDword(address + 4, (target & 0xffff0000) | ((uint)access << 8));
    }
    /// <summary>Builds an isolated CPU/bus fixture with caller-supplied guest instruction bytes.</summary>
    internal static (X86Cpu Cpu, MemoryBus Memory) Rig(byte[] code, bool code32 = true, bool stack32 = true, uint target = 0x2000, IoPortBus? io = null, X86CpuModel model = X86CpuModel.Intel486Dx, DeviceScheduler? timestampSource = null, uint timestampFrequency = 0)
    {
        var memory = new MemoryBus(2 * 1024 * 1024); var cpu = new X86Cpu(memory, io ?? new(), model, timestampSource, timestampFrequency);
        cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x1000; cpu.SetRegister(Register.Sp, 0x9000);
        Descriptor(memory, 8, 0, 0xfffff, 0x9a, 12);
        Descriptor(memory, 16, 0, 0xfffff, 0x92, 12);
        Descriptor(memory, 24, 0, 0xffff, 0x9a);
        Descriptor(memory, 32, 0, 0xffff, 0x92);
        for (int i = 0; i < 256; i++) Gate(memory, (byte)i);
        memory.WriteByte(0x6000, 0xf4);
        memory.WriteWord(0x300, 0xff); memory.WriteDword(0x302, 0x400);
        memory.WriteWord(0x310, 0x7ff); memory.WriteDword(0x312, 0x8000);
        byte[] bootstrap = [0x0f,0x01,0x16,0,3, 0x66,0x0f,0x01,0x1e,0x10,3,
            0x66,0xb8,0x11,0,0,0x60, 0x0f,0x22,0xc0,
            0x66,0xea,(byte)target,(byte)(target>>8),(byte)(target>>16),(byte)(target>>24),(byte)(code32?8:24),0];
        bootstrap.CopyTo(memory.Ram, 0x1000); code.CopyTo(memory.Ram, (int)target);
        for (int i = 0; i < 5; i++) cpu.Step();
        Check(cpu.ProtectedMode && cpu.Eip == target && cpu.GetSegmentCache(Segment.Cs).Default32 == code32);
        cpu.SetSegment(Segment.Ss, (ushort)(stack32 ? 16 : 32));
        cpu.SetSegment(Segment.Ds, 16); cpu.SetSegment(Segment.Es, 16);
        cpu.SetRegister(Register.Sp, stack32 ? 0x18000u : 0xabcd8000u);
        return (cpu, memory);
    }
    /// <summary>Executes the component's explicit runner with the supplied bounds and reports its resulting evidence.</summary>
    private static void Run(X86Cpu cpu, int limit = 100)
    {
        for (int i = 0; i < limit && !cpu.Halted; i++) cpu.Step();
        Check(cpu.Halted && !cpu.TripleFaulted, "Guest did not reach its intended HLT");
    }
    /// <summary>Requires the expected architectural fault before inspecting or repairing the private guest context.</summary>
    internal static uint Fault(X86Cpu c, MemoryBus m, byte vector, uint? error = 0, uint expectedIp = 0x2000)
    {
        byte? entered = null; c.InterruptEntered += v => entered = v;
        uint stack = c.GetRegister(Register.Sp) & (c.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff);
        c.Step();
        uint frame = stack - (error.HasValue ? 16u : 12u);
        Check(entered == vector && c.Eip == 0x6000 && !c.TripleFaulted, $"Expected vector {vector}, got {entered}, {c.Position}");
        Check((c.GetRegister(Register.Sp) & (c.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff)) == frame);
        uint ipAddress = frame;
        if (error is { } value) { Check(m.ReadWord(frame) == value, "Fault error code mismatch"); ipAddress += 4; }
        Check(m.ReadDword(ipAddress) == expectedIp, "Fault did not save instruction start");
        Check((m.ReadDword(ipAddress + 8) & 0x10000) != 0, "Fault frame is missing RF");
        return frame;
    }

    /// <summary>Registers the named protected mode regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("PE entry far jump loads 32-bit code base limit attributes and accessed bit", () =>
        {
            var (c, m) = Rig([0xf4]); var cache = c.GetSegmentCache(Segment.Cs);
            Check(c.CurrentPrivilegeLevel == 0 && c.GetSegment(Segment.Cs) == 8);
            Check(cache.Usable && cache.Base == 0 && cache.Limit == uint.MaxValue && cache.Default32 && cache.Access == 0x9b);
            Check(m.ReadByte(0x40d) == 0x9b && m.ReadByte(0x415) == 0x93);
        });
        test("cached data base limit and rights survive later GDT replacement until reload", () =>
        {
            var (c, m) = Rig([0xa1,0,3,0,0,0x8e,0xd8,0xa1,0,3,0,0,0xf4]);
            m.WriteDword(0x300, 0x11223344); m.WriteDword(0x10300, 0xaabbccdd);
            Descriptor(m, 16, 0x10000, 0xffff, 0x92); c.Step(); Check(c.GetRegister(Register.Ax) == 0x11223344);
            c.SetRegister(Register.Ax, 16); c.Step(); Check(c.GetSegmentBase(Segment.Ds) == 0x10000 && c.GetSegmentCache(Segment.Ds).Limit == 0xffff);
            c.Step(); Check(c.GetRegister(Register.Ax) == 0xaabbccdd);
        });
        test("GDT byte granularity and page granularity yield different exact limits", () =>
        {
            var (c, m) = Rig([0xf4]); Descriptor(m, 40, 0x12345678, 0x12345, 0x92);
            c.SetSegment(Segment.Fs, 40); Check(c.GetSegmentBase(Segment.Fs) == 0x12345678 && c.GetSegmentCache(Segment.Fs).Limit == 0x12345);
            Descriptor(m, 40, 0x87654321, 0x12345, 0x92, 8); c.SetSegment(Segment.Fs, 40);
            Check(c.GetSegmentBase(Segment.Fs) == 0x87654321 && c.GetSegmentCache(Segment.Fs).Limit == 0x12345fff);
        });
        test("null data selector loads but memory use faults with GP zero", () =>
        {
            var (c, m) = Rig([0x8e,0xd8,0xa1,0,0,0,0]); c.SetRegister(Register.Ax, 3); c.Step();
            Check(c.GetSegment(Segment.Ds) == 3 && !c.GetSegmentCache(Segment.Ds).Usable);
            Fault(c, m, 13, 0, 0x2002);
        });
        foreach (ushort selector in new ushort[] { 0x100, 0x104, 4 })
            test($"selector {selector:X4} outside GDT or without valid LDT faults without replacing DS", () =>
            {
                var (c, m) = Rig([0x8e,0xd8]); c.SetRegister(Register.Ax, selector); Fault(c, m, 13, (uint)selector & 0xfffc);
                Check(c.GetSegment(Segment.Ds) == 16);
            });
        test("last eight-byte GDT entry is addressable at the table byte limit", () =>
        {
            var (c, m) = Rig([0x8e,0xd8,0xf4]); Descriptor(m, 0xf8, 0x345000, 0x789a, 0x92);
            c.SetRegister(Register.Ax, 0xf8); c.Step(); Check(c.GetSegmentBase(Segment.Ds) == 0x345000);
        });
        test("nonpresent data descriptor delivers NP selector and keeps old cache", () =>
        {
            var (c, m) = Rig([0x8e,0xd8]); Descriptor(m, 40, 0x1234, 0xffff, 0x12);
            c.SetRegister(Register.Ax, 40); var old = c.GetSegmentCache(Segment.Ds); Fault(c, m, 11, 40);
            Check(c.GetSegmentCache(Segment.Ds) == old && m.ReadByte(0x42d) == 0x12);
        });
        test("nonpresent writable stack delivers SS selector and keeps prior stack", () =>
        {
            var (c, m) = Rig([0x8e,0xd0]); Descriptor(m, 40, 0, 0xffff, 0x12);
            c.SetRegister(Register.Ax, 40); Fault(c, m, 12, 40); Check(c.GetSegment(Segment.Ss) == 16);
        });
        foreach (ushort selector in new ushort[] { 0, 8, 19 })
            test($"invalid stack selector {selector:X4} delivers GP and does not install SS", () =>
            {
                var (c, m) = Rig([0x8e,0xd0]); c.SetRegister(Register.Ax, selector); Fault(c, m, 13, (uint)selector & 0xfffc);
                Check(c.GetSegment(Segment.Ss) == 16);
            });
        test("execute-only code and system descriptor cannot be loaded as data", () =>
        {
            foreach (byte access in new byte[] { 0x98, 0x89 })
            {
                var (c, m) = Rig([0x8e,0xd8]); Descriptor(m, 40, 0, 0xffff, access); c.SetRegister(Register.Ax, 40); Fault(c, m, 13, 40);
            }
        });
        test("data selector RPL is checked against DPL and DPL three permits RPL three", () =>
        {
            var (c, m) = Rig([0x8e,0xd8]); c.SetRegister(Register.Ax, 19); Fault(c, m, 13, 16);
            var (other, ram) = Rig([0x8e,0xd8]); Descriptor(ram, 40, 0x12000, 0xffff, 0xf2);
            other.SetRegister(Register.Ax, 43); other.Step(); Check(other.GetSegment(Segment.Ds) == 43 && other.GetSegmentBase(Segment.Ds) == 0x12000);
        });
        test("conforming readable code can be used as data", () =>
        {
            var (c, m) = Rig([0x8e,0xd8]); Descriptor(m, 40, 0x12340000, 0xffff, 0x9e);
            c.SetRegister(Register.Ax, 43); c.Step(); Check(c.GetSegmentBase(Segment.Ds) == 0x12340000);
        });
        test("read-only destination fault preserves memory arithmetic flags and general registers", () =>
        {
            var (c, m) = Rig([0x83,0x05,0,3,0,0,1]); Descriptor(m, 40, 0, 0xffff, 0x90);
            c.SetSegment(Segment.Ds, 40); c.Eflags = 0x8d7; c.SetRegister(Register.Ax, 0xdeadbeef); m.WriteDword(0x300, uint.MaxValue);
            uint frame = Fault(c, m, 13); Check(m.ReadDword(0x300) == uint.MaxValue && c.GetRegister(Register.Ax) == 0xdeadbeef);
            Check(m.ReadDword(frame + 12) == 0x108d7);
        });
        test("data write through a code segment override faults before mutation", () =>
        {
            var (c, m) = Rig([0x2e,0xa3,0,3,0,0]); c.SetRegister(Register.Ax, 0xdeadbeef); Fault(c, m, 13); Check(m.ReadDword(0x300) != 0xdeadbeef);
        });
        test("segment boundary rejects an entire dword and keeps prior register value", () =>
        {
            var (c, m) = Rig([0xa1,0xfe,0xff,0,0]); c.SetSegment(Segment.Ds, 32); c.SetRegister(Register.Ax, 0x11223344);
            Fault(c, m, 13); Check(c.GetRegister(Register.Ax) == 0x11223344);
        });
        test("expand-down segment excludes its limit and honors sixteen-bit upper end", () =>
        {
            var (c, m) = Rig([0xa1,0,0x10,0,0,0xa1,0xfe,0xff,0,0,0xa1,0,0x10,0,0]);
            Descriptor(m, 40, 0, 0x1000, 0x96); c.SetSegment(Segment.Ds, 40); Fault(c, m, 13);
            c.Eip = 0x2005; Fault(c, m, 13, 0, 0x2005);
        });
        test("expand-down 32-bit segment permits a dword above the lower boundary", () =>
        {
            var (c, m) = Rig([0xa1,4,0x10,1,0]); Descriptor(m, 40, 0, 0x10000, 0x96, 4);
            c.SetSegment(Segment.Ds, 40); m.WriteDword(0x11004, 0xcafebabe); c.Step(); Check(c.GetRegister(Register.Ax) == 0xcafebabe);
        });
        test("32-bit CS decodes defaults and duplicate 66 prefixes independently", () =>
        {
            var (c, _) = Rig([0xb8,0x78,0x56,0x34,0x12,0x66,0x66,0xbb,0xef,0xbe,0xf4]);
            c.SetRegister(Register.Bx, 0xabcd0000); Run(c); Check(c.GetRegister(Register.Ax) == 0x12345678 && c.GetRegister(Register.Bx) == 0xabcdbeef);
        });
        test("32-bit CS address override selects sixteen-bit effective address", () =>
        {
            var (c, m) = Rig([0x67,0x8b,0x00,0xf4]); c.SetRegister(Register.Bx, 0xffff0300); c.SetRegister(Register.Si, 0xeeee0010);
            m.WriteDword(0x310, 0x87654321); Run(c); Check(c.GetRegister(Register.Ax) == 0x87654321);
        });
        test("32-bit short branches above 64KiB never truncate EIP", () =>
        {
            var (c, _) = Rig([0xeb,2,0x0f,0xff,0xb8,0xef,0xbe,0xad,0xde,0xf4], target: 0x12000);
            Run(c); Check(c.Eip == 0x1200a && c.GetRegister(Register.Ax) == 0xdeadbeef);
        });
        test("32-bit near CALL RET above 64KiB preserves full EIP and ESP", () =>
        {
            var (c, _) = Rig([0xe8,1,0,0,0,0xf4,0xb8,0x34,0x12,0xcd,0xab,0xc3], target: 0x12000);
            Run(c); Check(c.GetRegister(Register.Ax) == 0xabcd1234 && c.GetRegister(Register.Sp) == 0x18000 && c.Eip == 0x12006);
        });
        foreach (bool code32 in new[] { false, true })
        foreach (bool stack32 in new[] { false, true })
            test($"{(code32 ? 32 : 16)}-bit code uses independent {(stack32 ? 32 : 16)}-bit stack address size", () =>
            {
                var (c, m) = Rig([0x50,0x66,0x50,0x66,0x5b,0x5a,0xf4], code32, stack32);
                c.SetRegister(Register.Ax, 0x1234beef); uint before = c.GetRegister(Register.Sp); Run(c);
                Check(c.GetRegister(Register.Sp) == before);
                Check((c.GetRegister(Register.Bx) & (code32 ? 0xffffu : uint.MaxValue)) == (code32 ? 0xbeefu : 0x1234beefu));
                Check((c.GetRegister(Register.Dx) & (code32 ? uint.MaxValue : 0xffffu)) == (code32 ? 0x1234beefu : 0xbeefu));
            });
        test("32-bit SIB chooses SS base for ESP rather than DS", () =>
        {
            var (c, m) = Rig([0x8b,0x44,0x24,4]); Descriptor(m, 40, 0x20000, 0xfffff, 0x92, 12); c.SetSegment(Segment.Ss, 40);
            m.WriteDword(0x38004, 0xcafebabe); c.Step(); Check(c.GetRegister(Register.Ax) == 0xcafebabe);
        });
        test("stack limit fault leaves the failed PUSH absent and enters SS handler", () =>
        {
            var (c, m) = Rig([0x50]); Descriptor(m, 40, 0, 0x17ff0, 0x92, 4); c.SetSegment(Segment.Ss, 40);
            // PUSH faults; the same stack also cannot hold the #SS/#DF frames.
            c.Step(); Check(c.TripleFaulted && c.GetRegister(Register.Sp) == 0x18000 && m.ReadDword(0x17ffc) == 0);
        });
        test("protected far CALL to sixteen-bit code and 32-bit RETF restore the caller", () =>
        {
            var (c, m) = Rig([0x9a,0,0x30,0,0,24,0,0xf4]);
            new byte[] { 0xb8,0xef,0xbe,0x66,0xcb }.CopyTo(m.Ram,0x3000); Run(c);
            Check(c.GetSegment(Segment.Cs) == 8 && c.GetRegister(Register.Sp) == 0x18000 && (c.GetRegister(Register.Ax) & 0xffff) == 0xbeef);
        });
        test("far JMP checks code type and selector RPL before changing CS", () =>
        {
            foreach (ushort selector in new ushort[] { 16, 11 })
            {
                var (c, m) = Rig([0xea,0,0x30,0,0,(byte)selector,0]); Fault(c,m,13,(uint)selector & 0xfffc);
                Check(c.GetSegment(Segment.Cs) == 8);
            }
        });
        test("far JMP to absent code produces NP selector", () =>
        {
            var (c, m) = Rig([0xea,0,0x30,0,0,40,0]); Descriptor(m,40,0,0xffff,0x1a); Fault(c,m,11,40);
        });
        test("far JMP checks target offset against NEW code limit", () =>
        {
            var (c,m)=Rig([0xea,0,0x30,0,0,40,0]); Descriptor(m,40,0,0x2fff,0x9a); Fault(c,m,13);
            Check(m.ReadByte(0x42d)==0x9a);
        });
        test("LDS validates selector before replacing its general destination", () =>
        {
            var (c,m)=Rig([0xc5,0x05,0,3,0,0]); m.WriteDword(0x300,0xdeadbeef); m.WriteWord(0x304,40);
            Descriptor(m,40,0,0xffff,0x12); c.SetRegister(Register.Ax,0xaabbccdd); Fault(c,m,11,40);
            Check(c.GetRegister(Register.Ax)==0xaabbccdd && c.GetSegment(Segment.Ds)==16);
        });
        test("protected near RET checks target before consuming stack data", () =>
        {
            var (c,m)=Rig([0xc3]); Descriptor(m,40,0,0x2fff,0x9a,4);
            // Install a new limited code descriptor through an actual far jump.
            new byte[]{0xea,0,0x20,0,0,40,0}.CopyTo(m.Ram,0x2100); c.Eip=0x2100; c.Step();
            m.WriteDword(0x18000,0x3000); uint frame=Fault(c,m,13); Check(m.ReadDword(frame+16)==0x3000);
        });
        foreach (byte access in new byte[] { 0x8e, 0x8f })
            test($"32-bit {(access == 0x8e ? "interrupt" : "trap")} gate and IRETD preserve the proper flags", () =>
            {
                var (c,m)=Rig([0xcd,0x60,0xf4]); Gate(m,0x60,access:access); m.WriteByte(0x6000,0xcf); c.Eflags=0x602;
                c.Step(); Check(c.GetRegister(Register.Sp)==0x17ff4 && m.ReadDword(0x17ff4)==0x2002);
                Check((c.Eflags & X86Cpu.InterruptEnable)==(access==0x8e?0u:X86Cpu.InterruptEnable));
                c.Step(); Check(c.Eip==0x2002 && c.Eflags==0x602 && c.GetRegister(Register.Sp)==0x18000); Run(c);
            });
        test("sixteen-bit interrupt gate has a six-byte frame on a 32-bit stack", () =>
        {
            var (c,m)=Rig([0xcd,0x60,0xf4]); Gate(m,0x60,24,0x6000,0x86); m.WriteByte(0x6000,0xcf);
            c.Step(); Check(!c.GetSegmentCache(Segment.Cs).Default32 && c.GetRegister(Register.Sp)==0x17ffa && m.ReadWord(0x17ffa)==0x2002);
            c.Step(); Check(c.GetSegmentCache(Segment.Cs).Default32 && c.GetRegister(Register.Sp)==0x18000); Run(c);
        });
        test("prefixed invalid load saves the first prefix IP and selector error code", () =>
        {
            var(c,m)=Rig([0x66,0x66,0x8e,0xd8]); c.SetRegister(Register.Ax,0x100); Fault(c,m,13,0x100);
        });
        test("nonpresent software interrupt gate reports IDT index in NP frame", () =>
        {
            var(c,m)=Rig([0xcd,0x60]); Gate(m,0x60,access:0x0e); Fault(c,m,11,0x302);
        });
        test("fault handler removes GP error and IRETD retries the same instruction", () =>
        {
            var(c,m)=Rig([0x8e,0xd8,0xf4]); c.SetRegister(Register.Ax,40); Descriptor(m,40,0,0xffff,0x12);
            Gate(m,11,target:0x7000);
            // Make descriptor present, remove error, IRETD. Original EAX is retained.
            new byte[]{0xc6,0x05,0x2d,4,0,0,0x92,0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x7000);
            Run(c); Check(c.GetSegment(Segment.Ds)==40 && c.GetRegister(Register.Sp)==0x18000 && c.GetRegister(Register.Ax)==40);
        });
        test("contributory fault during NP delivery enters DF with zero error", () =>
        {
            var(c,m)=Rig([0x8e,0xd8]); Descriptor(m,40,0,0xffff,0x12); c.SetRegister(Register.Ax,40);
            Gate(m,11,access:0); Fault(c,m,8,0);
        });
        test("unusable DF delivery signals triple fault without fabricating an interrupt", () =>
        {
            var(c,m)=Rig([0x8e,0xd8]); c.SetRegister(Register.Ax,0x100); Gate(m,13,access:0); Gate(m,8,access:0);
            int entries=0;c.InterruptEntered+=_=>entries++; c.Step(); Check(c.TripleFaulted && c.Halted && entries==0 && c.GetRegister(Register.Sp)==0x18000);
        });
        test("PE clear preserves caches and a sixteen-bit far return reaches real mode", () =>
        {
            var(c,m)=Rig([0x0f,0x22,0xc0,0xea,0,0x30,0,0],code32:false,stack32:false);
            c.SetSegment(Segment.Ds,32); c.SetSegment(Segment.Es,32); c.SetRegister(Register.Ax,0x60000010);
            var ss=c.GetSegmentCache(Segment.Ss); c.Step(); Check(!c.ProtectedMode && c.GetSegmentCache(Segment.Ss)==ss);
            m.WriteByte(0x3000,0xf4); c.Step(); Check(c.GetSegment(Segment.Cs)==0 && c.Eip==0x3000 && !c.GetSegmentCache(Segment.Cs).Default32); Run(c);
        });
        test("PE clear retains a large data cache for controlled extended-memory access", () =>
        {
            var(c,m)=Rig([0x0f,0x22,0xc0,0x67,0x66,0xa1,0,0,1,0,0xf4],code32:false,stack32:false);
            c.SetRegister(Register.Ax,0x60000010);m.WriteDword(0x10000,0xcafebabe);Run(c);
            Check(!c.ProtectedMode && c.GetRegister(Register.Ax)==0xcafebabe && c.GetSegmentCache(Segment.Ds).Limit==uint.MaxValue);
        });
        test("hardware reset after PE restores every segment cache and mode", () =>
        {
            var(c,_)=Rig([0xf4]); c.Reset(); Check(!c.ProtectedMode && !c.TripleFaulted && c.CurrentPrivilegeLevel==0);
            foreach(Segment segment in Enum.GetValues<Segment>()) Check(c.GetSegmentCache(segment).Limit==0xffff && !c.GetSegmentCache(segment).Default32);
            Check(c.GetSegmentBase(Segment.Cs)==0xffff0000 && c.PhysicalInstructionAddress==0xfffffff0);
        });
        test("protected divide fault has no error slot and preserves the original dividend", () =>
        {
            var(c,m)=Rig([0xf7,0xf1]); c.SetRegister(Register.Ax,0x12345678); c.SetRegister(Register.Cx,0);
            Fault(c,m,0,null); Check(c.GetRegister(Register.Ax)==0x12345678);
        });
        test("protected WAIT NM has no error slot and guest CLTS IRETD retries", () =>
        {
            var(c,m)=Rig([0x0f,0x22,0xc0,0x9b,0xf4]); c.SetRegister(Register.Ax,0x6000001b);
            Gate(m,7,target:0x7000); new byte[]{0x0f,0x06,0xcf}.CopyTo(m.Ram,0x7000);
            Run(c); Check(c.Cr0==0x60000013 && c.GetRegister(Register.Sp)==0x18000);
        });
        test("runtime converts triple-fault shutdown into CPU reset while retaining RAM", () =>
        {
            var(_,source)=Rig([0xb8,0,1,0,0,0x8e,0xd8]);
            using var machine=PcMachine.CreateDemo();
            source.Ram.AsSpan(0x300,0x200).CopyTo(machine.Memory.Ram.AsSpan(0x300));
            source.Ram.AsSpan(0x1000,28).CopyTo(machine.Memory.Ram.AsSpan(0x1000));
            source.Ram.AsSpan(0x2000,7).CopyTo(machine.Memory.Ram.AsSpan(0x2000));
            machine.Cpu.SetSegment(Segment.Cs,0); machine.Cpu.Eip=0x1000; machine.Cpu.SetRegister(Register.Sp,0x9000);
            machine.Memory.WriteDword(0x6000,0xcafebabe);
            machine.RunSteps(7); Check(machine.Cpu.TripleFaulted && machine.HardwareResetCount==0);
            machine.RunSteps(1); Check(machine.HardwareResetCount==1 && machine.LastResetSource=="triple-fault");
            Check(!machine.Cpu.ProtectedMode && !machine.Cpu.TripleFaulted && machine.Memory.ReadDword(0x6000)==0xcafebabe);
        });
        test("protected hardware IRQ wakes HLT and uses the IDT without counting an instruction", () =>
        {
            var(c,m)=Rig([0xf4]); c.Eflags=0x202; c.Step(); long before=c.Instructions;
            c.EnterHardwareInterrupt(0x60);
            Check(!c.Halted && c.Eip==0x6000 && c.Instructions==before && c.GetRegister(Register.Sp)==0x17ff4);
            Check(m.ReadDword(0x17ff4)==0x2001 && (c.Eflags & X86Cpu.InterruptEnable)==0);
        });
        test("hardware IRQ selector fault carries EXT into the protected error code", () =>
        {
            var(c,m)=Rig([0xf4]); Gate(m,0x60,40); Descriptor(m,40,0,0xffff,0x1a);
            c.EnterHardwareInterrupt(0x60);
            Check(c.Eip==0x6000 && c.GetRegister(Register.Sp)==0x17ff0 && m.ReadDword(0x17ff0)==41);
            Check(m.ReadDword(0x17ff4)==0x2000);
        });
        test("IRETD restores RF for one successful retry and the retry then clears it", () =>
        {
            var(c,m)=Rig([0x8e,0xd8,0xf4]); c.SetRegister(Register.Ax,40); Descriptor(m,40,0,0xffff,0x12);
            Gate(m,11,target:0x7000); new byte[]{0xc6,0x05,0x2d,4,0,0,0x92,0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x7000);
            c.Step(); c.Step(); c.Step(); c.Step(); Check(c.Eip==0x2000 && (c.Eflags & 0x10000)!=0);
            c.Step(); Check(c.Eip==0x2002 && (c.Eflags & 0x10000)==0);
        });
        test("REP keeps RF until the complete instruction succeeds", () =>
        {
            var(c,_)=Rig([0xf3,0xaa,0xf4]); c.Eflags=0x10002; c.SetRegister(Register.Cx,2); c.SetRegister(Register.Di,0x300);
            c.Step(); Check(c.Eip==0x2000 && (c.Eflags & 0x10000)!=0);
            c.Step(); Check(c.Eip==0x2002 && (c.Eflags & 0x10000)==0);
        });
        test("PUSHFD saves zero RF without leaking the hidden resume state", () =>
        {
            var(c,m)=Rig([0x9c]); c.Eflags=0x10002; c.Step(); Check(m.ReadDword(0x17ffc)==2 && (c.Eflags & 0x10000)==0);
        });
    }
}
