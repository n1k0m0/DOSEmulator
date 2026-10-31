// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Independent bus/VGA comparisons plus exact scalar CPU device-checkpoint and rejection controls.</summary>
internal static class SimpleStoreAndRamTests
{
    /// <summary>Requires a separate scalar-device result or independently defined decode fact.</summary>
    private static void Need(bool value,string why){if(!value)throw new Exception(why);}
    /// <summary>Serializes all VGA fields, including complete planes, latches, revisions and register state.</summary>
    private static string State(VgaAdapter video)=>JsonSerializer.Serialize(video.SaveState(),StateValues.Json);
    /// <summary>Programs the real indexed port pair without private register mutation.</summary>
    private static void Indexed(VgaAdapter video,ushort port,int index,int value)
    {video.Write(port,(uint)index,1);video.Write((ushort)(port+1),(uint)value,1);}

    /// <summary>An arbitrary MMIO peer offers no acceleration and must never be called by a rejected plan.</summary>
    private sealed class Peer : IMemoryDevice
    {
        public int Calls;
        /// <summary>Records a real slow-bus read only; proof rejection must leave this count zero.</summary>
        public byte ReadByte(uint offset){Calls++;return 0x55;}
        /// <summary>Records a real slow-bus store only; proof rejection must leave this count zero.</summary>
        public void WriteByte(uint offset,byte value){Calls++;}
    }

    /// <summary>An explicitly cooperating negative peer whose storage or revision aliases actual code backing.</summary>
    private sealed class AliasedPeer(byte[] code,bool revisionAlias) : ISimpleByteWriteDevice
    {
        private readonly byte[] target=new byte[1];
        private readonly long[] revision=new long[1];
        /// <summary>Returns a fixed ordinary read value; admission must never need a device read.</summary>
        public byte ReadByte(uint offset)=>0x55;
        /// <summary>Retains the exact modeled fallback side effects, although the alias test only checks pure refusal.</summary>
        public void WriteByte(uint offset,byte value)
        {TryPlanSimpleByteWrite(offset,out var plan);plan.Store(value);}
        /// <summary>Offers a valid one-store shape, with one deliberately unsafe shared backing range.</summary>
        public bool TryPlanSimpleByteWrite(uint offset,out SimpleByteWritePlan plan)
        {
            plan=revisionAlias?new(target.AsSpan(),MemoryMarshal.Cast<byte,long>(code.AsSpan(0x2000,8))):
                new(code.AsSpan(0x2000,1),revision.AsSpan());return true;
        }
    }

    private sealed class VideoRig
    {
        public readonly VgaAdapter Video;
        public readonly MemoryBus Memory=new(2*1024*1024);
        public readonly S3Trio64? Card;
        /// <summary>Builds one ordinary fixed aperture with the actual VGA, ET4000 or enabled S3 target gates.</summary>
        public VideoRig(int model)
        {
            Video=new(new DeviceScheduler(),model==1,model==2);
            if(model==2)
            {
                Card=new(Memory,Video);Card.WriteConfiguration(4,3,2);
                Card.Write(0x46e8,0x10,1);Card.Write(0x102,1,1);Card.Write(0x46e8,8,1);
            }
            Memory.Map(0xa0000,0x20000,(IMemoryDevice?)Card??Video,"Own VGA");
            Video.Write(0x3c2,0x63,1);Indexed(Video,0x3ce,6,5);
            Indexed(Video,0x3ce,1,0);Indexed(Video,0x3ce,3,0);Indexed(Video,0x3ce,5,0);Indexed(Video,0x3ce,8,255);
        }
    }

    /// <summary>Registers whole-device comparisons and proof-purity controls rather than mirroring plan implementation.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(bool aliasValue in new[]{false,true})
        {
            bool revisionAlias=aliasValue;
            test($"SimpleVGA rejects cooperating physical code alias in {(revisionAlias?"revision":"storage")}",()=>
            {
                var a=DecodedBatchTests.Make(2,[0xa2,0,0x70,0,0]);var b=DecodedBatchTests.Make(2,[0xa2,0,0x70,0,0]);
                foreach(var rig in new[]{a,b})rig.Memory.Map(0x7000,4096,new AliasedPeer(rig.Memory.Ram,revisionAlias),"Own alias negative");
                Need(b.Cpu.TryRunDecodedBatch(64)==0,"A cooperating store could modify retained code.");
                DecodedBatchTests.Equal(a,b);
            });
        }
        test("SinglePage DRAM exact A20, bounds, mapping, decode and hole proofs",()=>
        {
            foreach(bool a20 in new[]{false,true})
            for(int variant=0;variant<4;variant++)
            {
                var memory=new MemoryBus(4*1024*1024){A20Enabled=a20};var peer=new Peer();
                if(variant==1)memory.Map(0x2345,3,peer,"Own partial page");
                if(variant==2)memory.SetDecodedRamBytes(3*1024*1024);
                if(variant==3)memory.SetRamHole(0x100000,0x10000);
                foreach(uint address in new uint[]{0,1,0xfff,0x1000,0x2000,0x2345,0xfffff,0x100000,0x100fff,
                    0x10ffff,0x110000,0x1fffff,0x200000,0x2fffff,0x3ff000,0x400000,uint.MaxValue})
                foreach(int length in new[]{int.MinValue,-1,0,1,2,4,4095,4096,4097,int.MaxValue})
                {
                    bool general=memory.TryGetRamSpan(address,length,out var original);
                    bool actual=memory.TryGetSinglePageRamSpan(address,length,out var span);
                    bool expected=length>0 && length<=4096 && (address&4095)+(uint)length<=4096 && general;
                    Need(actual==expected,$"RAM page proof differs at {address:X8}/{length}/{a20}/{variant}");
                    if(actual)Need(span.SequenceEqual(original) && span.Overlaps(original,out int offset) && offset==0,"RAM byte backing differs.");
                }
                Need(peer.Calls==0,"A proof touched MMIO.");
            }
        });
        foreach(int modelValue in new[]{0,1,2})
        {
            int model=modelValue;
            test($"SimpleVGA exact planes, latch/revision state and current addressing model{model}",()=>
            {
                var a=new VideoRig(model);var b=new VideoRig(model);
                foreach(int addressing in new[]{2,6,14})
                foreach(int mask in new[]{0,1,2,4,8,3,5,15})
                {
                    Indexed(a.Video,0x3c4,4,addressing);Indexed(b.Video,0x3c4,4,addressing);
                    Indexed(a.Video,0x3c4,2,mask);Indexed(b.Video,0x3c4,2,mask);
                    foreach(uint offset in new uint[]{0,1,2,3,0xfff,0x1000,0x7fff,0xffff})
                    foreach(byte value in new byte[]{0,0x69,0xff,0xff,0})
                    {
                        uint physical=0xa0000+offset;int selected=mask;
                        if((addressing&8)!=0)selected&=1<<(int)(offset&3);
                        else if((addressing&4)==0)selected&=(offset&1)==0?5:10;
                        bool expected=BitOperations.IsPow2((uint)selected);
                        Need(b.Memory.TryPlanSimpleByteWrite(physical,out var plan)==expected,"Current plane decode proof differs.");
                        if(expected){a.Memory.WriteByte(physical,value);plan.Store(value);}
                    }
                    Need(State(a.Video)==State(b.Video),"Complete VGA planes/latches/revision differ.");
                }
                string before=State(b.Video);
                Need(!b.Memory.TryPlanSimpleByteWrite(0x90000,out _),"Unmapped byte accepted.");
                Need(!b.Memory.TryPlanSimpleByteWrite(0xb0000,out _),"Inactive aperture accepted.");
                Need(State(b.Video)==before,"Rejected aperture mutated VGA state.");
            });
            test($"SimpleVGA rejects every non-simple raster/mask/enable mode model{model}",()=>
            {
                var rig=new VideoRig(model);Indexed(rig.Video,0x3c4,4,6);Indexed(rig.Video,0x3c4,2,1);
                foreach((int register,int value) in new[]{(1,1),(3,1),(3,8),(3,16),(3,24),(5,1),(5,2),(5,3),(8,0),(8,0x7f)})
                {
                    Indexed(rig.Video,0x3ce,register,value);string before=State(rig.Video);
                    Need(!rig.Memory.TryPlanSimpleByteWrite(0xa0000,out _),"Non-simple write was accepted.");
                    Need(State(rig.Video)==before,"Rejected mode changed state.");
                    Indexed(rig.Video,0x3ce,register,register==8?255:0);
                }
                rig.Video.Write(0x3c2,0x61,1);Need(!rig.Memory.TryPlanSimpleByteWrite(0xa0000,out _),"Disabled VGA aperture accepted.");
                if(rig.Card is not null)
                {
                    rig.Video.Write(0x3c2,0x63,1);rig.Card.WriteConfiguration(4,1,2);
                    Need(!rig.Memory.TryPlanSimpleByteWrite(0xa0000,out _),"Disabled PCI memory accepted.");
                }
            });
        }
        foreach(var modelValue in new[]{X86CpuModel.Intel486Dx,X86CpuModel.IntelP54cB5Subset})
        foreach(int modeValue in new[]{2,4})
        {
            var model=modelValue;int mode=modeValue;
            test($"SimpleVGA complete scalar CPU checkpoints and future store {model} mode{mode}",()=>
            {
                byte[] code=[0x88,0x07,0x47,0x49,0x90,0x90,0x75,0xf8,0xa2,4,0,0x0a,0];
                var a=DecodedBatchTests.Make(mode,code,model);var b=DecodedBatchTests.Make(mode,code,model);
                var av=new VgaAdapter();var bv=new VgaAdapter();
                foreach(var (rig,video) in new[]{(a,av),(b,bv)})
                {
                    video.Write(0x3c2,0x63,1);Indexed(video,0x3ce,6,5);Indexed(video,0x3c4,4,6);Indexed(video,0x3c4,2,1);
                    rig.Memory.Map(0xa0000,0x20000,video,"Own VGA");
                    uint entry=rig.Cpu.Eip;uint scratch=rig.Cpu.GetSegmentBase(Segment.Cs)+entry+0x400;
                    new byte[]{0xa2,0,0,0x0a,0}.CopyTo(rig.Memory.Ram,(int)scratch);rig.Cpu.Eip=entry+0x400;rig.Cpu.Step();rig.Cpu.Eip=entry;
                    rig.Cpu.SetRegister(Register.Di,0xa0000);rig.Cpu.SetRegister(Register.Cx,3);rig.Cpu.SetRegister(Register.Ax,0x69);
                }
                long checkpoints=a.Cpu.ControlCheckpointsSaved;
                int actual=b.Cpu.TryRunDecodedBatch(64);Need(actual>=18,"Real VGA loop did not execute.");
                for(int i=0;i<actual;i++)Need(a.Cpu.Step()==4,"Scalar clocks changed.");
                DecodedBatchTests.Equal(a,b);Need(State(av)==State(bv),"Real VGA writes differ.");
                Need(b.Cpu.ControlCheckpointsSaved-checkpoints>=3,"Actual scalar device checkpoints were dropped.");
                int future=b.Cpu.TryRunDecodedBatch(64);Need(future==1,"The actual store following the loop was not admitted.");
                Need(a.Cpu.Step()==4,"Future scalar clocks changed.");DecodedBatchTests.Equal(a,b);
                Need(State(av)==State(bv),"The actual future VGA store differs.");
                Need(b.Cpu.ControlCheckpointsSaved-checkpoints==4,"Exact future device-checkpoint count changed.");
            });
        }
    }
}
