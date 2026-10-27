// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class SoundBlasterResourceTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value) { if (!value) throw new Exception("SB2 resource assertion failed"); }
    /// <summary>Builds the explicit supported sound-card resource profile used by the following case.</summary>
    private static MachineProfile Profile(int address = 0x240, int irq = 9) => new() {
        SoundCard = "sb2-opl2-development", SoundBlasterBasePort = address, SoundBlasterIrq = irq };
    /// <summary>Submits the requested operation through the component's normal command interface.</summary>
    private static IpcResponse Command(MachineSession session, string command, object payload)
    {
        var reply = session.Enqueue(new(1, Guid.NewGuid().ToString("N"), command, JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions)));
        session.PumpCommands(); return reply.GetAwaiter().GetResult();
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    private static void Reset(PcMachine machine)
    {
        ushort port = (ushort)(machine.Profile.SoundBlasterBasePort + 6);
        machine.Ports.Write(port, 1, 1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now + 3000);
        machine.Ports.Write(port, 0, 1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now + 100000);
        Check(machine.Ports.Read((ushort)(port + 4), 1) == 0xaa);
    }
    /// <summary>Sends the controlled test data through the real device-facing transfer interface.</summary>
    private static void Send(PcMachine machine, params byte[] bytes)
    {
        foreach (byte value in bytes)
        {
            machine.Ports.Write((ushort)(machine.Profile.SoundBlasterBasePort + 0x0c), value, 1);
            machine.Scheduler.AdvanceTo(machine.Scheduler.Now + 1000);
        }
    }
    /// <summary>Registers the named sound blaster resource regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("old JSON sound profiles retain A220 I5 D1 and new resource values roundtrip", () =>
        {
            var old = JsonSerializer.Deserialize<MachineProfile>("{\"soundCard\":\"sb2-dsp-development\"}", IpcProtocol.JsonOptions)!;
            old.Validate(); Check(old.GetBlasterEnvironment() == "A220 I5 D1 T3");
            var requested = Profile();
            var restored = JsonSerializer.Deserialize<MachineProfile>(JsonSerializer.Serialize(requested, IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
            restored.Validate(); Check(restored == requested && restored.GetBlasterEnvironment() == "A240 I9 D1 T3");
        });
        test("SB2 preflight rejects reserved board IRQs, floppy DMA and unsupported address ranges", () =>
        {
            var invalid = new List<MachineProfile>();
            foreach (int port in new[] { -1,0x1f0,0x210,0x230,0x260,0x388,65536 }) invalid.Add(Profile() with { SoundBlasterBasePort = port });
            foreach (int irq in new[] { -1,0,1,2,4,6,8,10,11,12,13,14,15,16 }) invalid.Add(Profile() with { SoundBlasterIrq = irq });
            foreach (int dma in new[] { -1,0,2,3,4,5,8 }) invalid.Add(Profile() with { SoundBlasterDmaChannel = dma });
            foreach (var profile in invalid)
            {
                bool rejected = false; try { profile.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected);
            }
            foreach (int port in new[] { 0x220,0x240 }) foreach (int irq in new[] { 3,5,7,9 }) Profile(port,irq).Validate();
        });
        test("relocated DSP responds only at its selected ports and old reset writes cannot abort it", () =>
        {
            using var machine = PcMachine.CreateDemo(Profile()); Reset(machine);
            Check(machine.SoundBlaster!.IoBasePort == 0x240 && machine.Ports.Read(0x22c,1) == 255);
            Send(machine,0x40,131,0x80,1,0); machine.Ports.Write(0x226,1,1);
            machine.Scheduler.AdvanceTo(machine.Scheduler.Now + 250000);
            Check(machine.SoundBlaster.CompletedBlocks == 1 && machine.SoundBlaster.InterruptAsserted && machine.SoundBlaster.ResetCount == 1);
            Send(machine,0xe1); Check(machine.Ports.Read(0x24a,1) == 2 && machine.Ports.Read(0x24a,1) == 1);
        });
        test("relocated SB FM alias shares the 388h latch and timer while the old alias is unmapped", () =>
        {
            using var machine = PcMachine.CreateDemo(Profile());
            Check(machine.Ports.Read(0x228,1) == 255 && machine.Ports.Read(0x248,1) == 6);
            machine.Ports.Write(0x248,2,1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+Opl2.AddressWaitNanoseconds);
            machine.Ports.Write(0x389,255,1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+Opl2.DataWaitNanoseconds);
            Check(machine.Opl!.InspectRegister(2) == 255);
            machine.Ports.Write(0x228,2,1); machine.Ports.Write(0x229,0,1);
            machine.Ports.Write(0x388,4,1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+Opl2.AddressWaitNanoseconds);
            machine.Ports.Write(0x249,1,1); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+100000);
            Check(machine.Ports.Read(0x248,1) == 0xc6 && machine.Ports.Read(0x388,1) == 0xc6);
            Reset(machine); Check(machine.Opl.Timer1Overflows >= 1 && machine.Opl.InspectRegister(2) == 255);
            Check(machine.Opl.RejectedWrites == 0 && machine.FmIoTiming!.Accesses > 0);
        });
        test("all SB2 resource pairs deliver real DSP blocks through the selected master or slave PIC vector", () =>
        {
            foreach (int port in new[] { 0x220,0x240 }) foreach (int irq in new[] { 3,5,7,9 })
            {
                using var machine = PcMachine.CreateDemo(Profile(port,irq)); Reset(machine);
                machine.Pic.Write(0x21,(uint)(255 ^ (1 << (irq < 8 ? irq : 2))),1);
                machine.Pic.Write(0xa1,(uint)(irq < 8 ? 255 : 255 ^ (1 << (irq-8))),1);
                for (int block = 0; block < 2; block++)
                {
                    Send(machine,0x80,0,0); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+256000);
                    Check(machine.Pic.HasPending && machine.Pic.Acknowledge() == (irq < 8 ? 8+irq : 0x70+irq-8));
                    machine.Ports.Read((ushort)(port+0x0e),1);
                    if (irq >= 8) machine.Pic.Write(0xa0,0x20,1);
                    machine.Pic.Write(0x20,0x20,1);
                    Check(!machine.Pic.HasPending && !machine.SoundBlaster!.InterruptAsserted);
                }
                Check(machine.SoundBlaster!.CompletedBlocks == 2);
            }
        });
        test("AT IRQ9 needs both slave unmasking and the master cascade and is not IRQ5", () =>
        {
            using var machine = PcMachine.CreateDemo(Profile()); Reset(machine);
            machine.Pic.Write(0x21,0xdf,1); Send(machine,0x80,0,0); machine.Scheduler.AdvanceTo(machine.Scheduler.Now+256000);
            Check(!machine.Pic.HasPending); machine.Pic.Write(0xa1,0xfd,1); Check(!machine.Pic.HasPending);
            machine.Pic.Write(0x21,0xfb,1); Check(machine.Pic.HasPending && machine.Pic.Acknowledge() == 0x71);
        });
        test("resource prepare is read-only and committed change replaces RAM while retaining CMOS and media", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo(Profile(0x220,5)));
            var before = session.Machine; before.Memory.WriteByte(0x6000,0x5a);
            before.Rtc.Write(0x70,0x38,1); before.Rtc.Write(0x71,0xa7,1);
            Check(Command(session,"PrepareConfigurationChange",Profile()).Success);
            Check(ReferenceEquals(before,session.Machine) && session.Revision == 1 && before.Memory.ReadByte(0x6000) == 0x5a);
            Check(Command(session,"CommitConfigurationChange",new ConfigurationPlan(1,Profile(),true)).Success);
            Check(!ReferenceEquals(before,session.Machine) && session.Revision == 2 && session.Machine.Memory.ReadByte(0x6000) == 0);
            session.Machine.Rtc.Write(0x70,0x38,1); Check(session.Machine.Rtc.Read(0x71,1) == 0xa7);
            Check(session.Machine.Ports.Read(0x22c,1) == 255 && session.Machine.Ports.Read(0x24c,1) == 128);
            session.Machine.RunSteps(100000); Check(session.Machine.Fault is null && session.Machine.Memory.ReadWord(0x500) == 0xbeef);
        });
        test("invalid and stale resource commits cannot alter current device state or its pending block", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo(Profile())); var before = session.Machine; Reset(before);
            Send(before,0x40,131,0x80,0,0); before.Memory.WriteByte(0x6000,0x5a);
            Check(!Command(session,"PrepareConfigurationChange",Profile() with { SoundBlasterIrq = 6 }).Success);
            Check(!Command(session,"CommitConfigurationChange",new ConfigurationPlan(1,Profile() with { SoundBlasterDmaChannel = 2 },true)).Success);
            Check(Command(session,"CommitConfigurationChange",new ConfigurationPlan(0,Profile(0x220,7),true)).ErrorCode == "StaleConfiguration");
            Check(ReferenceEquals(before,session.Machine) && session.Revision == 1 && before.Memory.ReadByte(0x6000) == 0x5a);
            before.Scheduler.AdvanceTo(before.Scheduler.Now+125000); Check(before.SoundBlaster!.CompletedBlocks == 1);
        });
        test("SB2 resource power cycles retain private IDE and floppy overlays without changing their bases", () =>
        {
            string directory = Path.Combine(Environment.CurrentDirectory,"artifacts","tests","sb2-resources-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string ide = Path.Combine(directory,"own-ide.img"), floppy = Path.Combine(directory,"own-floppy.img");
            File.WriteAllBytes(ide,new byte[4*512]); File.WriteAllBytes(floppy,new byte[1474560]);
            var hdd = new CopyOnWriteDisk(ide,Path.Combine(directory,"ide.cow"));
            var fdd = new CopyOnWriteDisk(floppy,Path.Combine(directory,"floppy.cow"));
            hdd.WriteSector(2,Enumerable.Repeat((byte)0x39,512).ToArray()); fdd.WriteSector(1,Enumerable.Repeat((byte)0x6a,512).ToArray());
            using var session = new MachineSession(new PcMachine(Profile(0x220,5) with { FloppyDriveA="1440k" },hdd,fdd));
            Check(Command(session,"CommitConfigurationChange",new ConfigurationPlan(1,Profile() with { FloppyDriveA="1440k" },true)).Success);
            var machine = session.Machine;
            machine.Ata.Write(0x1f6,0xe0,1); machine.Ata.Write(0x1f2,1,1); machine.Ata.Write(0x1f3,2,1); machine.Ata.Write(0x1f7,0x20,1);
            machine.Scheduler.AdvanceTo(5000); Check(machine.Ata.Read(0x1f0,2) == 0x3939);
            machine.Dma.Write(0x0c,0,1); machine.Dma.Write(4,0,1); machine.Dma.Write(4,0x30,1);
            machine.Dma.Write(5,255,1); machine.Dma.Write(5,1,1); machine.Dma.Write(0x0b,0x46,1); machine.Dma.Write(0x0a,2,1);
            machine.Floppy.Write(0x3f2,0x1c,1);
            foreach (byte value in new byte[] {0x46,0,0,0,2,2,2,0x1b,0xff}) machine.Floppy.Write(0x3f5,value,1);
            machine.Scheduler.AdvanceTo(machine.Scheduler.Now+400_000_000);
            Check(machine.Floppy.ReadSectorCount == 1 && machine.Memory.Ram.AsSpan(0x3000,512).ToArray().All(value=>value==0x6a));
            Check(File.ReadAllBytes(ide).All(value=>value==0) && File.ReadAllBytes(floppy).All(value=>value==0));
        });
        test("remembered resources do not install phantom DSP or alias ports with no card or AdLib only", () =>
        {
            foreach (string card in new[] { "none","adlib-opl2-development" })
            {
                using var machine = PcMachine.CreateDemo(Profile() with { SoundCard=card });
                Check(machine.SoundBlaster is null && machine.Ports.Read(0x22c,1) == 255 && machine.Ports.Read(0x24c,1) == 255);
                Check(machine.Ports.Read(0x228,1) == 255 && machine.Ports.Read(0x248,1) == 255);
                Check(machine.Ports.Read(0x388,1) == (card == "none" ? 255u : 6u));
            }
        });
    }
}
