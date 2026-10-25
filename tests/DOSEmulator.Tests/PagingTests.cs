using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class PagingTests
{
    private const uint Directory = 0x1c000, Table = 0x1d000;
    private static void Check(bool value, string message = "Paging invariant failed") { if (!value) throw new Exception(message); }
    internal static void Pte(MemoryBus m, uint linear, uint physical, uint flags = 7) => m.WriteDword(Table + ((linear >> 10) & 0xffc), (physical & 0xfffff000) | flags);
    internal static void At(X86Cpu c, MemoryBus m, byte[] code, uint address = 0x2200)
    { code.CopyTo(m.Ram, (int)address); c.Eip = address; c.Step(); }
    internal static (X86Cpu Cpu, MemoryBus Memory) Create(byte[] code, bool enable = true, bool wp = true, IoPortBus? io = null)
    {
        var (c, m) = ProtectedModeTests.Rig(code,io:io);
        m.WriteDword(Directory, Table | 7);
        for (uint i = 0; i < 1024; i++) m.WriteDword(Table + i * 4, i * 4096 | 7);
        if (enable) Enable(c, m, wp);
        return (c, m);
    }
    internal static void Enable(X86Cpu c, MemoryBus m, bool wp = true)
    {
        c.SetRegister(Register.Ax, Directory); At(c,m,[0x0f,0x22,0xd8]);
        c.SetRegister(Register.Ax, c.Cr0 | 0x80000000 | (wp ? 0x10000u : 0)); At(c,m,[0x0f,0x22,0xc0]); c.Eip=0x2000;
    }
    internal static void User(X86Cpu c, MemoryBus m, bool accessed = false)
    {
        Descriptor(m,0x28,0,0xfffff,(byte)(accessed?0xfb:0xfa),12); Descriptor(m,0x30,0,0xfffff,(byte)(accessed?0xf3:0xf2),12);
        m.WriteDword(0x18000,0x2000);m.WriteDword(0x18004,0x2b);m.WriteDword(0x18008,0x202);
        m.WriteDword(0x1800c,0x1b000);m.WriteDword(0x18010,0x33);
        At(c,m,[0xcf],0x2100);Check(c.CurrentPrivilegeLevel==3 && c.Eip==0x2000);
    }
    internal static void Tss(X86Cpu c, MemoryBus m)
    {
        Descriptor(m,0x40,0x9000,0x67,0x89);m.WriteDword(0x9004,0x1a000);m.WriteWord(0x9008,16);
        c.SetRegister(Register.Ax,0x40);At(c,m,[0x0f,0x00,0xd8]);
    }
    private sealed class Device : IMemoryDevice
    {
        public int Reads, Writes;
        public byte ReadByte(uint offset) { Reads++;return 0x5a; }
        public void WriteByte(uint offset,byte value) { Writes++; }
    }
    public static void AddTo(Action<string,Action> test)
    {
        test("EMS paging supplies extended RAM behind an undecoded ISA frame",()=>
        {
            var(c,m)=Create([0xa1,0,0,0x0d,0,0xa3,4,0,0x0d,0]);
            m.Map(0xc0000,0x30000,new OpenBusMemory(),"ISA hole");
            Pte(m,0xd0000,0x180000);m.WriteDword(0x180000,0x13579bdf);
            c.Step();c.Step();
            Check(c.GetRegister(Register.Ax)==0x13579bdf && m.ReadDword(0x180004)==0x13579bdf);
            Check(m.ReadDword(0xd0000)==0xffffffff && m.Ram[0xd0004]==0);
        });
        test("4-KiB paging translates a nonidentity page without translating physical busmasters",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0]);Pte(m,0x30000,0x40000);m.WriteDword(0x30000,0x11223344);m.WriteDword(0x40000,0xdeadbeef);
            c.Step();Check(c.GetRegister(Register.Ax)==0xdeadbeef && m.ReadDword(0x30000)==0x11223344 && c.PagingEnabled && c.PageWalks>0);
        });
        test("PDE index selects a second physical page table above the first 4 MiB",()=>
        {
            var(c,m)=Create([0xa1,0x34,0x12,0x40,0]);m.WriteDword(Directory+4,0x1e007);m.WriteDword(0x1e004,0x50007);m.WriteDword(0x50234,0xcafebabe);
            c.Step();Check(c.GetRegister(Register.Ax)==0xcafebabe);
        });
        test("page directory and table accessed bits and leaf dirty are updated in physical RAM",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0,0xa3,0,0,3,0]);Pte(m,0x30000,0x40000);m.WriteDword(0x40000,0x12345678);
            c.Step();Check((m.ReadDword(Directory)&0x60)==0x20 && (m.ReadDword(Table+0xc0)&0x60)==0x20);
            c.Step();Check((m.ReadDword(Table+0xc0)&0x60)==0x60);
        });
        test("cached read to write transition sets dirty without rewalking the mapping",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0,0xa3,0,0,3,0]);c.Step();long walks=c.PageWalks;c.Step();Check(c.PageWalks==walks && (m.ReadDword(Table+0xc0)&0x40)!=0);
        });
        foreach(bool directoryMissing in new[]{false,true})
        foreach(bool write in new[]{false,true})
            test($"missing {(directoryMissing?"PDE":"PTE")} {(write?"write":"read")} saves CR2 and exact 486 PF code",()=>
            {
                var(c,m)=Create([(byte)(write?0xa3:0xa1),0,0,0x40,0]);m.WriteDword(Directory+4,directoryMissing?0u:0x1e007u);m.WriteDword(0x1e000,0);
                c.SetRegister(Register.Ax,0xdeadbeef);Fault(c,m,14,write?2u:0u);Check(c.Cr2==0x400000 && c.PageFaults==1 && c.GetRegister(Register.Ax)==0xdeadbeef);
            });
        // Explicit permission table: PDE/PTE U/S and R/W are combined at both levels.
        foreach((uint dir,uint leaf,bool readAllowed,bool writeAllowed) in new (uint,uint,bool,bool)[]{(7,7,true,true),(7,5,true,false),(5,7,true,false),(5,5,true,false),(3,7,false,false),(7,3,false,false),(1,7,false,false),(7,1,false,false)})
        foreach(bool write in new[]{false,true})
            test($"ring-three page rights PDE {dir} PTE {leaf} {(write?"write":"read")}",()=>
            {
                var(c,m)=Create([(byte)(write?0xa3:0xa1),0,0,3,0]);User(c,m);c.SetSegment(Segment.Ds,0x33);Gate(m,14,0x2b);
                // Keep a valid user exception stack cached while changing the
                // directory rights of the otherwise untouched data page.
                c.Push(0,4);c.Pop(4);
                m.WriteDword(Directory,Table|dir);Pte(m,0x30000,0x40000,leaf);m.WriteDword(0x40000,0x12345678);c.SetRegister(Register.Ax,0xdeadbeef);
                if(write?writeAllowed:readAllowed){c.Step();Check(c.Eip==0x2005 && (write?m.ReadDword(0x40000)==0xdeadbeef:c.GetRegister(Register.Ax)==0x12345678));}
                else {Fault(c,m,14,write?7u:5u);Check(c.Cr2==0x30000 && m.ReadDword(0x40000)==0x12345678);}
            });
        foreach(uint leaf in new uint[]{1,3,5,7})
        foreach(bool wp in new[]{false,true})
            test($"supervisor write PTE {leaf} with CR0 WP {(wp?1:0)}",()=>
            {
                var(c,m)=Create([0xa3,0,0,3,0],wp:wp);Pte(m,0x30000,0x40000,leaf);c.SetRegister(Register.Ax,0xdeadbeef);
                if(wp && leaf is 1 or 5){Fault(c,m,14,3);Check(m.ReadDword(0x40000)==0);}
                else{c.Step();Check(m.ReadDword(0x40000)==0xdeadbeef);}
            });
        test("CR0 WP is checked on an existing TLB hit without requiring a flush",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0],wp:false);Pte(m,0x30000,0x40000,5);c.Step();
            c.SetRegister(Register.Ax,c.Cr0|0x10000);At(c,m,[0x0f,0x22,0xc0]);
            new byte[]{0xa3,0,0,3,0}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;Fault(c,m,14,3);
        });
        test("PTE changes remain cached until INVLPG and reload the new physical mapping afterward",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0]);Pte(m,0x30000,0x40000);m.WriteDword(0x40000,0x11111111);m.WriteDword(0x50000,0x22222222);
            c.Step();Pte(m,0x30000,0x50000);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==0x11111111);
            At(c,m,[0x0f,0x01,0x3d,0,0,3,0]);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==0x22222222);
        });
        test("MOV CR3 of the same value discards cached mappings",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0]);Pte(m,0x30000,0x40000);m.WriteDword(0x40000,1);m.WriteDword(0x50000,2);c.Step();Pte(m,0x30000,0x50000);
            c.SetRegister(Register.Ax,c.Cr3);At(c,m,[0x0f,0x22,0xd8]);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==2);
        });
        test("TLB dirty state is not reset by clearing PTE memory without invalidation",()=>
        {
            var(c,m)=Create([0xa3,0,0,3,0]);c.Step();uint pte=m.ReadDword(Table+0xc0);m.WriteDword(Table+0xc0,pte&~0x60u);
            c.Eip=0x2000;c.Step();Check((m.ReadDword(Table+0xc0)&0x60)==0);
            At(c,m,[0x0f,0x01,0x3d,0,0,3,0]);c.Eip=0x2000;c.Step();Check((m.ReadDword(Table+0xc0)&0x60)==0x60);
        });
        test("permission changes remain cached until INVLPG",()=>
        {
            var(c,m)=Create([0xa3,0,0,3,0]);c.SetRegister(Register.Ax,0xdeadbeef);c.Step();Pte(m,0x30000,0x30000,5);
            c.Eip=0x2000;c.Step();At(c,m,[0x0f,0x01,0x3d,0,0,3,0]);c.Eip=0x2000;Fault(c,m,14,3);
        });
        test("INVLPG targets only its page and never dereferences an absent target",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0,0xa1,0,0x10,3,0]);m.WriteDword(0x30000,1);m.WriteDword(0x31000,2);m.WriteDword(0x40000,3);m.WriteDword(0x41000,4);
            c.Step();c.Step();Pte(m,0x30000,0x40000);Pte(m,0x31000,0x41000);
            At(c,m,[0x0f,0x01,0x3d,0,0,3,0]);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==3);c.Step();Check(c.GetRegister(Register.Ax)==2);
            At(c,m,[0x0f,0x01,0x3d,0,0,0x40,0]);Check(c.Eip==0x2207 && c.PageFaults==0);
        });
        test("five colliding pages evict the least recently used of four TLB ways",()=>
        {
            var(c,m)=Create([0xa1,0,0,5,0]);
            for(uint i=0;i<5;i++)
            {
                uint address=0x50000+i*0x8000;m.WriteDword(address,i+1);m.WriteDword(0x2001,address);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==i+1);
            }
            Pte(m,0x50000,0x90000);m.WriteDword(0x90000,0xcafebabe);m.WriteDword(0x2001,0x50000);c.Eip=0x2000;c.Step();Check(c.GetRegister(Register.Ax)==0xcafebabe);
        });
        test("diagnostic instruction translation changes no accessed bits TLB counters or MMIO state",()=>
        {
            var(c,m)=Create([0xf4]);Pte(m,0x30000,0x40000);c.Eip=0x30000;long walks=c.PageWalks,hits=c.TlbHits;
            Check(c.MappedInstructionAddress==0x40000 && c.PhysicalInstructionAddress==0x40000 && c.PageWalks==walks && c.TlbHits==hits && (m.ReadDword(Table+0xc0)&0x60)==0);
            var device=new Device();m.Map(Directory,4096,device,"debug must not read MMIO tables");Check(c.MappedInstructionAddress is null && device.Reads==0);
        });
        test("cross-page dword reads join two noncontiguous physical frames",()=>
        {
            var(c,m)=Create([0xa1,0xfe,0x0f,3,0]);Pte(m,0x30000,0x40000);Pte(m,0x31000,0x50000);
            m.WriteWord(0x40ffe,0x5678);m.WriteWord(0x50000,0x1234);c.Step();Check(c.GetRegister(Register.Ax)==0x12345678);
        });
        test("cross-page dword writes join two noncontiguous physical frames",()=>
        {
            var(c,m)=Create([0xa3,0xfe,0x0f,3,0]);Pte(m,0x30000,0x40000);Pte(m,0x31000,0x50000);c.SetRegister(Register.Ax,0x12345678);
            c.Step();Check(m.ReadWord(0x40ffe)==0x5678 && m.ReadWord(0x50000)==0x1234);
        });
        foreach(bool write in new[]{false,true})
            test($"second-page PF leaves a complete failed dword {(write?"write":"read")} absent",()=>
            {
                var(c,m)=Create([(byte)(write?0xa3:0xa1),0xfe,0x0f,3,0]);Pte(m,0x31000,0,0);m.WriteWord(0x30ffe,0xbeef);c.SetRegister(Register.Ax,0xdeadbeef);
                Fault(c,m,14,write?2u:0u);Check(c.Cr2==0x31000 && m.ReadWord(0x30ffe)==0xbeef && c.GetRegister(Register.Ax)==0xdeadbeef);
            });
        test("cross-page store checks a read-only second page before writing the first",()=>
        {
            var(c,m)=Create([0xa3,0xff,0x0f,3,0]);Pte(m,0x31000,0x31000,5);m.WriteByte(0x30fff,0x55);c.SetRegister(Register.Ax,0x12345678);
            Fault(c,m,14,3);Check(c.Cr2==0x31000 && m.ReadByte(0x30fff)==0x55 && m.ReadDword(0x31000)==0);
        });
        test("a read with a missing second page does not consume first-page MMIO",()=>
        {
            var(c,m)=Create([0xa1,0xfe,0x0f,3,0]);var device=new Device();m.Map(0x40000,4096,device,"side effect read");Pte(m,0x30000,0x40000);Pte(m,0x31000,0,0);
            Fault(c,m,14,0);Check(device.Reads==0 && device.Writes==0);
        });
        foreach(byte[] code in new byte[][]{[0x83,0x05,0,0,3,0,1],[0xc1,0x25,0,0,3,0,1],[0xf7,0x15,0,0,3,0],[0x87,0x05,0,0,3,0]})
            test($"RMW {Convert.ToHexString(code)} checks write permission before a side-effect read",()=>
            {
                var(c,m)=Create(code);var device=new Device();m.Map(0x40000,4096,device,"readonly RMW");Pte(m,0x30000,0x40000,5);
                Fault(c,m,14,3);Check(device.Reads==0 && device.Writes==0 && c.Cr2==0x30000);
            });
        test("REP MOVSD validates destination before consuming source MMIO and retains restart registers",()=>
        {
            var(c,m)=Create([0xf3,0xa5]);var device=new Device();m.Map(0x40000,4096,device,"MOVS source");Pte(m,0x30000,0x40000);Pte(m,0x31000,0,0);
            c.SetRegister(Register.Si,0x30000);c.SetRegister(Register.Di,0x31000);c.SetRegister(Register.Cx,2);Fault(c,m,14,2);
            Check(device.Reads==0 && c.GetRegister(Register.Si)==0x30000 && c.GetRegister(Register.Di)==0x31000 && c.GetRegister(Register.Cx)==2);
        });
        test("REP MOVSD commits earlier iterations and restarts only the faulting iteration",()=>
        {
            var(c,m)=Create([0xf3,0xa5]);Pte(m,0x31000,0,0);m.WriteDword(0x30000,0x11111111);m.WriteDword(0x30004,0x22222222);
            c.SetRegister(Register.Si,0x30000);c.SetRegister(Register.Di,0x30ffc);c.SetRegister(Register.Cx,2);c.Step();Fault(c,m,14,2);
            Check(m.ReadDword(0x30ffc)==0x11111111 && c.GetRegister(Register.Si)==0x30004 && c.GetRegister(Register.Di)==0x31000 && c.GetRegister(Register.Cx)==1);
        });
        test("segment violation precedes an otherwise missing data page",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0]);Descriptor(m,0x50,0,0xffff,0x92);c.SetSegment(Segment.Ds,0x50);Pte(m,0x30000,0,0);Fault(c,m,13,0);
            Check(c.Cr2==0 && c.PageFaults==0);
        });
        test("prefixed fetch across a missing code page saves the first prefix IP and has no instruction-fetch error bit",()=>
        {
            var(c,m)=Create([0xf4]);new byte[]{0x66,0xb8,0x78}.CopyTo(m.Ram,0x2ffd);Pte(m,0x3000,0,0);c.Eip=0x2ffd;
            Fault(c,m,14,0,0x2ffd);Check(c.Cr2==0x3000);
        });
        test("instruction bytes crossing pages follow the second physical frame",()=>
        {
            var(c,m)=Create([0xf4]);m.WriteByte(0x2fff,0xb8);Pte(m,0x3000,0x7000);m.WriteDword(0x7000,0x12345678);c.Eip=0x2fff;
            uint seen=0;c.InstructionStarting+=(address,_)=>seen=address;c.Step();Check(c.Eip==0x3004 && c.GetRegister(Register.Ax)==0x12345678 && seen==0x2fff);
        });
        test("LGDT six-byte operand uses the linear mapping rather than the physical address",()=>
        {
            var(c,m)=Create([0x0f,0x01,0x15,0,0,3,0]);Pte(m,0x30000,0x40000);m.WriteWord(0x40000,0x1234);m.WriteDword(0x40002,0x50000);
            c.Step();Check(c.Gdtr==new DescriptorTableRegister(0x1234,0x50000));
        });
        test("SGDT cross-page store faults without a partial six-byte operand",()=>
        {
            var(c,m)=Create([0x0f,0x01,0x05,0xfc,0x0f,3,0]);Pte(m,0x31000,0,0);m.WriteDword(0x30ffc,0xdeadbeef);Fault(c,m,14,2);Check(m.ReadDword(0x30ffc)==0xdeadbeef);
        });
        test("LDS cross-page pointer faults before consuming first-page MMIO or replacing DS EAX",()=>
        {
            var(c,m)=Create([0xc5,0x05,0xfc,0x0f,3,0]);var device=new Device();m.Map(0x40000,4096,device,"far pointer");Pte(m,0x30000,0x40000);Pte(m,0x31000,0,0);
            c.SetRegister(Register.Ax,0xdeadbeef);Fault(c,m,14,0);Check(device.Reads==0 && c.GetSegment(Segment.Ds)==16 && c.GetRegister(Register.Ax)==0xdeadbeef);
        });
        test("ring-three interrupts read GDT IDT and TSS and write the inner stack as supervisor",()=>
        {
            var(c,m)=Create([0xcd,0x60],enable:false);Tss(c,m);Gate(m,0x60,access:0xee);Pte(m,0,0,3);Pte(m,0x8000,0x8000,3);Pte(m,0x9000,0x9000,3);Pte(m,0x19000,0x19000,3);
            Enable(c,m);User(c,m);c.Step();Check(c.CurrentPrivilegeLevel==0 && c.Eip==0x6000 && c.GetRegister(Register.Sp)==0x19fec && c.PageFaults==0);
        });
        test("descriptor accessed-bit PF is a supervisor write even when triggered at CPL3",()=>
        {
            var(other,ram)=Create([0x8e,0xd8],enable:false);Tss(other,ram);Descriptor(ram,0x50,0,0xfffff,0xf2,12);Pte(ram,0,0,1);Enable(other,ram);User(other,ram,true);
            other.SetRegister(Register.Ax,0x53);other.Step();Check(other.Cr2==0x455 && other.PageFaults==1 && other.CurrentPrivilegeLevel==0 && ram.ReadWord(0x19fe8)==3 && ram.ReadByte(0x455)==0xf2,
                $"CR2={other.Cr2:X} PF={other.PageFaults} CPL={other.CurrentPrivilegeLevel} ESP={other.GetRegister(Register.Sp):X} error={ram.ReadWord(0x19fe8):X} access={ram.ReadByte(0x455):X} IP={other.Eip:X}");
        });
        test("failed interrupt stack page leaves the original ring and caches intact before shutdown",()=>
        {
            var(c,m)=Create([0xcd,0x60],enable:false);Tss(c,m);Gate(m,0x60,access:0xee);Pte(m,0x19000,0,0);Enable(c,m);User(c,m);var cs=c.GetSegmentCache(Segment.Cs);var ss=c.GetSegmentCache(Segment.Ss);
            c.Step();Check(c.TripleFaulted && c.CurrentPrivilegeLevel==3 && c.GetSegmentCache(Segment.Cs)==cs && c.GetSegmentCache(Segment.Ss)==ss && c.GetRegister(Register.Sp)==0x1b000 && m.ReadDword(0x19ffc)==0);
        });
        test("clearing paging flushes cached mappings and retains protected mode",()=>
        {
            var(c,m)=Create([0xf4]);c.SetRegister(Register.Ax,c.Cr0&~0x80000000u);At(c,m,[0x0f,0x22,0xc0]);Check(!c.PagingEnabled && c.ProtectedMode);
        });
        test("reset clears paging translation cache counters and CR2 CR3",()=>
        {
            var(c,m)=Create([0xa1,0,0,3,0]);c.Step();c.Reset();Check(!c.PagingEnabled && c.PageFaults==0 && c.PageWalks==0 && c.TlbHits==0 && c.TlbInvalidations==0 && c.Cr2==0 && c.Cr3==0);
        });
    }
}
