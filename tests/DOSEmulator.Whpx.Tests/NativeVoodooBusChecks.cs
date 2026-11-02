// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

/// <summary>Actual native bus admission, unretired integer/x87 instructions and strict full continuation while an SST-1 transaction waits.</summary>
internal static class NativeVoodooBusChecks
{
    /// <summary>Requires independently specified memory effects and every saved CPU/device field.</summary>
    private static void Check(bool value,string why){if(!value)throw new Exception(why);}
    /// <summary>Checks original read-after-draw and a full PCI FIFO, without replacing the normal CPU or device deadlines.</summary>
    public static void Run()
    {
        foreach(string kind in new[]{"read","write","fstp"})RunCase(kind);
    }
    /// <summary>Captures before the blocked access can retire, then runs original and strict restored branches to one common future boundary.</summary>
    private static void RunCase(string kind)
    {
        var profile=new MachineProfile{Id=MachineProfile.MmxProfile166Id,Cpu=MachineProfile.P55cCpuId,CpuHz=166_000_000,RamMiB=16,
            VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId,ThreeDAccelerator=MachineProfile.VoodooGraphicsId};
        using var pc=new PcMachine(profile,null);var card=pc.Voodoo!;
        card.WriteConfiguration(0x10,0xd0000000,4);card.WriteConfiguration(4,2,4);card.WriteConfiguration(0x40,3,4);
        // Route each setup/command DWORD through the real card's decoded memory aperture.
        void W(uint register,uint value)=>pc.Memory.WriteDword(0xd0000000+register,value);
        W(0x210,0x10);W(0x214,0x20a8);W(0x218,150u<<11);W(0x20c,480u<<16|639);W(0x118,640);W(0x11c,480);W(0x110,0x200);W(0x148,0x112233);
        // A real synchronized swap keeps the head busy well beyond cold JIT startup; the cut must include an observed MMIO exit.
        W(0x128,15);int queued=kind=="read"?1:62;for(int i=0;i<queued;i++)W(0x124,0);
        byte[] code=kind switch
        {
            "read"=>[0xa1,0,0,0x40,0xd0,0xa3,0,0x70,0,0,0xf4],
            "write"=>[0xc7,0x05,0x24,0x01,0,0xd0,0,0,0,0,0xc7,0x05,0,0x70,0,0,0x78,0x56,0x34,0x12,0xf4],
            _=>[0xd9,0x1d,0,0,0x40,0xd0,0xc7,0x05,0,0x70,0,0,0x78,0x56,0x34,0x12,0xf4]
        };
        code.CopyTo(pc.Memory.Ram,0x1000);
        WhpxCpuState entry;
        using(var seed=new WhpxMappedMachine(pc.Memory,pc.Ports,pc.Pic,pc.Scheduler,WhpxCpuContract.IntelP55cMmxSubset,166_000_000))
        {
            seed.SetEntry(0,0x1000);var initial=seed.CaptureCpuState();
            entry=initial with {Registers=initial.Registers.Select(r=>r.Name switch
            {
                0x1c=>r with{Low=0x60000031},19=>r with{Low=0,High=0x409b0008ffffffff},
                18 or 20 or 21 or 22 or 23=>r with{Low=0,High=0x40930010ffffffff},
                0x1010=>r with{Low=0x8000000000000000,High=0x3fff},0x1018=>r with{Low=1UL<<32|0x37f,High=0},0x1019=>r with{Low=0},_=>r
            }).ToArray()};
        }
        using var native=WhpxPcSession.Resume(pc,new(pc.CaptureHardwareState(),pc.Memory.Ram.ToArray(),entry));
        byte[] initialRam=pc.Memory.Ram.ToArray();
        var waiting=native.RunUntilVirtualTime(50_000_000,TimeSpan.FromSeconds(3));native.Pause();var blocked=native.Capture();
        Check(waiting.MmioExits>0&&blocked.Cpu.Registers.SequenceEqual(entry.Registers)&&blocked.Ram.AsSpan().SequenceEqual(initialRam)&&card.Busy,"Blocked MMIO retired or changed CPU/RAM before the device deadline: "+kind);
        string root=Path.Combine(Path.GetTempPath(),"dose-voodoo-bus-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string save=Path.Combine(root,"waiting.whpxsav");WhpxSnapshotStore.Capture(native,save);
            native.RunUntilHalt(TimeSpan.FromSeconds(3));native.Pause();pc.Scheduler.AdvanceTo(1_000_000_000);pc.Audio.AdvanceTo(pc.Scheduler.Now);var a=native.Capture();
            uint marker=kind=="read"?0x11061106u:0x12345678u;Check(pc.Memory.ReadDword(0x7000)==marker&&!card.Busy,"Bus completion or readback differs: "+kind);
            Check(pc.Memory.ReadDword(0xd0400000)==(kind=="fstp"?0x3f800000u:0x11061106u),"Independent completed pixel oracle differs: "+kind);
            byte[] expected=initialRam.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(0x7000),marker);
            Check(a.Ram.AsSpan().SequenceEqual(expected),"An unrelated RAM byte changed while stalled: "+kind);
            if(kind=="fstp")Check((a.Cpu.Registers.Single(r=>r.Name==0x1018).Low>>27&7)==1,"Blocked x87 store did not pop exactly once");
            native.Dispose();using var restored=WhpxSnapshotStore.Load(save,Path.Combine(root,"copy.dosebranch"));var imported=restored.Capture();
            Check(imported.Ram.AsSpan().SequenceEqual(blocked.Ram)&&JsonSerializer.Serialize(imported.Cpu)==JsonSerializer.Serialize(blocked.Cpu)&&JsonSerializer.Serialize(imported.Hardware,StateValues.Json)==JsonSerializer.Serialize(blocked.Hardware,StateValues.Json),"Strict pending-access import differs: "+kind);
            restored.RunUntilHalt(TimeSpan.FromSeconds(3));restored.Pause();restored.Pc.Scheduler.AdvanceTo(1_000_000_000);restored.Pc.Audio.AdvanceTo(restored.Pc.Scheduler.Now);var b=restored.Capture();
            Check(a.Ram.AsSpan().SequenceEqual(b.Ram)&&JsonSerializer.Serialize(a.Cpu)==JsonSerializer.Serialize(b.Cpu)&&JsonSerializer.Serialize(a.Hardware,StateValues.Json)==JsonSerializer.Serialize(b.Hardware,StateValues.Json),"Whole bus-wait future differs: "+kind);
            Console.WriteLine("PASS: native SST-1 "+kind+" admission, untouched stalled CPU/16-MiB RAM, exact read/store, strict import and entire future state.");
        }
        finally{Directory.Delete(root,true);}
    }
}
