// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class FloppyDmaBoundaryTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "Floppy DMA boundary assertion failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Builds an isolated machine fixture with the specified guest code and optional identity paging.</summary>
    private static PcMachine Machine(ushort ebda, out string image, out string cow)
    {
        string root=Path.Combine(Environment.CurrentDirectory,"artifacts","tests","fdc-boundary-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);image=Path.Combine(root,"base.img");cow=Path.Combine(root,"private.cow");
        byte[] bytes=new byte[1474560];bytes.AsSpan().Fill(0x39);File.WriteAllBytes(image,bytes);
        var m=new PcMachine(new(){FloppyDriveA="1440k"},new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(),"own boot"),new CopyOnWriteDisk(image,cow));
        m.RunSteps(100000);Check(m.Fault is null && m.Cpu.Halted);
        m.Memory.Ram.AsSpan(0x9fc00,1024).CopyTo(m.Memory.Ram.AsSpan(ebda<<4));
        m.Memory.Ram.AsSpan(0x9fc00,1024).Fill(0xa5);m.Memory.WriteWord(0x40e,ebda);
        return m;
    }
    /// <summary>Executes the controlled guest service/instruction path used by the surrounding regression.</summary>
    private static byte Call(PcMachine m, ushort ax)
    {
        m.Cpu.SetSegment(Segment.Cs,0);m.Cpu.Eip=0x1000;m.Memory.WriteByte(0x1000,0xf4);
        m.Cpu.SetRegister(Register.Ax,ax);m.Cpu.EnterRealModeInterrupt(0x13);m.RunSteps(5_000_000);
        Check(m.Fault is null && m.Cpu.Halted,m.Fault??"BIOS did not return");
        byte status=(byte)(m.Cpu.GetRegister(Register.Ax)>>8);
        Check(((m.Cpu.Eflags&1)!=0)==(status!=0) && m.Memory.ReadByte(0x441)==status);
        return status;
    }
    /// <summary>Registers the named floppy dma boundary regression cases with the common runner.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(ushort ebda in new ushort[]{0x1fc0,0x1fc1,0x1fd0,0x1fdf,0x1fe0})
            foreach(byte function in new byte[]{2,3,4})
                test($"BIOS AH{function:X2} relocated EBDA {ebda:X4} checks DMA2 physical 64K limit",()=>
                {
                    using var m=Machine(ebda,out string image,out _);
                    m.Cpu.SetSegment(Segment.Es,0x5000);m.Cpu.SetRegister(Register.Bx,0xff00);
                    m.Cpu.SetRegister(Register.Cx,1);m.Cpu.SetRegister(Register.Dx,0);
                    m.Memory.Ram.AsSpan(0x5ff00,1024).Fill(0x87);
                    byte[] before=m.Memory.Ram.AsSpan(0x10000,0x10400).ToArray();
                    var writes=new List<ushort>();m.Ports.Accessed+=(p,_,_,write)=>{if(write)writes.Add(p);};
                    bool safe=ebda is 0x1fc0 or 0x1fe0;
                    Check(Call(m,(ushort)(function<<8|2))==(safe?0:9));
                    if(!safe)
                    {
                        Check(m.Floppy.ReadSectorCount==0 && m.Floppy.WrittenSectorCount==0);
                        Check(!writes.Any(p=>p is 4 or 5 or 0x81 or 0x3f5));
                        Check(m.Memory.Ram.AsSpan(0x10000,0x10400).SequenceEqual(before));
                        Check(m.Memory.Ram.AsSpan(0x5ff00,1024).ToArray().All(b=>b==0x87));
                    }
                    else
                    {
                        Check(function==3?m.Floppy.WrittenSectorCount==2:m.Floppy.ReadSectorCount==2);
                        Check(m.Memory.Ram.AsSpan(0x5ff00,1024).ToArray().All(b=>b==(function==2?0x39:0x87)));
                        Check(m.Dma.IsTerminalCount(2));
                    }
                    Check(m.Memory.Ram.AsSpan(0x9fc00,1024).ToArray().All(b=>b==0xa5));
                    Check(File.ReadAllBytes(image).All(b=>b==0x39));
                });
        foreach(var (ebda,count,safe) in new (ushort,int,bool)[]{(0x1fc0,18,true),(0x1fd0,18,true),(0x1fdb,18,true),(0x1fdc,18,false),(0x1fdf,4,true),(0x1fdf,5,false),(0x1fe0,18,true)})
            test($"BIOS AH05 relocated EBDA {ebda:X4} validates actual {count*4}-byte CHRN DMA length",()=>
            {
                using var m=Machine(ebda,out string image,out string cow);
                new byte[]{0xdf,2,0x25,2,(byte)count,0x1b,0xff,0x57,0xa7,15,8}.CopyTo(m.Memory.Ram,0x3000);
                m.Memory.WriteWord(0x78,0x3000);m.Memory.WriteWord(0x7a,0);
                for(int i=0;i<count;i++)new byte[]{2,1,(byte)(i+1),2}.CopyTo(m.Memory.Ram,0x5fffe+i*4);
                m.Cpu.SetSegment(Segment.Es,0x5000);m.Cpu.SetRegister(Register.Bx,0xfffe);
                m.Cpu.SetRegister(Register.Cx,0x02ff);m.Cpu.SetRegister(Register.Dx,0x100);
                byte[] before=m.Memory.Ram.AsSpan(0x10000,0x10400).ToArray();
                var writes=new List<ushort>();m.Ports.Accessed+=(p,_,_,write)=>{if(write)writes.Add(p);};
                Check(Call(m,0x0512)==(safe?0:9));
                Check(m.Floppy.WrittenSectorCount==(safe?count:0));
                if(!safe)
                {
                    Check(!writes.Any(p=>p is 4 or 5 or 0x81 or 0x3f5));
                    Check(m.Memory.Ram.AsSpan(0x10000,0x10400).SequenceEqual(before));
                }
                Check(m.Memory.Ram.AsSpan(0x9fc00,1024).ToArray().All(b=>b==0xa5));
                Check(File.ReadAllBytes(image).All(b=>b==0x39));
                // Re-open the real journal and check both formatted and adjacent sectors.
                if(safe)
                {
                    m.Dispose();using var d=new CopyOnWriteDisk(image,cow);byte[] sector=new byte[512];
                    for(int i=-1;i<=count;i++){d.ReadSector(90+i,sector);Check(sector.All(b=>b==(i>=0&&i<count?0xa7:0x39)));}
                }
            });
    }
}
