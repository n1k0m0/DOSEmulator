// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class PrivilegeTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "Privilege invariant failed") { if (!condition) throw new Exception(message); }
    /// <summary>Creates this regression's isolated CPU/device fixture with explicit parameters and private data.</summary>
    private static (X86Cpu Cpu, MemoryBus Memory) Create(byte[] code)
    {
        var (c, m) = ProtectedModeTests.Rig(code);
        Descriptor(m, 0x28, 0, 0xfffff, 0xfa, 12);
        Descriptor(m, 0x30, 0, 0xfffff, 0xf2, 12);
        Descriptor(m, 0x40, 0x9000, 0x67, 0x89);
        Descriptor(m, 0x48, 0xa000, 0x17, 0x82);
        m.WriteDword(0x9004, 0x1a000); m.WriteWord(0x9008, 16);
        return (c, m);
    }
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void ExecuteAt(X86Cpu c, MemoryBus m, byte[] code, uint address = 0x2200)
    {
        code.CopyTo(m.Ram, (int)address); c.Eip = address; c.Step();
    }
    /// <summary>Executes a real LTR with the controlled TSS selector.</summary>
    private static void LoadTr(X86Cpu c, MemoryBus m, ushort selector = 0x40)
    {
        c.SetRegister(Register.Ax, selector); ExecuteAt(c, m, [0x0f, 0x00, 0xd8]);
        Check(c.Tr == selector && c.TrCache.Usable);
    }
    /// <summary>Builds and executes the actual return frame used to enter the requested user privilege.</summary>
    private static void EnterUser(X86Cpu c, MemoryBus m, uint flags = 0x202, uint pointer = 0x1b000)
    {
        uint sp = c.GetRegister(Register.Sp);
        m.WriteDword(sp, 0x2000); m.WriteDword(sp + 4, 0x2b); m.WriteDword(sp + 8, flags);
        m.WriteDword(sp + 12, pointer); m.WriteDword(sp + 16, 0x33);
        ExecuteAt(c, m, [0xcf], 0x2100);
        Check(c.CurrentPrivilegeLevel == 3 && c.GetSegment(Segment.Cs) == 0x2b && c.GetSegment(Segment.Ss) == 0x33 && c.Eip == 0x2000);
    }
    /// <summary>Installs the fixture's local handler gate for the expected architectural fault.</summary>
    private static void LocalFaultGate(MemoryBus m, byte vector) => Gate(m, vector, 0x2b);
    /// <summary>Writes a controlled LDT data descriptor with the requested base/access fields.</summary>
    private static void LdtData(MemoryBus m, uint address, uint basis, byte access = 0xf2)
    {
        m.WriteDword(address, 0xffff | basis << 16);
        m.WriteDword(address + 4, (basis >> 16 & 255) | ((uint)access << 8) | (basis & 0xff000000));
    }

    /// <summary>Registers the named privilege regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("LLDT caches a GDT system descriptor without setting an accessed bit", () =>
        {
            var (c, m) = Create([0x0f,0x00,0xd0]); c.SetRegister(Register.Ax, 0x4b); c.Eflags = 0x8d7;
            c.Step(); Check(c.Ldtr == 0x4b && c.LdtrCache.Base == 0xa000 && c.LdtrCache.Limit == 0x17 && c.LdtrCache.Access == 0x82);
            Check(m.ReadByte(0x44d) == 0x82 && c.Eflags == 0x8d7);
        });
        test("LDT selector index zero is usable and LLDT cache survives a GDT edit", () =>
        {
            var (c, m) = Create([0x0f,0x00,0xd0]); c.SetRegister(Register.Ax, 0x48); c.Step();
            LdtData(m, 0xa000, 0x12000); LdtData(m, 0xb000, 0x13000);
            Descriptor(m, 0x48, 0xb000, 7, 0x82);
            c.SetSegment(Segment.Fs, 7); Check(c.GetSegmentBase(Segment.Fs) == 0x12000 && m.ReadByte(0xa005) == 0xf3);
            ExecuteAt(c, m, [0x0f,0x00,0xd0]); c.SetSegment(Segment.Fs, 7);
            Check(c.GetSegmentBase(Segment.Fs) == 0x13000 && c.LdtrCache.Limit == 7);
        });
        foreach (ushort nullSelector in new ushort[] { 0, 1, 2, 3 })
            test($"LLDT null {nullSelector} invalidates LDTR while preserving loaded data caches", () =>
            {
                var (c, m) = Create([0x0f,0x00,0xd0]); c.SetRegister(Register.Ax, 0x48); c.Step();
                LdtData(m, 0xa000, 0x12300); c.SetSegment(Segment.Fs, 7);
                c.SetRegister(Register.Ax, nullSelector); ExecuteAt(c, m, [0x0f,0x00,0xd0]);
                Check(c.Ldtr == nullSelector && !c.LdtrCache.Usable && c.GetSegmentBase(Segment.Fs) == 0x12300);
                c.SetRegister(Register.Ax, 4); new byte[]{0x8e,0xd8}.CopyTo(m.Ram, 0x2000); c.Eip = 0x2000; Fault(c,m,13,4);
            });
        foreach (ushort selector in new ushort[] { 4, 0x4c, 0x100 })
            test($"LLDT rejects local or out-of-table selector {selector:X4}", () =>
            {
                var (c,m)=Create([0x0f,0x00,0xd0]); c.SetRegister(Register.Ax,selector); Fault(c,m,13,(uint)selector & 0xfffc);
                Check(!c.LdtrCache.Usable);
            });
        foreach (byte access in new byte[] { 0x89, 0x92, 0x83, 0x80 })
            test($"LLDT rejects descriptor type {access:X2} before testing present", () =>
            {
                var(c,m)=Create([0x0f,0x00,0xd0]); Descriptor(m,0x48,0xa000,7,access);c.SetRegister(Register.Ax,0x48);Fault(c,m,13,0x48);
            });
        test("absent LDT produces NP and preserves the previous hidden LDTR", () =>
        {
            var(c,m)=Create([0x0f,0x00,0xd0]);c.SetRegister(Register.Ax,0x48);c.Step();var old=c.LdtrCache;
            Descriptor(m,0x50,0xb000,7,0x02);c.SetRegister(Register.Ax,0x50);c.Eip=0x2000;Fault(c,m,11,0x50);
            Check(c.Ldtr==0x48 && c.LdtrCache==old);
        });
        foreach(byte access in new byte[]{0x81,0x89})
            test($"LTR loads available {(access==0x81?16:32)} bit TSS and sets only its busy bit",()=>
            {
                var(c,m)=Create([0x0f,0x00,0xd8]);Descriptor(m,0x40,0x9000,9,access);c.SetRegister(Register.Ax,0xabcd0043);uint cr0=c.Cr0;
                c.Step();Check(c.Tr==0x43 && c.TrCache.Base==0x9000 && c.TrCache.Limit==9 && c.TrCache.Access==(access|2));
                Check(m.ReadByte(0x445)==(access|2) && c.Cr0==cr0 && c.GetRegister(Register.Ax)==0xabcd0043 && c.CurrentPrivilegeLevel==0);
            });
        foreach(ushort selector in new ushort[]{0,3,4,0x44,0x100})
            test($"LTR rejects invalid TSS selector {selector:X4}",()=>
            {
                var(c,m)=Create([0x0f,0x00,0xd8]);c.SetRegister(Register.Ax,selector);Fault(c,m,13,(uint)selector&0xfffc);Check(!c.TrCache.Usable);
            });
        foreach(byte access in new byte[]{0x82,0x83,0x8b,0x92,0x88})
            test($"LTR rejects busy or non-TSS type {access:X2}",()=>
            {
                var(c,m)=Create([0x0f,0x00,0xd8]);Descriptor(m,0x40,0x9000,0x67,access);c.SetRegister(Register.Ax,0x40);Fault(c,m,13,0x40);
                Check(!c.TrCache.Usable && m.ReadByte(0x445)==access);
            });
        test("absent available TSS faults before busy or TR mutation",()=>
        {
            var(c,m)=Create([0x0f,0x00,0xd8]);Descriptor(m,0x40,0x9000,0x67,0x09);c.SetRegister(Register.Ax,0x40);Fault(c,m,11,0x40);
            Check(!c.TrCache.Usable && m.ReadByte(0x445)==9);
        });
        test("LTR reload of a busy descriptor faults and keeps the old cached TSS",()=>
        {
            var(c,m)=Create([0x0f,0x00,0xd8]);LoadTr(c,m);var old=c.TrCache;c.Eip=0x2000;Fault(c,m,13,0x40);Check(c.TrCache==old);
        });
        test("loading a second TR does not clear the previous descriptor busy bit",()=>
        {
            var(c,m)=Create([0xf4]);LoadTr(c,m);Descriptor(m,0x50,0xb000,0x67,0x89);LoadTr(c,m,0x50);
            Check(m.ReadByte(0x445)==0x8b && m.ReadByte(0x455)==0x8b && c.TrCache.Base==0xb000);
        });
        test("SLDT STR preserve register upper halves with either operand size",()=>
        {
            var(c,m)=Create([0xf4]);LoadTr(c,m);c.SetRegister(Register.Ax,0x48);ExecuteAt(c,m,[0x0f,0x00,0xd0]);
            foreach(byte[] instruction in new byte[][]{[0x0f,0x00,0xc0],[0x66,0x0f,0x00,0xc0],[0x0f,0x00,0xc8],[0x66,0x0f,0x00,0xc8]})
            {
                c.SetRegister(Register.Ax,0xaabbffff);ExecuteAt(c,m,instruction);
                Check(c.GetRegister(Register.Ax)==(instruction[^1]==0xc0?0xaabb0048u:0xaabb0040u));
            }
        });
        test("SLDT STR memory forms write exactly two bytes at the segment end",()=>
        {
            var(c,m)=Create([0xf4]);LoadTr(c,m);Descriptor(m,0x50,0x10000,1,0x92);c.SetSegment(Segment.Ds,0x50);m.WriteDword(0x10000,0xfeedffff);
            ExecuteAt(c,m,[0x0f,0x00,0x0d,0,0,0,0]);Check(m.ReadDword(0x10000)==0xfeed0040);
            ExecuteAt(c,m,[0x0f,0x00,0x05,0,0,0,0]);Check(m.ReadDword(0x10000)==0xfeed0000);
        });
        test("LTR memory source is a word independent of the operand prefix",()=>
        {
            var(c,m)=Create([0x0f,0x00,0x1d,0,3,0,0]);Descriptor(m,0x50,0x300,1,0x92);c.SetSegment(Segment.Ds,0x50);m.WriteWord(0x300,0x40);
            new byte[]{0x0f,0x00,0x1d,0,0,0,0}.CopyTo(m.Ram,0x2000);c.Step();Check(c.Tr==0x40);
        });
        test("system selector instructions are UD in real mode without touching their operand",()=>
        {
            foreach(byte modrm in new byte[]{0x00,0x08,0x10,0x18})
            {
                var m=new MemoryBus(1024*1024);var c=new X86Cpu(m,new());c.SetSegment(Segment.Cs,0);c.Eip=0x2000;c.SetRegister(Register.Sp,0x9000);
                m.WriteWord(24,0x6000);m.WriteWord(26,0);new byte[]{0x0f,0x00,modrm}.CopyTo(m.Ram,0x2000);
                c.Step();Check(c.Eip==0x6000 && m.ReadWord(0x8ffa)==0x2000 && !c.TrCache.Usable && !c.LdtrCache.Usable);
            }
        });
        test("CPU reset invalidates both system segment caches and transition counters",()=>
        {
            var(c,m)=Create([0xf4]);LoadTr(c,m);c.SetRegister(Register.Ax,0x48);ExecuteAt(c,m,[0x0f,0x00,0xd0]);EnterUser(c,m);
            c.Reset();Check(c.Tr==0 && c.Ldtr==0 && !c.TrCache.Usable && !c.LdtrCache.Usable && c.OuterPrivilegeReturns==0 && c.PrivilegeStackSwitches==0);
        });
        test("IRETD enters ring three, restores IF at old CPL and invalidates privileged data caches",()=>
        {
            var(c,m)=Create([0xf4]);c.SetSegment(Segment.Es,0x33);Descriptor(m,0x58,0,0xfffff,0x9e,12);c.SetSegment(Segment.Gs,0x5b);c.Eflags=2;
            EnterUser(c,m);Check(c.GetRegister(Register.Sp)==0x1b000 && c.Eflags==0x202 && c.OuterPrivilegeReturns==1);
            Check(c.GetSegment(Segment.Ds)==0 && !c.GetSegmentCache(Segment.Ds).Usable && c.GetSegment(Segment.Es)==0x33 && c.GetSegment(Segment.Gs)==0x5b);
        });
        test("32-bit ring interrupt saves the five-slot frame on the cached TSS stack and IRETD returns",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);var old=c.TrCache;Descriptor(m,0x40,0xb000,0,0x09);
            Gate(m,0x60,access:0xee);m.WriteByte(0x6000,0xcf);EnterUser(c,m);c.Step();
            Check(c.CurrentPrivilegeLevel==0 && c.GetSegment(Segment.Ss)==16 && c.GetRegister(Register.Sp)==0x19fec && c.Eip==0x6000);
            Check(new[]{m.ReadDword(0x19fec),m.ReadDword(0x19ff0),m.ReadDword(0x19ff4),m.ReadDword(0x19ff8),m.ReadDword(0x19ffc)}.SequenceEqual(new uint[]{0x2002,0x2b,0x202,0x1b000,0x33}));
            Check(c.TrCache==old && c.PrivilegeStackSwitches==1 && (c.Eflags&0x200)==0);
            c.Step();Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x2002 && c.GetRegister(Register.Sp)==0x1b000 && c.Eflags==0x202 && c.OuterPrivilegeReturns==2);
        });
        foreach(int privilege in new[]{1,2})
            test($"ring three interrupt selects ESP{privilege}/SS{privilege} from the TSS",()=>
            {
                var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);
                Descriptor(m,0x58,0,0xfffff,(byte)(0x9a|privilege<<5),12);Descriptor(m,0x60,0,0xfffff,(byte)(0x92|privilege<<5),12);
                m.WriteDword(0x9004+(uint)privilege*8,0x1c000);m.WriteWord(0x9008+(uint)privilege*8,(ushort)(0x60|privilege));
                Gate(m,0x60,0x5b,access:0xee);m.WriteByte(0x6000,0xcf);EnterUser(c,m);c.Step();
                Check(c.CurrentPrivilegeLevel==privilege && c.GetSegment(Segment.Cs)==(0x58|privilege) && c.GetSegment(Segment.Ss)==(0x60|privilege) && c.GetRegister(Register.Sp)==0x1bfec);
                c.Step();Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==0x1b000);
            });
        test("a 16-bit TSS supplies SP0 SS0 to a 32-bit interrupt gate",()=>
        {
            var(c,m)=Create([0xcd,0x60]);Descriptor(m,0x40,0x9000,0x2b,0x81);m.WriteWord(0x9002,0xa000);m.WriteWord(0x9004,32);LoadTr(c,m);
            Gate(m,0x60,access:0xee);m.WriteByte(0x6000,0xcf);EnterUser(c,m);c.Step();
            Check(c.GetSegment(Segment.Ss)==32 && c.GetRegister(Register.Sp)==0x9fec && m.ReadDword(0x9ff8)==0x1b000);
            c.Step();Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==0x1b000);
        });
        test("16-bit interrupt gate uses ten bytes on an independently 32-bit TSS stack",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Gate(m,0x60,24,0x6000,0xe6);m.WriteByte(0x6000,0xcf);EnterUser(c,m);c.Step();
            Check(c.CurrentPrivilegeLevel==0 && c.GetRegister(Register.Sp)==0x19ff6 && !c.GetSegmentCache(Segment.Cs).Default32);
            Check(m.ReadWord(0x19ff6)==0x2002 && m.ReadWord(0x19ffe)==0x33 && m.ReadWord(0x19ffc)==0xb000);
            c.Step();Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==0x0033b000 && c.Eip==0x2002);
        });
        test("32-bit gate saves the entire old ESP even when the old SS has B zero",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Descriptor(m,0x30,0,0xffff,0xf2);Gate(m,0x60,access:0xee);m.WriteByte(0x6000,0xcf);
            EnterUser(c,m,pointer:0xabcd7000);Check(c.GetRegister(Register.Sp)==0x17000);c.SetRegister(Register.Sp,0xabcd7000);
            c.Step();Check(m.ReadDword(0x19ff8)==0xabcd7000);c.Step();Check(c.GetRegister(Register.Sp)==0x17000);
        });
        test("TSS privilege stack may use the loaded local descriptor table",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);c.SetRegister(Register.Ax,0x48);ExecuteAt(c,m,[0x0f,0x00,0xd0]);
            LdtData(m,0xa008,0,0x92);m.WriteWord(0x9008,12);m.WriteDword(0x9004,0xf000);Gate(m,0x60,access:0xee);EnterUser(c,m);c.Step();
            Check(c.GetSegment(Segment.Ss)==12 && c.GetRegister(Register.Sp)==0xefec && m.ReadByte(0xa00d)==0x93);
        });
        test("same-ring gate never reads an unavailable TSS",()=>
        {
            var(c,m)=Create([0xcd,0x60]);Gate(m,0x60,0x2b,access:0xef);EnterUser(c,m);c.Step();
            Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==0x1aff4 && !c.TrCache.Usable && c.PrivilegeStackSwitches==0 && (c.Eflags&0x200)!=0);
        });
        test("conforming interrupt target retains ring three and its stack",()=>
        {
            var(c,m)=Create([0xcd,0x60]);Descriptor(m,0x58,0,0xfffff,0x9e,12);Gate(m,0x60,0x58,access:0xef);EnterUser(c,m);c.Step();
            Check(c.CurrentPrivilegeLevel==3 && c.GetSegment(Segment.Cs)==0x5b && c.GetSegment(Segment.Ss)==0x33 && c.PrivilegeStackSwitches==0);
        });
        test("nested kernel interrupts use the active stack instead of reading TSS ESP0 again",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Gate(m,0x60,access:0xee);Gate(m,0x61);new byte[]{0xcd,0x61,0xcf}.CopyTo(m.Ram,0x6000);
            Gate(m,0x61,target:0x6100);m.WriteByte(0x6100,0xcf);EnterUser(c,m);c.Step();m.WriteWord(0x9008,0);c.Step();
            Check(c.Eip==0x6100 && c.GetRegister(Register.Sp)==0x19fe0 && c.PrivilegeStackSwitches==1);c.Step();c.Step();
            Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Sp)==0x1b000);
        });
        test("a user GP enters the inner TSS stack with error RF and exact instruction start",()=>
        {
            var(c,m)=Create([0xfa]);LoadTr(c,m);EnterUser(c,m);c.Step();
            Check(c.CurrentPrivilegeLevel==0 && c.GetRegister(Register.Sp)==0x19fe8 && m.ReadDword(0x19fe8)==0 && m.ReadDword(0x19fec)==0x2000);
            Check(m.ReadDword(0x19ff4)==0x10202 && m.ReadDword(0x19ff8)==0x1b000 && c.PrivilegeStackSwitches==1);
        });
        foreach(byte[] instruction in new byte[][]{[0x0f,0x00,0x15,0,0,0,0],[0x0f,0x00,0x1d,0,0,0,0],[0x0f,0x22,0xc0],[0xf4],[0xfb]})
            test($"ring three privileged instruction {Convert.ToHexString(instruction)} faults before an operand or state change",()=>
            {
                var(c,m)=Create(instruction);EnterUser(c,m);LocalFaultGate(m,13);c.SetRegister(Register.Ax,0xdeadbeef);
                Fault(c,m,13);Check(c.CurrentPrivilegeLevel==3 && c.GetRegister(Register.Ax)==0xdeadbeef && !c.TrCache.Usable && !c.LdtrCache.Usable);
            });
        test("SLDT STR remain unprivileged at ring three",()=>
        {
            var(c,m)=Create([0x0f,0x00,0xc0,0x0f,0x00,0xc8]);LoadTr(c,m);EnterUser(c,m);c.SetRegister(Register.Ax,0xaabbffff);c.Step();Check(c.GetRegister(Register.Ax)==0xaabb0000);
            c.Step();Check(c.GetRegister(Register.Ax)==0xaabb0040 && c.CurrentPrivilegeLevel==3);
        });
        test("software gate DPL faults but a hardware interrupt ignores gate DPL",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Gate(m,0x60);LocalFaultGate(m,13);EnterUser(c,m);Fault(c,m,13,0x302);
            var(other,ram)=Create([0xf4]);LoadTr(other,ram);Gate(ram,0x60);EnterUser(other,ram);other.EnterHardwareInterrupt(0x60);
            Check(other.CurrentPrivilegeLevel==0 && ram.ReadDword(0x19fec)==0x2000 && other.PrivilegeStackSwitches==1);
        });
        foreach((ushort selector,byte access,byte vector) in new (ushort,byte,byte)[]{(0,0x92,10),(19,0x92,10),(0x100,0x92,10),(0x50,0x9a,10),(0x50,0x90,10),(0x50,0xf2,10),(0x50,0x12,12)})
            test($"TSS stack selector {selector:X4} access {access:X2} faults atomically with vector {vector}",()=>
            {
                var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Gate(m,0x60,access:0xee);LocalFaultGate(m,vector);
                Descriptor(m,0x50,0,0xfffff,access,12);m.WriteWord(0x9008,selector);m.WriteDword(0x19ffc,0xcafebabe);EnterUser(c,m);
                Fault(c,m,vector,(uint)selector&0xfffc);Check(c.GetSegment(Segment.Ss)==0x33 && c.PrivilegeStackSwitches==0 && m.ReadDword(0x19ffc)==0xcafebabe);
            });
        foreach(uint limit in new uint[]{0,3,8})
            test($"cached TSS limit {limit} rejects an incomplete ESP0 SS0 pair with TS TR",()=>
            {
                var(c,m)=Create([0xcd,0x60]);Descriptor(m,0x40,0x9000,limit,0x89);LoadTr(c,m);Gate(m,0x60,access:0xee);LocalFaultGate(m,10);EnterUser(c,m);
                Fault(c,m,10,0x40);Check(c.GetSegment(Segment.Ss)==0x33 && c.PrivilegeStackSwitches==0);
            });
        test("TSS stack reads accept the exact last selector byte and observe later TSS memory edits",()=>
        {
            var(c,m)=Create([0xcd,0x60]);Descriptor(m,0x40,0x9000,9,0x89);LoadTr(c,m);m.WriteDword(0x9004,0x1c000);Gate(m,0x60,access:0xee);EnterUser(c,m);c.Step();
            Check(c.GetRegister(Register.Sp)==0x1bfec && c.PrivilegeStackSwitches==1);
        });
        test("inner interrupt without TR reports TS zero instead of inventing a stack",()=>
        {
            var(c,m)=Create([0xcd,0x60]);Gate(m,0x60,access:0xee);LocalFaultGate(m,10);EnterUser(c,m);Fault(c,m,10,0);Check(c.PrivilegeStackSwitches==0);
        });
        test("hardware TSS stack failure marks EXT in the selector error",()=>
        {
            var(c,m)=Create([0xf4]);LoadTr(c,m);m.WriteWord(0x9008,19);Gate(m,0x60);LocalFaultGate(m,10);EnterUser(c,m);c.EnterHardwareInterrupt(0x60);
            Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x6000 && m.ReadDword(0x1aff0)==17 && c.PrivilegeStackSwitches==0);
        });
        test("new stack bounds fault precedes invalid interrupt target offset and leaves no partial frame",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);Descriptor(m,0x50,0,0x19ff0,0x92,4);m.WriteWord(0x9008,0x50);Descriptor(m,0x58,0,0x2fff,0x9a,4);
            Gate(m,0x60,0x58,0x6000,0xee);LocalFaultGate(m,12);EnterUser(c,m);m.WriteDword(0x19fec,0xcafebabe);Fault(c,m,12);
            Check(m.ReadDword(0x19fec)==0xcafebabe && c.GetSegment(Segment.Ss)==0x33 && c.PrivilegeStackSwitches==0);
        });
        test("bad TSS selector precedes an out-of-limit interrupt target",()=>
        {
            var(c,m)=Create([0xcd,0x60]);LoadTr(c,m);m.WriteWord(0x9008,0);Descriptor(m,0x58,0,0x2fff,0x9a,4);Gate(m,0x60,0x58,0x6000,0xee);
            LocalFaultGate(m,10);EnterUser(c,m);Fault(c,m,10);
        });
        foreach((ushort selector,byte access,byte vector) in new (ushort,byte,byte)[]{(0,0xf2,13),(0x30,0xf2,13),(0x53,0x92,13),(0x53,0xfa,13),(0x53,0xf0,13),(0x53,0x72,12)})
            test($"outer IRETD validates return SS {selector:X4} access {access:X2} before changing CPL",()=>
            {
                var(c,m)=Create([0xcf]);Descriptor(m,0x50,0,0xfffff,access,12);uint sp=c.GetRegister(Register.Sp);
                m.WriteDword(sp,0x3000);m.WriteDword(sp+4,0x2b);m.WriteDword(sp+8,0x3202);m.WriteDword(sp+12,0x1b000);m.WriteDword(sp+16,selector);
                Fault(c,m,vector,(uint)selector&0xfffc);Check(c.CurrentPrivilegeLevel==0 && c.GetSegment(Segment.Ss)==16 && c.OuterPrivilegeReturns==0 && (c.Eflags&0x3000)==0);
            });
        test("outer RETF immediate skips copied parameters on both stacks",()=>
        {
            var(c,m)=Create([0xca,8,0]);uint sp=c.GetRegister(Register.Sp);m.WriteDword(sp,0x3000);m.WriteDword(sp+4,0x2b);
            m.WriteDword(sp+8,0xaaaaaaaa);m.WriteDword(sp+12,0xbbbbbbbb);m.WriteDword(sp+16,0x1b000);m.WriteDword(sp+20,0x33);
            c.Step();Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x3000 && c.GetRegister(Register.Sp)==0x1b008 && c.OuterPrivilegeReturns==1);
        });
        test("486 16-bit outer RETF into a 32-bit stack puts SS into the high ESP word",()=>
        {
            var(c,m)=Create([0x66,0xcb]);uint sp=c.GetRegister(Register.Sp);m.WriteWord(sp,0x3000);m.WriteWord(sp+2,0x2b);m.WriteWord(sp+4,0x7000);m.WriteWord(sp+6,0x33);
            c.Step();Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x3000 && c.GetRegister(Register.Sp)==0x00337000);
        });
        test("486 protected fault reserves four bytes but preserves the unwritten high error word",()=>
        {
            var(c,m)=Create([0x8e,0xd8]);c.SetRegister(Register.Ax,0x100);m.WriteDword(0x17ff0,0xbeefffff);uint frame=Fault(c,m,13,0x100);
            Check(m.ReadDword(frame)==0xbeef0100 && c.GetRegister(Register.Sp)==0x17ff0 && m.ReadDword(frame+4)==0x2000);
        });
        test("outer IRETD may restore a pointer whose subsequent memory access faults",()=>
        {
            var(c,m)=Create([0x50]);Descriptor(m,0x30,0,0x100,0xf2,4);EnterUser(c,m,pointer:0x200);LocalFaultGate(m,12);
            // The pointer itself is not dereferenced by IRET. Its use cannot fit a
            // fault frame either, so the checked result is shutdown, with no write.
            c.Step();Check(c.TripleFaulted && c.GetRegister(Register.Sp)==0x200 && m.ReadDword(0x1fc)==0);
        });
        test("ring three POPFD cannot raise IOPL or change IF beyond its old IOPL",()=>
        {
            var(c,m)=Create([0x9d]);EnterUser(c,m);m.WriteDword(0x1b000,0x3003);c.Step();Check((c.Eflags&0x3201)==0x201 && c.CurrentPrivilegeLevel==3);
        });
        test("outer return keeps the cached eligible data descriptor despite later GDT changes",()=>
        {
            var(c,m)=Create([0xf4]);c.SetSegment(Segment.Es,0x33);var old=c.GetSegmentCache(Segment.Es);Descriptor(m,0x30,0,0xfffff,0x92,12);
            Descriptor(m,0x50,0,0xfffff,0xf2,12);uint sp=c.GetRegister(Register.Sp);m.WriteDword(sp,0x2000);m.WriteDword(sp+4,0x2b);m.WriteDword(sp+8,2);m.WriteDword(sp+12,0x1b000);m.WriteDword(sp+16,0x53);
            ExecuteAt(c,m,[0xcf]);Check(c.GetSegmentCache(Segment.Es)==old && c.GetSegment(Segment.Es)==0x33);
        });
    }
}
