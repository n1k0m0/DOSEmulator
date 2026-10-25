using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class Virtual8086Tests
{
    private static void Check(bool value,string message="V86 invariant failed") { if(!value)throw new Exception(message); }
    private static (X86Cpu Cpu,MemoryBus Memory,Ports Io) Create(byte[] code,uint flags=0x20202,bool paging=true,uint tssLimit=0x2068,uint pointer=0xabcd8000)
    {
        var io=new IoPortBus();var ports=new Ports();io.Map(0x20,8,ports);
        var(c,m)=PagingTests.Create([0xf4],enable:false,io:io);
        Descriptor(m,0x40,0x9000,tssLimit,0x89);m.WriteDword(0x9004,0x1a000);m.WriteWord(0x9008,16);m.WriteWord(0x9066,0x68);
        m.Ram.AsSpan(0x9068,8193).Fill(0xff);c.SetRegister(Register.Ax,0x40);PagingTests.At(c,m,[0x0f,0x00,0xd8]);
        if(paging)PagingTests.Enable(c,m);
        uint[] frame=[0x100,0x3000,flags,pointer,0x4000,0x6000,0x5000,0x7000,0x8000];
        for(uint i=0;i<9;i++)m.WriteDword(0x18000+i*4,frame[i]);
        code.CopyTo(m.Ram,0x30100);PagingTests.At(c,m,[0xcf],0x2100);
        Check(c.Virtual8086 && c.CurrentPrivilegeLevel==3 && c.Eip==0x100 && c.GetRegister(Register.Sp)==pointer,"IRETD did not enter V86");
        return(c,m,ports);
    }
    private sealed class Ports:IIoPortDevice
    {
        public int Reads,Writes,LastWidth;public uint LastValue;
        public uint Read(ushort port,int width){Reads++;LastWidth=width;return 0x78563412;}
        public void Write(ushort port,uint value,int width){Writes++;LastWidth=width;LastValue=value;}
    }
    private static uint Monitor(X86Cpu c,MemoryBus m,byte vector,uint? error=0,uint ip=0x100)
    {
        byte? seen=null;c.InterruptEntered+=v=>seen=v;c.Step();uint sp=error.HasValue?0x19fd8u:0x19fdc;
        Check(seen==vector && !c.Virtual8086 && c.CurrentPrivilegeLevel==0 && c.Eip==0x6000 && c.GetRegister(Register.Sp)==sp,
            $"Expected monitor {vector}, got {seen}, {c.Position}, ESP={c.GetRegister(Register.Sp):X}, triple={c.TripleFaulted}");
        if(error is{} expected){Check(m.ReadWord(sp)==expected,"Monitor error code mismatch");sp+=4;}
        Check(m.ReadDword(sp)==ip && m.ReadDword(sp+4)==0x3000 && (m.ReadDword(sp+8)&0x20000)!=0);
        Check(m.ReadDword(sp+12)==0xabcd8000 && m.ReadDword(sp+16)==0x4000);
        Check(m.ReadDword(sp+20)==0x6000 && m.ReadDword(sp+24)==0x5000 && m.ReadDword(sp+28)==0x7000 && m.ReadDword(sp+32)==0x8000);
        foreach(Segment s in new[]{Segment.Es,Segment.Ds,Segment.Fs,Segment.Gs})Check(c.GetSegment(s)==0 && !c.GetSegmentCache(s).Usable);
        return error.HasValue?sp-4:sp;
    }
    public static void AddTo(Action<string,Action> test)
    {
        test("V86 CS override writes ordinary virtual memory without protected code-segment rights",()=>
        {
            var(c,m,_)=Create([0x2e,0xc7,0x06,0x20,0,0x34,0x12]);c.Step();
            Check(c.Virtual8086 && c.Eip==0x107 && m.ReadWord(0x30020)==0x1234 && c.Virtual8086Exits==0);
        });
        test("IRETD enters V86 with arbitrary real selectors all six fresh caches and full ESP",()=>
        {
            var(c,_,_)=Create([0x90]);Check(c.GetSegment(Segment.Cs)==0x3000 && c.GetSegmentBase(Segment.Cs)==0x30000 && c.Virtual8086Entries==1);
            foreach(Segment s in Enum.GetValues<Segment>())Check(c.GetSegmentCache(s).Limit==0xffff && !c.GetSegmentCache(s).Default32 && c.GetSegmentCache(s).Usable);
        });
        foreach(bool paging in new[]{false,true})
            test($"V86 has 16-bit default decoding with paging {(paging?"on":"off")}",()=>
            {
                var(c,_,_)=Create([0xb8,0x34,0x12,0x66,0xbb,0x78,0x56,0x34,0x12],paging:paging);c.SetRegister(Register.Ax,0xdead0000);c.Step();c.Step();
                Check(c.GetRegister(Register.Ax)==0xdead1234 && c.GetRegister(Register.Bx)==0x12345678 && c.Virtual8086Instructions==2);
            });
        test("V86 moffs and address prefixes use real segment bases with paging",()=>
        {
            var(c,m,_)=Create([0xa1,0x20,0,0x67,0x8b,0x1d,0x80,0,0,0]);m.WriteWord(0x50020,0xbeef);m.WriteWord(0x50080,0xcafe);c.Step();c.Step();
            Check((c.GetRegister(Register.Ax)&0xffff)==0xbeef && (c.GetRegister(Register.Bx)&0xffff)==0xcafe);
        });
        test("V86 segment loads accept zero and discard inherited protected limits",()=>
        {
            var(c,_,_)=Create([0x31,0xc0,0x8e,0xd8]);c.Step();c.Step();Check(c.GetSegment(Segment.Ds)==0 && c.GetSegmentCache(Segment.Ds).Usable && c.GetSegmentBase(Segment.Ds)==0 && c.GetSegmentCache(Segment.Ds).Limit==0xffff);
        });
        test("V86 stack operations use SP and preserve the unused high ESP half",()=>
        {
            var(c,m,_)=Create([0x50,0x5b]);c.SetRegister(Register.Ax,0xdeadbeef);c.Step();Check(c.GetRegister(Register.Sp)==0xabcd7ffe && m.ReadWord(0x47ffe)==0xbeef);
            c.Step();Check(c.GetRegister(Register.Sp)==0xabcd8000 && (c.GetRegister(Register.Bx)&0xffff)==0xbeef);
        });
        foreach(byte[] code in new byte[][]{[0xfa],[0xfb],[0x9c],[0x9d],[0xcf],[0xcd,0x60],[0x66,0x9c],[0x66,0x9d],[0x66,0xcf]})
            test($"V86 IOPL-zero sensitive instruction {Convert.ToHexString(code)} enters a ten-slot GP monitor frame",()=>
            {
                var(c,m,_)=Create(code);Monitor(c,m,13);Check((m.ReadDword(0x19fe4)&0x30000)==0x30000 && c.Virtual8086Exits==1);
            });
        test("V86 sensitive fault occurs before dereferencing an invalid user stack",()=>
        {
            var(c,m,_)=Create([0x9d],pointer:1);c.Step();Check(!c.Virtual8086 && c.Eip==0x6000 && m.ReadWord(0x19fd8)==0 && m.ReadDword(0x19fe8)==1);
        });
        test("kernel GP handler can advance the saved V86 IP and IRETD back to the guest",()=>
        {
            var(c,m,_)=Create([0xfa,0xb8,0x34,0x12]);Monitor(c,m,13);
            new byte[]{0x83,0x44,0x24,4,1,0x83,0xc4,4,0xcf}.CopyTo(m.Ram,0x6000);
            c.Step();c.Step();c.Step();Check(c.Virtual8086 && c.Eip==0x101 && c.GetRegister(Register.Sp)==0xabcd8000 && c.Virtual8086Entries==2);
            c.Step();Check((c.GetRegister(Register.Ax)&0xffff)==0x1234 && (c.Eflags&0x10000)==0);
        });
        test("V86 INT3 bypasses the INT-n IOPL check and saves its following IP",()=>
        {
            var(c,m,_)=Create([0xcc]);Gate(m,3,access:0xee);Monitor(c,m,3,null,0x101);
        });
        test("V86 INTO bypasses IOPL when OF is set",()=>
        {
            var(c,m,_)=Create([0xce],flags:0x20a02);Gate(m,4,access:0xee);Monitor(c,m,4,null,0x101);
        });
        test("hardware interrupt leaves V86 through the IDT and IRETD restores all segment images",()=>
        {
            var(c,m,_)=Create([0x90]);Gate(m,0x60);m.WriteByte(0x6000,0xcf);c.EnterHardwareInterrupt(0x60);
            Check(!c.Virtual8086 && c.GetRegister(Register.Sp)==0x19fdc && m.ReadDword(0x19fdc)==0x100 && (c.Eflags&0x200)==0);
            c.Step();Check(c.Virtual8086 && c.Eip==0x100 && c.GetSegment(Segment.Ds)==0x5000 && c.GetSegment(Segment.Gs)==0x8000 && c.GetRegister(Register.Sp)==0xabcd8000);
        });
        test("V86 trap gate preserves IF in the protected monitor",()=>
        {
            var(c,m,_)=Create([0xcc]);Gate(m,3,access:0xef);Monitor(c,m,3,null,0x101);Check((c.Eflags&0x200)!=0);
        });
        foreach(byte[] code in new byte[][]{[0xf4],[0x0f,0x20,0xc0],[0x0f,0x06],[0x0f,0x01,0x3e,0,0]})
            test($"V86 privileged instruction {Convert.ToHexString(code)} faults even at IOPL three",()=>
            {
                var(c,m,_)=Create(code,flags:0x23202);Monitor(c,m,13);
            });
        foreach(byte modrm in new byte[]{0xc0,0xc8,0xd0,0xd8})
            test($"V86 system selector form {modrm:X2} is UD rather than an operand access",()=>
            {
                var(c,m,_)=Create([0x0f,0x00,modrm]);Monitor(c,m,6,null);
            });
        test("V86 IOPL-three CLI STI PUSHF POPF execute without changing VM or IOPL",()=>
        {
            var(c,m,_)=Create([0xfa,0xfb,0x9c,0x9d],flags:0x23202);c.Step();Check((c.Eflags&0x200)==0);c.Step();c.Step();
            Check(m.ReadWord(0x47ffe)==0x3202);c.Step();Check(c.Virtual8086 && (c.Eflags&0x3200)==0x3200 && c.GetRegister(Register.Sp)==0xabcd8000);
        });
        test("V86 IOPL-three IRET uses the real CS IP and low flags frame",()=>
        {
            var(c,m,_)=Create([0xcf],flags:0x23202);m.WriteWord(0x48000,0x200);m.WriteWord(0x48002,0x3100);m.WriteWord(0x48004,0x0003);c.Step();
            Check(c.Virtual8086 && c.GetSegment(Segment.Cs)==0x3100 && c.Eip==0x200 && c.GetRegister(Register.Sp)==0xabcd8006 && (c.Eflags&0x23001)==0x23001);
        });
        foreach(bool missing in new[]{false,true})
            test($"V86 {(missing?"missing":"supervisor")} page uses user PF bits and the monitor frame",()=>
            {
                var(c,m,_)=Create([0xa1,0,0]);PagingTests.Pte(m,0x50000,0x50000,missing?0u:3u);Monitor(c,m,14,missing?4u:5u);Check(c.Cr2==0x50000);
            });
        test("V86 32-bit effective offsets above FFFF cause GP before paging",()=>
        {
            var(c,m,_)=Create([0x67,0xa1,0,0,1,0]);Monitor(c,m,13);Check(c.PageFaults==0);
        });
        foreach(int iopl in new[]{0,3})
            test($"V86 denied IN uses the bitmap even at IOPL {iopl}",()=>
            {
                var(c,m,p)=Create([0xe4,0x20],flags:(uint)(0x20202|iopl<<12));Monitor(c,m,13);Check(p.Reads==0 && p.Writes==0);
            });
        foreach(byte[] code in new byte[][]{[0xe4,0x20],[0xe5,0x20],[0x66,0xe5,0x20],[0xe6,0x20],[0xe7,0x20],[0x66,0xe7,0x20]})
            test($"V86 permitted port operation {Convert.ToHexString(code)} reaches the real I/O bus",()=>
            {
                var(c,m,p)=Create(code);m.WriteWord(0x906c,0);c.SetRegister(Register.Ax,0xdeadbeef);c.Step();
                int width=code.Length==3?4:(code[0]&1)==0?1:2;Check(c.Virtual8086 && p.Reads+p.Writes==1 && p.LastWidth==width);
            });
        test("I/O bitmap checks each byte of a wide port operation before issuing it",()=>
        {
            var(c,m,p)=Create([0x66,0xe5,0x20]);m.WriteWord(0x906c,8);Monitor(c,m,13);Check(p.Reads==0);
        });
        test("I/O bitmap checks a wide operation spanning two bitmap bytes",()=>
        {
            var(c,m,p)=Create([0x66,0xed]);c.SetRegister(Register.Dx,0x27);m.WriteWord(0x906c,0x0100);Monitor(c,m,13);Check(p.Reads==0);
        });
        test("I/O bitmap requires its extra byte inside the cached TSS limit",()=>
        {
            var(c,m,p)=Create([0xe4,0x20],tssLimit:0x6c);m.WriteWord(0x906c,0);Monitor(c,m,13);Check(p.Reads==0);
            var(other,ram,ports)=Create([0xe4,0x20],tssLimit:0x6d);ram.WriteWord(0x906c,0);other.Step();Check(other.Virtual8086 && ports.Reads==1);
        });
        test("I/O bitmap word page fault is a supervisor read and causes no port access",()=>
        {
            var(c,m,p)=Create([0xe4,0],tssLimit:0xe000);m.WriteWord(0x9066,0xdfff);m.WriteByte(0x16fff,0);PagingTests.Pte(m,0x17000,0,0);
            Monitor(c,m,14,0);Check(c.Cr2==0x17000 && p.Reads==0);
        });
        test("INS validates the V86 destination page before consuming an allowed port read",()=>
        {
            var(c,m,p)=Create([0x6c]);m.WriteWord(0x906c,0);c.SetRegister(Register.Dx,0x20);c.SetRegister(Register.Di,0);PagingTests.Pte(m,0x60000,0,0);
            Monitor(c,m,14,6);Check(p.Reads==0);
        });
        test("OUTS validates the V86 source page before issuing an allowed port write",()=>
        {
            var(c,m,p)=Create([0x6e]);m.WriteWord(0x906c,0);c.SetRegister(Register.Dx,0x20);c.SetRegister(Register.Si,0);PagingTests.Pte(m,0x50000,0,0);
            Monitor(c,m,14,4);Check(p.Writes==0);
        });
        test("V86 far CALL RETF use real selectors and a four-byte frame",()=>
        {
            var(c,m,_)=Create([0x9a,0,2,0,0x31]);m.WriteByte(0x31200,0xcb);c.Step();Check(c.GetSegment(Segment.Cs)==0x3100 && c.GetRegister(Register.Sp)==0xabcd7ffc);
            c.Step();Check(c.Virtual8086 && c.GetSegment(Segment.Cs)==0x3000 && c.Eip==0x105 && c.GetRegister(Register.Sp)==0xabcd8000);
        });
        test("V86 reset clears mode counters and the full system state",()=>
        {
            var(c,_,_)=Create([0x90]);c.Step();c.Reset();Check(!c.Virtual8086 && c.Virtual8086Entries==0 && c.Virtual8086Exits==0 && c.Virtual8086Instructions==0 && c.Tr==0);
        });
    }
}
