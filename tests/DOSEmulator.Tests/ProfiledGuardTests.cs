// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Current-guard negatives and scalar futures for the newly admitted profiled memory/stack families.</summary>
internal static class ProfiledGuardTests
{
    private sealed record Vector(string Name,byte[] Bytes,int AddressAt,int Width,bool DataWrite,bool Push=false);
    private sealed class Peer : IMemoryDevice
    {
        public int Calls;
        /// <summary>Counts actual fallback reads while rejected acceleration remains side-effect free.</summary>
        public byte ReadByte(uint offset){Calls++;return 0x55;}
        /// <summary>Counts actual fallback stores while rejected acceleration remains side-effect free.</summary>
        public void WriteByte(uint offset,byte value){Calls++;}
    }
    /// <summary>Requires the selected current guard or complete scalar future to agree.</summary>
    private static void Need(bool value,string why){if(!value)throw new Exception(why);}
    /// <summary>Changes one explicit fixture field through the ordinary component state interface.</summary>
    private static void Field<T>(X86Cpu cpu,string name,T value)
    {var s=cpu.SaveState();s.Fields[name]=JsonSerializer.SerializeToElement(value,StateValues.Json);cpu.LoadState(s);}
    /// <summary>Creates a controlled current cached-rights/dirty change without changing an encoding plan.</summary>
    private static void Tlb(X86Cpu cpu,uint page,string flag,bool value)
    {
        var entries=cpu.SaveState().Fields["_tlb"].EnumerateArray().Select(e=>JsonNode.Parse(e.GetRawText())!).ToArray();
        foreach(var entry in entries)if((uint)entry["page"]! == page && (bool)entry["valid"]!)entry[flag]=value;
        Field(cpu,"_tlb",entries);
    }
    /// <summary>Warms real stack translations, primes actual decoding, then restores every architectural byte.</summary>
    private static DecodedBatchTests.Rig Rig(byte[] bytes,X86CpuModel model)
    {
        var r=DecodedBatchTests.Make(4,[],model);
        uint scratch=r.Start+0x400;
        new byte[]{0x50,0x58,0x58,0x50}.CopyTo(r.Memory.Ram,(int)scratch);r.Cpu.Eip=scratch;
        for(int i=0;i<4;i++)r.Cpu.Step();r.Cpu.Eip=r.Start;
        byte[] installed=[..bytes,0x90,0x90,0xf4];installed.CopyTo(r.Memory.Ram,(int)r.Start);
        var before=r.Cpu.SaveState();byte[] ram=(byte[])r.Memory.Ram.Clone();
        Need(r.Cpu.TryRunDecodedBatch(64)==3,"Positive fixture never entered the actual decoded family.");
        ram.CopyTo(r.Memory.Ram,0);r.Cpu.LoadState(before);return r;
    }
    /// <summary>Registers current mapping, permission, segment, alias, observer and prefix controls plus real scalar fallback.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        Vector[] vectors=[new("prefixed counter",[0x66,0xff,0x05,0,0x70,0,0],3,2,true),
            new("unary",[0xf7,0x1d,0,0x70,0,0],2,4,true),new("immediate store",[0xc7,0x05,0,0x70,0,0,1,2,3,4],2,4,true),
            new("memory extension",[0x0f,0xbf,0x05,0,0x70,0,0],3,2,false),
            new("TEST with trailing immediate",[0xf7,0x05,0,0x70,0,0,1,2,3,4],2,4,false),
            new("PUSH memory",[0xff,0x35,0,0x70,0,0],2,4,false,true)];
        foreach(var modelValue in new[]{X86CpuModel.Intel486Dx,X86CpuModel.IntelP54cB5Subset})
        foreach(var vectorValue in vectors)
        foreach(string guardValue in new[]{"readonly","clean","segment","boundary","alignment","code-alias","MMIO","code-MMIO","observer","trap","prefix-lock","prefix-rep"})
        {
            var model=modelValue;var v=vectorValue;string guard=guardValue;
            test($"ProfiledGuard {v.Name} current {guard} {model}",()=>
            {
                var a=Rig(v.Bytes,model);var b=Rig(v.Bytes,model);var peers=new List<Peer>();
                bool reject=true;
                foreach(var r in new[]{a,b})
                {
                    if(guard is "readonly" or "clean")
                    {
                        uint page=v.Push?(r.Cpu.GetRegister(Register.Sp)-(uint)v.Width)>>12:7;
                        Tlb(r.Cpu,page,guard=="readonly"?"writable":"dirty",false);
                        reject=v.DataWrite||v.Push;
                        if(guard=="readonly")Field(r.Cpu,"Cr0",r.Cpu.Cr0|0x10000u);
                    }
                    else if(guard=="segment")r.Cpu.SetSegment(Segment.Ds,0);
                    else if(guard is "boundary" or "alignment")
                    {
                        r.Memory.WriteDword(r.Start+(uint)v.AddressAt,guard=="boundary"?0x7fffu:0x7001u);
                        if(guard=="alignment"){Field(r.Cpu,"Cr0",r.Cpu.Cr0|0x40000);Field(r.Cpu,"_cpl",3);r.Cpu.Eflags|=0x40000;}
                    }
                    else if(guard=="code-alias")
                    {
                        if(v.Push){r.Cpu.SetRegister(Register.Sp,r.Start+8);Tlb(r.Cpu,2,"dirty",true);}
                        else {r.Memory.WriteDword(r.Start+(uint)v.AddressAt,r.Start);Tlb(r.Cpu,2,"dirty",true);}
                        reject=v.DataWrite||v.Push;
                    }
                    else if(guard is "MMIO" or "code-MMIO")
                    {var peer=new Peer();peers.Add(peer);r.Memory.Map(guard=="MMIO"?0x7000u:0x2000u,4096,peer,"Own negative mapping");}
                    else if(guard=="observer")r.Cpu.InstructionStarting+=(_,_)=>{};
                    else if(guard=="trap")r.Cpu.Eflags|=0x100;
                    else
                    {
                        byte[] installed=[guard=="prefix-lock"?(byte)0xf0:(byte)0xf3,..v.Bytes,0x90,0x90,0xf4];
                        installed.CopyTo(r.Memory.Ram,(int)r.Start);
                    }
                }
                int count=b.Cpu.TryRunDecodedBatch(64);
                if(reject)
                {
                    Need(count==0,"A changed current guard was ignored.");DecodedBatchTests.Equal(a,b);
                    Need(peers.All(p=>p.Calls==0),"Rejected plan called an arbitrary MMIO peer.");
                    // Actual ordinary fallback, including its own fault/interrupt/checkpoint path.
                    if(guard is not ("code-MMIO" or "prefix-lock" or "prefix-rep"))
                    {Need(a.Cpu.Step()==b.Cpu.Step(),"Fallback clocks differ.");DecodedBatchTests.Equal(a,b);}
                }
                else
                {
                    Need(count==3,"A legal read-only/clean/read-alias operand was unnecessarily rejected.");
                    for(int i=0;i<count;i++)Need(a.Cpu.Step()==4,"Scalar clocks differ.");DecodedBatchTests.Equal(a,b);
                }
            });
        }
    }
}
