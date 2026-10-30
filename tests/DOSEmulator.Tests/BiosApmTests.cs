// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual firmware calls, protected ABI, physical interrupt wakeup and complete idle-state continuation.</summary>
internal static class BiosApmTests
{
    /// <summary>Requires an independently specified firmware, register, IRQ or complete state result.</summary>
    private static void Need(bool ok,string why="APM firmware invariant") { if(!ok)throw new Exception(why); }
    /// <summary>One actual caller return, including the firmware's carry/error contract.</summary>
    private sealed record Result(uint Ax,uint Bx,uint Cx,uint Dx,uint Si,uint Di,uint Flags);
    /// <summary>Own boot disk and real Windows-board POST, with all endpoints absent.</summary>
    private static PcMachine Create(bool fast=false)
    {
        var profile=new MachineProfile { Id=fast?MachineProfile.Windows95Profile133Id:MachineProfile.Windows95ProfileId,
            Name="Own APM BIOS fixture",Cpu=MachineProfile.P54cCpuId,CpuHz=fast?133000000:100000000,RamMiB=32,
            VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId };
        byte[] disk=new byte[128*512];new byte[]{0xfa,0xf4,0xeb,0xfd}.CopyTo(disk,0);disk[510]=0x55;disk[511]=0xaa;
        var pc=PcMachine.CreateWithDisks(profile,[new MemoryDisk(disk,"Own APM boot fixture"),null,null,null]);
        pc.RunSteps(250000);
        Need(pc.Fault is null && pc.Cpu.Halted && pc.Cpu.GetSegment(Segment.Cs)==0 && pc.Cpu.Eip==0x7c02,pc.Fault??"APM POST/own boot failed.");
        pc.Ports.Write(0x21,0xff,1);pc.Ports.Write(0xa1,0xff,1);return pc;
    }
    /// <summary>Steps through the real owner, without CPU instruction or interrupt observers.</summary>
    private static void Until(PcMachine pc,Func<bool> done)
    {
        for(int i=0;i<20000;i++)
        {
            if(done())return;
            Need(pc.RunSteps(1)==1 && pc.Fault is null && !pc.Cpu.TripleFaulted,pc.Fault??pc.Cpu.Position);
        }
        throw new Exception("APM caller did not return: "+pc.Cpu.Position);
    }
    /// <summary>Reads the returned architectural values at the actual caller boundary.</summary>
    private static Result Returned(PcMachine pc)=>new(pc.Cpu.GetRegister(Register.Ax),pc.Cpu.GetRegister(Register.Bx),
        pc.Cpu.GetRegister(Register.Cx),pc.Cpu.GetRegister(Register.Dx),pc.Cpu.GetRegister(Register.Si),pc.Cpu.GetRegister(Register.Di),pc.Cpu.Eflags);
    /// <summary>Sets only an own caller's initial registers and stack; the BIOS service itself executes guest code.</summary>
    private static void Inputs(PcMachine pc,ushort ax,ushort bx,ushort cx)
    {
        pc.Cpu.SetRegister(Register.Ax,0x12340000u|ax);pc.Cpu.SetRegister(Register.Bx,0x23450000u|bx);
        pc.Cpu.SetRegister(Register.Cx,0x34560000u|cx);pc.Cpu.SetRegister(Register.Dx,0x456789ab);
        pc.Cpu.SetRegister(Register.Si,0x56789abc);pc.Cpu.SetRegister(Register.Di,0x6789abcd);
        pc.Cpu.SetRegister(Register.Bp,0x789abcde);pc.Cpu.Eflags=0x403;
    }
    /// <summary>Enters INT15 through its real POST vector and observes the completed IRET.</summary>
    private static Result Real(PcMachine pc,ushort ax,ushort bx=0,ushort cx=0)
    {
        var cpu=pc.Cpu;cpu.Reset();cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Ss,0);
        cpu.SetSegment(Segment.Ds,0x1234);cpu.SetSegment(Segment.Es,0x2345);cpu.SetRegister(Register.Sp,0x9000);
        Inputs(pc,ax,bx,cx);cpu.Eip=0x1000;new byte[]{0xcd,0x15,0xfa,0xf4}.CopyTo(pc.Memory.Ram,0x1000);
        Until(pc,()=>cpu.GetSegment(Segment.Cs)==0 && cpu.Eip==0x1002);
        Need(cpu.GetRegister(Register.Sp)==0x9000 && cpu.GetRegister(Register.Bp)==0x789abcde &&
            cpu.GetSegment(Segment.Ds)==0x1234 && cpu.GetSegment(Segment.Es)==0x2345,"Real APM caller stack/segments changed.");
        return Returned(pc);
    }
    /// <summary>Checks the independent carry and AH result without interpreting an output register as a host service.</summary>
    private static void Status(Result r,int error=0)
        =>Need((r.Flags&1)==(error==0?0u:1u) && (error==0 || ((r.Ax>>8)&255)==error),$"APM expected error{error:X2};AX={r.Ax:X8},FLAGS={r.Flags:X8}");
    /// <summary>Writes a caller-owned descriptor in its separate GDT, keeping the BDA/EBDA untouched.</summary>
    private static void Descriptor(PcMachine pc,ushort selector,uint basis,uint limit,byte access,byte flags)
    {
        uint at=0x10000u+selector;pc.Memory.WriteDword(at,(limit&65535)|(basis<<16));
        pc.Memory.WriteDword(at+4,((basis>>16)&255)|((uint)access<<8)|(limit&0xf0000)|((uint)flags<<20)|(basis&0xff000000));
    }
    /// <summary>Establishes the actual reported consecutive 16/32-bit code/data selectors through real PE bootstrap instructions.</summary>
    private static uint Protected(PcMachine pc,bool wide)
    {
        Result connect=Real(pc,wide?(ushort)0x5303:(ushort)0x5302);Status(connect);
        uint entry=wide?connect.Bx:connect.Bx&65535;uint data=wide?(connect.Dx&65535)<<4:(connect.Cx&65535)<<4;
        Need((connect.Ax&65535)==0xf000 && entry<0xa000 && (connect.Di&65535)==1024);
        Need(wide?connect.Si==0xa000a000:(connect.Si&65535)==0xa000,"APM code lengths differ.");
        Descriptor(pc,8,0,0xfffff,0x9a,wide?(byte)12:(byte)8);Descriptor(pc,16,0,0xfffff,0x92,wide?(byte)12:(byte)8);
        Descriptor(pc,0x28,0xf0000,0x9fff,0x9a,wide?(byte)4:(byte)0);
        if(wide)Descriptor(pc,0x30,0xf0000,0x9fff,0x9a,0);
        Descriptor(pc,wide?(ushort)0x38:(ushort)0x30,data,1023,0x92,0);
        pc.Memory.WriteWord(0x300,0xff);pc.Memory.WriteDword(0x302,0x10000);
        pc.Memory.WriteWord(0x310,0x7ff);pc.Memory.WriteDword(0x312,0x8000);
        for(uint i=0;i<256;i++) {pc.Memory.WriteDword(0x8000+i*8,0x00086000);pc.Memory.WriteDword(0x8004+i*8,wide?0x8e00u:0x8600u);}
        new byte[]{0x50,0xb0,0x20,0xe6,0x20,0x58,0xcf}.CopyTo(pc.Memory.Ram,0x6000);
        var cpu=pc.Cpu;cpu.Reset();cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Ss,0);cpu.SetRegister(Register.Sp,0x9000);cpu.Eip=0x1000;
        byte[] bootstrap=[0x0f,0x01,0x16,0,3,0x66,0x0f,0x01,0x1e,0x10,3,0x66,0xb8,0x11,0,0,0x60,
            0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];bootstrap.CopyTo(pc.Memory.Ram,0x1000);
        Until(pc,()=>cpu.ProtectedMode && cpu.GetSegment(Segment.Cs)==8 && cpu.Eip==0x2000);
        cpu.SetSegment(Segment.Ss,16);cpu.SetSegment(Segment.Ds,16);cpu.SetSegment(Segment.Es,16);cpu.SetRegister(Register.Sp,0x7000);return entry;
    }
    /// <summary>Calls the returned BIOS entry with the genuine far-call width and the unmodified caller stack.</summary>
    private static uint Prepare(PcMachine pc,bool wide,uint entry,ushort ax,ushort bx=0,ushort cx=0)
    {
        Inputs(pc,ax,bx,cx);pc.Cpu.Eip=0x2000;
        byte[] call=wide?[0x9a,(byte)entry,(byte)(entry>>8),(byte)(entry>>16),(byte)(entry>>24),0x28,0,0xfa,0xf4]
            :[0x9a,(byte)entry,(byte)(entry>>8),0x28,0,0xfa,0xf4];call.CopyTo(pc.Memory.Ram,0x2000);return wide?0x2007u:0x2005u;
    }
    /// <summary>Completes one protected-mode call and checks caller register/segment preservation.</summary>
    private static Result Pm(PcMachine pc,bool wide,uint entry,ushort ax,ushort bx=0,ushort cx=0)
    {
        uint end=Prepare(pc,wide,entry,ax,bx,cx);Until(pc,()=>pc.Cpu.GetSegment(Segment.Cs)==8 && pc.Cpu.Eip==end);
        Need(pc.Cpu.GetRegister(Register.Sp)==0x7000 && pc.Cpu.GetRegister(Register.Bp)==0x789abcde && pc.Cpu.GetSegment(Segment.Ds)==16 && pc.Cpu.GetSegment(Segment.Es)==16,"PM APM caller stack/segments changed.");
        return Returned(pc);
    }
    /// <summary>Requires every CPU/RAM/VRAM/device/scheduler field, including ordinary saved diagnostics.</summary>
    private static void Same(PcMachine a,PcMachine b)
    {
        Need(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram),"APM whole RAM differs.");
        Need(JsonSerializer.Serialize(a.CaptureHardwareState(),StateValues.Json)==JsonSerializer.Serialize(b.CaptureHardwareState(),StateValues.Json),"APM whole serialized hardware differs.");
    }
    /// <summary>Registers normative installation/session errors, real protected calls, physical IRQ wake and strict snapshots.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("APM connection survives actual INT13 verify through the reserved whole bounce sector",()=>
        {
            using var pc=Create();Status(Real(pc,0x5301));Status(Real(pc,0x530e,0,0x0101));
            uint data=(uint)pc.Memory.ReadWord(0x40e)*16;byte[] before=pc.Memory.Ram.AsSpan((int)data+0x180,6).ToArray();
            var cpu=pc.Cpu;cpu.Reset();cpu.SetSegment(Segment.Cs,0);cpu.SetSegment(Segment.Ss,0);
            cpu.SetSegment(Segment.Ds,0);cpu.SetSegment(Segment.Es,0);cpu.SetRegister(Register.Sp,0x9000);cpu.Eip=0x1000;
            cpu.SetRegister(Register.Ax,0x0401);cpu.SetRegister(Register.Cx,1);cpu.SetRegister(Register.Dx,0x80);
            new byte[]{0xcd,0x13,0xfa,0xf4}.CopyTo(pc.Memory.Ram,0x1000);
            Until(pc,()=>cpu.GetSegment(Segment.Cs)==0 && cpu.Eip==0x1002);
            Need((cpu.Eflags&1)==0 && pc.Memory.ReadWord(data+0x3fe)==0xaa55,"Real INT13 bounce verify did not consume the own boot sector.");
            Need(pc.Memory.Ram.AsSpan((int)data+0x180,6).SequenceEqual(before),"Storage bounce overwrote APM state.");
            Status(Real(pc,0x5306));
        });
        test("APM real installation, missing connection, enabled state and exact errors",()=>
        {
            using var pc=Create();var r=Real(pc,0x5300);Status(r);Need((r.Ax&65535)==0x0101 && (r.Bx&65535)==0x504d && (r.Cx&65535)==3);
            Status(Real(pc,0x5300,2),9);Status(Real(pc,0x5305),3);Status(Real(pc,0x5301));
            foreach(ushort f in new ushort[]{0x5301,0x5302,0x5303})Status(Real(pc,f),2);
            Status(Real(pc,0x530e,0,0x0102),0x0a);r=Real(pc,0x530e,0,0x0101);Status(r);Need((r.Ax&65535)==0x0101);
            Status(Real(pc,0x5308,1,0));r=Real(pc,0x5300);Status(r);Need((r.Cx&8)!=0);
            Status(Real(pc,0x5305));Status(Real(pc,0x530f,1,1),1);Status(Real(pc,0x5308,0xffff,1));
            Status(Real(pc,0x530f,1,0));Status(Real(pc,0x5306),0x0b);Status(Real(pc,0x530f,1,1));
            Status(Real(pc,0x530b),0x80);Status(Real(pc,0x5307,1,3),0x60);Status(Real(pc,0x5304));Status(Real(pc,0x5304),3);
            r=Real(pc,0x530a,1);Status(r);Need((r.Bx&65535)==0x01ff && (r.Cx&65535)==0x80ff && (r.Dx&65535)==0xffff);
        });
        foreach(bool wide in new[]{false,true})
        foreach(bool fast in new[]{false,true})
        {
            test($"APM actual protected session ABI wide={wide} fast={fast}",()=>
            {
                using var pc=Create(fast);uint entry=Protected(pc,wide);Status(Pm(pc,wide,entry,0x5306));
                var r=Pm(pc,wide,entry,0x530e,0,0x0101);Status(r);Need((r.Ax&65535)==0x0101);
                Status(Pm(pc,wide,entry,0x5308,1,0));Status(Pm(pc,wide,entry,0x5305));
                Status(Pm(pc,wide,entry,0x5308,1,1));Status(Pm(pc,wide,entry,0x530b),0x80);
                r=Pm(pc,wide,entry,0x530a,1);Status(r);Need((r.Bx&65535)==0x01ff && (r.Cx&65535)==0x80ff);
                Status(Pm(pc,wide,entry,0x5304));Status(Pm(pc,wide,entry,0x5306),3);
            });
            test($"APM actual HLT physical PIT IRQ wakes and strictly resumes wide={wide} fast={fast}",()=>
            {
                using var pc=Create(fast);uint entry=Protected(pc,wide);pc.Ports.Write(0x21,0xfe,1);
                uint end=Prepare(pc,wide,entry,0x5305);Until(pc,()=>pc.Cpu.Halted);
                Need(pc.Cpu.GetSegment(Segment.Cs)==0x28 && pc.Cpu.CanAcceptMaskableInterrupt,"APM idle did not execute its own STI/HLT.");
                string root=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts/tests/apm-idle-"+Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(root);
                try
                {
                    SnapshotStore.Capture(pc,Path.Combine(root,"cut.dossav"));using var restored=SnapshotStore.Load(Path.Combine(root,"cut.dossav"),Path.Combine(root,"resume.dosebranch"));Same(pc,restored);
                    int directAcks=0,resumedAcks=0,directEoi=0,resumedEoi=0;
                    pc.Pic.InterruptAcknowledged+=(irq,vector,spurious)=>{Need(irq==0 && vector==8 && !spurious);directAcks++;};
                    restored.Pic.InterruptAcknowledged+=(irq,vector,spurious)=>{Need(irq==0 && vector==8 && !spurious);resumedAcks++;};
                    pc.Ports.Accessed+=(port,value,width,write)=>{if(write && port==0x20 && value==0x20 && width==1)directEoi++;};
                    restored.Ports.Accessed+=(port,value,width,write)=>{if(write && port==0x20 && value==0x20 && width==1)resumedEoi++;};
                    ulong before=pc.Scheduler.Now;
                    Until(pc,()=>pc.Cpu.GetSegment(Segment.Cs)==8 && pc.Cpu.Eip==end);
                    Until(restored,()=>restored.Cpu.GetSegment(Segment.Cs)==8 && restored.Cpu.Eip==end);Same(pc,restored);
                    Need(pc.Scheduler.Now>before && directAcks==1 && resumedAcks==1 && directEoi==1 && resumedEoi==1,"Actual IRQ0/EOI ledger differs.");
                    Need(pc.Pic.StateChildren.Single(c=>c.Name=="_master").Value.SaveState().Fields["Isr"].GetByte()==0,"Original handler did not clear IRQ0 in-service.");
                    Status(Returned(pc));Need(pc.Cpu.GetRegister(Register.Sp)==0x7000 && (pc.Cpu.Eflags&0x600)==0x400);
                }
                finally
                {
                    string parent=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,"artifacts/tests"));
                    Need(Path.GetDirectoryName(root)==parent && Path.GetFileName(root).StartsWith("apm-idle-",StringComparison.Ordinal));Directory.Delete(root,true);
                }
            });
        }
    }
}
