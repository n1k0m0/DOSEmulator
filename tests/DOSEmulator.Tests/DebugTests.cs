using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class DebugTests
{
    private static void Check(bool value, string message = "486 debug invariant failed")
    { if (!value) throw new Exception(message); }
    private static void Dr(X86Cpu c, MemoryBus m, int dr, uint value)
    {
        uint ip = c.Eip; c.SetRegister(Register.Ax, value);
        PagingTests.At(c, m, [0x0f, 0x23, (byte)(0xc0 | dr << 3)], 0x2400); c.Eip = ip;
    }
    private static uint Frame(X86Cpu c, MemoryBus m, uint ip, bool fault = false, uint status = 0)
    {
        byte? entered = null; c.InterruptEntered += v => entered = v;
        c.Step(); uint sp = c.GetRegister(Register.Sp);
        Check(entered == 1 && c.Eip == 0x6000 && !c.TripleFaulted, $"Expected #DB, got {entered} at {c.Position}");
        Check(m.ReadDword(sp) == ip, $"Saved IP {m.ReadDword(sp):X}, expected {ip:X}");
        Check((m.ReadDword(sp+8) & 0x10000) == 0, "Debug fault/trap must not synthesize RF in the 486 reference frame");
        Check((c.Eflags & X86Cpu.Trap) == 0 && (c.Dr6 & status) == status);
        return sp;
    }
    private static void Return(X86Cpu c, MemoryBus m, uint? flags = null)
    {
        uint sp=c.GetRegister(Register.Sp);
        if(flags.HasValue)m.WriteDword(sp+8,flags.Value);
        m.WriteByte(0x6000,0xcf);c.Step();
    }
    private static byte[] Memory(bool write, uint address=0x30000) => [(byte)(write?0xa3:0xa1),..BitConverter.GetBytes(address)];
    public static void AddTo(Action<string,Action> test)
    {
        foreach(int debug in Enumerable.Range(0,8)) foreach(int mod in Enumerable.Range(0,4))
            test($"MOV DR{debug} Mod={mod} always transfers 32 bits and consumes only ModRM",()=>
            {
                int actual=debug is 4 or 5?debug+2:debug;
                byte mrm=(byte)((mod<<6)|(debug<<3)|3);
                var(c,m)=ProtectedModeTests.Rig([0x66,0x67,0x0f,0x23,mrm,0x0f,0x21,(byte)(mrm & 0xf8)]);
                uint value=actual==6?0x8003u:actual==7?0x1000u:0x1234abcd;
                c.SetRegister(Register.Bx,value);c.Step();Check(c.Eip==0x2005);
                c.Step();uint expected=actual==6?0xffff8ff3u:actual==7?0x1400u:value;
                Check(c.Eip==0x2008 && c.GetRegister(Register.Ax)==expected && c.GeneralDetectFaults==0);
            });
        foreach(bool code32 in new[]{false,true}) foreach(bool prefix in new[]{false,true})
            test($"MOV debug transfer code32={code32} prefix66={prefix} retains high GPR half",()=>
            {
                byte[] code=[0x0f,0x23,0xc0,0x0f,0x21,0xc1];if(prefix)code=[0x66,..code];
                var(c,m)=ProtectedModeTests.Rig(code,code32:code32);c.SetRegister(Register.Ax,0x89abcdef);c.Step();c.Step();
                Check(c.Dr0==0x89abcdef && c.GetRegister(Register.Cx)==0x89abcdef);
            });
        test("486 DR6 fixed bits and writable status survive software clear",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Dr(c,m,6,uint.MaxValue);Check(c.Dr6==0xffffefff);Dr(c,m,4,0);Check(c.Dr6==0xffff0ff0);
        });
        test("486 DR7 fixed bit 10, writable bit 12 and ignored LE GE",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Dr(c,m,7,0xffffdfff);Check(c.Dr7==0xffff17ff);Dr(c,m,5,0);Check(c.Dr7==0x400);
        });
        foreach(int ring in new[]{1,2,3}) foreach(bool write in new[]{false,true})
            test($"MOV debug {(write?"write":"read")} ring {ring} gets GP without changing DR or GPR",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x0f,(byte)(write?0x23:0x21),0xc0]);PagingTests.Tss(c,m);
                Descriptor(m,0x28,0,0xfffff,(byte)(0x9a|ring<<5),12);Descriptor(m,0x30,0,0xfffff,(byte)(0x92|ring<<5),12);
                uint[] values=[0x2000,(uint)(0x28|ring),2,0x1b000,(uint)(0x30|ring)];
                for(int i=0;i<5;i++)m.WriteDword(0x18000u+(uint)i*4,values[i]);PagingTests.At(c,m,[0xcf],0x2100);
                c.SetRegister(Register.Ax,0x12345678);byte? entered=null;c.InterruptEntered+=v=>entered=v;c.Step();
                Check(entered==13 && c.CurrentPrivilegeLevel==0 && c.GetRegister(Register.Ax)==0x12345678 && c.Dr0==0 && c.GeneralDetectFaults==0);
            });
        foreach(bool write in new[]{false,true}) foreach(int dr in new[]{0,4,5,6,7})
            test($"GD faults before {(write?"writing":"reading")} DR{dr} and is cleared for handler",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x0f,(byte)(write?0x23:0x21),(byte)(0xc0|dr<<3)]);Dr(c,m,7,0x2000);
                c.SetRegister(Register.Ax,0x89abcdef);Frame(c,m,0x2000,true,0x2000);
                Check(c.GeneralDetectFaults==1 && c.Dr0==0 && c.Dr7==0x400 && c.GetRegister(Register.Ax)==0x89abcdef);
                Return(c,m);c.Step();Check(c.Eip==0x2003 && c.GeneralDetectFaults==1);
            });
        test("LOCK MOV DR produces UD before GD and keeps GD armed",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xf0,0x0f,0x21,0xc0]);Dr(c,m,7,0x2000);Fault(c,m,6,null);
            Check(c.GeneralDetectFaults==0 && c.Dr7==0x2400 && c.Dr6==0xffff0ff0);
        });
        foreach(int index in Enumerable.Range(0,4)) foreach(bool global in new[]{false,true})
            test($"execution breakpoint {index} global={global} precedes opcode execution",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x40,0x90]);Dr(c,m,index,0x2000);Dr(c,m,7,1u<<(2*index+(global?1:0)));c.SetRegister(Register.Ax,7);
                uint frame=Frame(c,m,0x2000,true,1u<<index);Check(c.GetRegister(Register.Ax)==7 && c.InstructionBreakpointFaults==1);
                Return(c,m,m.ReadDword(frame+8)|0x10000);c.Step();Check(c.GetRegister(Register.Ax)==8 && c.Eip==0x2001 && (c.Eflags&0x10000)==0);
            });
        test("execution breakpoint matches first prefix rather than an interior opcode",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x66,0x40]);Dr(c,m,0,0x2001);Dr(c,m,7,1);c.SetRegister(Register.Ax,0x12340005);c.Step();
            Check(c.GetRegister(Register.Ax)==0x12340006 && c.InstructionBreakpointFaults==0);
        });
        test("execution breakpoint before undefined opcode leaves flags and operands untouched",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x0f,0xa2]);Dr(c,m,0,0x2000);Dr(c,m,7,1);Frame(c,m,0x2000,true,1);Check(c.InstructionBreakpointFaults==1);
        });
        test("instruction comparator reports disabled equal slot when another slot enables the fault",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Dr(c,m,0,0x2000);Dr(c,m,2,0x2000);Dr(c,m,7,1);Frame(c,m,0x2000,true,5);
        });
        foreach(bool write in new[]{false,true}) foreach(int kind in new[]{0,1,2,3}) foreach(int length in new[]{1,2,4})
            test($"data breakpoint RW={kind} length={length} write={write}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(Memory(write));Dr(c,m,0,0x30000);uint len=length==4?3u:length==2?1u:0;
                Dr(c,m,7,1u|((uint)kind|len<<2)<<16);c.SetRegister(Register.Ax,0xabcdef12);m.WriteDword(0x30000,0x12345678);
                bool trap=kind==3 || (kind==1 && write);
                if(trap){Frame(c,m,0x2005,status:1);Check(c.DataBreakpointTraps==1);}else{c.Step();Check(c.Eip==0x2005 && c.DataBreakpointTraps==0);}
                Check(write?m.ReadDword(0x30000)==0xabcdef12:c.GetRegister(Register.Ax)==0x12345678);
            });
        foreach(int offset in Enumerable.Range(-2,9))
            test($"four-byte data comparator masks DR low bits and overlaps byte {offset}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0xa0,..BitConverter.GetBytes(unchecked((uint)(0x30000+offset)))]);
                Dr(c,m,0,0x30003);Dr(c,m,7,0xf0001);
                if(offset>=0 && offset<4)Frame(c,m,0x2005,status:1);else{c.Step();Check(c.DataBreakpointTraps==0);}
            });
        test("data breakpoint uses linear address and ignores alternate physical alias",()=>
        {
            var(c,m)=PagingTests.Create(Memory(false));PagingTests.Pte(m,0x30000,0x40000);m.WriteDword(0x40000,0xdeadbeef);
            Dr(c,m,0,0x40000);Dr(c,m,1,0x30000);Dr(c,m,7,0x00ff0005);Frame(c,m,0x2005,status:2);
            Check((c.Dr6&15)==2 && c.GetRegister(Register.Ax)==0xdeadbeef);
        });
        test("data fault prevents a pending memory breakpoint trap and keeps destination unchanged",()=>
        {
            var(c,m)=PagingTests.Create([0xf3,0xa5]);Dr(c,m,0,0x30000);Dr(c,m,7,0xf0001);
            c.SetRegister(Register.Si,0x30000);c.SetRegister(Register.Di,0x31000);c.SetRegister(Register.Cx,2);
            PagingTests.Pte(m,0x31000,0,0);Fault(c,m,14,2);
            Check(c.DataBreakpointTraps==0 && c.Dr6==0xffff0ff0 && c.GetRegister(Register.Cx)==2 && c.GetRegister(Register.Si)==0x30000);
        });
        test("RF suppresses execution faults but never data breakpoint traps",()=>
        {
            var(c,m)=ProtectedModeTests.Rig(Memory(true));Dr(c,m,0,0x2000);Dr(c,m,1,0x30000);Dr(c,m,7,0xd00005);c.Eflags=0x10002;c.SetRegister(Register.Ax,5);
            Frame(c,m,0x2005,status:2);Check(c.InstructionBreakpointFaults==0 && m.ReadDword(0x30000)==5);
        });
        test("TF is sampled before POPFD sets it and traps after the following instruction",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x9d,0x40]);m.WriteDword(0x18000,0x102);c.Step();Check(c.Eip==0x2001 && c.SingleStepTraps==0);
            c.SetRegister(Register.Ax,11);Frame(c,m,0x2002,status:0x4000);Check(c.GetRegister(Register.Ax)==12 && c.SingleStepTraps==1);
        });
        test("TF still traps after POPFD clears TF because sampling preceded the instruction",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x9d]);m.WriteDword(0x18000,2);c.Eflags=0x102;uint frame=Frame(c,m,0x2001,status:0x4000);
            Check((m.ReadDword(frame+8)&0x100)==0 && c.SingleStepTraps==1);
        });
        test("TF and a data breakpoint combine in one committed trap frame",()=>
        {
            var(c,m)=ProtectedModeTests.Rig(Memory(true));Dr(c,m,0,0x30000);Dr(c,m,7,0xd0001);c.SetRegister(Register.Ax,77);c.Eflags=0x102;
            Frame(c,m,0x2005,status:0x4001);Check(m.ReadDword(0x30000)==77 && c.SingleStepTraps==1 && c.DataBreakpointTraps==1);
        });
        test("TF trap also records a matching disabled data comparator",()=>
        {
            var(c,m)=ProtectedModeTests.Rig(Memory(false));Dr(c,m,0,0x30000);Dr(c,m,7,0xf0000);c.Eflags=0x102;
            Frame(c,m,0x2005,status:0x4001);Check(c.SingleStepTraps==1 && c.DataBreakpointTraps==0);
        });
        test("ordinary data breakpoint frame uses the original user stack and ring-zero TSS",()=>
        {
            var(c,m)=PagingTests.Create(Memory(true));Dr(c,m,0,0x30000);Dr(c,m,7,0xd0001);PagingTests.Tss(c,m);PagingTests.User(c,m);
            c.SetSegment(Segment.Ds,0x33);c.SetRegister(Register.Ax,0x11223344);uint sp=Frame(c,m,0x2005,status:1);
            Check(c.CurrentPrivilegeLevel==0 && sp==0x19fec && m.ReadDword(sp+12)==0x1b000 && m.ReadWord(sp+16)==0x33);
            Check(m.ReadDword(0x30000)==0x11223344 && c.PrivilegeStackSwitches==1);
        });
        test("MOV DR in V86 raises GP even at IOPL3",()=>
        {
            var(c,m)=PagingTests.Create([0xf4]);PagingTests.Tss(c,m);uint sp=c.GetRegister(Register.Sp);
            uint[] values=[0x100,0x3000,0x23002,0x8000,0x4000,0x5000,0x6000,0,0];
            for(int i=0;i<values.Length;i++)m.WriteDword(sp+(uint)i*4,values[i]);PagingTests.At(c,m,[0xcf]);
            new byte[]{0x0f,0x21,0xc0}.CopyTo(m.Ram,0x30100);byte? entered=null;c.InterruptEntered+=v=>entered=v;c.Step();
            Check(entered==13 && !c.Virtual8086 && c.CurrentPrivilegeLevel==0 && c.Dr0==0 && c.GeneralDetectFaults==0);
        });
        test("real-mode TF uses the actual 16-bit INT1 frame and clears live TF",()=>
        {
            var m=new MemoryBus(2*1024*1024);var c=new X86Cpu(m,new());c.SetSegment(Segment.Cs,0);c.SetSegment(Segment.Ss,0);
            c.Eip=0x1000;c.SetRegister(Register.Sp,0x9000);c.Eflags=0x302;m.WriteByte(0x1000,0x40);m.WriteWord(4,0x6000);m.WriteWord(6,0);
            c.Step();Check(c.Eip==0x6000 && c.GetRegister(Register.Sp)==0x8ffa && c.GetRegister(Register.Ax)==1);
            Check(m.ReadWord(0x8ffa)==0x1001 && m.ReadWord(0x8ffc)==0 && m.ReadWord(0x8ffe)==0x302 && c.Eflags==2 && c.SingleStepTraps==1);
        });
        test("software INT consumes TF without stepping the handler entry",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xcd,0x60]);c.Eflags=0x102;byte? entered=null;c.InterruptEntered+=v=>entered=v;c.Step();
            Check(entered==0x60 && c.Eip==0x6000 && c.SingleStepTraps==0 && (m.ReadDword(c.GetRegister(Register.Sp)+8)&0x100)!=0);
        });
        foreach(bool pop in new[]{false,true})
            test($"{(pop?"POP":"MOV")} SS defers TF through the following stack-pointer instruction",()=>
            {
                byte[] code=pop?[0x17,0xbc,0,0x70,1,0]:[0x8e,0xd0,0xbc,0,0x70,1,0];var(c,m)=ProtectedModeTests.Rig(code);
                c.SetRegister(Register.Ax,16);m.WriteDword(0x18000,16);c.Eflags=0x102;c.Step();
                Check(c.SingleStepTraps==0 && c.Eip==(pop?0x2001u:0x2002));
                Frame(c,m,pop?0x2006u:0x2007,status:0x4000);Check(c.GetRegister(Register.Sp)==0x17000-12 && c.SingleStepTraps==1);
            });
        test("SS shadow suppresses execution fault on immediately following instruction",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x8e,0xd0,0x40]);Dr(c,m,0,0x2002);Dr(c,m,7,1);c.SetRegister(Register.Ax,16);c.Step();c.Step();
            Check(c.InstructionBreakpointFaults==0 && c.GetRegister(Register.Ax)==17);
        });
        test("POP SS data breakpoint is deferred and reported after a new ESP is installed",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x17,0xbc,0,0x70,1,0]);Dr(c,m,0,0x18000);Dr(c,m,7,0xf0001);m.WriteDword(0x18000,16);
            c.Step();Check(c.DataBreakpointTraps==0);Frame(c,m,0x2006,status:1);Check(c.GetRegister(Register.Sp)==0x17000-12);
        });
        test("REP data trap reports one committed element and resumes at the first prefix",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0xf3,0xa5]);Dr(c,m,0,0x31000);Dr(c,m,7,0xd0001);m.WriteDword(0x30000,0x12345678);
            c.SetRegister(Register.Si,0x30000);c.SetRegister(Register.Di,0x31000);c.SetRegister(Register.Cx,2);
            c.Step();uint sp=c.GetRegister(Register.Sp);Check(c.Eip==0x6000 && m.ReadDword(sp)==0x2000 && (m.ReadDword(sp+8)&0x10000)!=0);
            Check(c.GetRegister(Register.Cx)==1 && c.GetRegister(Register.Si)==0x30004 && m.ReadDword(0x31000)==0x12345678 && m.ReadDword(0x31004)==0);
            Return(c,m);c.Step();Check(c.Eip==0x2002 && c.GetRegister(Register.Cx)==0 && c.DataBreakpointTraps==1 && (c.Eflags&0x10000)==0);
        });
        test("DMA and direct host bus writes do not generate CPU debug traps",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Dr(c,m,0,0x30000);Dr(c,m,7,0xd0001);m.WriteDword(0x30000,7);c.Step();
            Check(c.DataBreakpointTraps==0 && c.Eip==0x2001);
        });
        test("debug registers and all debug deferral state reset with the CPU",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x90]);Dr(c,m,0,0x2000);Dr(c,m,7,1);Frame(c,m,0x2000,true,1);c.Reset();
            Check(c.Dr0==0 && c.Dr1==0 && c.Dr2==0 && c.Dr3==0 && c.Dr6==0xffff0ff0 && c.Dr7==0x400);
            Check(c.InstructionBreakpointFaults==c.DataBreakpointTraps && c.DataBreakpointTraps==c.SingleStepTraps && c.SingleStepTraps==c.GeneralDetectFaults && c.GeneralDetectFaults==0);
        });
    }
}
