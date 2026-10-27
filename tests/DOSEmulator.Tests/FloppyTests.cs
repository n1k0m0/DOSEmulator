// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class FloppyTests
{
    private sealed class Rig : IDisposable
    {
        public readonly MemoryBus Memory = new(2 * 1048576);
        public readonly DeviceScheduler Clock = new();
        public readonly Dma8237 Dma;
        public readonly Pic8259 Pic = new();
        public readonly Fdc82077 Fdc;
        /// <summary>Builds the isolated rig fixture and initializes its caller-owned test state.</summary>
        public Rig(IReadOnlyBlockDevice? medium = null)
        {
            Dma = new(Memory);
            Fdc = new(Clock, Dma, high => Pic.SetLine(6, high), FloppyGeometry.FromId("1440k"), FloppyGeometry.FromId("1440k"),
                medium ?? new MemoryDisk(Image(), "A"), new MemoryDisk(Image(0xbb), "B"));
            Fdc.Write(0x3f2, 0x1c, 1); Send(3, 0xdf, 2);
        }
        /// <summary>Sends the controlled test data through the real device-facing transfer interface.</summary>
        public void Send(params byte[] bytes) { foreach (byte value in bytes) Fdc.Write(0x3f5, value, 1); }
        /// <summary>Publishes the device command's result bytes and interrupt state.</summary>
        public byte[] Result(int count = 7) => Enumerable.Range(0, count).Select(_ => (byte)Fdc.Read(0x3f5, 1)).ToArray();
        /// <summary>Advances the fixture's virtual device clock by the requested bounded interval.</summary>
        public void Wait(ulong ns = 400_000_000) => Clock.AdvanceTo(Clock.Now + ns);
        /// <summary>Programs the fixture's actual DMA controller rather than injecting a completed transfer.</summary>
        public void ProgramDma(int count, bool read = true, int address = 0x2000)
        {
            Dma.Write(0x0a, 6, 1); Dma.Write(0x0c, 0, 1); Dma.Write(4, (uint)address & 255, 1); Dma.Write(4, (uint)(address >> 8) & 255, 1);
            Dma.Write(0x81, (uint)address >> 16, 1); Dma.Write(5, (uint)(count - 1) & 255, 1); Dma.Write(5, (uint)(count - 1) >> 8, 1);
            Dma.Write(0x0b, read ? 0x46u : 0x4au, 1); Dma.Write(0x0a, 2, 1);
        }
        /// <summary>Releases the resources or ownership lease held by this instance.</summary>
        public void Dispose() => Fdc.Dispose();
    }
    /// <summary>Builds the explicit private raw-image bytes used by this test case.</summary>
    private static byte[] Image(byte? fill = null)
    {
        byte[] image = new byte[1474560];
        for (int sector = 0; sector < 2880; sector++) image.AsSpan(sector * 512, 512).Fill(fill ?? (byte)sector);
        return image;
    }
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "Floppy assertion failed.") { if (!value) throw new Exception(message); }
    /// <summary>Requires the supplied operation to throw the selected exception type.</summary>
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
    }
    /// <summary>Creates fresh private base/journal fixture files for the persistence assertions.</summary>
    private static (string Base, string Overlay) DiskFiles()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "floppy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); string image = Path.Combine(directory, "base.img"); File.WriteAllBytes(image, Image());
        return (image, Path.Combine(directory, "private.cow"));
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine machine, ushort ax)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.Eip = 0x1000; machine.Memory.WriteByte(0x1000, 0xf4);
        machine.Cpu.SetRegister(Register.Ax, ax); machine.Cpu.EnterRealModeInterrupt(0x13); machine.RunSteps(5_000_000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "BIOS did not return.");
    }
    /// <summary>Registers the named floppy regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("floppy BIOS DMA and CPU bounce copies follow a memory-manager-relocated EBDA",()=>
        {
            using var m=new PcMachine(new(){FloppyDriveA="1440k"},new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(),"own boot"),new MemoryDisk(Image(),"own floppy"));
            m.RunSteps(100000);Check(m.Fault is null && m.Cpu.Halted);
            m.Memory.Ram.AsSpan(0x9fc00,1024).CopyTo(m.Memory.Ram.AsSpan(0xe920));
            m.Memory.Ram.AsSpan(0x9fc00,1024).Fill(0xa5);m.Memory.WriteWord(0x40e,0x0e92);m.Memory.WriteWord(0x413,640);
            m.Cpu.SetSegment(Segment.Es,0x5000);m.Cpu.SetRegister(Register.Bx,0);m.Cpu.SetRegister(Register.Cx,2);m.Cpu.SetRegister(Register.Dx,0);
            Bios(m,0x0201);Check((m.Cpu.Eflags&1)==0 && m.Memory.Ram.AsSpan(0x50000,512).ToArray().All(b=>b==1));
            Check(m.Memory.Ram.AsSpan(0x9fc00,1024).ToArray().All(b=>b==0xa5) && m.Memory.Ram.AsSpan(0xeb20,512).ToArray().All(b=>b==1));
        });
        test("FDC reset IRQ exposes four sense statuses then invalid-command status", () =>
        {
            using var r = new Rig(); r.Fdc.Write(0x3f2, 0, 1); Check(r.Fdc.Read(0x3f4, 1) == 0);
            r.Fdc.Write(0x3f2, 0x0c, 1); r.Wait(1_000_000); Check(r.Fdc.InterruptAsserted);
            for (int drive = 0; drive < 4; drive++) { r.Send(8); Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { (byte)(0xc0 | drive), 0 })); }
            r.Send(8); Check(r.Result(1)[0] == 0x80 && !r.Fdc.InterruptAsserted && r.Fdc.Read(0x3f4, 1) == 0x80);
        });
        test("FDC DMA read observes motor delay terminal count result phase and IRQ", () =>
        {
            using var r = new Rig(); r.ProgramDma(512); r.Send(0x46, 0, 0, 0, 2, 2, 18, 0x1b, 0xff);
            Check(r.Fdc.Read(0x3f4, 1) == 0x10); r.Wait(300_000_000); Check(r.Fdc.ReadSectorCount == 0);
            r.Wait(12_000_000); Check(r.Fdc.InterruptAsserted && r.Fdc.Read(0x3f4, 1) == 0xd0);
            byte[] result = r.Result(); Check(result[0] == 0 && result[1] == 0 && result[5] == 3);
            Check(r.Memory.Ram.AsSpan(0x2000, 512).ToArray().All(b => b == 1)); Check(!r.Fdc.InterruptAsserted && r.Dma.IsTerminalCount(2));
        });
        test("FDC multi-track DMA crosses from head zero to head one", () =>
        {
            using var r = new Rig(); r.ProgramDma(1024); r.Send(0xc6, 0, 0, 0, 18, 2, 18, 0x1b, 0xff); r.Wait();
            byte[] result = r.Result(); Check(result[0] == 4 && result[1] == 0 && r.Fdc.ReadSectorCount == 2);
            Check(r.Memory.ReadByte(0x2000) == 17 && r.Memory.ReadByte(0x2200) == 18);
        });
        test("FDC drive B reads its own medium and never aliases a mismatched selection", () =>
        {
            using var r = new Rig(); r.Fdc.Write(0x3f2, 0x2d, 1); r.ProgramDma(512);
            r.Send(0x46, 1, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait(); Check(r.Result()[1] == 0 && r.Memory.ReadByte(0x2000) == 0xbb);
            r.ProgramDma(512); r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait();
            byte[] error = r.Result(); Check((error[0] & 0x48) == 0x48 && r.Fdc.ReadSectorCount == 1);
        });
        test("FDC seek updates cylinder and clears disk change only after a step", () =>
        {
            using var r = new Rig(); Check(r.Fdc.Read(0x3f7, 1) == 0x80); r.Send(0x0f, 0, 3);
            Check((r.Fdc.Read(0x3f4, 1) & 1) != 0); r.Wait(6_000_000); r.Send(8);
            Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { 0x20, 3 })); Check(r.Fdc.Read(0x3f7, 1) == 0);
            r.Send(0x4a, 0); r.Wait(); byte[] result = r.Result(); Check(result[1] == 0 && result[3] == 3 && result[6] == 2);
        });
        test("FDC rejects write-protected media and masked DMA without sector mutation", () =>
        {
            using var r = new Rig(); r.ProgramDma(512, false); r.Send(0x45, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait();
            Check(r.Result()[1] == 2 && r.Fdc.WrittenSectorCount == 0);
            r.ProgramDma(512); r.Dma.Write(0x0a, 6, 1); r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait();
            Check(r.Result()[1] == 0x10 && r.Fdc.ReadSectorCount == 0);
        });
        test("FDC reports wrong-cylinder and wrong-rate instead of returning invented data", () =>
        {
            using var r = new Rig(); r.ProgramDma(512); r.Send(0x46, 0, 1, 0, 1, 2, 18, 0x1b, 0xff); r.Wait(); Check(r.Result()[2] == 0x10);
            r.Fdc.Write(0x3f7, 2, 1); r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait(); Check(r.Result()[1] == 4);
        });
        test("FDC reset cancels pending data transfer without touching guest RAM", () =>
        {
            using var r = new Rig(); r.ProgramDma(512); r.Memory.WriteByte(0x2000, 0x77);
            r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Fdc.Write(0x3f2, 0, 1); r.Fdc.Write(0x3f2, 0x0c, 1); r.Wait();
            Check(r.Fdc.ReadSectorCount == 0 && r.Memory.ReadByte(0x2000) == 0x77);
        });
        test("FDC media changes are validated and blocked during execution and result phases", () =>
        {
            using var r = new Rig(); using var wrong = new MemoryDisk(new byte[512], "wrong");
            Reject<ArgumentException>(() => r.Fdc.ReplaceMedia(0, wrong)); r.ProgramDma(512);
            r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); Reject<InvalidOperationException>(() => r.Fdc.ReplaceMedia(0, null));
            r.Wait(); Reject<InvalidOperationException>(() => r.Fdc.ReplaceMedia(0, null)); r.Result();
            r.Fdc.ReplaceMedia(0, new MemoryDisk(Image(0x73), "replacement")); Check(r.Fdc.Read(0x3f7, 1) == 0x80);
            r.ProgramDma(512); r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait(); r.Result(); Check(r.Memory.ReadByte(0x2000) == 0x73);
            r.Fdc.ReplaceMedia(0, null); Check(r.Fdc.MediaName(0) == "Empty");
        });
        test("FDC DMA write and format persist in overlay without modifying floppy base", () =>
        {
            var files = DiskFiles(); byte[] original = File.ReadAllBytes(files.Base);
            using (var r = new Rig(new CopyOnWriteDisk(files.Base, files.Overlay)))
            {
                r.Memory.Ram.AsSpan(0x2000, 512).Fill(0xa7); r.ProgramDma(512, false); r.Send(0x45, 0, 0, 0, 2, 2, 18, 0x1b, 0xff); r.Wait(); Check(r.Result()[1] == 0);
                new byte[] { 0, 0, 3, 2, 0, 0, 4, 2 }.CopyTo(r.Memory.Ram, 0x2000); r.ProgramDma(8, false);
                r.Send(0x4d, 0, 2, 2, 0x54, 0xf6); r.Wait(); Check(r.Result()[1] == 0 && r.Fdc.WrittenSectorCount == 3);
            }
            using var disk = new CopyOnWriteDisk(files.Base, files.Overlay); byte[] sector = new byte[512];
            disk.ReadSector(1, sector); Check(sector.All(b => b == 0xa7)); disk.ReadSector(2, sector); Check(sector.All(b => b == 0xf6));
            Check(File.ReadAllBytes(files.Base).AsSpan().SequenceEqual(original));
        });
        test("guest floppy BIOS uses DMA bounce sector across an odd segment boundary", () =>
        {
            using var m = new PcMachine(new() { FloppyDriveA = "1440k" }, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "IDE"), new MemoryDisk(Image(0x63), "A"));
            m.RunSteps(100000); m.Cpu.SetSegment(Segment.Es, 0x1000); m.Cpu.SetRegister(Register.Bx, 0xffff);
            m.Cpu.SetRegister(Register.Cx, 1); m.Cpu.SetRegister(Register.Dx, 0); Bios(m, 0x0201);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Floppy.ReadSectorCount == 1);
            Check(m.Memory.Ram.AsSpan(0x1ffff, 512).ToArray().All(b => b == 0x63)); Check(m.Memory.ReadByte(0x9fc00) == 1);
        });
        test("guest floppy BIOS writes an overlay and returns write protection for readonly media", () =>
        {
            var files = DiskFiles();
            using var m = new PcMachine(new() { FloppyDriveA = "1440k", FloppyDriveB = "1440k" },
                new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "IDE"), new CopyOnWriteDisk(files.Base, files.Overlay), new MemoryDisk(Image(), "B"));
            m.RunSteps(100000); m.Memory.Ram.AsSpan(0x4000, 512).Fill(0x91);
            m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetRegister(Register.Bx, 0x4000); m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Dx, 0);
            Bios(m, 0x0301); Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Floppy.WrittenSectorCount == 1);
            m.Cpu.SetRegister(Register.Dx, 1); Bios(m, 0x0301);
            Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && (m.Cpu.GetRegister(Register.Ax) >> 8 & 255) == 3 && m.Floppy.WrittenSectorCount == 1);
        });
        test("automated BIOS typing respects type-ahead capacity during slow disk work", () =>
        {
            using var m = PcMachine.CreateDemo(); m.RunSteps(100000); m.Memory.WriteWord(0x41a, 0x1e); m.Memory.WriteWord(0x41c, 0x2e);
            Check(!GuestKeyboard.TryInjectForBios(m, 0x1e) && m.Keyboard.OutputByteCount == 0);
            m.Memory.WriteWord(0x41c, 0x1e); Check(GuestKeyboard.TryInjectForBios(m, 0x1e)); Check(m.Keyboard.OutputByteCount == 1);
            Check(!GuestKeyboard.TryInjectForBios(m, 0x9e));
        });
        foreach(string pause in new[]{"controller","scanning","translation"})
        test($"acceptance keyboard retains input while guest pauses {pause}",()=>
        {
            using var m=PcMachine.CreateDemo();m.RunSteps(100000);
            if(pause=="controller")m.Keyboard.Write(0x64,0xad,1);
            else if(pause=="scanning"){m.Keyboard.Write(0x60,0xf5,1);m.Keyboard.Read(0x60,1);}
            else{m.Keyboard.Write(0x64,0x60,1);m.Keyboard.Write(0x60,0x05,1);}
            Check(!GuestKeyboard.TryInjectForBios(m,0x2a) && m.Keyboard.OutputByteCount==0);
            if(pause=="controller")m.Keyboard.Write(0x64,0xae,1);
            else if(pause=="scanning"){m.Keyboard.Write(0x60,0xf4,1);m.Keyboard.Read(0x60,1);}
            else{m.Keyboard.Write(0x64,0x60,1);m.Keyboard.Write(0x60,0x45,1);}
            Check(GuestKeyboard.TryInjectForBios(m,0x2a) && m.Keyboard.Read(0x60,1)==0x2a);
        });
        test("FDC completion reaches PIC IRQ6 and acknowledges on result read", () =>
        {
            using var r = new Rig(); r.Pic.Write(0x21, 0xbf, 1); r.ProgramDma(512);
            r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Wait();
            Check(r.Pic.Acknowledge() == 0x0e); r.Result(); r.Pic.Write(0x20, 0x20, 1);
            Check(!r.Fdc.InterruptAsserted && !r.Pic.HasPending);
        });
        test("FDC reads standard floppy geometry and data-rate matrix", () =>
        {
            foreach (string id in new[] { "360k", "720k", "1200k", "1440k", "2880k" })
            {
                var g = FloppyGeometry.FromId(id)!; var memory = new MemoryBus(1048576); var dma = new Dma8237(memory); var clock = new DeviceScheduler();
                byte[] bytes = new byte[g.SectorCount * 512]; bytes[0] = g.CmosType;
                using var controller = new Fdc82077(clock, dma, _ => { }, g, null, new MemoryDisk(bytes, id));
                dma.Write(0x0c, 0, 1); dma.Write(4, 0, 1); dma.Write(4, 0x20, 1); dma.Write(5, 0xff, 1); dma.Write(5, 1, 1); dma.Write(0x0b, 0x46, 1); dma.Write(0x0a, 2, 1);
                controller.Write(0x3f2, 0x1c, 1); controller.Write(0x3f7, g.DataRate, 1);
                foreach (byte value in new byte[] { 0x46, 0, 0, 0, 1, 2, (byte)g.SectorsPerTrack, 0x1b, 0xff }) controller.Write(0x3f5, value, 1);
                clock.AdvanceTo(400_000_000); Check(controller.ReadSectorCount == 1 && memory.ReadByte(0x2000) == g.CmosType, id);
            }
        });
        test("IPC floppy insertion and ejection preserve running CPU and expose changed medium", () =>
        {
            var files = DiskFiles(); using var session = new MachineSession(new PcMachine(new() { FloppyDriveA = "1440k" }, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "IDE")));
            session.Machine.RunSteps(100000); long instructions = session.Machine.Cpu.Instructions;
            var task = session.Enqueue(new(1, "insert", "InsertFloppy", JsonSerializer.SerializeToElement(new { drive = 0, path = files.Base, overlayPath = files.Overlay })));
            session.PumpCommands(); Check(task.Result.Success && session.Machine.Cpu.Instructions == instructions && session.Machine.GetStatus().FloppyAMedia.Contains("private.cow"));
            session.Machine.Cpu.SetSegment(Segment.Es, 0); session.Machine.Cpu.SetRegister(Register.Bx, 0x4000); session.Machine.Cpu.SetRegister(Register.Cx, 2); session.Machine.Cpu.SetRegister(Register.Dx, 0);
            Bios(session.Machine, 0x0201); Check(session.Machine.Memory.ReadByte(0x4000) == 1);
            task = session.Enqueue(new(1, "eject", "EjectFloppy", JsonSerializer.SerializeToElement(new { drive = 0 }))); session.PumpCommands();
            Check(task.Result.Success && session.Machine.GetStatus().FloppyAMedia == "Empty");
        });
        test("failed floppy geometry change releases prepared overlay lease and retains old machine", () =>
        {
            var files = DiskFiles(); using var session = new MachineSession(new PcMachine(new() { FloppyDriveA = "1440k" },
                new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "IDE"), new CopyOnWriteDisk(files.Base, files.Overlay)));
            session.Machine.RunSteps(100000); var old = session.Machine;
            var plan = new ConfigurationPlan(1, old.Profile with { FloppyDriveA = "720k" }, true);
            var task = session.Enqueue(new(1, "invalid-geometry", "CommitConfigurationChange", JsonSerializer.SerializeToElement(plan, IpcProtocol.JsonOptions)));
            session.PumpCommands(); Check(!task.Result.Success && ReferenceEquals(old, session.Machine) && session.Revision == 1);
            old.Cpu.SetSegment(Segment.Es, 0); old.Cpu.SetRegister(Register.Bx, 0x4000); old.Cpu.SetRegister(Register.Cx, 2); old.Cpu.SetRegister(Register.Dx, 0);
            Bios(old, 0x0201); Check(old.Memory.ReadByte(0x4000) == 1 && old.Fault is null);
        });
    }
}
