using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using static ProtectedModeTests;

internal static class X87ControlTests
{
    private static void Check(bool condition) { if (!condition) throw new Exception("x87/model invariant failed"); }
    private static (X86Cpu Cpu, MemoryBus Memory) Real(byte[] code)
    {
        var m=new MemoryBus(2*1048576);var c=new X86Cpu(m,new());
        c.SetSegment(Segment.Cs,0);c.Eip=0x2000;c.SetRegister(Register.Sp,0x9000);
        code.CopyTo(m.Ram,0x2000);return(c,m);
    }
    private static void At(X86Cpu c,MemoryBus m,byte[] code)
    {code.CopyTo(m.Ram,0x2000);c.Eip=0x2000;c.Step();}
    public static void AddTo(Action<string,Action> test)
    {
        test("Original DOS4GW x87 detection sequence observes initialized status and control",()=>
        {
            var(c,m)=Real([0xdb,0xe3,0xdd,0x3e,0,0x30,0xd9,0x3e,2,0x30,0xdf,0xe0]);
            c.SetRegister(Register.Ax,0xa5a51234);c.Eflags=0x203;
            for(int i=0;i<4;i++)c.Step();
            Check(m.ReadWord(0x3000)==0 && m.ReadWord(0x3002)==0x37f && c.GetRegister(Register.Ax)==0xa5a50000);
            Check(c.FpuTagWord==0xffff && c.Eflags==0x203);
        });
        foreach(ushort control in new ushort[]{0x37f,0x27f,0x77f,0xb7f,0xf7f})
            test($"x87 FLDCW/FNSTCW preserves defined control fields {control:X4}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0xd9,0x2d,0,0x30,0,0,0xd9,0x3d,2,0x30,0,0]);
                m.WriteWord(0x3000,control);c.Step();c.Step();
                Check(c.FpuControlWord==control && m.ReadWord(0x3002)==control);
                At(c,m,[0xdb,0xe3]);Check(c.FpuControlWord==0x37f && c.FpuStatusWord==0 && c.FpuTagWord==0xffff);
            });
        byte[][] instructions=[[0xdb,0xe3],[0xdb,0xe2],[0xdf,0xe0],[0xd9,0x3d,0,0,3,0],[0xdd,0x3d,0,0,3,0],[0xd9,0x2d,0,0,3,0],[0xd9,0xd0],[0xdb,0xe4]];
        foreach(byte[] code in instructions)foreach(uint flags in new uint[]{4,8,12})
            test($"x87 {Convert.ToHexString(code)} raises NM before effects with CR0 bits {flags}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(code);c.SetRegister(Register.Ax,c.Cr0|flags);
                PagingTests.At(c,m,[0x0f,0x22,0xc0],0x2100);c.Eip=0x2000;
                c.SetRegister(Register.Ax,0xaabbccdd);m.WriteDword(0x30000,0x11223344);
                Fault(c,m,7,null);Check(c.FpuControlWord==0x37f && c.FpuTagWord==0xffff && c.FpuStatusWord==0);
                Check(c.GetRegister(Register.Ax)==0xaabbccdd && m.ReadDword(0x30000)==0x11223344);
            });
        foreach(byte[] code in new byte[][]{[0xd9,0xd0],[0xdb,0xe2],[0xdb,0xe4]})
            test($"x87 defined control/no-op {Convert.ToHexString(code)} preserves general state",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(code);c.SetRegister(Register.Ax,0xdeadbeef);c.Eflags=0x247;c.Step();
                Check(c.GetRegister(Register.Ax)==0xdeadbeef && c.Eflags==0x247 && c.FpuControlWord==0x37f);
            });
        test("FLDCW crossing an absent page does not change the control word",()=>
        {
            var(c,m)=PagingTests.Create([0xd9,0x2d,0xff,0x0f,3,0]);PagingTests.Pte(m,0x30000,0x40000);PagingTests.Pte(m,0x31000,0,0);
            m.WriteByte(0x40fff,0x7f);Fault(c,m,14,0);Check(c.Cr2==0x31000 && c.FpuControlWord==0x37f);
        });
        test("FNSTCW crossing a protected page leaves the first byte unchanged",()=>
        {
            var(c,m)=PagingTests.Create([0xd9,0x3d,0xff,0x0f,3,0]);PagingTests.Pte(m,0x30000,0x40000);PagingTests.Pte(m,0x31000,0x50000,1);
            m.WriteByte(0x40fff,0xa5);m.WriteByte(0x50000,0x5a);Fault(c,m,14,3);
            Check(c.Cr2==0x31000 && m.ReadWord(0x40fff)==0x00a5 && m.ReadByte(0x50000)==0x5a);
        });
        test("x87 device unavailable takes precedence over absent operand page",()=>
        {
            var(c,m)=PagingTests.Create([0xd9,0x2d,0,0,3,0]);PagingTests.Pte(m,0x30000,0,0);
            c.SetRegister(Register.Ax,c.Cr0|8);PagingTests.At(c,m,[0x0f,0x22,0xc0],0x2100);c.Eip=0x2000;
            Fault(c,m,7,null);Check(c.Cr2==0 && c.PageFaults==0);
        });
        test("486 CPU reset without BIST keeps the initialized FPU control",()=>
        {
            var(c,m)=Real([0xd9,0x2e,0,0x30]);m.WriteWord(0x3000,0xb7f);c.Step();c.Reset();Check(c.FpuControlWord==0xb7f);
        });
        foreach(bool protectedMode in new[]{false,true})
            test($"Early 486 POPFD does not advertise CPUID VIF VIP in {(protectedMode?"PE":"real mode")}",()=>
            {
                var(c,m)=protectedMode?ProtectedModeTests.Rig([0x9d,0x9c]):Real([0x66,0x9d,0x66,0x9c]);uint stack=c.GetRegister(Register.Sp);
                m.WriteDword(stack,0x003c3002);c.Step();c.Step();
                Check((m.ReadDword(stack)&0x00380000)==0 && (c.Eflags&0x40000)!=0);
            });
        test("Early 486 unsupported CPUID delivers UD rather than a host stop",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x0f,0xa2]);c.SetRegister(Register.Ax,0x12345678);c.SetRegister(Register.Dx,0xaabbccdd);
            Fault(c,m,6,null);Check(c.GetRegister(Register.Ax)==0x12345678 && c.GetRegister(Register.Dx)==0xaabbccdd);
        });
    }
}
