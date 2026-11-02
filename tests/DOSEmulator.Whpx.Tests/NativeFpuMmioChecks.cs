// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Whpx;
using DOSEmulator.Cpu.X86;

/// <summary>Full native-register and whole-RAM comparisons between actual FST/FSTP to ordinary RAM and the same instruction to a side-effecting mapping.</summary>
internal static class NativeFpuMmioChecks
{
    private const uint Target = 0x110000;
    private sealed class Sink(int size=4,uint first=0) : IMemoryDevice
    {
        public int Writes;
        public readonly byte[] Data=new byte[size];
        /// <summary>A floating-point store must not read its destination device.</summary>
        public byte ReadByte(uint offset) => throw new Exception("Unexpected FST device read");
        /// <summary>Records every actual device byte separately from the covered physical RAM backing.</summary>
        public void WriteByte(uint offset, byte value) { if(offset<first||offset-first>=Data.Length)throw new Exception("Unexpected FST destination");Writes++;Data[offset-first]=value; }
    }
    /// <summary>Executes a manually seeded protected32 x87 instruction with the same raw register file on either actual memory path.</summary>
    private static (WhpxCpuState Cpu,byte[] Ram,byte[] Initial,byte[] Stored,int Writes) Execute(bool device,int top,int rounding,bool pop,int form,string? rejection=null,bool sparse=false,Float80Value? sample=null,bool chain=false,bool wide=false,int alignment=4)
    {
        int width=wide?8:4;uint target=Target+(wide?(uint)alignment:0);byte opcode=wide?(byte)0xdd:(byte)0xd9;
        var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();Sink? sink=null;
        if(device){sink=new(width,target-Target);memory.Map(Target,rejection=="split-device"?8u:4096u,sink,"Own FST side-effect oracle");
            if(rejection=="split-device")memory.Map(target+4,4,new Sink(),"Own different last-DWORD device");}
        byte ext=(byte)(pop?0x18:0x10);
        byte[] code=form switch
        {
            0 => [opcode,(byte)(ext|7),0xf4],                       // [EDI], DS
            1 => [opcode,(byte)(0x40|ext|5),4,0xf4],                // [EBP+4], SS
            2 => [opcode,(byte)(0x40|ext|4),0x4c,8,0xf4],           // [ESP+ECX*2+8], SS
            3 => [0x64,opcode,(byte)(ext|5),(byte)target,0,0x11,0,0xf4], // FS:[disp32]
            4 => [opcode,(byte)(0x40|ext),0,0xf4],                  // Actual Motorhead [EAX+0].
            _ => [opcode,(byte)(0x40|ext),2,0xf4]                   // Actual Motorhead [EAX+2].
        };
        if(chain)code=[opcode,0x1f,0xd8,0xc1,0xd9,0x1f,0xf4];
        code.CopyTo(memory.Ram,0x1000);memory.Ram.AsSpan((int)target,width).Fill(0xcc);
        using var native=new WhpxMappedMachine(memory,new(),new(),clock,WhpxCpuContract.IntelP55cMmxSubset,166_000_000);
        native.SetEntry(0,0x1000);var initial=native.CaptureCpuState();
        // The logical register data and physical TOP/tag image are deliberately independent.
        var registers=initial.Registers.Select(r=>r.Name switch
        {
            0 when form>=4=>r with{Low=target-(form==5?2u:0)},
            1 => r with {Low=2}, 4 => r with {Low=target-12}, 5 => r with {Low=target-4}, 7 => r with {Low=target},
            0x10 => r with {Low=0x1000}, 0x11 => r with {Low=2}, 0x1c => r with {Low=0x60000031},
            19 => r with {Low=0,High=0x409b0008ffffffff},
            18 or 20 or 21 or 22 or 23 => r with {Low=0,High=0x40930010ffffffff},
            >=0x1010 and <=0x1017 => r with {Low=r.Name==0x1010?(sample?.Significand??0xc90fdaa22168c235):0x8000000000000000+(r.Name-0x1010)*0x100000000000000UL,High=r.Name==0x1010&&sample is {} number?number.SignExponent:(ulong)(0x4000|((top&1)<<15))},
            0x1018 => r with {Low=(sparse?(chain?(1UL<<top|1UL<<((top+1)&7)|1UL<<((top+2)&7)):1UL<<top):255UL)<<32|(ulong)((uint)top<<11|0x200u)<<16|(0x37fUL|(ulong)(uint)rounding<<10),High=0},
            0x1019 => r with {Low=0},
            _ => r
        }).ToArray();
        if(rejection is not null)registers=registers.Select(r=>rejection switch
        {
            "alignment-check" when r.Name==0x11=>r with{Low=r.Low|0x40000},
            "alignment-check" when r.Name==0x1c=>r with{Low=r.Low|0x40000},
            "alignment-check" when r.Name==19=>r with{High=0x40fb000bffffffff},
            "alignment-check" when r.Name is 18 or 20 or 21 or 22 or 23=>r with{High=0x40f30013ffffffff},
            "TF" when r.Name==0x11=>r with{Low=r.Low|0x100},
            "debug" when r.Name==0x26=>r with{Low=r.Low|1},
            "TS" when r.Name==0x1c=>r with{Low=r.Low|8},
            "unmasked" when r.Name==0x1018=>r with{Low=r.Low&~1UL},
            "pending" when r.Name==0x1018=>r with{Low=r.Low|0x80UL<<16},
            "empty" when r.Name==0x1018=>r with{Low=r.Low&~(255UL<<32)},
            "pointer" when r.Name==0x1018=>r with{High=0x12345678},
            "nan" when r.Name==0x1010=>r with{High=0x7fff},
            "denormal" when r.Name==0x1010=>r with{High=0,Low=1},
            "read-only" when r.Name==21=>r with{High=0x40910010ffffffff},
            "null" when r.Name==21=>r with{High=0x40930000ffffffff},
            "limit" when r.Name==21=>r with{High=0x409300100010ffff},
            "last-byte-limit" when r.Name==21=>r with{High=0x4093001000000000UL|target+(uint)width-2},
            "misaligned" when r.Name==7=>r with{Low=target+1},
            "code16" when r.Name==19=>r with{High=0x009b0008ffffffff},
            _=>r
        }).ToArray();
        native.RestoreCpuState(initial with {Registers=registers,Halted=false});
        byte[] before=memory.Ram.ToArray();
        if(rejection is not null)
        {
            byte[] exit=new byte[256];exit[48]=16;code.CopyTo(exit,52);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(exit.AsSpan(68),5);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(exit.AsSpan(72),target);
            if(rejection=="wrong-gpa")System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(exit.AsSpan(72),target+4);
            if(rejection=="short")exit[48]=1;
            if(rejection=="prefix")exit[52]=0x67;
            if(rejection=="changed-code")memory.WriteByte(0x1000,0x90);
            byte[] originalRam=memory.Ram.ToArray();var original=native.CaptureCpuState();
            var emulator=typeof(WhpxMappedMachine).GetField("_emulator",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(native)!;
            var method=emulator.GetType().GetMethod("TryCompleteProtectedFpuStore",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            bool completed=(bool)method.Invoke(emulator,[exit])!;
            if(completed||sink!.Writes!=0||!memory.Ram.AsSpan().SequenceEqual(originalRam)||JsonSerializer.Serialize(original)!=JsonSerializer.Serialize(native.CaptureCpuState()))
                throw new Exception("FST admission modified rejected native CPU/RAM/device case "+rejection);
            return(original,originalRam,before,sink.Data,sink.Writes);
        }
        native.RunUntilHalt(TimeSpan.FromSeconds(3));
        return(native.CaptureCpuState(),memory.Ram.ToArray(),before,sink?.Data??memory.Ram.AsSpan((int)target,width).ToArray(),sink?.Writes??0);
    }
    /// <summary>Checks every TOP, rounding direction, FST/FSTP variant and real ModRM/SIB/segment form, including both signs and inexact rounding.</summary>
    public static void Run()
    {
        int cases=0;
        for(int top=0;top<8;top++)for(int rc=0;rc<4;rc++)foreach(bool pop in new[]{false,true})for(int form=0;form<5;form++)foreach(bool sparse in new[]{false,true})
        {
            var a=Execute(false,top,rc,pop,form,sparse:sparse);var b=Execute(true,top,rc,pop,form,sparse:sparse);
            uint bits=(top&1)==0?(rc is 0 or 2?0x40490fdbu:0x40490fdau):(rc is 0 or 1?0xc0490fdbu:0xc0490fdau);
            byte[] expected=a.Initial.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((int)Target,4),bits);
            if(b.Writes!=4||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||
                !a.Stored.AsSpan().SequenceEqual(b.Stored)||System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.Stored)!=bits||
                JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))
                throw new Exception($"Native FST MMIO differs top={top} rc={rc} pop={pop} form={form}: writes={b.Writes}, data={Convert.ToHexString(a.Stored)}/{Convert.ToHexString(b.Stored)}, registers={JsonSerializer.Serialize(a.Cpu.Registers.Zip(b.Cpu.Registers).Where(p=>p.First!=p.Second))}");
            cases++;
        }
        foreach(var number in new (Float80Value Value,uint Bits)[]{(Float80Value.Zero,0),(new(0,0x8000),0x80000000),(new(0x8000000000000000,0x7fff),0x7f800000),
            (new(0x8000000000000000,0xffff),0xff800000),(Float80Value.Indefinite,0xffc00000),(new(0xc123456789abcdef,0x7fff),0x7fc12345),(new(0x8123456789abcdef,0x7fff),0x7fc12345)})
        for(int top=0;top<8;top++)foreach(bool pop in new[]{false,true})
        {
            var a=Execute(false,top,0,pop,0,sample:number.Value);var b=Execute(true,top,0,pop,0,sample:number.Value);
            byte[] expected=a.Initial.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((int)Target,4),number.Bits);
            if(b.Writes!=4||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||
                !a.Stored.AsSpan().SequenceEqual(b.Stored)||JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))throw new Exception($"Native masked special FST differs raw={number.Value} top={top} pop={pop}");
            cases++;
        }
        for(int top=0;top<8;top++)foreach(bool sparse in new[]{false,true})
        {
            var a=Execute(false,top,0,true,0,sparse:sparse,chain:true);var b=Execute(true,top,0,true,0,sparse:sparse,chain:true);
            uint bits=(top&1)==0?0x40818000u:0xc0818000u;byte[] expected=a.Initial.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((int)Target,4),bits);
            if(b.Writes!=8||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||
                !a.Stored.AsSpan().SequenceEqual(b.Stored)||JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))throw new Exception($"Native FST/arithmetic/FST chain differs top={top} sparse={sparse}");
            cases++;
        }

        // Original Motorhead uses four-byte-aligned DD /2 writes of eight framebuffer bytes.
        for(int top=0;top<8;top++)for(int rc=0;rc<4;rc++)foreach(bool pop in new[]{false,true})for(int form=0;form<5;form++)foreach(bool sparse in new[]{false,true})
        {
            var a=Execute(false,top,rc,pop,form,sparse:sparse,wide:true);var b=Execute(true,top,rc,pop,form,sparse:sparse,wide:true);
            ulong bits=((top&1)==0?0x400921fb54442d18UL:0xc00921fb54442d18UL)+((top&1)==0?rc==2?1UL:0:rc==1?1UL:0);
            byte[] expected=a.Initial.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan((int)Target+4,8),bits);
            if(b.Writes!=8||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||!a.Stored.AsSpan().SequenceEqual(b.Stored)||JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))
                throw new Exception($"Native eight-byte FST differs top={top} rc={rc} pop={pop} form={form} data={Convert.ToHexString(a.Stored)}/{Convert.ToHexString(b.Stored)}");
            cases++;
        }
        foreach(ulong raw in new[]{0UL,0x8000000000000000UL,0x7ff0000000000000UL,0xfff0000000000000UL,0x7ff82468acf13579UL,ulong.MaxValue,1UL,0x003f003f003f003fUL,0x4093cc40b0ff1000UL})
        for(int top=0;top<8;top++)foreach(bool pop in new[]{false,true})
        {
            var value=Float80Math.LoadBinary(raw,64,0x37f).Value;
            var a=Execute(false,top,0,pop,0,sample:value,wide:true);var b=Execute(true,top,0,pop,0,sample:value,wide:true);
            byte[] expected=a.Initial.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan((int)Target+4,8),raw);
            if(b.Writes!=8||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||!a.Stored.AsSpan().SequenceEqual(b.Stored)||JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))
                throw new Exception($"Native eight-byte pixel/special copy differs raw={raw:X16} top={top} pop={pop}");
            cases++;
        }
        foreach(int alignment in new[]{0,1,2,3,5,6,7})for(int top=0;top<8;top++)foreach(bool pop in new[]{false,true})
        {
            var a=Execute(false,top,0,pop,5,wide:true,alignment:alignment);var b=Execute(true,top,0,pop,5,wide:true,alignment:alignment);
            ulong bits=(top&1)==0?0x400921fb54442d18UL:0xc00921fb54442d18UL;byte[] expected=a.Initial.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan((int)Target+alignment,8),bits);
            if(b.Writes!=8||!a.Ram.AsSpan().SequenceEqual(expected)||!b.Ram.AsSpan().SequenceEqual(b.Initial)||!a.Stored.AsSpan().SequenceEqual(b.Stored)||JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))
                throw new Exception($"Unaligned native Motorhead eight-byte FST differs alignment={alignment} top={top} pop={pop}");
            cases++;
        }
        string[] rejections=["TF","debug","TS","unmasked","pending","empty","pointer","denormal","read-only","null","limit","misaligned","code16","wrong-gpa","short","prefix","changed-code"];
        foreach(string rejection in rejections)foreach(bool wideStore in new[]{false,true})_=Execute(true,0,0,true,0,rejection,wide:wideStore);
        foreach(string rejection in new[]{"split-device","last-byte-limit"})_=Execute(true,0,0,true,0,rejection,wide:true);
        _=Execute(true,0,0,true,0,"alignment-check",wide:true,alignment:2);
        Console.WriteLine($"PASS: WHPX original FST/FSTP MMIO; {cases} full native-register comparisons, independent whole 2-MiB RAM oracles on both sides, exact aligned/unaligned 4/8-byte device stores and 32-bit arithmetic chains; {rejections.Length*2+3} complete native rejection-purity checks.");
    }
}
