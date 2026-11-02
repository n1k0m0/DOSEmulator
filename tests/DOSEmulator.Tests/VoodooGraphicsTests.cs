// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual SST-1 bus transfers, independent RGB/depth oracles, command deadlines and complete continuation.</summary>
internal static class VoodooGraphicsTests
{
    private const uint Base = 0xd0000000, Lfb = Base + 0x400000;
    /// <summary>Requires a concrete independent expected value rather than a renderer self-comparison.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    /// <summary>Own isolated PCI/memory/scheduler graph with actual BAR and initialization transactions.</summary>
    internal sealed class Rig
    {
        public readonly MemoryBus Memory = new(2 * 1048576);
        public readonly DeviceScheduler Clock = new();
        public readonly PciConfigurationBus Pci = new();
        public readonly VoodooGraphics Card;
        /// <summary>Registers only the actual SST-1 peer, then performs real PCI writes and original 640x480 setup.</summary>
        public Rig()
        {
            Card = new(Memory, Clock); Memory.MapDynamic(Card); Pci.RegisterFunction(0,6,0,Card);
            Config(0x10, Base); Config(4,2); Config(0x40,3); Setup(Memory);
        }
        /// <summary>Writes the selected DWORD through real mechanism-1 ports.</summary>
        public void Config(int offset,uint value) { Pci.Write(0xcf8,0x80003000u+(uint)offset,4); Pci.Write(0xcfc,value,4); }
        /// <summary>Writes one complete original MMIO DWORD.</summary>
        public void Write(int reg,uint value) => Memory.WriteDword(Base+(uint)reg,value);
        /// <summary>Completes exactly the next scheduled graphics operation.</summary>
        public void Finish() => Clock.AdvanceTo(Card.CompletionNanoseconds);
    }
    /// <summary>Programs geometry, 640-byte-pixel pitch, two color buffers and auxiliary depth storage through real MMIO.</summary>
    private static void Setup(MemoryBus m)
    {
        m.WriteDword(Base+0x210,0); m.WriteDword(Base+0x214,0x20a8); m.WriteDword(Base+0x218,150u<<11);
        m.WriteDword(Base+0x20c,480u<<16|639); m.WriteDword(Base+0x21c,479u<<22);
        m.WriteDword(Base+0x118,640); m.WriteDword(Base+0x11c,480); m.WriteDword(Base+0x110,0x600);
    }
    /// <summary>Sets an independently simple right triangle with constant iterated RGBA and Z.</summary>
    internal static void Triangle(Rig r,uint color=0x00ff0000,uint z=200)
    {
        r.Write(8,0); r.Write(12,0); r.Write(16,32*16); r.Write(20,0); r.Write(24,0); r.Write(28,32*16);
        r.Write(0x20,(color>>16&255)<<12); r.Write(0x24,(color>>8&255)<<12); r.Write(0x28,(color&255)<<12);
        r.Write(0x2c,z<<12); r.Write(0x30,255<<12);
    }
    /// <summary>Registers negative transfer/resource cases and positive draw/texture/scanout/snapshot checks.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        test("Voodoo actual PCI identity BAR sizing and complete memory enable",()=>
        {
            var r=new Rig(); Check(r.Card.ReadConfiguration(0,4)==0x0001121a && r.Card.ReadConfiguration(8,4)==2,"Wrong original SST-1 identity/class");
            r.Config(0x10,uint.MaxValue); Check(r.Card.ReadConfiguration(0x10,4)==0xff000000,"BAR sizing differs");
            r.Config(0x10,0xd3123456); Check(r.Card.BarBase==0xd3000000,"BAR alignment differs");
            r.Config(0x10,0); Check(!r.Card.MemoryEnabled,"Card decoded installed RAM");
            r.Config(0x10,Base); r.Memory.Map(Base+0xfff000,4096,new OpenBusMemory(),"Own fixed priority blocker");
            Check(!r.Card.MemoryEnabled && r.Memory.ReadDword(Base)==uint.MaxValue,"Partial BAR bypassed fixed resource conflict");
        });
        foreach(int width in new[]{1,2}) test($"Voodoo register width {width} cannot execute a command",()=>
        {
            var r=new Rig(); if(width==1) r.Memory.WriteByte(Base+0x124,0); else r.Memory.WriteWord(Base+0x124,0);
            Check(!r.Card.Busy && (width==1?r.Memory.ReadByte(Base):r.Memory.ReadWord(Base))==(width==1?255:65535),"Byte/WORD register access was accepted");
        });
        test("Voodoo wrap/chip-select and initialization write gates",()=>
        {
            var r=new Rig(); r.Write(0x114,0x10); r.Memory.WriteDword(Base+0x4000+0x114,0x40);
            Check(r.Memory.ReadDword(Base+0x114)==0x40,"Register wrap differs");
            r.Memory.WriteDword(Base+0x800+0x114,0x80); Check(r.Memory.ReadDword(Base+0x114)==0x40,"TMU-only chip-select changed FBI");
            r.Config(0x40,2); r.Write(0x214,0x1100); Check(r.Card.OutputEnabled,"Init gate ignored");
        });
        test("Voodoo FASTFILL real deadline masks and exclusive clip rectangle",()=>
        {
            var r=new Rig(); r.Write(0x118,3u<<16|12); r.Write(0x11c,5u<<16|9); r.Write(0x148,0x00ff0000); r.Write(0x130,0x1234); r.Write(0x124,0);
            Check(r.Card.Busy && (r.Memory.ReadDword(Base)&0x280)==0x280 && r.Memory.ReadWord(Lfb+5*2048+6)==0,"Clear completed before deadline");
            r.Clock.AdvanceTo(r.Card.CompletionNanoseconds-1); Check(r.Memory.ReadWord(Lfb+5*2048+6)==0,"Early pixel write"); r.Finish();
            for(int y=0;y<12;y++) for(int x=0;x<16;x++) Check(r.Memory.ReadWord(Lfb+(uint)(y*2048+x*2))==(x>=3&&x<12&&y>=5&&y<9?0xf800:0),"Independent clip/mask oracle failed");
            r.Write(0x114,0x80); Check(r.Memory.ReadWord(Lfb+5*2048+6)==0x1234,"Auxiliary depth clear missing");
        });
        foreach(int format in new[]{0,1,2,4,5,12,13,14,15}) test($"Voodoo real LFB format {format} and read selector",()=>
        {
            var r=new Rig(); r.Write(0x114,(uint)format); uint data=format is 1 or 2 or 13 or 14 ? 0x98767c00u : format is 4 or 5 ? 0xaaff0000u : 0x9876f800u;
            r.Memory.WriteDword(Lfb+2*4,data); r.Write(0x114,0); ushort expected=format==15?(ushort)0:(ushort)0xf800;
            uint readAt=Lfb+(uint)((format is 4 or 5 or 12 or 13 or 14?2:4)*2);
            Check(r.Memory.ReadWord(readAt)==expected,"LFB channel or address conversion differs");
            if(format>=12) { r.Write(0x114,0x80); Check(r.Memory.ReadWord(readAt)==(format==15?(ushort)data:(ushort)(data>>16)),"LFB depth lane differs"); }
        });
        test("Voodoo direct triangle independent pixel coverage and counters",()=>
        {
            var r=new Rig(); Triangle(r); r.Write(0x110,0x600); r.Write(0x80,0); r.Finish(); int expected=0;
            for(int y=0;y<35;y++) for(int x=0;x<35;x++) { bool inside=x+y+1<32; if(inside) expected++; Check(r.Memory.ReadWord(Lfb+(uint)(y*2048+x*2))==(inside?0xf800:0),"Independent triangle edge oracle differs"); }
            Check(expected==496 && r.Memory.ReadDword(Base+0x14c)==496 && r.Memory.ReadDword(Base+0x15c)==496,"Actual pixel counters differ");
            r.Write(0x120,1); r.Finish(); Check(r.Memory.ReadDword(Base+0x15c)==0,"NOP did not clear counters");
        });
        for(int op=0;op<8;op++)
        {
            int selected=op; test($"Voodoo actual Z function {op}",()=>
            {
                var r=new Rig(); r.Write(0x130,300); r.Write(0x124,0); r.Finish(); Triangle(r);
                r.Write(0x110,0x610u|(uint)selected<<5); r.Write(0x80,0); r.Finish(); bool pass=selected is 1 or 3 or 5 or 7;
                Check(r.Memory.ReadWord(Lfb)==(pass?0xf800:0),"Independent depth function result differs"); r.Write(0x114,0x80);
                Check(r.Memory.ReadWord(Lfb)==(pass?200:300),"Depth write mask/commit differs");
            });
        }
        foreach(bool second in new[]{false,true}) test($"Voodoo real TMU upload sampled texel {second}",()=>
        {
            var r=new Rig(); r.Write(0x300,10<<8|1<<12|1<<18); r.Write(0x30c,0x100); r.Memory.WriteDword(Base+0x800000,0x001ff800);
            Check(r.Memory.ReadDword(Base+0x800000)==uint.MaxValue,"Texture write-only aperture leaked backing storage");
            Triangle(r,0x0000ff00); r.Write(0x34,second?1u<<18:0); r.Write(0x104,1u<<27|1); r.Write(0x80,0); r.Finish();
            Check(r.Memory.ReadWord(Lfb)==(second?0x001f:0xf800),"TMU color did not come from actual uploaded DRAM");
        });
        test("Voodoo ordered TMU upload cannot mutate an earlier queued triangle",()=>
        {
            var r=new Rig();r.Write(0x300,10<<8|1<<12|1<<18);r.Memory.WriteDword(Base+0x800000,0xf800f800);
            Triangle(r);r.Write(0x104,1u<<27|1);r.Write(0x80,0);r.Memory.WriteDword(Base+0x800000,0x001f001f);
            r.Finish();Check(r.Memory.ReadWord(Lfb)==0xf800,"Later upload affected an earlier primitive");
            r.Finish();r.Write(0x80,0);r.Finish();Check(r.Memory.ReadWord(Lfb)==0x001f,"Ordered upload did not affect the later primitive");
        });
        test("Voodoo original Glide upload range does not restrict aperture-selected mip levels",()=>
        {
            var r=new Rig();r.Write(0x300,10<<8|1<<12|1<<18);r.Write(0x304,32<<6);r.Write(0x30c,0x100);
            for(int level=0;level<=8;level++)
            {
                uint color=(uint)((level+1)<<11|17);
                r.Memory.WriteDword(Base+0x800000+(uint)(level<<17),color|color<<16);
            }
            Triangle(r);r.Write(0x104,1u<<27|1);
            for(int level=0;level<=8;level++)
            {
                r.Write(0x304,(uint)(level*4|(level*4)<<6));r.Write(0x80,0);r.Finish();
                Check(r.Memory.ReadWord(Lfb)==(ushort)((level+1)<<11|17),"Aperture LOD upload or retained chain base differs");
            }
            r.Write(0x304,32<<6);r.Write(0x54,1u<<19);r.Write(0x80,0);r.Finish();
            Check(r.Memory.ReadWord(Lfb)==(ushort)(2<<11|17),"Variable LOD did not select the mip level from S/T derivatives");
            r.Write(0x54,1u<<18);r.Write(0x304,4u<<12|32u<<6);r.Write(0x80,0);r.Finish();
            Check(r.Memory.ReadWord(Lfb)==(ushort)(2<<11|17),"Signed quarter-step LOD bias was ignored");
        });
        foreach(bool network in new[]{false,true}) test($"Voodoo actual BIOS BAR assignment and INT1A enumeration network {network}",()=>
        {
            var profile=new MachineProfile{Id=MachineProfile.MmxProfile166Id,Cpu=MachineProfile.P55cCpuId,CpuHz=166_000_000,RamMiB=16,VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId,ThreeDAccelerator=MachineProfile.VoodooGraphicsId,
                NetworkAdapters=network?[new(){Model=NetworkAdapterProfile.Rtl8029Pci,IoBase=0x6000,Irq=11,MacAddress="02:44:4F:53:57:01"}]:[]};
            using var pc=new PcMachine(profile,null);pc.RunSteps(300000);
            Check(pc.Board.Post10Count==1&&pc.Voodoo!.BarBase==Base&&pc.Voodoo.MemoryEnabled,"Original BIOS did not assign the real additional BAR");
            Check(pc.Voodoo!.ReadConfiguration(0x3c,1)==11&&pc.PiixIrq!.ReadConfiguration(0x60,1)==11&&
                (pc.PiixIrq.Elcr&0x800)!=0&&(pc.PiixIrq.Elcr&0x20)==0,"SST-1 BIOS IRQ assignment conflicts with ISA SB16 instead of sharing the actual PIRQA/IRQ11 route");
            byte[] code=[0xb8,2,0xb1,0xb9,1,0,0xba,0x1a,0x12,0x31,0xf6,0xcd,0x1a,0xf4];code.CopyTo(pc.Memory.Ram,0x2000);
            pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Cs,0);pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Ss,0);pc.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Sp,0x9000);pc.Cpu.Eip=0x2000;pc.Cpu.Eflags=2;var state=pc.Cpu.SaveState();state.Fields["Halted"]=JsonSerializer.SerializeToElement(false);pc.Cpu.LoadState(state);
            for(int i=0;i<2000&&!pc.Cpu.Halted;i++)pc.RunSteps(1);
            Check(pc.Cpu.Halted&&(pc.Cpu.Eflags&1)==0&&(pc.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx)&65535)==0x30,"Real PCI BIOS did not find actual SST-1 BDF 00:06.0");
            // A fresh real-mode caller executes the actual installed handler, including its ordinary IRET and CF8 restoration.
            void Bios(ushort ax, ushort bx, ushort cx = 0)
            {
                pc.Cpu.Reset(); pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Cs,0); pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Ss,0);
                pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Ds,0xf000); pc.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Es,0x2000);
                pc.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Sp,0x9000); pc.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Ax,ax);
                pc.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Bx,bx); pc.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Cx,cx);
                pc.Cpu.Eip=0x2000;pc.Cpu.Eflags=2;new byte[]{0xcd,0x1a,0xf4}.CopyTo(pc.Memory.Ram,0x2000);
                for(int i=0;i<4000&&!pc.Cpu.Halted;i++)pc.RunSteps(1);
                Check(pc.Cpu.Halted,"PCI BIOS caller did not return");
            }
            pc.Memory.Ram.AsSpan(0x20000,0x180).Fill(0xcd);
            pc.Memory.WriteWord(0x20000,96);pc.Memory.WriteWord(0x20002,0x100);pc.Memory.WriteWord(0x20004,0x2000);
            pc.Ports.Write(0xcf8,0x800030fc,4);Bios(0xb10e,0);
            int length=network?96:64,entry=network?80:48;
            Check((pc.Cpu.Eflags&1)==0&&pc.Cpu.GetRegister(DOSEmulator.Cpu.X86.Register.Bx)==0x800&&pc.Memory.ReadWord(0x20000)==length&&
                pc.Memory.ReadDword(0x20100u+(uint)entry)==0x00603000&&pc.Memory.ReadDword(0x20104u+(uint)entry)==8&&
                pc.Memory.ReadDword(0x20108u+(uint)entry)==0&&pc.Memory.ReadDword(0x2010cu+(uint)entry)==0&&pc.Ports.Read(0xcf8,4)==0x800030fc&&
                pc.Memory.Ram.AsSpan(0x20100+length,16).IndexOfAnyExcept((byte)0xcd)<0,"Actual routing table does not describe SST-1 INTA/PIRQA/IRQ11");
            pc.PiixIrq!.WriteConfiguration(0x60,0x80,1);pc.Ports.Write(0x4d1,0,1);Bios(0xb10f,0x30,0x0a0a);
            Check((pc.Cpu.Eflags&1)!=0&&pc.PiixIrq.ReadConfiguration(0x60,1)==0x80&&pc.PiixIrq.Elcr==0,"Wrong IRQ accepted for SST-1");
            Bios(0xb10f,0x30,0x0b0a);
            Check((pc.Cpu.Eflags&1)==0&&pc.PiixIrq.ReadConfiguration(0x60,1)==11&&pc.PiixIrq.Elcr==0x800&&
                pc.Ports.Read(0xcf8,4)==0x800030fc&&pc.Voodoo.ReadConfiguration(0x3c,1)==11,"Actual SST-1 route assignment or caller configuration changed");
        });
        test("Voodoo synchronized swap status and pass-through return",()=>
        {
            var r=new Rig(); r.Write(0x114,0x10); r.Memory.WriteWord(Lfb,0x07e0); r.Write(0x128,1);
            Check((r.Memory.ReadDword(Base)>>28&7)==1 && (r.Memory.ReadDword(Base)>>10&3)==0,"Pending swap not reported");
            r.Finish(); Check((r.Memory.ReadDword(Base)>>10&3)==1 && (r.Memory.ReadDword(Base)>>28&7)==0,"Swap did not reach retrace");
            r.Write(0x114,0); Check(r.Memory.ReadWord(Lfb)==0x07e0,"Front/back selection did not swap actual storage");
            var buffer=new VgaFrameBuffer(); var first=r.Card.Capture(r.Clock.Now,buffer); Check(first.Width==640&&first.Height==480&&first.ArgbPixels[0]!=0xff000000,"No actual Voodoo scanout");
            r.Card.Capture(r.Clock.Now,buffer); Check(buffer.RenderedFrames==1,"Stable frame was unnecessarily rendered");
            r.Write(0x210,1); Check(!r.Card.OutputEnabled,"VGA pass-through did not return");
        });
        test("Voodoo reset cancels queued completion and retains powered DRAM",()=>
        {
            var r=new Rig(); r.Memory.WriteWord(Lfb,0x1234); Triangle(r); r.Write(0x80,0); r.Card.ApplyPiixHardReset();
            r.Clock.AdvanceTo(100_000_000); Check(!r.Card.Busy && !r.Card.OutputEnabled,"Reset retained pending work");
            r.Config(0x10,Base); r.Config(4,2); r.Config(0x40,3); Setup(r.Memory); Check(r.Memory.ReadWord(Lfb)==0x1234,"Reset cleared powered FBI DRAM");
        });
        test("Voodoo MMX full SnapshotStore continues active command and queued swap",()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"dose-voodoo-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var profile=new MachineProfile{Id=MachineProfile.MmxProfile166Id,Cpu=MachineProfile.P55cCpuId,CpuHz=166_000_000,RamMiB=16,VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId,ThreeDAccelerator=MachineProfile.VoodooGraphicsId};
                using var a=new PcMachine(profile,null); a.Voodoo!.WriteConfiguration(0x10,Base,4); a.Voodoo.WriteConfiguration(4,2,2); a.Voodoo.WriteConfiguration(0x40,3,4); Setup(a.Memory);
                a.Memory.WriteDword(Base+0xb4,0xffc00000); // Actual unused nonfinite S latch survives the queued clear/swap and full PC import.
                a.Memory.WriteDword(Base+0x200,450u<<8|511u<<18);a.Memory.WriteDword(Base+0x210,0x2000);a.Memory.WriteDword(Base+0x300,0xa00);
                a.Memory.WriteDword(Base+0x148,0x000000ff); a.Memory.WriteDword(Base+0x124,0); a.Memory.WriteDword(Base+0x128,1);
                for(uint i=0;i<256;i++)a.Memory.WriteDword(Base+0x800000,i|i<<16);
                for(uint i=0;i<256;i++)a.Memory.WriteDword(Base+0x334+(i&7)*4,0x80000000|i/2<<24|i*0x010101);
                // Actual MOVD leaves an occupied physical MM register in the complete PC snapshot.
                a.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Cs,0); a.Cpu.Eip=0x2000; new byte[]{0x0f,0x6e,0xc0}.CopyTo(a.Memory.Ram,0x2000); a.Cpu.SetRegister(DOSEmulator.Cpu.X86.Register.Ax,0x87654321); a.RunSteps(1);
                SnapshotStore.Capture(a,Path.Combine(root,"active.dossav")); using var b=SnapshotStore.Load(Path.Combine(root,"active.dossav"),Path.Combine(root,"copy.dosebranch"));
                Check(a.Cpu.GetMmxRegister(0)==b.Cpu.GetMmxRegister(0),"Full PC lost active MMX alias");
                foreach(ulong now in new[]{a.Scheduler.Now,a.Voodoo.CompletionNanoseconds-1,a.Voodoo.CompletionNanoseconds,20_000_000UL})
                {
                    a.Scheduler.AdvanceTo(now); b.Scheduler.AdvanceTo(now);
                    Check(JsonSerializer.Serialize(a.CaptureHardwareState(),StateValues.Json)==JsonSerializer.Serialize(b.CaptureHardwareState(),StateValues.Json)&&a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram),"Complete resumed CPU/RAM/DRAM/register/scheduler state differs");
                }
                Check(a.CaptureFrame().ArgbPixels.AsSpan().SequenceEqual(b.CaptureFrame().ArgbPixels),"Resumed final display differs");
            }
            finally { Directory.Delete(root,true); }
        });
    }
}
