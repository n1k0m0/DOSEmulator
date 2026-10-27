// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Runtime;
using DOSEmulator.Cpu.X86;

internal static class BiosBlockMoveTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message="BIOS block move invariant failed") { if(!value)throw new Exception(message); }
    /// <summary>Creates this regression's isolated CPU/device fixture with explicit parameters and private data.</summary>
    private static PcMachine Create(ushort count=2048, uint source=0x30000, uint destination=0x120000)
    {
        var m=PcMachine.CreateDemo();m.RunSteps(100000);Check(m.Fault is null);
        m.Cpu.SetSegment(Segment.Cs,0);m.Cpu.Eip=0x1000;m.Memory.WriteByte(0x1000,0xf4);
        m.Cpu.SetSegment(Segment.Es,0x2000);m.Cpu.SetRegister(Register.Si,0xabcd0100);m.Cpu.SetRegister(Register.Cx,0x12340000u|count);
        m.Cpu.SetRegister(Register.Ax,0x5678875a);m.Cpu.SetRegister(Register.Dx,0xdeadbeef);m.Cpu.SetRegister(Register.Di,0xc0de1234);
        m.Cpu.SetSegment(Segment.Fs,0x987);m.Cpu.SetSegment(Segment.Gs,0x876);
        m.Cpu.Eflags=0x603; // Preserve original IF/DF; success clears only CF.
        foreach((uint address,uint basis) in new[]{(0x20110u,source),(0x20118u,destination)})
        {
            m.Memory.WriteDword(address,(count==0?0xffffu:(uint)count*2-1)|(basis<<16));
            m.Memory.WriteDword(address+4,(basis>>16&255)|0x9200|(basis&0xff000000));
        }
        return m;
    }
    /// <summary>Executes the controlled guest service/instruction path used by the surrounding regression.</summary>
    private static void Call(PcMachine m)
    {
        m.Cpu.EnterRealModeInterrupt(0x15);
        // Stop at the caller's first HLT, before the restored IF allows a later
        // periodic IRQ to wake it. No disk or DMA operation is used by AH87.
        for(int i=0;i<100000 && !m.Cpu.Halted;i++)m.Cpu.Step();
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position=="0000:00001001",m.Fault??m.Cpu.Position);
    }
    /// <summary>Registers the named bios block move regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(ushort relocated in new ushort[]{0x0e92,0x8000})
            test($"BIOS memory services follow relocated EBDA {relocated:X4} and updated conventional size",()=>
            {
                using var m=Create(2);uint basis=(uint)relocated<<4;
                m.Memory.Ram.AsSpan(0x9fc00,1024).CopyTo(m.Memory.Ram.AsSpan((int)basis));
                m.Memory.Ram.AsSpan(0x9fc00,1024).Fill(0xa5);m.Memory.WriteWord(0x40e,relocated);m.Memory.WriteWord(0x413,640);
                m.Memory.WriteDword(0x30000,0x12345678);Call(m);
                Check(m.Memory.ReadDword(0x120000)==0x12345678 && m.Memory.Ram.AsSpan(0x9fc00,1024).ToArray().All(b=>b==0xa5));
                m.Cpu.Eip=0x1000;m.Cpu.SetRegister(Register.Ax,0xc100);Call(m);Check(m.Cpu.GetSegment(Segment.Es)==relocated);
                m.Cpu.Eip=0x1000;m.Cpu.EnterHardwareInterrupt(0x12);for(int i=0;i<1000 && !m.Cpu.Halted;i++)m.Cpu.Step();
                Check((m.Cpu.GetRegister(Register.Ax)&0xffff)==640);
            });
        test("reference floppy IRQ6 acknowledges the PIC and records completion without changing registers",()=>
        {
            using var m=Create();m.Cpu.Eflags=0x202;m.Cpu.SetRegister(Register.Ax,0xdeadbeef);m.Cpu.SetSegment(Segment.Ds,0x1234);
            m.Pic.Write(0x21,0xbf,1);m.Pic.SetLine(6,true);Check(m.Pic.Acknowledge()==0x0e);
            m.Cpu.EnterHardwareInterrupt(0x0e);for(int i=0;i<1000 && !m.Cpu.Halted;i++)m.Cpu.Step();
            m.Pic.Write(0x20,0x0b,1);
            Check(m.Cpu.Halted && (m.Memory.ReadByte(0x43e)&0x80)!=0 && m.Pic.Read(0x20,1)==0);
            Check(m.Cpu.GetRegister(Register.Ax)==0xdeadbeef && m.Cpu.GetSegment(Segment.Ds)==0x1234 && m.Cpu.Eflags==0x202);
        });
        foreach(ushort count in new ushort[]{0,1,2048,0x8000})
        foreach(bool a20 in new[]{false,true})
            test($"BIOS AH87 copies {count} words through PE and restores A20 {(a20?1:0)}, tables and registers",()=>
            {
                using var m=Create(count);byte[] expected=Enumerable.Range(0,count*2).Select(i=>(byte)(i*37+0x5a)).ToArray();
                expected.CopyTo(m.Memory.Ram,0x30000);m.Memory.Ram.AsSpan(0x120000,count*2).Fill(0xa5);
                m.Ports.Write(0x92,a20?2u:0u,1);Check(m.Memory.A20Enabled==a20);
                var cpu=m.Cpu;var gdtr=cpu.Gdtr;var idtr=cpu.Idtr;uint cr0=cpu.Cr0,sp=cpu.GetRegister(Register.Sp);
                byte[] input=m.Memory.Ram.AsSpan(0x20100,48).ToArray();Call(m);
                Check(m.Memory.Ram.AsSpan(0x120000,count*2).SequenceEqual(expected));
                Check(m.Memory.Ram.AsSpan(0x20100,48).SequenceEqual(input) && m.Memory.A20Enabled==a20);
                Check(cpu.Gdtr==gdtr && cpu.Idtr==idtr && cpu.Cr0==cr0 && !cpu.ProtectedMode && !cpu.PagingEnabled);
                Check(cpu.GetRegister(Register.Ax)==0x5678005a && cpu.GetRegister(Register.Cx)==(0x12340000u|count));
                Check(cpu.GetRegister(Register.Si)==0xabcd0100 && cpu.GetRegister(Register.Di)==0xc0de1234 && cpu.GetRegister(Register.Dx)==0xdeadbeef);
                Check(cpu.GetRegister(Register.Sp)==sp && cpu.GetSegment(Segment.Es)==0x2000 && cpu.GetSegment(Segment.Fs)==0x987 && cpu.GetSegment(Segment.Gs)==0x876);
                Check(cpu.GetSegmentCache(Segment.Es).Limit==0xffff && cpu.GetSegmentCache(Segment.Ds).Limit==0xffff);
                Check(cpu.Eflags==0x602 && cpu.ProtectedModeEntries==(count==0?0:1) && cpu.ProtectedModeExits==cpu.ProtectedModeEntries);
            });
        test("BIOS AH87 accepts a read-only source and a granular destination descriptor",()=>
        {
            using var m=Create(2,0x130000,0x140000);m.Memory.WriteByte(0x20115,0x90);m.Memory.WriteByte(0x2011e,0x80);
            m.Memory.WriteDword(0x130000,0x12345678);Call(m);Check(m.Memory.ReadDword(0x140000)==0x12345678 && (m.Cpu.Eflags&1)==0);
        });
        foreach(string reason in new[]{"count","table-boundary","source-limit","destination-limit","source-not-present","destination-readonly","source-expand-down"})
            test($"BIOS AH87 rejects {reason} before entering PE or modifying data",()=>
            {
                using var m=Create();
                switch(reason)
                {
                    case "count":m.Cpu.SetRegister(Register.Cx,0x8001);break;
                    case "table-boundary":m.Cpu.SetRegister(Register.Si,0xffd1);break;
                    case "source-limit":m.Memory.WriteWord(0x20110,4094);break;
                    case "destination-limit":m.Memory.WriteWord(0x20118,4094);break;
                    case "source-not-present":m.Memory.WriteByte(0x20115,0x12);break;
                    case "destination-readonly":m.Memory.WriteByte(0x2011d,0x90);break;
                    default:m.Memory.WriteByte(0x20115,0x96);break;
                }
                m.Memory.WriteDword(0x120000,0xdeadbeef);Call(m);
                Check((m.Cpu.Eflags&1)!=0 && (m.Cpu.GetRegister(Register.Ax)&0xffff)==0x025a && m.Cpu.ProtectedModeEntries==0);
                Check(m.Memory.ReadDword(0x120000)==0xdeadbeef);
            });
    }
}
