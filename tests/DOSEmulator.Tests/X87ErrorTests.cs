// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using static ProtectedModeTests;

internal static class X87ErrorTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string detail = "numeric error invariant") { if (!value) throw new Exception(detail); }
    /// <summary>Places controlled bytes at a private guest address and executes through the normal decoder.</summary>
    private static void At(X86Cpu c, MemoryBus m, byte[] bytes, uint address = 0x2200)
    { bytes.CopyTo(m.Ram, (int)address); c.Eip = address; c.Step(); }
    /// <summary>Selects the highest eligible request under the current mask and in-service priority.</summary>
    private static void Pending(X86Cpu c, MemoryBus m, bool ne = false)
    {
        c.SetRegister(Register.Ax, ne ? c.Cr0 | 0x20 : c.Cr0 & ~0x20u);
        At(c,m,[0x0f,0x22,0xc0]);
        m.WriteWord(0x30000,0x37e); At(c,m,[0xd9,0x2d,0,0,3,0]);
        At(c,m,[0xd8,0xc0]); // Empty stack: unmasked IE/SF, no arithmetic commit.
        Check(c.FpuErrorAsserted && (c.FpuStatusWord & 0x80c1) == 0x80c1);
    }
    /// <summary>Connects the test CPU's actual FERR/IGNNE levels and PIC IRQ13 through the real AT latch.</summary>
    private static FpuErrorLatch Wire(X86Cpu c, IoPortBus io, Pic8259 pic)
    {
        var latch = new FpuErrorLatch(high=>pic.SetLine(13,high),high=>c.IgnoreNumericError=high);
        io.Map(0xf0,1,latch); c.FpuErrorChanged += latch.SetFerr;
        return latch;
    }
    /// <summary>Registers the named x87 error regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(bool ne in new[]{false,true}) foreach(int flag in new[]{1,2,4,8,16,32})
            test($"Restored unmasked flag {flag:X2} asserts FERR independently of NE={ne}",()=>
            {
                var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x90],io:io);var latch=Wire(c,io,new());
                if(ne){c.SetRegister(Register.Ax,c.Cr0|0x20);At(c,m,[0x0f,0x22,0xc0]);}
                m.WriteWord(0x31000,(ushort)(0x37f&~flag));m.WriteWord(0x31004,(ushort)flag);m.WriteWord(0x31008,0xffff);
                At(c,m,[0xdd,0x25,0,0x10,3,0]);Check(c.FpuErrorAsserted && latch.FerrAsserted && latch.ErrorEdges==1);
                Check(c.FpuStatusWord==(flag|0x8080));
                At(c,m,[0xdd,0x35,0,0x20,3,0]);Check(!c.FpuErrorAsserted && !latch.FerrAsserted && latch.InterruptAsserted);
                Check(m.ReadWord(0x32004)==(flag|0x8080) && c.FpuControlWord==0x37f);
            });
        test("FNSTENV masks pending IE without clearing sticky flag; later FLDCW produces a new FERR edge",()=>
        {
            var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x90],io:io);var latch=Wire(c,io,new());Pending(c,m);
            At(c,m,[0xd9,0x35,0,0x10,3,0]);Check(!c.FpuErrorAsserted && (c.FpuStatusWord&0x41)==0x41);
            io.Write(0xf0,0,1);At(c,m,[0xd9,0x2d,0,0,3,0]);Check(c.FpuErrorAsserted && latch.ErrorEdges==2 && latch.InterruptAsserted);
        });
        foreach(bool ne in new[]{false,true}) foreach(byte opcode in new byte[]{0xd9,0xdd})
            test($"Nonwaiting {(opcode==0xd9?"FNSTENV":"FNSAVE")} PF preserves pending state and FERR, NE={ne}",()=>
            {
                var io=new IoPortBus();var(c,m)=PagingTests.Create([opcode,0x35,0,0x10,3,0],io:io);var latch=Wire(c,io,new());Pending(c,m,ne);
                ushort cw=c.FpuControlWord,sw=c.FpuStatusWord;PagingTests.Pte(m,0x31000,0,0);c.Eip=0x2000;Fault(c,m,14,2);
                Check(c.FpuControlWord==cw && c.FpuStatusWord==sw && c.FpuErrorAsserted && latch.FerrAsserted && latch.ErrorEdges==1);
            });
        foreach (bool ne in new[]{false,true}) foreach (bool ignore in new[]{false,true})
            test($"FERR asserts independently of NE={ne}; IGNNE={ignore} only bypasses external waits",()=>
            {
                var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x90],io:io);var pic=new Pic8259();var latch=Wire(c,io,pic);
                Pending(c,m,ne);Check(latch.FerrAsserted && latch.InterruptAsserted && latch.ErrorEdges==1);
                if(ignore)io.Write(0xf0,0,1);
                new byte[]{0x66,0xd9,0xd0}.CopyTo(m.Ram,0x2000);c.Eip=0x2000;
                long instructions=c.Instructions, esc=c.X87Instructions;
                if(ne){Fault(c,m,16,null);Check(c.NumericErrorFaults==1 && !c.WaitingForNumericError);}
                else if(ignore){c.Step();Check(c.Eip==0x2003 && c.X87Instructions==esc+1 && !c.WaitingForNumericError);}
                else
                {
                    c.Eflags|=X86Cpu.Trap|0x10000;uint flags=c.Eflags;
                    c.Step();for(int i=0;i<8;i++)c.Step();
                    Check(c.Eip==0x2000 && c.Instructions==instructions && c.X87Instructions==esc && c.Eflags==flags);
                    Check(c.WaitingForNumericError && !c.Halted && c.NumericErrorWaits==1 && c.SingleStepTraps==0);
                }
                Check(c.FpuErrorAsserted && latch.FerrAsserted && c.FpuTagWord==0xffff);
            });
        foreach(bool ne in new[]{false,true})
        foreach(byte[] code in new byte[][]{[0xdf,0xe0],[0xd9,0x3d,0x10,0,3,0],[0xdd,0x3d,0x10,0,3,0],[0xdb,0xe0],[0xdb,0xe1],[0xdb,0xe4]})
            test($"Nonwaiting {Convert.ToHexString(code)} executes with a pending error, NE={ne}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig(code);Pending(c,m,ne);c.Eip=0x2000;long esc=c.X87Instructions;c.Step();
                Check(!c.WaitingForNumericError && c.Eip==0x2000+code.Length && c.X87Instructions==esc+1 && c.FpuErrorAsserted);
            });
        foreach(bool ne in new[]{false,true})
        foreach(byte[] code in new byte[][]{[0xdb,0xe2],[0xdb,0xe3],[0xdd,0x35,0x10,0,3,0],[0xd9,0x35,0x10,0,3,0]})
            test($"Nonwaiting clear/mask {Convert.ToHexString(code)} deasserts FERR but preserves board latch, NE={ne}",()=>
            {
                var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig(code,io:io);var latch=Wire(c,io,new());Pending(c,m,ne);
                c.Eip=0x2000;c.Step();Check(!c.FpuErrorAsserted && !latch.FerrAsserted && latch.InterruptAsserted && !latch.IgnoreNumericError);
                io.Write(0xf0,0xa5,1);Check(!latch.InterruptAsserted && !c.IgnoreNumericError);
            });
        test("AT F0 acknowledge holds IGNNE through waiting FNOP until FNCLEX; next error is a new edge",()=>
        {
            var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x90],io:io);var latch=Wire(c,io,new());Pending(c,m);
            io.Write(0xf0,0x1234,2);Check(latch.Acknowledgements==0 && latch.InterruptAsserted);
            io.Write(0xf0,0x5a,1);Check(c.IgnoreNumericError && !latch.InterruptAsserted);
            At(c,m,[0xd9,0xd0]);Check(c.FpuErrorAsserted && latch.ErrorEdges==1 && c.IgnoreNumericError);
            At(c,m,[0xdb,0xe2]);Check(!c.FpuErrorAsserted && !c.IgnoreNumericError);
            At(c,m,[0xd8,0xc0]);Check(latch.ErrorEdges==2 && latch.InterruptAsserted);
            io.Write(0xf0,0,1);At(c,m,[0xdb,0xe3]);Check(!c.IgnoreNumericError && !latch.FerrAsserted);
        });
        test("PIC edge IRR survives both FNCLEX and port F0 until real IRQ acknowledge; EOI is separate",()=>
        {
            var pic=new Pic8259();var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x90],io:io);var latch=Wire(c,io,pic);
            pic.Write(0x21,0xfb,1);pic.Write(0xa1,0xdf,1);Pending(c,m);
            At(c,m,[0xdb,0xe2]);io.Write(0xf0,0,1);Check(pic.HasPending && !latch.InterruptAsserted);
            Check(pic.Acknowledge()==0x75 && !pic.HasPending);
            pic.Write(0xa0,0x0b,1);pic.Write(0x20,0x0b,1);Check(pic.Read(0xa0,1)==0x20 && pic.Read(0x20,1)==4);
            pic.Write(0xa0,0x20,1);Check(pic.Read(0xa0,1)==0 && pic.Read(0x20,1)==4);
            pic.Write(0x20,0x20,1);Check(pic.Read(0x20,1)==0 && !pic.HasPending);
        });
        foreach(bool ne in new[]{false,true}) foreach(uint unavailable in new uint[]{4,8,12})
            test($"NM precedes pending numeric error NE={ne} with CR0 EM/TS={unavailable}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0xd9,0xd0]);Pending(c,m,ne);c.SetRegister(Register.Ax,c.Cr0|unavailable);
                At(c,m,[0x0f,0x22,0xc0]);c.Eip=0x2000;Fault(c,m,7,null);
                Check(c.NumericErrorWaits==0 && c.NumericErrorFaults==0 && c.FpuErrorAsserted);
            });
        foreach(bool ne in new[]{false,true}) foreach(uint unavailable in new uint[]{0,2,8,10})
            test($"FWAIT uses MP+TS rather than ESC EM/TS contract NE={ne}, bits={unavailable}",()=>
            {
                var(c,m)=ProtectedModeTests.Rig([0x9b]);Pending(c,m,ne);c.SetRegister(Register.Ax,(c.Cr0&~0xeu)|unavailable|4);
                At(c,m,[0x0f,0x22,0xc0]);c.Eip=0x2000;
                if(unavailable==10){Fault(c,m,7,null);Check(c.NumericErrorFaults==0 && c.NumericErrorWaits==0);}
                else if(ne)Fault(c,m,16,null);
                else {c.Step();Check(c.WaitingForNumericError && c.Eip==0x2000);}
            });
        foreach(bool ne in new[]{false,true})
            test($"Pending x87 check precedes data operand page fault NE={ne}",()=>
            {
                var(c,m)=PagingTests.Create([0xd8,0x05,0,0x10,3,0]);Pending(c,m,ne);PagingTests.Pte(m,0x31000,0,0);c.Eip=0x2000;
                if(ne)Fault(c,m,16,null);else{c.Step();Check(c.WaitingForNumericError);}
                Check(c.PageFaults==0 && c.Cr2==0 && c.FpuErrorAsserted);
            });
        test("Actual MF frame retries the waiting instruction after FNCLEX and IRETD",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x9b,0x40]);Pending(c,m,true);c.Eip=0x2000;uint before=c.GetRegister(Register.Ax);
            uint frame=Fault(c,m,16,null);new byte[]{0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);
            c.Step();c.Step();Check(c.Eip==0x2000 && !c.FpuErrorAsserted && c.GetRegister(Register.Sp)==frame+12);
            c.Step();c.Step();Check(c.Eip==0x2002 && c.GetRegister(Register.Ax)==before+1 && c.NumericErrorFaults==1);
        });
        test("Interrupt handler can execute integer and no-wait code while FWAIT is blocked",()=>
        {
            var(c,m)=ProtectedModeTests.Rig([0x66,0x9b,0x90]);Pending(c,m);c.Eip=0x2000;c.Eflags=0x202;c.Step();
            Check(c.WaitingForNumericError);uint sp=c.GetRegister(Register.Sp);c.EnterHardwareInterrupt(0x75);
            Check(!c.WaitingForNumericError && m.ReadDword(sp-12)==0x2000);
            new byte[]{0xdf,0xe0,0xdb,0xe2,0xcf}.CopyTo(m.Ram,0x6000);
            c.Step();Check((c.GetRegister(Register.Ax)&0x80c1)==0x80c1);c.Step();c.Step();c.Step();
            Check(c.Eip==0x2002 && !c.FpuErrorAsserted && c.NumericErrorWaits==1);
        });
        test("486 hot reset abandons wait but retains FPU error and powered external latch",()=>
        {
            var io=new IoPortBus();var(c,m)=ProtectedModeTests.Rig([0x9b],io:io);var latch=Wire(c,io,new());Pending(c,m);c.Eip=0x2000;c.Step();
            c.Reset();Check(!c.WaitingForNumericError && c.FpuErrorAsserted && latch.InterruptAsserted && latch.ErrorEdges==1);
            Check(c.NumericErrorWaits==0 && c.NumericErrorFaults==0 && (c.Cr0&0x20)==0);
        });
        test("Runtime numeric wait advances device deadline without retiring and resumes through IRQ13",()=>
        {
            using var machine=new PcMachine(new(),null);var c=machine.Cpu;var m=machine.Memory;
            c.SetSegment(Segment.Cs,0);c.Eip=0x2000;c.SetRegister(Register.Sp,0x9000);c.Eflags=2;
            new byte[]{0xd9,0x2e,0,0x30,0xd8,0xc0,0x9b,0xf4}.CopyTo(m.Ram,0x2000);m.WriteWord(0x3000,0x37e);
            machine.RunSteps(3);Check(c.WaitingForNumericError && c.Eip==0x2006 && c.Instructions==2);
            bool called=false;machine.Scheduler.ScheduleAfter(1000,()=>called=true);
            machine.RunSteps(20,machine.Scheduler.Now+2000);Check(called && c.Instructions==2 && machine.Fault is null);
            m.WriteWord(0x75*4,0x4000);m.WriteWord(0x75*4+2,0);
            new byte[]{0xe6,0xf0,0xd9,0xd0,0xdb,0xe2,0xb0,0x20,0xe6,0xa0,0xe6,0x20,0xcf}.CopyTo(m.Ram,0x4000);
            machine.Ports.Write(0x21,0xfb,1);machine.Ports.Write(0xa1,0xdf,1);c.Eflags|=0x200;
            machine.RunSteps(20);Check(c.Halted && c.Eip==0x2008 && !c.FpuErrorAsserted && !machine.FpuError.InterruptAsserted && !machine.Pic.HasPending);
            Check(machine.FpuError.Acknowledgements==1 && machine.FpuError.ErrorEdges==1 && machine.Fault is null);
        });
    }
}
