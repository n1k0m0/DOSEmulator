// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Security.Cryptography;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;


/// <summary>Compares bounded VGA loop execution with the actual native CPU, including every register, RAM byte, VGA member and rejected guard.</summary>
internal static class NativeVideoBatchChecks
{
    /// <summary>Executes both loop families and partial-restart controls before checking pure guard rejection.</summary>
    internal static void Run()
    {
var checks = new List<object>();
foreach(bool word in new[] { false,true })
foreach (int count in new[] { 1,2,31,32,33,64,127 })
foreach (uint stride in new uint[] { 2,320 })
foreach (uint step in new uint[] { 0,0x02000000,0xfe000000,0x030c051c })
{
    if(word&&stride!=2)continue;
    var ordinary = Execute(count,stride,step,false,word:word);
    var batched = Execute(count,stride,step,true,word:word);
    if (ordinary.ram != batched.ram || ordinary.cpu != batched.cpu || ordinary.video != batched.video || batched.loops != count)
        throw new InvalidOperationException(JsonSerializer.Serialize(new { count,stride,step,ordinary,batched },new JsonSerializerOptions { IncludeFields=true }));
    checks.Add(new { word,count,stride,step,ordinary,batched });
}
foreach(bool word in new[] { false,true })
foreach(int count in new[] { 2,3,33 })
{
    var ordinary=Execute(count,word?2U:320U,0,false,sparseLookup:true,word:word);var batched=Execute(count,word?2U:320U,0,true,sparseLookup:true,word:word);
    if(ordinary.ram!=batched.ram||ordinary.cpu!=batched.cpu||ordinary.video!=batched.video||batched.loops<=0||batched.loops>=count)
        throw new InvalidOperationException("Partial preflight restart differs: "+JsonSerializer.Serialize(new { count,ordinary,batched },new JsonSerializerOptions { IncludeFields=true }));
    checks.Add(new { word,partialLookupRestart=count,ordinary,batched });
}
foreach(int count in new[]{1,2,31,32,33,64,127})foreach(uint high in new uint[]{0,0x12340000,0xffff0000})
{
    var ordinary=Execute(count,2,0,false,gather:true,siHigh:high);var batched=Execute(count,2,0,true,gather:true,siHigh:high);
    if(ordinary.ram!=batched.ram||ordinary.cpu!=batched.cpu||ordinary.video!=batched.video||batched.loops!=count)throw new InvalidOperationException("Gathered pixel/native register result differs.");
    checks.Add(new { gatheredPairs=count,siHigh=high,ordinary,batched });
}
{
    var ordinary=Execute(3,2,0,false,gather:true,sparseLookup:true);var batched=Execute(3,2,0,true,gather:true,sparseLookup:true);
    if(ordinary.ram!=batched.ram||ordinary.cpu!=batched.cpu||ordinary.video!=batched.video||batched.loops!=3||batched.mmio<4)throw new InvalidOperationException("Gathered-pixel RAM/VGA source restart differs: "+JsonSerializer.Serialize(new{ordinary,batched},new JsonSerializerOptions{IncludeFields=true}));
    checks.Add(new { gatheredPartialRestart=true,ordinary,batched });
}
foreach(bool word in new[] { false,true })for(int guard=0;guard<25;guard++) Execute(1,2,0,true,guard,word:word);
for(int guard=0;guard<25;guard++)if(guard is not (15 or 16 or 17 or 18))Execute(1,2,0,true,guard,gather:true);
Console.WriteLine("PASS: "+checks.Count+" whole native CPU/RAM/VGA loop comparisons, including seven partial restarts; 71 pure guard rejections.");
Environment.SetEnvironmentVariable("DOSE_WHPX_VIDEO_BATCH",null);


    }
// Builds a complete own flat-32 fixture, then compares all native registers, RAM and VGA fields.
static (string ram,string cpu,string video,long loops,int mmio,double milliseconds) Execute(int count,uint stride,uint step,bool batched,int guard=-1,bool sparseLookup=false,bool word=false,bool gather=false,uint siHigh=0)
{
    Environment.SetEnvironmentVariable("DOSE_WHPX_VIDEO_BATCH",batched?"1":"0");
    var memory=new MemoryBus(2<<20);var clock=new DeviceScheduler();var vga=new VgaAdapter(clock);
    memory.Map(0xa0000,0x20000,vga,"own real VGA");
    vga.Write(0x3c4,2,1);vga.Write(0x3c5,15,1);vga.Write(0x3ce,6,1);vga.Write(0x3cf,5,1);
    byte[] code=[0x8b,0xcd,0x81,0xc5,0,0,0,0,0x88,0x07,0xc1,0xe9,0x19,0x8b,0xd5,
        0x81,0xc5,0,0,0,0,0x88,0x5f,1,0xc1,0xea,0x19,0x8a,0x04,0x0e,
        0x81,0xc7,0,0,0,0,0x8a,0x1c,0x16,0xff,0x0d,0,0x30,0,0,0x8a,0x00,0x8a,0x1b,0x75,0xcd,0xf4];
    if(word)code=[0x0f,0xa4,0xe9,0x16,0x8a,0x33,0x0f,0xa4,0xe9,6,0x81,0xc5,0,0,0,0,
        0x81,0xe1,0xff,0x0f,0,0,0x66,0x89,0x17,0x0f,0xa4,0xea,0x16,0x83,0xc7,2,0x0f,0xa4,0xea,6,
        0x81,0xc5,0,0,0,0,0x81,0xe2,0xff,0x0f,0,0,0x8a,4,0x0e,0x8a,0x1c,0x16,0xff,0x0d,0,0x30,0,0,0x8a,0x10,0x75,0xc0,0xf4];
    if(gather)code=[0x31,0xc9,0x8a,0x48,4,0x66,0x0f,0xb6,0x30,0xc1,0xe1,8,0x83,0xc2,2,0x01,0xf1,0x83,0xc0,8,0x66,0x89,0x4a,0xfe,0x4b,0x75,0xe5,0xf4];
    else
    {
        BitConverter.GetBytes(step).CopyTo(code,word?12:4);BitConverter.GetBytes(sparseLookup?(word?0x800U:0x80000000U):step).CopyTo(code,word?38:17);
        if(!word)BitConverter.GetBytes(stride).CopyTo(code,32);
    }
    code.CopyTo(memory.Ram,0x1000);BitConverter.GetBytes(count).CopyTo(memory.Ram,0x3000);
    for(int i=0;i<256;i++){memory.Ram[0x4000+i]=(byte)(i*11);memory.Ram[0x6000+i]=(byte)(i^0xa5);memory.Ram[0x6100+i]=(byte)(i^0x69);}
    if(sparseLookup)for(int i=0;i<64;i++)memory.Ram[memory.Ram.Length-64+i]=(byte)(i*11);
    if(gather)for(int i=0;i<2048;i++)memory.Ram[(sparseLookup?0x9f800:0x4000)+i]=(byte)(i*13);
    if(guard==14)memory.Ram[0x1000]=0x90;
    if(guard is 15 or 16)BitConverter.GetBytes(guard==15?0x1020:0xa0000).CopyTo(memory.Ram,word?0x1038:0x1029);
    if(guard is 23 or 24)BitConverter.GetBytes(guard==23?0:4097).CopyTo(memory.Ram,0x3000);
    using var cpu=new WhpxMappedMachine(memory,new IoPortBus(),new Pic8259(),clock);
    cpu.SetEntry(0,0x1000);var state=cpu.CaptureCpuState();var regs=state.Registers;
    // Replaces one authoritative fixture register without touching the software CPU.
    void Set(uint n,ulong low,ulong high=0)=>regs[Array.FindIndex(regs,r=>r.Name==n)]=new(n,low,high);
    Set(0x13,0,0xffffffffUL|(8UL<<32)|(0xc09bUL<<48));
    foreach(uint n in new uint[]{0x12,0x14,0x15})Set(n,0,0xffffffffUL|(0x10UL<<32)|(0xc093UL<<48));
    Set(0x1c,cpu.GetRegister(0x1c)|1);Set(0,0x6037);Set(3,0x61cd);Set(5,0);Set(6,0x4000);Set(7,0xa0100);Set(4,0x8000);
    if(sparseLookup)Set(6,(uint)memory.Ram.Length-64);
    if(gather){Set(0,sparseLookup?0x9fff0UL:0x4000UL);Set(3,(uint)count);Set(2,0xa0100);Set(6,siHigh|0xaa55);}
    if(guard>=0)
    {
        Set(0x10,gather?0x1014UL:word?0x1016UL:0x1008UL);
        if(gather){Set(0,0x4008);Set(2,0xa0102);if(guard==23)Set(3,0);if(guard==24)Set(3,4097);}
        switch(guard)
        {
            case 0:Set(0x11,0x102);break;case 1:Set(0x11,0x10002);break;
            case 2:Set(0x26,0x401);break;case 3:Set(0x1c,cpu.GetRegister(0x1c)|0x80000001);break;
            case 4:Set(0x13,0x10000,0xffffffffUL|(8UL<<32)|(0xc09bUL<<48));break;
            case 5:Set(0x15,0x10000,0xffffffffUL|(0x10UL<<32)|(0xc093UL<<48));break;
            case 6:Set(0x13,0,0xffffUL|(8UL<<32)|(0x409bUL<<48));break;
            case 7:Set(0x15,0,0xffffUL|(0x10UL<<32)|(0x4093UL<<48));break;
            case 8:Set(0x13,0,0xffffffffUL|(11UL<<32)|(0xc0fbUL<<48));Set(0x14,0,0xffffffffUL|(0x13UL<<32)|(0xc0f3UL<<48));break;
            case 9:Set(0x13,0,0xffffffffUL|(8UL<<32)|(0x809bUL<<48));break;
            case 10:Set(0x15,0,0xffffffffUL|(0x10UL<<32)|(0xc091UL<<48));break;
            case 11:Set(0x11,0x202);Set(0x80000000,0x200001);break;case 12:Set(0x80000002,0x60001);break;case 13:Set(0x80000001,1);break;
            case 17:Set(6,0x3000);break;case 18:Set(0,0x3037);break;case 19:if(gather)Set(2,0xc0002);else Set(7,0xc0000);break;
            case 21:memory.A20Enabled=false;break;
        }
    }
    try { cpu.RestoreCpuState(state with { Registers=regs }); }
    catch(Exception e) { throw new InvalidOperationException("Own fixture guard="+guard,e); }
    if(guard>=0)
    {
        var internalMemory=typeof(WhpxMappedMachine).GetField("_memory",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(cpu)!;
        if(guard!=22)internalMemory.GetType().GetMethod("ActivateSharedRam",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(internalMemory,null);
        object privateBatch=typeof(WhpxMappedMachine).GetField("_videoBatch",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(cpu)!;
        byte[] exit=new byte[4096];exit[0]=1;exit[10]=(byte)(gather?4:word?3:2);exit[48]=exit[10];exit[52]=(byte)(word||gather?0x66:0x88);exit[53]=(byte)(word||gather?0x89:0x07);exit[54]=(byte)(gather?0x4a:0x17);exit[55]=0xfe;exit[68]=1;
        BitConverter.GetBytes(gather?0x1014UL:word?0x1016UL:0x1008UL).CopyTo(exit,32);BitConverter.GetBytes(guard==20?0xa0101UL:guard==19?0xc0000UL:0xa0100UL).CopyTo(exit,72);
        string beforeCpu=JsonSerializer.Serialize(cpu.CaptureCpuState()),beforeVideo=JsonSerializer.Serialize(vga.SaveState(),StateValues.Json);
        byte[] beforeRam=memory.PhysicalRam.ToArray();
        bool accepted=(bool)privateBatch.GetType().GetMethod("TryRun",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(privateBatch,[exit])!;
        bool changed=!beforeRam.AsSpan().SequenceEqual(memory.PhysicalRam)||beforeCpu!=JsonSerializer.Serialize(cpu.CaptureCpuState())||beforeVideo!=JsonSerializer.Serialize(vga.SaveState(),StateValues.Json)||clock.Now!=0;
        internalMemory.GetType().GetMethod("DeactivateSharedRam",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(internalMemory,null);
        if(accepted||changed)
            throw new InvalidOperationException("Unsafe acceptance or guard side effects, guard="+guard);
        return ("rejected","unchanged","unchanged",0,0,0);
    }
    var watch=Stopwatch.StartNew();var run=cpu.RunUntilHalt(TimeSpan.FromSeconds(5),10000);watch.Stop();
    object batch=typeof(WhpxMappedMachine).GetField("_videoBatch",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(cpu)!;
    long loops=(long)batch.GetType().GetProperty("Loops")!.GetValue(batch)!;

    if(!cpu.CaptureCpuState().Halted||cpu.GetRegister(0x10)!=(gather?0x101cUL:word?0x1041UL:0x1034UL)||(gather?cpu.GetRegister(3)!=0:BitConverter.ToUInt32(memory.Ram,0x3000)!=0))
        throw new InvalidOperationException("Own pair loop did not reach its natural HLT.");
    return (Hash(memory.Ram),Hash(JsonSerializer.SerializeToUtf8Bytes(cpu.CaptureCpuState())),Hash(JsonSerializer.SerializeToUtf8Bytes(vga.SaveState(),StateValues.Json)),loops,run.MmioExits,watch.Elapsed.TotalMilliseconds);
}
// Produces a digest of the complete retained image, without field exclusions.
static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));

}
