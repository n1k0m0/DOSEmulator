using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class ResetTests
{
    private static void Check(bool value, string message = "Reset invariant failed") { if (!value) throw new Exception(message); }
    private static IpcResponse Command(MachineSession session, string name, object? payload = null)
    {
        var response = session.Enqueue(new(1, name, name, JsonSerializer.SerializeToElement(payload ?? new { }, IpcProtocol.JsonOptions)));
        session.PumpCommands(); return response.Result;
    }
    private static void Idle(PcMachine m)
    {
        m.Cpu.Reset();
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1100; m.Cpu.SetRegister(Register.Sp, 0x9000);
        new byte[] { 0xfb, 0xf4, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1100);
        m.Cpu.Eflags = 2;
    }
    public static void AddTo(Action<string, Action> test)
    {
        test("port92 reset is edge triggered and consumes each request once", () =>
        {
            var board = new DevelopmentBoardPorts(new(1048576));
            board.Write(0x92, 3, 2); Check(!board.ResetRequested);
            board.Write(0x92, 3, 1); Check(board.ConsumeResetRequest() && !board.ConsumeResetRequest());
            Check(board.Read(0x92, 1) == 3);
            board.Write(0x92, 3, 1); Check(!board.ConsumeResetRequest());
            board.Write(0x92, 2, 1); board.Write(0x92, 3, 1); Check(board.ConsumeResetRequest());
        });
        test("KBC distinguishes pulse masks from a held output-port reset", () =>
        {
            var k = new Keyboard8042(new(1048576), _ => { });
            k.Write(0x64, 0xff, 1); Check(!k.ResetRequested);
            k.Write(0x64, 0xfe, 2); Check(!k.ResetRequested);
            k.Write(0x64, 0xfe, 1); Check(k.ConsumeResetPulse() && !k.ConsumeResetPulse() && !k.ResetLineAsserted);
            k.Write(0x64, 0xd1, 1); k.Write(0x60, 2, 1); Check(k.ResetLineAsserted && !k.ConsumeResetPulse());
            k.Write(0x64, 0xd1, 1); k.Write(0x60, 3, 1); Check(!k.ResetRequested);
        });
        test("guest OUT completes before reset and no following instruction leaks through", () =>
        {
            using var m = PcMachine.CreateDemo();
            m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000;
            new byte[] { 0xb0, 0xfe, 0xe6, 0x64, 0xc6, 0x06, 0x00, 0x60, 0xaa }.CopyTo(m.Memory.Ram, 0x1000);
            m.Cpu.SetRegister(Register.Bx, 0xdeadbeef); m.Cpu.SetSegment(Segment.Fs, 0x1234);
            m.RunSteps(2); ulong before = m.Scheduler.Now;
            Check(m.Keyboard.ResetRequested && m.HardwareResetCount == 0);
            m.RunSteps(1);
            Check(m.Memory.ReadByte(0x6000) == 0 && m.HardwareResetCount == 1 && m.LastResetSource == "kbc-pulse");
            Check(m.Cpu.PhysicalInstructionAddress == 0xfffffff0 && m.Cpu.Eip == 0xfff0 && m.Cpu.Instructions == 0);
            Check(m.Cpu.GetRegister(Register.Bx) == 0 && m.Cpu.GetRegister(Register.Dx) == 0x400 && m.Cpu.GetSegment(Segment.Fs) == 0);
            Check(m.Cpu.Eflags == 2 && m.Cpu.Cr0 == 0x60000010 && !m.Cpu.Halted);
            Check(m.LastResetTime == before && m.Scheduler.Now == before + 6000);
        });
        test("reset ROM remains reachable with A20 masked and real BIOS boots again", () =>
        {
            using var m = PcMachine.CreateDemo(); m.Memory.A20Enabled = false;
            Check(m.Memory.ReadByte(0xfffffff0) == 0xea);
            m.Keyboard.Write(0x64, 0xfe, 1); m.RunSteps(1); m.RunSteps(100000);
            Check(m.Fault is null && m.Memory.ReadWord(0x500) == 0xbeef && m.HardwareResetCount == 1);
        });
        test("CPU hot reset retains graph RAM RTC DMA PIC and powered device deadlines", () =>
        {
            using var m = PcMachine.CreateDemo();
            var cpu=m.Cpu; var ram=m.Memory; var pic=m.Pic; var pit=m.Pit; var rtc=m.Rtc; var dma=m.Dma;
            var ata=m.Ata; var fdc=m.Floppy; var audio=m.Audio; var scheduler=m.Scheduler;
            m.Memory.WriteDword(0x6000, 0x76543210); m.Memory.WriteWord(0x472, 0x1234);
            m.Rtc.Write(0x70, 0x38, 1); m.Rtc.Write(0x71, 0xa5, 1);
            m.Pic.Write(0x21, 0x6c, 1);
            m.Dma.Write(0x0c, 0, 1); m.Dma.Write(4, 0x34, 1); m.Dma.Write(4, 0x12, 1);
            m.Ata.Write(0x1f6, 0xe0, 1); m.Ata.Write(0x1f2, 1, 1); m.Ata.Write(0x1f7, 0x20, 1);
            ulong deadline=m.Scheduler.NextEventTime!.Value; m.HardwareReset();
            Check(ReferenceEquals(cpu,m.Cpu)&&ReferenceEquals(ram,m.Memory)&&ReferenceEquals(pic,m.Pic)&&ReferenceEquals(pit,m.Pit));
            Check(ReferenceEquals(rtc,m.Rtc)&&ReferenceEquals(dma,m.Dma)&&ReferenceEquals(ata,m.Ata)&&ReferenceEquals(fdc,m.Floppy));
            Check(ReferenceEquals(audio,m.Audio)&&ReferenceEquals(scheduler,m.Scheduler));
            Check(m.Memory.ReadDword(0x6000)==0x76543210 && m.LastWarmBootFlag==0x1234 && m.Rtc.Read(0x71,1)==0xa5);
            Check(m.Pic.Read(0x21,1)==0x6c && m.Scheduler.NextEventTime==deadline);
            m.Dma.Write(0x0c,0,1); Check(m.Dma.Read(4,1)==0x34 && m.Dma.Read(4,1)==0x12);
            m.Scheduler.AdvanceTo(deadline); Check((m.Ata.Status&8)!=0);
            for(int i=0;i<256;i++) m.Ata.Read(0x1f0,2);
            Check(m.Ata.ReadSectorCount==1 && m.Ata.Status==0x50);
        });
        test("held KBC reset prevents CPU execution but clocks devices and releases once", () =>
        {
            using var m=PcMachine.CreateDemo();
            m.Keyboard.Write(0x64,0xd1,1); m.Keyboard.Write(0x60,2,1);
            m.RunSteps(100,1_000_000); Check(m.HardwareResetCount==1 && m.Cpu.Instructions==0 && m.Scheduler.Now==1_000_000);
            Check(m.GetStatus().State==MachineRunState.ResetHeld && m.GetStatus().Reset!.Held);
            m.RunSteps(100,2_000_000); Check(m.HardwareResetCount==1 && m.Cpu.Instructions==0);
            m.Keyboard.Write(0x64,0xd1,1); m.Keyboard.Write(0x60,3,1); m.RunSteps(1);
            Check(m.HardwareResetCount==1 && m.Cpu.Instructions==1 && !m.CpuResetHeld && m.Fault is null);
        });
        test("simultaneous board and KBC pulses coalesce into one reset boundary", () =>
        {
            using var m=PcMachine.CreateDemo(); m.Board.Write(0x92,3,1); m.Keyboard.Write(0x64,0xfe,1);
            m.RunSteps(1); Check(m.HardwareResetCount==1 && m.LastResetSource=="port92+kbc-pulse");
            m.RunSteps(1); Check(m.HardwareResetCount==1);
        });
        test("power cycle clears RAM and device time while battery CMOS and elapsed RTC survive", () =>
        {
            using var m=PcMachine.CreateDemo(); m.Memory.WriteByte(0x6000,0x55);
            m.Rtc.Write(0x70,0x38,1); m.Rtc.Write(0x71,0xa7,1); m.Scheduler.AdvanceTo(1_500_000_000);
            using var cold=m.CreatePowerCycle(m.Profile);
            Check(cold.Memory.ReadByte(0x6000)==0 && cold.Scheduler.Now==0 && cold.HardwareResetCount==0);
            cold.Rtc.Write(0x70,0x38,1); Check(cold.Rtc.Read(0x71,1)==0xa7);
            cold.Scheduler.AdvanceTo(500_000_000); cold.Rtc.Write(0x70,0,1); Check(cold.Rtc.Read(0x71,1)==2);
        });
        test("session hardware reset preserves paused machine and invalidates a prepared plan", () =>
        {
            using var session=new MachineSession(PcMachine.CreateDemo()); var old=session.Machine;
            var plan=new ConfigurationPlan(session.Revision,new(){RamMiB=16},true);
            Check(Command(session,"HardwareReset").Success && ReferenceEquals(old,session.Machine) && !old.Running);
            Check(session.Revision==2 && Command(session,"CommitConfigurationChange",plan).ErrorCode=="StaleConfiguration");
            Check(Command(session,"PowerCycleCurrent").Success && session.Revision==3 && !ReferenceEquals(old,session.Machine));
        });
        test("guest reset invalidates session plans without replacing or reopening media", () =>
        {
            using var session=new MachineSession(PcMachine.CreateDemo()); var old=session.Machine;
            old.Keyboard.Write(0x64,0xfe,1); old.RunSteps(1);
            Check(session.Revision==2 && ReferenceEquals(old,session.Machine));
        });
        test("Ctrl Alt Del is delivered through IRQ1 and BIOS sets a warm flag before KBC pulse", () =>
        {
            using var session=new MachineSession(PcMachine.CreateDemo()); var m=session.Machine;
            m.RunSteps(100000); Idle(m);
            Check(Command(session,"SendCtrlAltDel").Success && m.HardwareResetCount==0);
            m.RunSteps(100000);
            Check(m.Fault is null && m.HardwareResetCount==1 && m.LastWarmBootFlag==0x1234 && m.LastResetSource=="kbc-pulse");
            Check(m.Memory.ReadWord(0x500)==0xbeef && session.Revision==2);
        });
        test("Delete alone does not reset and CPU reset wakes a halted interpreter", () =>
        {
            using var m=PcMachine.CreateDemo(); m.RunSteps(100000); Idle(m);
            m.Keyboard.InjectTranslatedSet1(0x53); m.Keyboard.InjectTranslatedSet1(0xd3); m.RunSteps(1000);
            Check(m.HardwareResetCount==0 && m.Fault is null);
            m.HardwareReset(); Check(!m.Cpu.Halted && m.Cpu.PhysicalInstructionAddress==0xfffffff0);
        });
        test("hot reset preserves exclusive disk overlay ownership and committed data", () =>
        {
            string root=Path.Combine("artifacts","tests","reset-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string basis=Path.Combine(root,"base.img"), overlay=Path.Combine(root,"private.cow");
            File.WriteAllBytes(basis,new byte[1024]); var disk=new CopyOnWriteDisk(basis,overlay);
            using(var m=new PcMachine(new(),disk))
            {
                byte[] sector=Enumerable.Repeat((byte)0x59,512).ToArray(); disk.WriteSector(1,sector);
                m.HardwareReset(); byte[] read=new byte[512]; disk.ReadSector(1,read); Check(read.SequenceEqual(sector));
                try { using var collision=new CopyOnWriteDisk(basis,overlay); throw new Exception("Overlay lease lost"); }
                catch(IOException) { }
            }
            using var reopened=new CopyOnWriteDisk(basis,overlay); byte[] final=new byte[512]; reopened.ReadSector(1,final); Check(final.All(v=>v==0x59));
        });
        test("CPU reset leaves an accepted ATA sector write scheduled and committed exactly once", () =>
        {
            string root=Path.Combine("artifacts","tests","reset-write-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string basis=Path.Combine(root,"base.img"), overlay=Path.Combine(root,"private.cow");
            File.WriteAllBytes(basis,new byte[1024]); var disk=new CopyOnWriteDisk(basis,overlay);
            using var m=new PcMachine(new(),disk);
            m.Ata.Write(0x1f6,0xe0,1); m.Ata.Write(0x1f2,1,1); m.Ata.Write(0x1f3,1,1); m.Ata.Write(0x1f7,0x30,1);
            m.Scheduler.AdvanceTo(5000); for(int i=0;i<256;i++) m.Ata.Write(0x1f0,0x5a5a,2);
            Check(m.Ata.Status==0x80 && m.Ata.WrittenSectorCount==0);
            m.HardwareReset(); m.Scheduler.AdvanceTo(10000); m.Scheduler.AdvanceTo(20000);
            byte[] read=new byte[512]; disk.ReadSector(1,read);
            Check(m.Ata.WrittenSectorCount==1 && m.Ata.InterruptAsserted && read.All(v=>v==0x5a));
        });
        test("CPU reset retains pending FDC DMA completion and its result phase", () =>
        {
            byte[] medium=Enumerable.Repeat((byte)0x67,1474560).ToArray();
            using var m=new PcMachine(new(){FloppyDriveA="1440k"},null,new MemoryDisk(medium,"own scratch"));
            m.Dma.Write(0x0c,0,1); m.Dma.Write(4,0,1); m.Dma.Write(4,0x30,1);
            m.Dma.Write(5,0xff,1); m.Dma.Write(5,1,1); m.Dma.Write(0x0b,0x46,1); m.Dma.Write(0x0a,2,1);
            m.Floppy.Write(0x3f2,0x1c,1);
            foreach(byte b in new byte[]{0x46,0,0,0,1,2,1,0x1b,0xff}) m.Floppy.Write(0x3f5,b,1);
            Check(m.Floppy.Busy); m.HardwareReset(); m.Scheduler.AdvanceTo(400_000_000);
            Check(m.Floppy.ReadSectorCount==1 && m.Memory.Ram.AsSpan(0x3000,512).ToArray().All(v=>v==0x67));
            Check(m.Floppy.Busy && m.Floppy.InterruptAsserted);
            for(int i=0;i<7;i++) m.Floppy.Read(0x3f5,1); Check(!m.Floppy.Busy);
        });
        test("reset-aware scenarios cannot inject into a prompt left over from the previous boot", () =>
        {
            using var m=PcMachine.CreateDemo(); m.RunSteps(100000);
            for(int i=0;i<5;i++) m.Memory.WriteByte((uint)(0xb8000+i*2),(byte)"READY"[i]);
            var scenario=new GuestTestScenario([new("READY",Text:"a",SettleNanoseconds:0,MinimumResetCount:1,MinimumPost10Count:2)]);
            scenario.Pump(m); Check(scenario.InputBytesSent==0);
            m.HardwareReset(); scenario.Pump(m); Check(scenario.InputBytesSent==0);
            m.Board.Write(0x80,0x10,1); m.Board.Write(0x80,0x10,1); scenario.Pump(m);
            Check(scenario.InputBytesSent==1);
        });
        test("keyboard reset overrides a pending parameter and POST can recover a disabled keyboard", () =>
        {
            using var m=PcMachine.CreateDemo();
            m.Keyboard.Write(0x60,0xf5,1); m.Keyboard.Read(0x60,1);
            m.Keyboard.Write(0x60,0xed,1); m.Keyboard.Read(0x60,1);
            m.HardwareReset(); m.RunSteps(100000); m.Keyboard.InjectTranslatedSet1(0x1e);
            Check(m.Fault is null && m.Keyboard.OutputByteCount==1 && m.Keyboard.Read(0x60,1)==0x1e);
        });
        test("session media power cycles preserve battery CMOS while replacing volatile RAM", () =>
        {
            using var session=new MachineSession(PcMachine.CreateDemo());
            session.Machine.Rtc.Write(0x70,0x38,1); session.Machine.Rtc.Write(0x71,0xb7,1);
            session.Machine.Memory.WriteByte(0x6000,0x44);
            Check(Command(session,"PowerCycleDemo").Success);
            session.Machine.Rtc.Write(0x70,0x38,1);
            Check(session.Machine.Rtc.Read(0x71,1)==0xb7 && session.Machine.Memory.ReadByte(0x6000)==0);
        });
    }
}
