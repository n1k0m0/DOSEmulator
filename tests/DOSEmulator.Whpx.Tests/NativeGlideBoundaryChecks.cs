// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Independent native stopping points qualify partial gradient commits, event yields, live source changes and reset ownership.</summary>
internal static class NativeGlideBoundaryChecks
{
    private const uint Target=0x110000;
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    /// <summary>Own aligned parameter storage with a complete ordered-byte oracle.</summary>
    internal sealed class Sink(bool allow):IMemoryDevice,IImmediateMemoryWrite
    {
        internal readonly List<(uint Address,byte Value)> Writes=[];
        /// <summary>Reads are outside this own parameter-only contract.</summary>
        public byte ReadByte(uint offset)=>throw new Exception("Unexpected gradient latch read");
        /// <summary>Records every physical byte once without mutating RAM or another device.</summary>
        public void WriteByte(uint offset,byte value)=>Writes.Add((offset,value));
        /// <summary>Admits exactly three own twelve-byte parameter rows.</summary>
        public bool IsImmediateDwordWrite(uint address)=>allow&&address>=Target&&address<=Target+32;
    }
    /// <summary>Own actual native preparation stops immediately before the first parameter MOV, with its real preceding x87 computations completed.</summary>
    private sealed class Fixture:IDisposable
    {
        internal readonly MemoryBus Memory=new(2<<20);
        internal readonly DeviceScheduler Clock=new();
        internal readonly WhpxMappedMachine Native;
        internal readonly Sink Device;
        internal readonly int Store;
        internal readonly byte[] Code;
        internal long TranslationQueries,BoundaryKeeps;
        /// <summary>Uses own relocatable instruction bytes and ordinary native execution to produce the starting raw stack.</summary>
        internal Fixture(bool allow,bool paged=false)
        {
            Device=new(allow);Memory.Map(Target,36,Device,"Own gradient boundary latches");
            Code=(byte[])typeof(NativeGlideBatchChecks).GetMethod("Program",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[false])!;
            Store=Enumerable.Range(0,Code.Length-1).Single(i=>Code[i]==0x89&&Code[i+1]==0x2f);
            Code.CopyTo(Memory.Ram,0x1000);Memory.Ram[0x1000+Store]=0xf4;
            float[] constants=[0.17f,-1.33f,2.37f,0.00137f];for(int i=0;i<4;i++)BinaryPrimitives.WriteSingleLittleEndian(Memory.Ram.AsSpan(0x3000+i*4),constants[i]);
            for(int i=0;i<3;i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(Memory.Ram.AsSpan(0x4040+i*4),1.7f+i*0.13f);
                BinaryPrimitives.WriteSingleLittleEndian(Memory.Ram.AsSpan(0x5040+i*4),-2.1f-i*1.19f);
                BinaryPrimitives.WriteSingleLittleEndian(Memory.Ram.AsSpan(0x6040+i*4),0.97f-i*0.19f);
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan(0x7004+i*8),Target+(uint)i*12);
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan(0x7008+i*8),i<2?0x44+(uint)i*4:0);
            }
            if(paged)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan(0x20000),0x21003);
                for(int page=0;page<512;page++)BinaryPrimitives.WriteUInt32LittleEndian(Memory.Ram.AsSpan(0x21000+page*4),(uint)(page*4096)|3);
            }
            Native=new(Memory,new(),new(),Clock,WhpxCpuContract.IntelP55cMmxSubset,233_000_000);Native.SetEntry(0,0x1000);
            var state=Native.CaptureCpuState();
            Native.RestoreCpuState(state with{Halted=false,Registers=state.Registers.Select(r=>r.Name switch
            {
                0=>r with{Low=0x4000},1=>r with{Low=0x6000},2=>r with{Low=0x40},3=>r with{Low=0x5000},6=>r with{Low=0x7000},
                0x1c=>r with{Low=paged?0xe0000031u:0x60000031u},0x1e=>r with{Low=paged?0x20000u:0},19=>r with{Low=0,High=0x409b0008ffffffff},
                18 or 20 or 21 or 22 or 23=>r with{Low=0,High=0x40930010ffffffff},
                0x1018=>r with{Low=0x27f,High=0},0x1019=>r with{Low=0},_=>r
            }).ToArray()});
            Native.RunUntilHalt(TimeSpan.FromSeconds(3));Memory.Ram[0x1000+Store]=0x89;
            var prepared=Native.CaptureCpuState();Native.RestoreCpuState(prepared with{Halted=false,Registers=prepared.Registers.Select(r=>r.Name==0x10?r with{Low=0x1000u+(uint)Store}:r).ToArray()});
            Clock.AdvanceTo(1_000_000_000);
        }
        /// <summary>Shares/unshares RAM through the existing stopped WHP mapper, without a native opcode between the explicit probes.</summary>
        internal void Share(bool active)
        {
            var memory=typeof(WhpxMappedMachine).GetField("_memory",Hidden)!.GetValue(Native)!;
            memory.GetType().GetMethod(active?"ActivateSharedRam":"DeactivateSharedRam",Hidden|BindingFlags.Public)!.Invoke(memory,null);
        }
        /// <summary>Runs the actual guarded interpreter with a deterministic owner-clock oracle, using a genuine prepared native context.</summary>
        internal bool Batch(Func<ulong> observe,Func<ulong>? boundary=null,bool observeVersion=false,bool retain=true)
        {
            var type=typeof(WhpxMappedMachine).Assembly.GetType("DOSEmulator.Whpx.WhpxGlideBatch")!;
            var handle=typeof(WhpxMappedMachine).GetField("_partition",Hidden)!.GetValue(Native)!;
            object batch=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[handle,Memory,observe],null)!;
            if(observeVersion)
            {
                var mapper=typeof(WhpxMappedMachine).GetField("_memory",Hidden)!.GetValue(Native)!;
                var version=mapper.GetType().GetProperty("TranslationMemoryVersion",Hidden)!.GetGetMethod(true)!.CreateDelegate<Func<ulong>>(mapper);
                type.GetProperty("ReadBoundaryMemoryVersion",Hidden)!.SetValue(batch,version);
                type.GetProperty("RetainReadOnlyBoundaryTranslations",Hidden)!.SetValue(batch,retain);
            }
            byte[] exit=new byte[256];exit[48]=2;exit[52]=0x89;exit[53]=0x2f;
            BinaryPrimitives.WriteUInt64LittleEndian(exit.AsSpan(32),0x1000u+(uint)Store);BinaryPrimitives.WriteUInt32LittleEndian(exit.AsSpan(68),1);BinaryPrimitives.WriteUInt64LittleEndian(exit.AsSpan(72),Target);
            Share(true);
            try
            {
                bool ran=(bool)type.GetMethod("TryRun",Hidden)!.Invoke(batch,[exit,1UL,boundary])!;
                TranslationQueries=(long)type.GetProperty("TranslationQueries",Hidden)!.GetValue(batch)!;
                BoundaryKeeps=(long)type.GetProperty("ReadOnlyBoundaryKeeps",Hidden)!.GetValue(batch)!;
                return ran;
            }
            finally{Share(false);}
        }
        /// <summary>Stops ordinary native execution at the exact next instruction, then removes only the own test stop and rewinds its one-byte HLT.</summary>
        internal void NativeUntil(uint ip)
        {
            byte previous=Memory.Ram[ip];Memory.Ram[ip]=0xf4;Native.RunUntilHalt(TimeSpan.FromSeconds(3));
            var cpu=Native.CaptureCpuState();if(cpu.Registers.Single(r=>r.Name==0x10).Low!=ip+1)throw new Exception("Independent native stop differs");
            Memory.Ram[ip]=previous;Native.RestoreCpuState(cpu with{Halted=false,Registers=cpu.Registers.Select(r=>r.Name==0x10?r with{Low=ip}:r).ToArray()});
        }
        /// <summary>Releases the single native owner and its memory; no host endpoint exists.</summary>
        public void Dispose()=>Native.Dispose();
    }
    /// <summary>Compares all architectural fields, the complete RAM image and independent ordered parameter bytes without exclusions.</summary>
    private sealed record Result(string Cpu,byte[] Ram,(uint Address,byte Value)[] Writes,uint Ip);
    /// <summary>Captures the whole stopped result before disposing this sole native owner and creating the independent reference.</summary>
    private static Result Capture(Fixture fixture)=>new(JsonSerializer.Serialize(fixture.Native.CaptureCpuState()),fixture.Memory.Ram.ToArray(),
        fixture.Device.Writes.ToArray(),(uint)fixture.Native.GetRegister(0x10));
    /// <summary>Checks every saved CPU/RAM byte and actual ordered register write against a separately executed native reference.</summary>
    private static void Equal(Result a,Fixture b,string label)
    {
        if(a.Cpu!=JsonSerializer.Serialize(b.Native.CaptureCpuState())||
            !a.Ram.AsSpan().SequenceEqual(b.Memory.Ram)||!a.Writes.SequenceEqual(b.Device.Writes))throw new Exception("Gradient boundary differs: "+label);
    }
    /// <summary>Rejects unsafe native contexts before a single register-latch byte or architectural field changes.</summary>
    private static void Rejections()
    {
        (string Label,uint Register,ulong Clear,ulong Set,bool High)[] cases=
        [
            ("real mode",0x1c,1,0,false),("EM",0x1c,0,4,false),("TS",0x1c,0,8,false),
            ("TF",0x11,0,0x100,false),("RF",0x11,0,0x10000,false),("V86",0x11,0,0x20000,false),
            ("debug breakpoint",0x26,0,1,false),("CS base",19,0,16,false),("CS limit",19,uint.MaxValue,65535,true),
            ("DS base",21,0,16,false),("normal DS limit",21,uint.MaxValue,0x1000,true),
            ("expand-down DS limit",21,uint.MaxValue|4UL<<48,Target|4UL<<48,true),
            ("unmasked x87",0x1018,1,0,false),("pending x87",0x1018,0,0x80UL<<16,false),
            ("reserved precision",0x1018,0x300,0x100,false),
            ("x87 opcode",0x1018,0,1UL<<48,false),("x87 instruction pointer",0x1018,0,1,true),("x87 data pointer",0x1019,0,1,false)
        ];
        foreach(var item in cases)
        {
            using var fixture=new Fixture(true);var cpu=fixture.Native.CaptureCpuState();
            fixture.Native.RestoreCpuState(cpu with{Registers=cpu.Registers.Select(r=>r.Name!=item.Register?r:item.High?
                r with{High=(r.High&~item.Clear)|item.Set}:r with{Low=(r.Low&~item.Clear)|item.Set}).ToArray()});
            var before=Capture(fixture);if(fixture.Batch(()=>0))throw new Exception("Unsafe native gradient context admitted: "+item.Label);
            Equal(before,fixture,"pure rejection "+item.Label);
        }
        using(var opaque=new Fixture(false)){var before=Capture(opaque);if(opaque.Batch(()=>0))throw new Exception("Opaque peer admitted");Equal(before,opaque,"opaque peer");}
        Console.WriteLine("PASS: native gradient 19 pure full CPU/2-MiB RAM/ordered-byte rejections; real/EM/TS/debug/segment/x87/opaque contexts remain unchanged.");
    }
    /// <summary>Compares unchanged-event reuse and actual notified writes/PTE remaps against the old always-clear policy on independent native partitions.</summary>
    private static void MemoryEpochs()
    {
        long keptQueries=0,clearedQueries=0;
        foreach(int mode in Enumerable.Range(0,4))
        {
            Result reference;
            using(var old=new Fixture(true,mode==3))
            {
                Prepare(old,mode);int count=0;
                if(!old.Batch(()=>1,()=>{Change(old,mode,++count);return 1;},true,false))throw new Exception("Always-clear reference did not execute");
                reference=Capture(old);if(mode==0)clearedQueries=old.TranslationQueries;
            }
            using(var current=new Fixture(true,mode==3))
            {
                Prepare(current,mode);int count=0;
                if(!current.Batch(()=>1,()=>{Change(current,mode,++count);return 1;},true))throw new Exception("Observed memory epoch did not execute");
                Equal(reference,current,"notified memory epoch "+mode);
                if(current.BoundaryKeeps==0)throw new Exception("Read-only boundary reuse was not actually exercised");
                if(mode==0)keptQueries=current.TranslationQueries;
            }
            if(mode==0)using(var native=new Fixture(false)){native.NativeUntil(reference.Ip);Equal(reference,native,"read-only events versus real native execution");}
        }
        if(keptQueries>=clearedQueries||keptQueries==0)throw new Exception("Read-only boundaries did not reduce actual native translations");
        // Probe the real mapper, not an artificial integer stamp. Reads/unchanged refreshes must retain it; each RAM/topology/firmware mutation must advance it.
        var memory=new MemoryBus(2<<20);var host=new Intel82437Fx(memory,new byte[65536]);memory.Map(0xc0000,0x40000,host,"Own PAM epoch fixture");
        using(var native=new WhpxMappedMachine(memory,new(),new(),new()))
        {
            var mapper=typeof(WhpxMappedMachine).GetField("_memory",Hidden)!.GetValue(native)!;
            var version=mapper.GetType().GetProperty("TranslationMemoryVersion",Hidden)!.GetGetMethod(true)!.CreateDelegate<Func<ulong>>(mapper);
            var refresh=mapper.GetType().GetMethod("Refresh",Hidden)!;
            ulong before=version();memory.ReadDword(0x3000);refresh.Invoke(mapper,null);
            if(version()!=before)throw new Exception("Read-only RAM/refresh advanced its mutation epoch");
            Action[] mutations=[()=>memory.WriteDword(0x3000,123),()=>memory.SetRamHole(0x180000,0x10000),
                ()=>memory.A20Enabled=false,()=>host.WriteConfiguration(0x59,0x30,1)];
            foreach(var mutation in mutations)
            {
                before=version();mutation();refresh.Invoke(mapper,null);
                if(version()==before)throw new Exception("Real RAM/topology/PAM mutation retained a stale epoch");
            }
        }
        Console.WriteLine($"PASS: native gradient memory epochs: four whole CPU/2-MiB RAM/ordered-byte independent always-clear comparisons, real native read-only reference and five real mapper epoch controls; translations {clearedQueries}->{keptQueries}. PTE mutation uses the original live-page-walk policy, not a new native TLB/silicon claim.");
    }
    /// <summary>Installs an alternate physical constant page before either stopped helper begins; the PTE switch occurs only after its original translation was used.</summary>
    private static void Prepare(Fixture fixture,int mode)
    {
        if(mode!=3)return;
        fixture.Memory.Ram.AsSpan(0x3000,4096).CopyTo(fixture.Memory.Ram.AsSpan(0xa000));
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Memory.Ram.AsSpan(0xa004),0x3f100001);
    }
    /// <summary>Mutates the actual source or PTE at the third committed instruction boundary, through notified byte/dword/leased-span bus paths.</summary>
    private static void Change(Fixture fixture,int mode,int count)
    {
        if(count!=3)return;
        if(mode==1)fixture.Memory.WriteDword(0x3004,0x3f100001);
        if(mode==2)
        {
            if(!fixture.Memory.TryGetPhysicalRamSpan(0x3004,4,null,out var bytes))throw new Exception("Own RAM lease unavailable");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x3f100001);
        }
        if(mode==3)fixture.Memory.WriteDword(0x2100c,0xa003);
    }
    /// <summary>Checks twenty-seven unique instruction cuts, complete device yields, source mutation and a reset which must retain the new CPU context.</summary>
    internal static void Run()
    {
        for(int count=1;count<=27;count++)
        {
            Result result;
            using(var a=new Fixture(true))
            {
                int observations=0;if(!a.Batch(()=>(++observations<count)?0UL:1UL))throw new Exception("Own real MMIO instruction was not completed");
                result=Capture(a);
            }
            using var b=new Fixture(false);b.NativeUntil(result.Ip);Equal(result,b,"instruction "+count);
        }
        Result yielded;
        using(var a=new Fixture(true))
        {
            int yields=0;
            if(!a.Batch(()=>1,()=>{yields++;return 1;}))throw new Exception("Yielding gradient was not executed");
            yielded=Capture(a);if(yields<10)throw new Exception("Device boundaries were not actually exercised");
        }
        using(var b=new Fixture(false)){b.NativeUntil(yielded.Ip);Equal(yielded,b,"every-instruction device boundary");}
        Result mutated;
        using(var a=new Fixture(true))
        {
            bool once=false;
            if(!a.Batch(()=>1,()=>{if(!once){a.Memory.WriteDword(0x3008,0x3f100001);once=true;}return 1;}))throw new Exception("Live RAM-source mutation was not exercised");
            mutated=Capture(a);
        }
        using(var b=new Fixture(false))
        {
            uint first=0x1000u+(uint)b.Store+2;b.NativeUntil(first);b.Memory.WriteDword(0x3008,0x3f100001);
            b.NativeUntil(mutated.Ip);Equal(mutated,b,"real source changed at first committed boundary");
        }
        Result foldedResult;
        using(var a=new Fixture(true))
        {
            if(!a.Batch(()=>1,()=>{a.Memory.A20Enabled=false;return ulong.MaxValue;}))throw new Exception("A20 boundary was not exercised");
            foldedResult=Capture(a);
        }
        using(var b=new Fixture(false))
        {
            b.NativeUntil(0x1000u+(uint)b.Store+2);b.Memory.A20Enabled=false;Equal(foldedResult,b,"A20 ends the block before another source load");
        }
        Result resetResult;
        using(var a=new Fixture(true))
        {
            var reset=typeof(WhpxMappedMachine).GetMethod("ResetProcessor",Hidden)!;
            if(!a.Batch(()=>1,()=>{reset.Invoke(a.Native,[false]);return 0;}))throw new Exception("Reset boundary was not exercised");
            resetResult=Capture(a);
        }
        using(var b=new Fixture(false))
        {
            // The same one real parameter write precedes the independent reset; both reset at the same explicit own virtual time.
            b.Memory.WriteDword(Target,BinaryPrimitives.ReadUInt32LittleEndian(b.Memory.Ram.AsSpan(0x4040)));
            typeof(WhpxMappedMachine).GetMethod("ResetProcessor",Hidden)!.Invoke(b.Native,[false]);Equal(resetResult,b,"reset keeps new context, no stale replay commit");
        }
        Console.WriteLine("PASS: native gradient 31 complete boundary CPU/2-MiB RAM/ordered-byte controls; 27 instruction cuts, every-instruction event yields, live source mutation, A20 fence and reset without stale context restoration.");
        Rejections();
        MemoryEpochs();
    }
}
