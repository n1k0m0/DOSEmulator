// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Whpx;

/// <summary>Real paged x87 stores across independently mapped code/data pages, compared with native ordinary-RAM execution.</summary>
internal static class NativeFpuPageChecks
{
    /// <summary>Records only the expected device bytes; page admission may not read the device.</summary>
    private sealed class Sink(uint offset,int width):IMemoryDevice
    {
        public readonly byte[] Bytes=new byte[width];
        public int Writes;
        /// <summary>Rejects an unintended destination read.</summary>
        public byte ReadByte(uint at)=>throw new Exception("Paged FST read its destination");
        /// <summary>Checks and records each independently observed device byte.</summary>
        public void WriteByte(uint at,byte value){if(at<offset||at-offset>=width)throw new Exception("Paged FST destination extent differs");Bytes[at-offset]=value;Writes++;}
    }
    /// <summary>Seeds two page tables with noncontiguous code pages and optionally straddling destination pages.</summary>
    private static (WhpxCpuState Cpu,byte[] Ram,byte[] Data,int Writes,uint Physical) Execute(bool device,bool wide,bool pop,bool codeCross,bool dataEdge,int top,string? rejection=null)
    {
        int width=wide?8:4;uint linear=0x400000+(dataEdge?4092u:0),physical=0x110000+(dataEdge?4092u:0),ip=codeCross?0x1ffdu:0x1000;
        var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();Sink? sink=null;
        if(device){sink=new(physical-0x110000,width);memory.Map(0x110000,8192,sink,"Own paged FST side-effect oracle");}
        // Full low identity map, with the two instruction pages deliberately moved apart.
        // Write only the declared own page-directory/table entries before starting the native processor.
        void Dword(uint address,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(memory.Ram.AsSpan((int)address,4),value);
        Dword(0x8000,0x9007);Dword(0x8004,0xa007);
        for(uint i=0;i<512;i++)Dword(0x9000+i*4,i*4096|7);
        Dword(0x9004,0x20007);Dword(0x9008,0x22007);Dword(0xa000,0x110007);Dword(0xa004,0x111007);
        if(rejection=="data-noncontiguous")Dword(0xa004,0x112007);
        if(rejection=="data-read-only")Dword(0xa004,0x111005);
        if(rejection=="data-not-present")Dword(0xa004,0x111006);
        if(rejection=="code-not-present")Dword(0x9008,0x22006);
        byte[] code=[wide?(byte)0xdd:(byte)0xd9,pop?(byte)0x1d:(byte)0x15,(byte)linear,(byte)(linear>>8),(byte)(linear>>16),(byte)(linear>>24),0xf4];
        for(uint i=0;i<code.Length;i++){uint address=ip+i;memory.Ram[(address<0x2000?0x20000:0x22000)+(address&4095)]=code[i];}
        memory.Ram.AsSpan((int)physical,width).Fill(0xcc);
        using var native=new WhpxMappedMachine(memory,new(),new(),clock,WhpxCpuContract.IntelP55cMmxSubset,166_000_000);
        native.SetEntry(0,0x1000);var initial=native.CaptureCpuState();
        var registers=initial.Registers.Select(r=>r.Name switch
        {
            0x10=>r with{Low=ip},0x11=>r with{Low=2},0x1c=>r with{Low=0xe0010031},0x1e=>r with{Low=0x8000},
            19=>r with{Low=0,High=0x409b0008ffffffff},18 or 20 or 21 or 22 or 23=>r with{Low=0,High=0x40930010ffffffff},
            >=0x1010 and <=0x1017=>r with{Low=r.Name==0x1010?0xc90fdaa22168c235:0x8000000000000000,High=0x4000},
            0x1018=>r with{Low=255UL<<32|(ulong)(uint)top<<27|0x37f,High=0},0x1019=>r with{Low=0},_=>r
        }).ToArray();
        native.RestoreCpuState(initial with{Registers=registers,Halted=false});
        if(rejection is not null)
        {
            byte[] before=memory.Ram.ToArray();var cpu=native.CaptureCpuState();byte[] exit=new byte[256];exit[48]=16;code.CopyTo(exit,52);
            BinaryPrimitives.WriteUInt32LittleEndian(exit.AsSpan(68),5);BinaryPrimitives.WriteUInt64LittleEndian(exit.AsSpan(72),physical);
            var emulator=typeof(WhpxMappedMachine).GetField("_emulator",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
            bool done=(bool)emulator.GetType().GetMethod("TryCompleteProtectedFpuStore",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(emulator,[exit])!;
            if(done||sink!.Writes!=0||JsonSerializer.Serialize(cpu)!=JsonSerializer.Serialize(native.CaptureCpuState()))throw new Exception("Paged FST rejection changed CPU/device: "+rejection);
            // A real WHP page walk may update only architectural A/D bits even when a later page denies the whole operation.
            var allowed=new Dictionary<int,uint>{{0x8000,0x20},{0x8004,0x20},{0x9004,0x20},{0x9008,0x20},{0xa000,0x60},{0xa004,0x60}};
            for(int at=0;at<before.Length;at+=4)
            {
                uint difference=BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(at,4))^BinaryPrimitives.ReadUInt32LittleEndian(memory.Ram.AsSpan(at,4));
                if(difference!=0&&(!allowed.TryGetValue(at,out uint mask)||(difference&~mask)!=0))throw new Exception("Paged rejection changed a non-pagewalk RAM bit");
            }
            return(cpu,memory.Ram.ToArray(),sink.Bytes,sink.Writes,physical);
        }
        native.RunUntilHalt(TimeSpan.FromSeconds(3));return(native.CaptureCpuState(),memory.Ram.ToArray(),sink?.Bytes??memory.Ram.AsSpan((int)physical,width).ToArray(),sink?.Writes??0,physical);
    }
    /// <summary>Requires all native CPU fields, every RAM byte including PTE A/D bits and the exact IEEE payload, with explicit denial controls.</summary>
    public static void Run()
    {
        int cases=0;
        foreach(bool wide in new[]{false,true})foreach(bool pop in new[]{false,true})foreach(bool codeCross in new[]{false,true})foreach(bool dataEdge in new[]{false,true})foreach(int top in new[]{0,7})
        {
            var a=Execute(false,wide,pop,codeCross,dataEdge,top);var b=Execute(true,wide,pop,codeCross,dataEdge,top);int width=wide?8:4;
            ulong expected=wide?0x400921fb54442d18UL:0x40490fdb;
            if(b.Writes!=width||!a.Data.AsSpan().SequenceEqual(b.Data)||(wide?BinaryPrimitives.ReadUInt64LittleEndian(b.Data):BinaryPrimitives.ReadUInt32LittleEndian(b.Data))!=expected||
                JsonSerializer.Serialize(a.Cpu)!=JsonSerializer.Serialize(b.Cpu))throw new Exception("Paged native FST CPU/value differs");
            a.Ram.AsSpan((int)a.Physical,width).Fill(0xcc);if(!a.Ram.AsSpan().SequenceEqual(b.Ram))throw new Exception("Paged FST whole RAM/PTE A/D differs");cases++;
        }
        foreach(string rejection in new[]{"data-noncontiguous","data-read-only","data-not-present","code-not-present"})_=Execute(true,true,true,true,true,7,rejection);
        Console.WriteLine($"PASS: native paged FST {cases} full CPU/2-MiB RAM/PTE comparisons, noncontiguous code pages, data-page boundary and 4 rejected extents (only hardware page-walk A/D allowed).");
    }
}
