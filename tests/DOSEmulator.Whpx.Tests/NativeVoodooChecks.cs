// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using System.Runtime.InteropServices;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Runtime;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Own native protected-mode MMX/MMIO guest and strict active SST-1 full-PC snapshot continuation.</summary>
internal static class NativeVoodooChecks
{
    /// <summary>Requires actual guest-visible data or complete resumed state equality.</summary>
    private static void Check(bool value,string why) { if(!value) throw new Exception(why); }
    /// <summary>Constructs the same explicitly limited additional-card profile for native and scalar controls.</summary>
    private static MachineProfile Profile() => new(){Id=MachineProfile.MmxProfile166Id,Cpu=MachineProfile.P55cCpuId,CpuHz=166_000_000,RamMiB=16,
        VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId,ThreeDAccelerator=MachineProfile.VoodooGraphicsId};
    /// <summary>Reads only the bounded original client embedded by the independently assembled test fixture builder.</summary>
    private static byte[] Code()
    { using var source=typeof(NativeVoodooChecks).Assembly.GetManifestResourceStream("MMX-VOODOO.BIN")??throw new Exception("Missing own guest resource");using var output=new MemoryStream();source.CopyTo(output);return output.ToArray(); }
    /// <summary>Checks exact CPUID, original PCI enumeration, MMX saturation and the guest's natural terminal marker.</summary>
    private static void Proof(PcMachine pc)
    {
        Check(pc.Memory.ReadDword(0x7000)==0x543 && pc.Memory.ReadDword(0x7004)==0x00800135,"Guest CPUID differs");
        Check(pc.Memory.ReadDword(0x7008)==0x012cffff && pc.Memory.ReadDword(0x700c)==0x0001ffff,"Guest packed saturated sum differs");
        Check(pc.Memory.ReadDword(0x7010)==0x0001121a && pc.Memory.ReadDword(0x7014)==0x314f4f56,"Guest actual PCI or terminal proof differs");
    }
    /// <summary>Computes each scene pixel from independent integer half-plane equations, setup constants and explicit RGB565/gamma arithmetic.</summary>
    private static uint ExpectedPixel(int x,int y)
    {
        int red=32,green=48,blue=80;
        int dx=x-80,dy=y-60;
        if(dx>=0&&dy>=0&&6*dx+8*dy+7<2880)
        {red=255;green=(255*4096/480)*dx/4096;blue=(255*4096/360)*dy/4096;}
        dx=x-380;dy=y-160;
        if(dx>=0&&dy>=0&&20*dx+22*dy+21<4400)
        {int u=((256*262144/220)*dx)>>(18+3),v=((256*262144/200)*dy)>>(18+3);bool greenTexel=((u/4)^(v/4))%2!=0;red=0;green=greenTexel?255:0;blue=greenTexel?0:255;}
        red>>=3;green>>=2;blue>>=3;
        red=red*8+red/4;green=green*4+green/16;blue=blue*8+blue/4;
        // Interpolates the independent nominal identity gamma knots after RGB565 expansion.
        int Gamma(int value)=>((value/8*8)*(8-value%8)+Math.Min(255,(value/8+1)*8)*(value%8))/8;
        return 0xff000000u|(uint)Gamma(red)<<16|(uint)Gamma(green)<<8|(uint)Gamma(blue);
    }
    /// <summary>Runs actual MMX/PCI/MMIO on hardware and compares the later rendered image with independently vector-qualified scalar execution.</summary>
    public static void Run()
    {
        byte[] code=Code(); using var pc=new PcMachine(Profile(),null); code.CopyTo(pc.Memory.Ram,0x1000);
        WhpxCpuState entry;
        using(var seed=new WhpxMappedMachine(pc.Memory,pc.Ports,pc.Pic,pc.Scheduler,WhpxCpuContract.IntelP55cMmxSubset,166_000_000))
        { seed.SetEntry(0,0x1000);entry=seed.CaptureCpuState(); }
        using var native=WhpxPcSession.Resume(pc,new(pc.CaptureHardwareState(),pc.Memory.Ram.ToArray(),entry));
        var run=native.RunUntilHalt(TimeSpan.FromSeconds(5)); native.Pause(); Proof(pc);
        Check(run.MmioExits>=25 && run.IoExits>=8 && pc.Voodoo!.Busy,$"Actual guest MMIO={run.MmioExits}, I/O={run.IoExits}, busy={pc.Voodoo!.Busy}, time={pc.Scheduler.Now}, deadline={pc.Voodoo.CompletionNanoseconds}");
        string root=Path.Combine(Path.GetTempPath(),"dose-native-voodoo-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string save=Path.Combine(root,"active.whpxsav"); WhpxSnapshotStore.Capture(native,save);
            ulong[] boundaries=[pc.Scheduler.Now,pc.Voodoo!.CompletionNanoseconds,pc.Scheduler.Now+5_000_000_000UL];
            var baseline=new List<WhpxPcCheckpoint>();
            foreach(ulong at in boundaries)
            {
                // One native owner runs at a time. Each baseline capture owns all its bytes, never a mutable observer shared with the replay.
                pc.Scheduler.AdvanceTo(at);pc.Audio.AdvanceTo(at);baseline.Add(native.Capture());
            }
            var frame=pc.CaptureFrame(); native.Dispose();
            using var restored=WhpxSnapshotStore.Load(save,Path.Combine(root,"copy.dosebranch"));
            int comparisons=0;
            foreach(ulong at in boundaries)
            {
                restored.Pc.Scheduler.AdvanceTo(at);restored.Pc.Audio.AdvanceTo(at);
                var a=baseline[comparisons];var b=restored.Capture();
                Check(a.Ram.AsSpan().SequenceEqual(b.Ram) && JsonSerializer.Serialize(a.Hardware,StateValues.Json)==JsonSerializer.Serialize(b.Hardware,StateValues.Json) &&
                    JsonSerializer.Serialize(a.Cpu)==JsonSerializer.Serialize(b.Cpu),"Full active native CPU/RAM/FBI/TMU/queue/device/scheduler continuation differs");comparisons++;
            }
            Proof(restored.Pc); Check(!pc.Voodoo!.Busy,"Queued draw/swap never completed");
            using var scalar=new PcMachine(Profile(),null); code.CopyTo(scalar.Memory.Ram,0x1000);scalar.Cpu.SetSegment(DOSEmulator.Cpu.X86.Segment.Cs,0);scalar.Cpu.Eip=0x1000;
            for(int i=0;i<20000&&!scalar.Cpu.Halted;i++) scalar.RunSteps(1); Proof(scalar); Check(scalar.Cpu.Halted,"Scalar original guest did not terminate");
            scalar.Scheduler.AdvanceTo(5_100_000_000);scalar.Audio.AdvanceTo(scalar.Scheduler.Now);
            var reference=scalar.CaptureFrame();
            Check(frame.Width==640&&frame.Height==480&&frame.ArgbPixels.AsSpan().SequenceEqual(reference.ArgbPixels),"Actual native guest image differs from scalar reference");
            for(int y=0;y<480;y++)for(int x=0;x<640;x++)Check(frame.ArgbPixels[y*640+x]==ExpectedPixel(x,y),$"Independent whole-pixel scene differs at {x},{y}");
            Check(pc.Memory.ReadDword(0xd000014c)==108400&&pc.Memory.ReadDword(0xd000015c)==108400,"Actual Gouraud triangle coverage/counters differ");
            if(Environment.GetEnvironmentVariable("DOSE_VOODOO_IMAGE_PATH") is string image)
                File.WriteAllBytes(image,MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan()).ToArray());
            Console.WriteLine($"PASS: own {code.Length}-byte actual native MMX/PCI/SST-1 client, {run.MmioExits} MMIO/{run.IoExits} port exits, 307200 whole pixels and {comparisons} complete active snapshot comparisons.");
        }
        finally { Directory.Delete(root,true); }
        foreach(int mhz in new[]{166,233}) Boot(mhz);
    }
    /// <summary>Boots the own demo through the actual reset ROM, BIOS FDC/IRQ6/DMA and floppy boot sector on both MMX profile clocks.</summary>
    private static void Boot(int mhz)
    {
        using var stream=typeof(NativeVoodooChecks).Assembly.GetManifestResourceStream("MMX-VOODOO.IMG")??throw new Exception("Missing own demo floppy");
        using var output=new MemoryStream();stream.CopyTo(output);
        var profile=Profile().WithMmxCpu(true,mhz) with {FloppyDriveA="1440k",BootDevice="floppy-a"};
        using var pc=new PcMachine(profile,null,new MemoryDisk(output.ToArray(),"Own MMX/SST-1 demo floppy"));using var native=WhpxPcSession.Start(pc);
        Check(native.GetRegister(2)==0x543,"Native MMX reset signature differs");
        native.RunUntilVirtualTime(5_500_000_000,TimeSpan.FromSeconds(9),100000);native.Pause();Proof(pc);
        Check(pc.Board.Post10Count==1 && pc.Voodoo is {Busy:false,OutputEnabled:true},"Actual BIOS/floppy boot did not finish the scene");
        var frame=pc.CaptureFrame();
        for(int y=0;y<480;y++)for(int x=0;x<640;x++)Check(frame.ArgbPixels[y*640+x]==ExpectedPixel(x,y),$"Booted MMX/Voodoo scene differs at {x},{y}");
        Console.WriteLine($"PASS: actual {mhz}-MHz MMX/SST-1 ROM/FDC/floppy cold start, POST 1, original finite client and 307200 independent scene pixels.");
    }
}
