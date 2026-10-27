// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class RepBatchTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition,string detail="REP batch invariant") {if(!condition)throw new Exception(detail);}
    /// <summary>Builds an isolated machine fixture with the specified guest code and optional identity paging.</summary>
    internal static PcMachine Machine(byte[] code,bool paging=false)
    {
        var m=PcMachine.CreateDemo(new MachineProfile{RamMiB=8});var c=m.Cpu;var ram=m.Memory;
        // Writes the explicit code/data descriptor required by the benchmark's protected-mode setup.
        void Desc(uint at,byte access){ram.WriteDword(at,0xffff);ram.WriteDword(at+4,0xcf0000u|((uint)access<<8));}
        Desc(0x408,0x9a);Desc(0x410,0x92);ram.WriteWord(0x300,0x17);ram.WriteDword(0x302,0x400);
        byte[] boot=[0x0f,0x01,0x16,0,3,0x66,0xb8,0x11,0,0,0x60,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
        boot.CopyTo(ram.Ram,0x1000);code.CopyTo(ram.Ram,0x2000);c.SetSegment(Segment.Cs,0);c.Eip=0x1000;
        for(int i=0;i<4;i++)m.RunSteps(1);Check(c.Eip==0x2000 && c.ProtectedMode);
        c.SetSegment(Segment.Ds,16);c.SetSegment(Segment.Es,16);c.SetSegment(Segment.Ss,16);c.SetRegister(Register.Sp,0x18000);c.Eflags=2;
        if(paging)
        {
            ram.WriteDword(0x1c000,0x1d007);for(uint i=0;i<1024;i++)ram.WriteDword(0x1d000+i*4,i*4096|7);
            c.SetRegister(Register.Ax,0x1c000);new byte[]{0x0f,0x22,0xd8}.CopyTo(ram.Ram,0x2200);c.Eip=0x2200;m.RunSteps(1);
            c.SetRegister(Register.Ax,c.Cr0|0x80010000);new byte[]{0x0f,0x22,0xc0}.CopyTo(ram.Ram,0x2200);c.Eip=0x2200;m.RunSteps(1);c.Eip=0x2000;
        }
        return m;
    }
    /// <summary>Executes the component's explicit runner with the supplied bounds and reports its resulting evidence.</summary>
    private static void Run(PcMachine m,int steps,bool scalar)
    {if(scalar){for(int i=0;i<steps;i++)if(m.RunSteps(1)==0)break;}else m.RunSteps(steps);}
    /// <summary>Compares independent baseline and optimized machine state, including RAM, registers and virtual time.</summary>
    private static void Same(PcMachine a,PcMachine b)
    {
        Check(a.Fault==b.Fault && a.Scheduler.Now==b.Scheduler.Now,"fault/time");
        Check(a.Cpu.Eip==b.Cpu.Eip && a.Cpu.Eflags==b.Cpu.Eflags && a.Cpu.Halted==b.Cpu.Halted,"IP/FLAGS/halt");
        Check(a.Cpu.Instructions==b.Cpu.Instructions && a.Cpu.ProtectedInstructions==b.Cpu.ProtectedInstructions,"step counters");
        foreach(Register r in Enum.GetValues<Register>())Check(a.Cpu.GetRegister(r)==b.Cpu.GetRegister(r),"GPR "+r);
        foreach(Segment s in Enum.GetValues<Segment>())Check(a.Cpu.GetSegment(s)==b.Cpu.GetSegment(s)&&a.Cpu.GetSegmentCache(s)==b.Cpu.GetSegmentCache(s),"segment "+s);
        Check(a.Cpu.Cr0==b.Cpu.Cr0&&a.Cpu.Cr2==b.Cpu.Cr2&&a.Cpu.Cr3==b.Cpu.Cr3,"CR state");
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram),"guest RAM/page-table bytes");
        Check(a.Cpu.PageWalks==b.Cpu.PageWalks && a.Cpu.TlbHits==b.Cpu.TlbHits,"REP translation accounting");
    }
    /// <summary>Registers the named rep batch regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(bool paging in new[]{false,true})foreach(int width in new[]{1,2,4})foreach(bool reverse in new[]{false,true})foreach(bool store in new[]{false,true})foreach(bool address16 in new[]{false,true})
            test($"REP batch vs Step, paging={paging}, width={width}, DF={reverse}, store={store}, address16={address16}",()=>
            {
                byte opcode=(byte)(store?(width==1?0xaa:0xab):(width==1?0xa4:0xa5));byte[] prefix=[..(address16?new byte[]{0x67}:Array.Empty<byte>()),..(width==2?new byte[]{0x66}:Array.Empty<byte>())];
                byte[] code=[..prefix,0xf3,opcode,0xf4];using var a=Machine(code,paging);using var b=Machine(code,paging);
                foreach(var m in new[]{a,b})
                {
                    for(int i=0x30000;i<0x3c000;i++)m.Memory.Ram[i]=(byte)(i*31+7);
                    for(int i=0x4000;i<0xc000;i++)m.Memory.Ram[i]=(byte)(i*17+3);
                    m.Cpu.SetRegister(Register.Cx,address16?0xbeef1000u:4096);m.Cpu.SetRegister(Register.Ax,0x1234cafe);
                    m.Cpu.SetRegister(Register.Si,(address16?0xaaaa4000u:0x30000u)+(uint)(reverse?16380:0));
                    m.Cpu.SetRegister(Register.Di,(address16?0xbbbb8000u:0x38000u)+(uint)(reverse?16380:0));if(reverse)m.Cpu.Eflags|=X86Cpu.Direction;
                }
                Run(a,4097,true);Run(b,4097,false);Same(a,b);Check(b.Cpu.RepBatchedElements>1000,"batch was not exercised");
            });
        foreach(bool paging in new[]{false,true})foreach(bool reverse in new[]{false,true})foreach(int delta in new[]{-4,-1,0,1,4})
            test($"REP destructive overlap, paging={paging}, DF={reverse}, delta={delta}",()=>
            {
                using var a=Machine([0xf3,0xa5,0xf4],paging);using var b=Machine([0xf3,0xa5,0xf4],paging);
                foreach(var m in new[]{a,b}){for(int i=0;i<8192;i++)m.Memory.Ram[0x30000+i]=(byte)(i*19+11);uint at=reverse?0x30f00u:0x30010u;m.Cpu.SetRegister(Register.Si,at);m.Cpu.SetRegister(Register.Di,unchecked(at+(uint)delta));m.Cpu.SetRegister(Register.Cx,500);if(reverse)m.Cpu.Eflags|=X86Cpu.Direction;}
                Run(a,501,true);Run(b,501,false);Same(a,b);Check(b.Cpu.RepBatchedElements>0);
            });
        foreach(bool paging in new[]{false,true})
            test($"REP DMA deadlines observe identical intermediate RAM, paging={paging}",()=>
            {
                using var a=Machine([0xf3,0xa4,0xf4],paging);using var b=Machine([0xf3,0xa4,0xf4],paging);var logs=new List<string>[2]{[],[]};
                foreach(var(m,index) in new[]{(a,0),(b,1)})
                {
                    for(int i=0;i<4096;i++)m.Memory.Ram[0x30000+i]=(byte)(i*19+3);m.Cpu.SetRegister(Register.Si,0x30000);m.Cpu.SetRegister(Register.Di,0x31000);m.Cpu.SetRegister(Register.Cx,2000);
                    for(int n=1;n<=12;n++){int row=n;m.Scheduler.ScheduleAfter((ulong)(n*7913),()=>{logs[index].Add($"{m.Scheduler.Now}:{m.Cpu.GetRegister(Register.Cx)}:{m.Memory.ReadDword(0x31000u+(uint)(row*32))}");m.Memory.WriteByte(0x30000u+(uint)(row*64),(byte)row);});}
                }
                Run(a,2001,true);Run(b,2001,false);Same(a,b);Check(logs[0].SequenceEqual(logs[1])&&logs[0].Count==12,"intermediate event/CPU/RAM order");
            });
        foreach(bool firstWrite in new[]{false,true})
            test($"REP detects {(firstWrite?"first-element":"device")} self-modified opcode before continuing",()=>
            {
                using var a=Machine([0xf3,0xa4,0x90,0x90,0x90]);using var b=Machine([0xf3,0xa4,0x90,0x90,0x90]);
                foreach(var m in new[]{a,b})
                {
                    m.Cpu.SetRegister(Register.Si,0x30000);m.Cpu.SetRegister(Register.Di,firstWrite?0x2001u:0x31000u);m.Cpu.SetRegister(Register.Cx,500);m.Cpu.SetRegister(Register.Ax,0x90);m.Memory.WriteByte(0x30000,0xaa);
                    if(!firstWrite)m.Scheduler.ScheduleAfter(7913,()=>m.Memory.WriteByte(0x2001,0xaa));
                }
                if(firstWrite){Run(a,1,true);Run(b,1,false);Check(!b.Cpu.HasRepContinuation,"changed opcode was incorrectly cached");Run(a,499,true);Run(b,499,false);}
                else {Run(a,500,true);Run(b,500,false);}
                Same(a,b);
            });
        foreach(bool debug in new[]{false,true})
            test($"REP {(debug?"TF":"instruction observer")} keeps element boundaries and disables batching",()=>
            {
                using var m=Machine([0xf3,0xa4,0xf4]);m.Cpu.SetRegister(Register.Si,0x30000);m.Cpu.SetRegister(Register.Di,0x31000);m.Cpu.SetRegister(Register.Cx,128);
                if(debug){m.Memory.WriteWord(0x330,0x7ff);m.Memory.WriteDword(0x332,0x8000);ProtectedModeTests.Gate(m.Memory,1);new byte[]{0x0f,0x01,0x1d,0x30,3,0,0}.CopyTo(m.Memory.Ram,0x2200);m.Cpu.Eip=0x2200;m.RunSteps(1);m.Memory.WriteByte(0x6000,0xf4);m.Cpu.Eip=0x2000;m.Cpu.Eflags|=0x100;}
                else m.Cpu.InstructionStarting+=(_,_)=>{};
                m.RunSteps(129);Check(m.Cpu.RepBatchedElements==0);
            });
        foreach(int firstPageBytes in new[]{1,4,127,4095})
            test($"REP reaches missing destination page after exactly {firstPageBytes} successful elements",()=>
            {
                using var a=Machine([0xf3,0xaa],true);using var b=Machine([0xf3,0xaa],true);
                foreach(var m in new[]{a,b})
                {
                    m.Cpu.SetRegister(Register.Di,0x31000u-(uint)firstPageBytes);m.Cpu.SetRegister(Register.Cx,8192);m.Cpu.SetRegister(Register.Ax,0x55);
                    m.Memory.WriteDword(0x1d000+0x31*4,0);m.Memory.WriteWord(0x330,0x7ff);m.Memory.WriteDword(0x332,0x8000);ProtectedModeTests.Gate(m.Memory,14);m.Memory.WriteByte(0x6000,0xf4);
                    new byte[]{0x0f,0x01,0x1d,0x30,3,0,0}.CopyTo(m.Memory.Ram,0x2200);m.Cpu.Eip=0x2200;m.RunSteps(1);m.Cpu.Eip=0x2000;
                }
                Run(a,8200,true);Run(b,8200,false);Same(a,b);Check(b.Cpu.PageFaults==1 && b.Cpu.Cr2==0x31000 && b.Cpu.GetRegister(Register.Cx)==8192-firstPageBytes);
            });
    }
}
