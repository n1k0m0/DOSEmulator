// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Actual native consecutive stores compare every CPU/RAM byte and independent ordered device data with ordinary-RAM execution.</summary>
internal static class NativeFpuContinuationChecks
{
    private const uint Target=0x110000;
    /// <summary>An explicit own parameter-latch peer; pure admission cannot change its observed byte stream.</summary>
    private sealed class Sink(int size,bool allow):IMemoryDevice,IImmediateMemoryWrite
    {
        public readonly byte[] Bytes=new byte[size];
        public readonly List<uint> Writes=[];
        /// <summary>Rejects an unexpected destination read.</summary>
        public byte ReadByte(uint offset)=>throw new Exception("FSTP read a parameter latch");
        /// <summary>Records every actual ordered byte once and rejects an out-of-range side effect.</summary>
        public void WriteByte(uint offset,byte value){if(offset>=size)throw new Exception("FSTP extent differs");Bytes[offset]=value;Writes.Add(offset);}
        /// <summary>Admits only the complete declared DWORD extent, without observing or changing device data.</summary>
        public bool IsImmediateDwordWrite(uint address)=>allow&&address>=Target&&(ulong)address+4<=Target+(uint)size;
    }
    /// <summary>Seeds a real protected32 VP and executes a complete contiguous store sequence on either genuine memory path.</summary>
    private static (WhpxCpuState Cpu,byte[] Ram,byte[] Data,int Mmio,long Assisted) Execute(bool device,int count,int top,bool allow,bool pending,bool changedShape)
    {
        var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();var pic=new Pic8259();var sink=new Sink(count*4,allow);
        if(device)memory.Map(Target,(uint)(count*4),sink,"Own explicitly isolated FSTP latches");
        var code=new List<byte>();for(int i=0;i<count;i++)code.AddRange(changedShape&&i==1?[0xd9,0x9f,(byte)(i*4),0,0,0]:[0xd9,0x5f,(byte)(i*4)]);
        code.Add(0xf4);code.AddRange(Enumerable.Repeat((byte)0x90,12));code.ToArray().CopyTo(memory.Ram,0x1000);memory.Ram.AsSpan((int)Target,count*4).Fill(0xcc);
        using var native=new WhpxMappedMachine(memory,new(),pic,clock,WhpxCpuContract.IntelP55cMmxSubset,233_000_000);
        native.SetEntry(0,0x1000);var initial=native.CaptureCpuState();
        var registers=initial.Registers.Select(r=>r.Name switch
        {
            7=>r with{Low=Target},0x10=>r with{Low=0x1000},0x11=>r with{Low=2},0x1c=>r with{Low=0x60000031},
            19=>r with{Low=0,High=0x409b0008ffffffff},18 or 20 or 21 or 22 or 23=>r with{Low=0,High=0x40930010ffffffff},
            >=0x1010 and <=0x1017=>r with{Low=Float80Math.FromInteger((int)(r.Name-0x1010+1)).Significand,High=Float80Math.FromInteger((int)(r.Name-0x1010+1)).SignExponent},
            0x1018=>r with{Low=255UL<<32|(ulong)(uint)top<<27|0x37f,High=0},0x1019=>r with{Low=0},_=>r
        }).ToArray();
        native.RestoreCpuState(initial with{Registers=registers,Halted=false});if(pending){pic.Write(0x21,0xfe,1);pic.SetLine(0,true);}
        var run=native.RunUntilHalt(TimeSpan.FromSeconds(3));var cpu=native.CaptureCpuState();
        var emulator=typeof(WhpxMappedMachine).GetField("_emulator",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(native)!;
        long assisted=(long)emulator.GetType().GetProperty("AdditionalFpuStores",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(emulator)!;
        if(device&&!sink.Writes.SequenceEqual(Enumerable.Range(0,count*4).Select(i=>(uint)i)))throw new Exception("Assisted FSTP omitted or reordered an actual byte");
        return(cpu,memory.Ram.ToArray(),device?sink.Bytes:memory.Ram.AsSpan((int)Target,count*4).ToArray(),run.MmioExits,assisted);
    }
    /// <summary>Checks all TOPs, short/full stacks, opaque peers, pending PIC lines and a changed next instruction form.</summary>
    public static void Run()
    {
        int cases=0;
        foreach(int count in new[]{2,4,8})for(int top=0;top<8;top++)foreach(int policy in new[]{0,1,2,3})
        {
            bool allow=policy!=1,pending=policy==2,shape=policy==3;
            var a=Execute(false,count,top,allow,pending,shape);var b=Execute(true,count,top,allow,pending,shape);
            if(JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu)||!a.Data.AsSpan().SequenceEqual(b.Data))throw new Exception($"Assisted full native FPU differs {count}/{top}/{policy}");
            for(int i=0;i<count;i++)if(BinaryPrimitives.ReadUInt32LittleEndian(b.Data.AsSpan(i*4,4))!=BitConverter.SingleToUInt32Bits(i+1))throw new Exception("Assisted independent IEEE data differs");
            a.Ram.AsSpan((int)Target,count*4).Fill(0xcc);if(!a.Ram.AsSpan().SequenceEqual(b.Ram))throw new Exception("Assisted whole 2-MiB RAM differs");
            if(!allow||pending){if(b.Assisted!=0||b.Mmio!=count)throw new Exception($"An opaque/pending context continued {count}/{top}/{policy}: assisted={b.Assisted}, exits={b.Mmio}");}
            else if(!shape&&b.Assisted!=count-1)throw new Exception($"Consecutive original stores were not actually admitted: {b.Assisted}/{count}");
            cases++;
        }
        Console.WriteLine($"PASS: native consecutive FSTP {cases} whole CPU/2-MiB RAM/ordered-byte comparisons; real additional stores counted, opaque/PIC/changed-code forms retain scalar execution.");
    }
}
