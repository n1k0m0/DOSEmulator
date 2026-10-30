// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Actual Windows-board POST masks and DOS-style direct PS/2 IRQ12/keyboard delivery.</summary>
internal static class WindowsPostCascadeTests
{
    /// <summary>Requires the independent POST or actual guest-handler result.</summary>
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>Runs the real ROM to its no-medium halt without altering either interrupt mask.</summary>
    private static PcMachine Post(bool fast, bool floppy)
    {
        var profile = new MachineProfile { Id=fast?MachineProfile.Windows95Profile133Id:MachineProfile.Windows95ProfileId,
            Cpu=MachineProfile.P54cCpuId,CpuHz=fast?133000000:100000000,RamMiB=32,
            VideoCard=MachineProfile.S3Trio64VideoId,OpticalDrive=MachineProfile.AtapiCdRomId,
            FloppyDriveA=floppy?"1440k":"none",Mouse="ps2-two-button" };
        var machine=PcMachine.CreateWithOpticalDiscs(profile,new DOSEmulator.Hardware.IReadOnlyBlockDevice?[4],null);
        machine.RunSteps(300000);
        Check(machine.Fault is null && machine.Cpu.Halted,machine.Fault??"Actual POST did not reach no-medium halt.");
        return machine;
    }

    /// <summary>Starts an own RAM guest that installs IVT handlers and enables only the slave IRQ12 mask.</summary>
    private static void StartGuest(PcMachine machine)
    {
        byte[] start = [0xfa,0x31,0xc0,0x8e,0xd8,0x8e,0xd0,0xbc,0,0x90,
            0xc7,6,0xd0,1,0,0x20,0xc7,6,0xd2,1,0,0,
            0xc7,6,0x24,0,0,0x21,0xc7,6,0x26,0,0,0,
            0xb0,0xa8,0xe6,0x64,0xb0,0x60,0xe6,0x64,0xb0,0x47,0xe6,0x60,
            0xb0,0xef,0xe6,0xa1,0xb0,0xd4,0xe6,0x64,0xb0,0xf4,0xe6,0x60,
            0xc6,6,2,0x30,0xa5,0xfb,0xf4,0xeb,0xfd];
        // IRQ12 reads only an available AUX byte and acknowledges both physical controllers.
        byte[] auxiliary = [0x50,0x53,0xe4,0x64,0x24,0x21,0x3c,0x21,0x75,0x10,
            0x31,0xdb,0x8a,0x1e,0,0x30,0xe4,0x60,0x88,0x87,0x10,0x30,0xfe,6,0,0x30,
            0xb0,0x20,0xe6,0xa0,0xe6,0x20,0x5b,0x58,0xcf];
        // IRQ1 consumes only keyboard data and preserves all registers used by this handler.
        byte[] keyboard = [0x50,0x53,0xe4,0x64,0x24,0x21,0x3c,1,0x75,0x10,
            0x31,0xdb,0x8a,0x1e,1,0x30,0xe4,0x60,0x88,0x87,0x20,0x30,0xfe,6,1,0x30,
            0xb0,0x20,0xe6,0x20,0x5b,0x58,0xcf];
        start.CopyTo(machine.Memory.Ram,0x1000);auxiliary.CopyTo(machine.Memory.Ram,0x2000);keyboard.CopyTo(machine.Memory.Ram,0x2100);
        machine.Cpu.Reset();machine.Cpu.SetSegment(Segment.Cs,0);machine.Cpu.Eip=0x1000;
        machine.RunSteps(100000,machine.Scheduler.Now+1000000);
        Check(machine.Fault is null && machine.Memory.Ram[0x3002]==0xa5,"Own guest did not enable its direct PS/2 path.");
    }

    /// <summary>Registers POST combinations and the real AUX-to-keyboard queue transition on both clocks.</summary>
    public static void AddTo(Action<string,Action> test)
    {
        foreach(bool fast in new[]{false,true}) foreach(bool floppy in new[]{false,true})
        {
            bool f=fast,d=floppy;
            test($"Windows POST cascade mask {(f?133:100)}MHz FDC{d}",()=>
            {
                using var m=Post(f,d);
                Check(m.Pic.Read(0x21,1)==(d?0xb8u:0xf8u),"POST must expose cascade and only installed IRQ6.");
                Check(m.Pic.Read(0xa1,1)==255,"POST must leave every slave source masked.");
            });
        }
        foreach(bool fast in new[]{false,true})
        {
            bool f=fast;
            test($"Windows POST cascade direct DOS IRQ12 and keyboard {(f?133:100)}MHz",()=>
            {
                using var m=Post(f,true);var acks=new List<int>();m.Pic.InterruptAcknowledged+=(irq,_,spurious)=>{if(!spurious)acks.Add(irq);};
                StartGuest(m);m.Mouse!.Inject(1,-2,1);
                Check(m.Keyboard.TryInjectTranslatedSet1([0x1e,0x9e]),"Own keyboard queue rejected two bytes.");
                m.RunSteps(100000,m.Scheduler.Now+30000000);
                Check(m.Fault is null,m.Fault??"Guest fault");
                Check(m.Memory.Ram[0x3000]==4 && m.Memory.Ram.AsSpan(0x3010,4).SequenceEqual(new byte[]{0xfa,0x29,1,0xfe}),"Guest did not consume ACK and complete actual mouse packet.");
                Check(m.Memory.Ram[0x3001]==2 && m.Memory.Ram.AsSpan(0x3020,2).SequenceEqual(new byte[]{0x1e,0x9e}),"AUX blocked subsequent real keyboard IRQ1.");
                Check(acks.Count(x=>x==12)==4 && acks.Count(x=>x==1)==2 && m.Keyboard.OutputByteCount==0,"Actual IRQ acknowledgements/queue differ.");
                Check(m.Pic.Read(0x21,1)==0xb8 && m.Pic.Read(0xa1,1)==0xef,"Guest unexpectedly changed the inherited master mask.");
            });
        }
    }
}
