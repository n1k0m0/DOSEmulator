// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Own gradient opcodes compare complete actual native CPU/RAM and every ordered SST parameter byte.</summary>
internal static class NativeGlideBatchChecks
{
    private const uint Target=0x110000;
    private sealed class Sink(int size,bool allow):IMemoryDevice,IImmediateMemoryWrite
    {
        internal readonly byte[] Bytes=new byte[size];
        internal readonly List<uint> Writes=[];
        /// <summary>Parameter latches are never read by the admitted instruction family.</summary>
        public byte ReadByte(uint offset)=>throw new Exception("Gradient read a parameter latch");
        /// <summary>Records every actual byte and its sequence without any guest memory, mapping or interrupt effect.</summary>
        public void WriteByte(uint offset,byte value){Bytes[offset]=value;Writes.Add(offset);}
        /// <summary>Only the entire own bounded DWORD storage qualifies for the explicit pure-write contract.</summary>
        public bool IsImmediateDwordWrite(uint address)=>allow&&address>=Target&&(ulong)address+4<=Target+(uint)size;
    }
    /// <summary>Builds own relocatable arithmetic and store instructions; the backward branch really reloads the next source row.</summary>
    private static byte[] Program(bool changed)
    {
        var code=new List<byte>();
        // Appends exactly the own declared instruction bytes, preserving ordering.
        void Add(params byte[] b)=>code.AddRange(b);
        // Emits one little-endian own immediate or displacement.
        void Dword(uint x)=>Add(BitConverter.GetBytes(x));
        Add(0x8b,0x7e,4,0x8b,0x2c,2,0xf7,0xc2,1,0,0,0,0x74,1,0xf4,
            0xd9,4,2,0xd8,0x24,0x1a,0xd9,4,0x1a,0xd8,0x24,0x0a,0xd9,0xc1,0xd8,0x0d);Dword(0x3000);
        Add(0xd9,0xc1,0xd8,0x0d);Dword(0x3004);Add(0xd9,0xcb,0x89,0x2f,0xd8,0x0d);Dword(0x3008);
        Add(0xd9,changed?(byte)0xc9:(byte)0xca,0xd8,0x0d);Dword(0x300c);
        Add(0xd9,0xcb,0xde,0xe9,0xd9,0xca,0xde,0xe1,0xd9,0xc9,0x8b,0x56,8,0x83,0xc6,8,
            0xd9,0x5f,4,0xd9,0x5f,8,0x85,0xd2,0x0f,0x85);Dword(unchecked(0u-(uint)(code.Count+4)));
        Add(0xf4);Add(Enumerable.Repeat((byte)0x90,16).ToArray());return code.ToArray();
    }
    /// <summary>Own vertex and zero-parameter instruction streams keep RAM XCHG and post-setup FPU instructions on actual native execution.</summary>
    private static byte[] ParameterProgram(int family)
    {
        var code=new List<byte>();
        // Appends the own parameter instruction stream without an assembler dependency.
        void Add(params byte[] b)=>code.AddRange(b);
        // Emits one exact little-endian own instruction immediate.
        void Dword(uint x)=>Add(BitConverter.GetBytes(x));
        if(family==1)
        {
            Add(0x8b,0x10,0x8b,0x68,4,0x89,0x97);Dword(0x88);Add(0x89,0xaf);Dword(0x8c);
            Add(0x8b,0x13,0x8b,0x6b,4,0x89,0x97);Dword(0x90);Add(0x89,0xaf);Dword(0x94);
            Add(0x8b,0x11,0x8b,0x69,4,0x89,0x97);Dword(0x98);Add(0x89,0xaf);Dword(0x9c);
            Add(0x8b,0x56,0x14,0x83,0xc6,0x14,0x85,0xd2,0x0f,0x84);Dword(8);
            Add(0xd9,0xee,0xd9,0x1d);Dword(0x3020);Add(0xf4);
        }
        else
        {
            Add(0x8b,0x7e,4,0x8b,0x2c,2,0xf7,0xc2,1,0,0,0,0x74,0x2c,0xf7,0xc2,2,0,0,0,0x74,6,0x87,5);Dword(0x3000);
            Add(0xc7,7,0,0,0,0,0x74,6,0x87,5);Dword(0x3000);
            Add(0x8b,0x56,8,0x83,0xc6,8,0x85,0xd2,0x75);Add(unchecked((byte)-(code.Count+1)));Add(0xf4);
        }
        Add(Enumerable.Repeat((byte)0x90,16).ToArray());return code.ToArray();
    }
    /// <summary>Executes an actual VP with identical ordinary RAM or explicit pure parameter latches.</summary>
    private static (WhpxCpuState Cpu,byte[] Ram,byte[] Data,long Instructions,long Stores,int Exits) Execute(bool device,int count,int top,ushort cw,int policy,bool expandDown=false,bool paging=false,int family=0)
    {
        var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();var pic=new Pic8259();var sink=new Sink(family==1?24:count*(family==2?4:12),policy!=1);
        if(device)memory.Map(Target,(uint)sink.Bytes.Length,sink,"Own gradient latches");
        (family==0?Program(policy==3):ParameterProgram(family)).CopyTo(memory.Ram,0x1000);memory.Ram.AsSpan((int)Target,sink.Bytes.Length).Fill(0xcc);
        if(paging)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x8000),0x9007);
            for(int i=0;i<512;i++)BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x9000+i*4),(uint)i*4096|7);
        }
        float[] constants=[0.17f,-1.33f,2.37f,0.00137f];for(int i=0;i<4;i++)BinaryPrimitives.WriteSingleLittleEndian(memory.Ram.AsSpan(0x3000+i*4),constants[i]);
        for(int i=0;i<count;i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(memory.Ram.AsSpan(0x4040+i*4),1.7f+i*0.13f);
            BinaryPrimitives.WriteSingleLittleEndian(memory.Ram.AsSpan(0x5040+i*4),-2.1f-i*1.19f);
            BinaryPrimitives.WriteSingleLittleEndian(memory.Ram.AsSpan(0x6040+i*4),0.97f-i*0.19f);
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x7004+i*8),Target+(uint)i*12);
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x7008+i*8),i+1<count?0x44+(uint)i*4:0);
        }
        if(family==1)
        {
            float[] data=[1.5f,-2.75f,3f,4.2f,5.5f,-6.75f];for(int i=0;i<6;i++)BinaryPrimitives.WriteSingleLittleEndian(memory.Ram.AsSpan(0x4000+i/2*0x1000+i%2*4),data[i]);
        }
        if(family==2)for(int i=0;i<count;i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x7004+i*8),Target+(uint)i*4);
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan(0x7008+i*8),i+1==count?0:i%2==0?3u:1u);
        }
        using var native=new WhpxMappedMachine(memory,new(),pic,clock,WhpxCpuContract.IntelP55cMmxSubset,233_000_000);
        native.SetEntry(0,0x1000);var initial=native.CaptureCpuState();
        var registers=initial.Registers.Select(r=>r.Name switch
        {
            0=>r with{Low=0x4000},1=>r with{Low=0x6000},2=>r with{Low=family==2?1u:0x40u},3=>r with{Low=0x5000},6=>r with{Low=0x7000},7=>r with{Low=family==1?Target-0x88:Target},
            0x10=>r with{Low=0x1000},0x11=>r with{Low=2},0x1c=>r with{Low=paging?0xe0000031u:0x60000031u},0x1e=>r with{Low=paging?0x8000u:0},
            19=>r with{Low=0,High=0x409b0008ffffffff},21 when expandDown=>r with{Low=0,High=0x4097001000002fff},
            18 or 20 or 21 or 22 or 23=>r with{Low=0,High=0x40930010ffffffff},
            >=0x1010 and <=0x1017=>r with{Low=Float80Math.FromInteger((int)(r.Name-0x1010+1)).Significand,High=Float80Math.FromInteger((int)(r.Name-0x1010+1)).SignExponent},
            0x1018=>r with{Low=(ulong)(uint)top<<27|cw,High=0},0x1019=>r with{Low=0},_=>r
        }).ToArray();native.RestoreCpuState(initial with{Registers=registers,Halted=false});
        if(policy==2){pic.Write(0x21,0xfe,1);pic.SetLine(0,true);}
        var run=native.RunUntilHalt(TimeSpan.FromSeconds(5));var cpu=native.CaptureCpuState();
        var batch=typeof(WhpxMappedMachine).GetField("_glideBatch",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(native)!;
        long Instructions=(long)batch.GetType().GetProperty("Instructions",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(batch)!;
        long Stores=(long)batch.GetType().GetProperty("Stores",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(batch)!;
        if(device&&!sink.Writes.SequenceEqual(Enumerable.Range(0,sink.Bytes.Length).Select(i=>(uint)i)))throw new Exception("Gradient changed actual parameter byte order");
        return(cpu,memory.Ram.ToArray(),device?sink.Bytes:memory.Ram.AsSpan((int)Target,sink.Bytes.Length).ToArray(),Instructions,Stores,run.MmioExits);
    }
    /// <summary>Checks all TOPs/precision/rounding controls, repeated source rows, opaque peers, pending PIC and a live changed opcode form.</summary>
    internal static void Run()
    {
        int cases=0;long instructions=0,stores=0;
        foreach(int family in new[]{0,1,2})foreach(int precision in new[]{0,2,3})for(int rounding=0;rounding<4;rounding++)for(int top=0;top<8;top++)
        foreach(int policy in new[]{0,1,2,3})foreach(int addressing in new[]{0,1,2})
        {
            if(family!=0&&policy==3)continue;
            ushort cw=(ushort)(0x7f|precision<<8|rounding<<10);int count=3;
            var a=Execute(false,count,top,cw,policy,addressing!=0,addressing==2,family);var b=Execute(true,count,top,cw,policy,addressing!=0,addressing==2,family);
            if(JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))
            {
                var differences=a.Cpu.Registers.Zip(b.Cpu.Registers).Where(p=>p.First!=p.Second).Select(p=>$"{p.First.Name:X}: {p.First.Low:X}/{p.First.High:X} vs {p.Second.Low:X}/{p.Second.High:X}");
                throw new Exception($"Gradient whole CPU differs {precision}/{rounding}/{top}/{policy}: "+string.Join("; ",differences));
            }
            if(!a.Data.AsSpan().SequenceEqual(b.Data))throw new Exception($"Gradient actual parameter bytes differ {precision}/{rounding}/{top}/{policy}");
            if(family==1)
            {
                float[] expected=[1.5f,-2.75f,3f,4.2f,5.5f,-6.75f];for(int i=0;i<6;i++)if(BinaryPrimitives.ReadSingleLittleEndian(b.Data.AsSpan(i*4))!=expected[i])throw new Exception("Independent vertex word differs");
            }
            if(family==2&&b.Data.Any(x=>x!=0))throw new Exception("Independent zero latch bytes differ");
            a.Ram.AsSpan((int)Target,a.Data.Length).Fill(0xcc);if(!a.Ram.AsSpan().SequenceEqual(b.Ram))throw new Exception("Gradient whole 2-MiB RAM differs");
            if(policy!=0&&b.Instructions!=0)throw new Exception("Gradient admitted an opaque, pending or changed-code context");
            instructions+=b.Instructions;stores+=b.Stores;cases++;
        }
        if(instructions==0||stores==0)throw new Exception("Gradient batch was never actually exercised");
        Console.WriteLine($"PASS: native gradient {cases} complete CPU/2-MiB RAM/ordered-byte comparisons; {instructions} actual instructions/{stores} actual stores, all TOP/PC/RC and opaque/PIC/changed-opcode controls.");
    }
}
