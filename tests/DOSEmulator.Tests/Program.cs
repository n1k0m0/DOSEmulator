// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

if (args.Length == 3 && args[0] == "--network-native-check") return NetworkBackendNativeTests.Run(args[1], args[2]);
if (args.Length == 4 && args[0] == "--network-native-peer") return NetworkBackendNativeTests.RunPeer(args[1], args[2], args[3]);
if (args.Length == 2 && args[0] == "--opl-audit-benchmark") return OplAuditTests.Run(args[1]);
if (args.Length == 2 && args[0] == "--cow-audit-benchmark") return CowAuditPerformanceTests.Run(args[1]);
if (args.Length == 2 && args[0] == "--audit-cpu-benchmark") return CpuAuditTests.Benchmark(args[1]);
if (args.Length == 2 && args[0] == "--audit2-cpu-guest") return Audit2CpuGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--atapi-guest") return AtapiGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--mscdex-guest") return MscdexGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--pci-bios-guest") return PciBiosGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--windows95-guest") return Windows95GuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--s3-trio64-guest") return S3Trio64GuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--piix-reset-guest") return PiixResetGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--piix-ide-guest") return PiixIdeGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--piix-irq-guest") return PiixIrqGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--fx-host-guest") return FxHostGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--p54c-pc-guest") return P54cPcGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--p54c-debug-guest") return DebugGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--selector-guest") return SelectorGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--tsc-guest") return TimestampGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--p54c-core-guest") return P54cCoreGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--combined-audio-guest") return CombinedAudioGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--lss-boundary-guest") return LssGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--snapshot-doom") return SnapshotDoomRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--snapshot-guest") return SnapshotGuestRunner.Run(args[1]);
if (args.Length == 2 && args[0] == "--dma16-guest") return DmaWordGuestRunner.Run(args[1]);
if (args.Length == 1 && args[0] == "--ipc-shutdown-check") { IpcLifecycleTests.CheckShutdown(); return 0; }
if (args.Length == 3 && args[0] == "--x87-native-vectors") return X87NumericTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-compare-native") return X87ComparisonTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-round-native") return X87RoundingTests.VerifyNativeVectors(args[1], args[2]);

if (args.Length == 3 && args[0] == "--x87-sqrt-native") return X87SquareRootTests.VerifyNativeVectors(args[1], args[2]);

if (args.Length == 3 && args[0] == "--x87-constant-native") return X87ConstantTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-bcd-native") return X87PackedBcdTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-scale-native") return X87ScalingTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-extract-native") return X87ExtractionTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-remainder-native") return X87RemainderTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-exp2m1-math") return X87ExponentialTests.VerifyNativeVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-logarithm-math") return X87LogarithmTests.VerifyMathVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-atan-math") return X87ArcTangentTests.VerifyMathVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-tangent-math") return X87TangentTests.VerifyMathVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-sincos-math") return X87SineCosineTests.VerifyMathVectors(args[1], args[2]);
if (args.Length == 3 && args[0] == "--x87-fsincos-math") return X87SinCosPairTests.VerifyMathVectors(args[1], args[2]);

var tests = new List<(string Name, Func<Task> Run)>();
// Registers one named synchronous regression in the surrounding runner.
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
// Registers one named asynchronous regression in the surrounding runner.
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
// Compares expected and actual regression values using their normal equality contract.
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
// Requires a regression condition to hold before the test continues.
static void True(bool condition, string message = "Assertion failed.") { if (!condition) throw new Exception(message); }
// Requires a regression action to reject its input with the selected exception type.
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
// Builds an isolated CPU/bus fixture with caller-supplied guest instruction bytes.
static (X86Cpu Cpu, MemoryBus Memory, IoPortBus Io) Rig(params byte[] code)
{
    var memory = new MemoryBus(2 * 1024 * 1024); var io = new IoPortBus(); var cpu = new X86Cpu(memory, io);
    cpu.SetSegment(Segment.Cs, 0); cpu.Eip = 0x1000; cpu.SetRegister(Register.Sp, 0x9000);
    code.CopyTo(memory.Ram, 0x1000); return (cpu, memory, io);
}
// Executes the component's explicit runner with the supplied bounds and reports its resulting evidence.
static void Run(X86Cpu cpu, int limit = 10000)
{
    for (int i = 0; i < limit && !cpu.Halted; i++) cpu.Step();
    True(cpu.Halted, "CPU did not halt within the test budget.");
}
// Programs an actual one-channel LBA28 read request through ATA task-file ports.
static void AtaRead(AtaPioController ata, int count = 1, int lba = 0)
{
    ata.Write(0x1f6, (uint)(0xe0 | ((lba >> 24) & 15)), 1);
    ata.Write(0x1f2, (uint)count, 1); ata.Write(0x1f3, (uint)lba & 255, 1);
    ata.Write(0x1f4, (uint)(lba >> 8) & 255, 1); ata.Write(0x1f5, (uint)(lba >> 16) & 255, 1); ata.Write(0x1f7, 0x20, 1);
}

Test("486 reset has hidden CS base and top-of-address-space fetch", () =>
{
    var cpu = new X86Cpu(new MemoryBus(2 * 1024 * 1024), new());
    Equal(0xfffffff0u, cpu.PhysicalInstructionAddress); Equal((ushort)0xf000, cpu.GetSegment(Segment.Cs)); Equal(0x60000010u, cpu.Cr0);
});
Test("ROM is immutable through both aliases", () =>
{
    using var m = PcMachine.CreateDemo(); byte original = m.Memory.ReadByte(0xfffffff0);
    m.Memory.WriteByte(0xfffffff0, 0); m.Memory.WriteByte(0xffff0, 0);
    Equal((byte)0xea, original); Equal(original, m.Memory.ReadByte(0xffff0)); Equal(original, m.Memory.ReadByte(0xfffffff0));
});
Test("first far jump clears reset CS base", () => { using var m = PcMachine.CreateDemo(); m.Cpu.Step(); Equal(0xf0000u, m.Cpu.PhysicalInstructionAddress); });
Test("RAM mapping conflicts rejected before mutation", () => { var m = new MemoryBus(1048576); m.Map(0x1000, 16, new RomDevice([1]), "a"); Throws<InvalidOperationException>(() => m.Map(0x100f, 4, new RomDevice([2]), "b")); });
Test("A20 masks only bit 20", () => { var m = new MemoryBus(4 * 1048576); m.A20Enabled = false; m.WriteByte(0x100123, 0x42); Equal((byte)0x42, m.Ram[0x123]); m.WriteByte(0x300123, 0x71); Equal((byte)0x71, m.Ram[0x200123]); });
Test("unmapped bus reads are all ones", () => { var m = new MemoryBus(1048576); Equal(0xffffffffu, m.ReadDword(0xf0000000)); Equal(0xffffu, new IoPortBus().Read(123, 2)); });
foreach (int ramMiB in new[] { 4, 8, 16 })
    Test($"ISA option window has no DRAM with {ramMiB} MiB installed", () =>
    {
        using var machine = PcMachine.CreateDemo(new MachineProfile { RamMiB = ramMiB });
        foreach (uint address in new uint[] { 0xc0000, 0xc7fff, 0xc8000, 0xd0000, 0xe0000, 0xeffff })
        {
            machine.Memory.Ram[address] = 0x12;
            machine.Memory.WriteByte(address, 0x34);
            Equal((byte)0xff, machine.Memory.ReadByte(address));
            Equal((byte)0x12, machine.Memory.Ram[address]);
            True(machine.Memory.TryPeekByte(address, out byte peek)); Equal((byte)0xff, peek);
        }
        machine.Memory.WriteDword(0x100000, 0x13579bdf); Equal(0x13579bdfu, machine.Memory.ReadDword(0x100000));
        Equal((byte)0xea, machine.Memory.ReadByte(0xffff0));
    });
Test("I/O conflicts do not partially install a mapping", () => { var io = new IoPortBus(); var board = new DevelopmentBoardPorts(new(1048576)); io.Map(0x92, 1, board); Throws<InvalidOperationException>(() => io.Map(0x90, 4, board)); Equal(0xffu, io.Read(0x91, 1)); });
Test("scheduler preserves ordering of simultaneous events", () => { var c = new DeviceScheduler(); var result = new List<int>(); c.ScheduleAfter(3, () => result.Add(1)); c.ScheduleAfter(3, () => result.Add(2)); c.AdvanceTo(3); Equal("1,2", string.Join(',', result)); });
Test("scheduler carries fractional CPU clocks without drift", () => { var c = new DeviceScheduler(); for (int i = 0; i < 33; i++) c.AdvanceCycles(1, 33); Equal(1_000_000_000UL, c.Now); });
Test("scheduler cancellation suppresses callback", () => { var c = new DeviceScheduler(); bool called = false; c.Cancel(c.ScheduleAfter(1, () => called = true)); c.AdvanceTo(2); True(!called); Throws<ArgumentOutOfRangeException>(() => c.AdvanceTo(1)); });
Test("8-bit high registers preserve all other bits", () => { var (c, _, _) = Rig(0xb4,0x77,0xf4); c.SetRegister(Register.Ax, 0x12345678); Run(c); Equal(0x12347778u, c.GetRegister(Register.Ax)); });
Test("16-bit MOV preserves upper register", () => { var (c, _, _) = Rig(0xb8,0xef,0xbe,0xf4); c.SetRegister(Register.Ax, 0x12340000); Run(c); Equal(0x1234beefu, c.GetRegister(Register.Ax)); });
Test("duplicate operand prefix does not toggle back to 16-bit", () => { var (c, _, _) = Rig(0x66,0x66,0xb8,0x78,0x56,0x34,0x12,0xf4); Run(c); Equal(0x12345678u, c.GetRegister(Register.Ax)); });
Test("ADD overflow flags", () => { var (c, _, _) = Rig(0xb0,0x7f,0x04,1,0xf4); Run(c); Equal(0x80u, c.GetRegister(Register.Ax) & 255); True((c.Eflags & X86Cpu.Overflow) != 0 && (c.Eflags & X86Cpu.Carry) == 0); });
Test("ADD carry and zero flags", () => { var (c, _, _) = Rig(0xb0,0xff,0x04,1,0xf4); Run(c); Equal(0u, c.GetRegister(Register.Ax) & 255); True((c.Eflags & (X86Cpu.Carry | X86Cpu.Zero | X86Cpu.Parity | X86Cpu.Auxiliary)) == (X86Cpu.Carry | X86Cpu.Zero | X86Cpu.Parity | X86Cpu.Auxiliary)); });
Test("ADC consumes carry", () => { var (c, _, _) = Rig(0xb0,0xff,0xf9,0x14,0,0xf4); Run(c); Equal(0u, c.GetRegister(Register.Ax) & 255); True((c.Eflags & X86Cpu.Carry) != 0); });
Test("SBB propagates borrow", () => { var (c, _, _) = Rig(0xb0,0,0xf9,0x1c,0,0xf4); Run(c); Equal(255u, c.GetRegister(Register.Ax) & 255); True((c.Eflags & X86Cpu.Carry) != 0); });
Test("32-bit ADD carry", () => { var (c, _, _) = Rig(0x66,0xb8,0xff,0xff,0xff,0xff,0x66,0x05,1,0,0,0,0xf4); Run(c); Equal(0u, c.GetRegister(Register.Ax)); True((c.Eflags & X86Cpu.Carry) != 0); });
Test("INC preserves carry", () => { var (c, _, _) = Rig(0xb8,0xff,0xff,0xf9,0x40,0xf4); Run(c); Equal(0u, c.GetRegister(Register.Ax)); True((c.Eflags & X86Cpu.Carry) != 0); });
Test("CMP changes flags but not register", () => { var (c, _, _) = Rig(0xb8,5,0,0x3d,5,0,0xf4); Run(c); Equal(5u, c.GetRegister(Register.Ax)); True((c.Eflags & X86Cpu.Zero) != 0); });
Test("sign-extended group immediate", () => { var (c, _, _) = Rig(0xb8,5,0,0x83,0xc0,0xff,0xf4); Run(c); Equal(4u, c.GetRegister(Register.Ax)); });
Test("BP addressing defaults to SS", () => { var (c,m,_) = Rig(0x8b,0x46,2,0xf4); c.SetSegment(Segment.Ss,0x100); c.SetRegister(Register.Bp,0x100); m.WriteWord(0x1102,0x4567); Run(c); Equal(0x4567u,c.GetRegister(Register.Ax)); });
Test("segment override selects DS for BP", () => { var (c,m,_) = Rig(0x3e,0x8b,0x46,2,0xf4); c.SetSegment(Segment.Ss,0x100); c.SetRegister(Register.Bp,0x100); m.WriteWord(0x102,0x2345); Run(c); Equal(0x2345u,c.GetRegister(Register.Ax)); });
Test("32-bit SIB uses SS for EBP base", () => { var (c,m,_) = Rig(0x67,0x66,0x8b,0x44,0x8d,0x10,0xf4); c.SetSegment(Segment.Ss,0x200); c.SetRegister(Register.Bp,0x100); c.SetRegister(Register.Cx,3); m.WriteDword(0x211c,0x98765432); Run(c); Equal(0x98765432u,c.GetRegister(Register.Ax)); });
Test("near CALL and RET preserve stack", () => { var (c,_,_) = Rig(0xe8,1,0,0xf4,0xb8,0x34,0x12,0xc3); Run(c); Equal(0x1234u,c.GetRegister(Register.Ax)); Equal(0x9000u,c.GetRegister(Register.Sp)); });
Test("INT and IRET use guest vector table", () => { var (c,m,_) = Rig(0xcd,0x21,0xf4); m.WriteWord(0x84,0x2000); m.WriteWord(0x86,0); new byte[]{0xb8,0x34,0x12,0xcf}.CopyTo(m.Ram,0x2000); c.Eflags |= X86Cpu.InterruptEnable; Run(c); Equal(0x1234u,c.GetRegister(Register.Ax)); Equal(0x9000u,c.GetRegister(Register.Sp)); True((c.Eflags & X86Cpu.InterruptEnable) != 0); });
Test("REP zero count performs no access", () => { var (c,m,_) = Rig(0xf3,0xab,0xf4); c.SetRegister(Register.Di,0x300); c.SetRegister(Register.Ax,0x1234); Run(c); Equal((ushort)0,m.ReadWord(0x300)); Equal(0x300u,c.GetRegister(Register.Di)); });
Test("REP executes one element at a time", () => { var (c,m,_) = Rig(0xf3,0xab,0xf4); c.SetRegister(Register.Cx,2); c.SetRegister(Register.Di,0x300); c.SetRegister(Register.Ax,0x1234); c.Step(); Equal(1u,c.GetRegister(Register.Cx)); Equal(0x1000u,c.Eip); Equal((ushort)0x1234,m.ReadWord(0x300)); Equal((ushort)0,m.ReadWord(0x302)); Run(c); Equal((ushort)0x1234,m.ReadWord(0x302)); });
Test("REP MOVS honors direction flag", () => { var (c,m,_) = Rig(0xfd,0xf3,0xa4,0xf4); c.SetRegister(Register.Cx,2); c.SetRegister(Register.Si,0x301); c.SetRegister(Register.Di,0x401); m.WriteWord(0x300,0xabcd); Run(c); Equal((ushort)0xabcd,m.ReadWord(0x400)); Equal(0x2ffu,c.GetRegister(Register.Si)); });
Test("unclaimed instructions stop explicitly", () => { var (c,_,_) = Rig(0x0f,0xfe); Throws<UnsupportedInstructionException>(() => c.Step()); Equal(0x1000u,c.Eip); Equal(0L,c.Instructions); });
Test("profile rejects unimplemented Pentium", () => Throws<ArgumentException>(() => (new MachineProfile { Cpu = "pentium" }).Validate()));
Test("invalid PCM blocks rejected", () => { Throws<ArgumentException>(() => new AudioBlock(0,48000,2,[1]).Validate()); Throws<ArgumentException>(() => new AudioBlock(0,48000,1,[float.NaN]).Validate()); new AudioBlock(0,48000,2,[0,0]).Validate(); });
Test("ATA read waits for virtual time, then transfers exactly a sector", () =>
{
    using var disk = new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(),"test"); var s = new DeviceScheduler(); var ata = new AtaPioController(s,disk);
    AtaRead(ata); Equal((byte)0x80,ata.Status); s.AdvanceTo(5000); Equal((byte)0x58,ata.Status); True(ata.InterruptAsserted);
    byte[] sector = new byte[512]; for(int i=0;i<256;i++) BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(i*2),(ushort)ata.Read(0x1f0,2));
    Equal((ushort)0xaa55,BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(510))); Equal(1L,ata.ReadSectorCount); Equal((byte)0x50,ata.Status);
});
Test("ATA regular status acknowledges IRQ, alternate status does not", () => { using var d = new MemoryDisk(new byte[512],"t"); var s = new DeviceScheduler(); var a = new AtaPioController(s,d); AtaRead(a); s.AdvanceTo(5000); a.Read(0x3f6,1); True(a.InterruptAsserted); a.Read(0x1f7,1); True(!a.InterruptAsserted); });
Test("ATA absent slave does not alias master", () => { using var d = new MemoryDisk(new byte[512],"t"); var a = new AtaPioController(new(),d); a.Write(0x1f6,0xf0,1); Equal(0u,a.Read(0x1f7,1)); });
Test("ATA out-of-range reads return IDNF", () => { using var d = new MemoryDisk(new byte[512],"t"); var s = new DeviceScheduler(); var a = new AtaPioController(s,d); AtaRead(a,1,1); s.AdvanceTo(5000); Equal((byte)0x51,a.Status); Equal(0x10u,a.Read(0x1f1,1)); });
Test("ATA write command is rejected without touching media", () => { using var d = new MemoryDisk(new byte[512],"t"); var a = new AtaPioController(new(),d); a.Write(0x1f7,0x30,1); Equal((byte)0x51,a.Status); Equal(4u,a.Read(0x1f1,1)); });
Test("ATA reset cancels pending read", () => { using var d = new MemoryDisk(new byte[512],"t"); var s = new DeviceScheduler(); var a = new AtaPioController(s,d); AtaRead(a); a.Write(0x3f6,4,1); a.Write(0x3f6,0,1); s.AdvanceTo(5000); Equal((byte)0x50,a.Status); True(!a.InterruptAsserted); });
Test("ATA IDENTIFY advertises LBA and correct size", () => { using var d = new MemoryDisk(new byte[1024],"t"); var s = new DeviceScheduler(); var a = new AtaPioController(s,d); a.Write(0x1f7,0xec,1); s.AdvanceTo(5000); ushort[] words = new ushort[256]; for(int i=0;i<256;i++) words[i]=(ushort)a.Read(0x1f0,2); Equal((ushort)0x200,words[49]); Equal((ushort)2,words[60]); });
Test("raw disk cannot modify original file", () =>
{
    string directory=Path.Combine(Environment.CurrentDirectory,"artifacts","tests"); Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,$"original-{Guid.NewGuid():N}.img"); byte[] bytes=DevelopmentFirmware.CreateDemoDisk(); File.WriteAllBytes(path,bytes);
    byte[] before=SHA256.HashData(bytes); using(var d=new ReadOnlyRawDisk(path)){ var a=new AtaPioController(new(),d); a.Write(0x1f7,0x30,1); a.Write(0x1f0,0xffff,2); }
    True(before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
});
Test("boot path executes a loaded sector and publishes readable frame", () =>
{
    using var m=PcMachine.CreateDemo(); m.RunSteps(100000); Equal<string?>(null,m.Fault); True(m.Cpu.Halted); Equal(1L,m.Ata.ReadSectorCount);
    Equal((ushort)0xbeef,m.Memory.ReadWord(0x500)); Equal((byte)0x30,m.Board.PostCode); True(m.Video.ReadText().Contains("486 BOOT PATH VERIFIED"));
    var f=m.CaptureFrame(); Equal(640,f.Width); Equal(400,f.Height); True(f.ArgbPixels.Distinct().Count()>1);
});
Test("boot output is deterministic between independent machines", () =>
{
    using var a=PcMachine.CreateDemo(); using var b=PcMachine.CreateDemo(); a.RunSteps(100000); b.RunSteps(100000);
    True(a.CaptureFrame().ArgbPixels.AsSpan().SequenceEqual(b.CaptureFrame().ArgbPixels)); Equal(a.Scheduler.Now,b.Scheduler.Now); Equal(a.Cpu.Instructions,b.Cpu.Instructions);
});
Test("BIOS reports invalid boot signature without jumping into it", () => { using var m=new PcMachine(new(),new MemoryDisk(new byte[512],"bad")); m.RunSteps(100000); Equal<string?>(null,m.Fault); True(m.Video.ReadText().Contains("55 AA")); Equal((ushort)0,m.Memory.ReadWord(0x500)); });
Test("BIOS reports missing disk instead of spinning forever", () => { using var m=new PcMachine(new(),null); m.RunSteps(100000); Equal<string?>(null,m.Fault); True(m.Cpu.Halted); True(m.Video.ReadText().Contains("no readable")); });
Test("IPC requests remain queued until safe point", () => { using var session=new MachineSession(PcMachine.CreateDemo()); var t=session.Enqueue(new(1,"t","Pause",JsonSerializer.SerializeToElement(new{}))); True(!t.IsCompleted); session.PumpCommands(); True(t.Result.Success); });
Test("IPC unknown command and protocol mismatch are explicit", () => { using var s=new MachineSession(PcMachine.CreateDemo()); var t=s.Enqueue(new(1,"a","NoSuchCommand",default)); var p=s.Enqueue(new(99,"b","Ping",default)); s.PumpCommands(); Equal("UnsupportedCommand",t.Result.ErrorCode); Equal("ProtocolMismatch",p.Result.ErrorCode); });
Test("configuration preflight validates before altering running profile", () => { using var s=new MachineSession(PcMachine.CreateDemo()); var t=s.Enqueue(new(1,"a","PrepareConfigurationChange",JsonSerializer.SerializeToElement(new MachineProfile{RamMiB=16},IpcProtocol.JsonOptions))); s.PumpCommands(); True(t.Result.Success); Equal(8,s.Machine.Profile.RamMiB); Equal(1L,s.Revision); });
AsyncTest("Named Pipe round trip uses framing and safe command queue", async () =>
{
    using var session=new MachineSession(PcMachine.CreateDemo()); await using var server=new IpcServer(session);
    using var client=new NamedPipeClientStream(".",server.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)); await client.ConnectAsync(timeout.Token);
    byte[] body=JsonSerializer.SerializeToUtf8Bytes(new IpcRequest(1,"roundtrip","GetStatus",JsonSerializer.SerializeToElement(new{})),IpcProtocol.JsonOptions);
    byte[] header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,body.Length); await client.WriteAsync(header,timeout.Token); await client.WriteAsync(body,timeout.Token);
    Task reader=Task.Run(async()=>{await client.ReadExactlyAsync(header,timeout.Token); int length=BinaryPrimitives.ReadInt32LittleEndian(header); True(length>0&&length<=65536); byte[] reply=new byte[length]; await client.ReadExactlyAsync(reply,timeout.Token); var response=JsonSerializer.Deserialize<IpcResponse>(reply,IpcProtocol.JsonOptions)!; True(response.Success); Equal("roundtrip",response.RequestId);});
    while(!reader.IsCompleted){session.PumpCommands(); await Task.Delay(1,timeout.Token);} await reader;
});

Test("STI inhibits maskable interrupts until following instruction", () => { var (c,_,_)=Rig(0xfb,0x90,0xf4); c.Step(); True(!c.CanAcceptMaskableInterrupt); c.Step(); True(c.CanAcceptMaskableInterrupt); c.Step(); True(c.Halted&&c.CanAcceptMaskableInterrupt); });
Test("MOV SS inhibits interrupts until following instruction", () => { var (c,_,_)=Rig(0x8e,0xd0,0x90,0xf4); c.Eflags|=X86Cpu.InterruptEnable; c.Step(); True(!c.CanAcceptMaskableInterrupt); c.Step(); True(c.CanAcceptMaskableInterrupt); });
Test("PIC masked edge remains pending until unmasked", () => { var p=new Pic8259(); p.SetLine(1,true); True(!p.HasPending); p.Write(0x21,0xfd,1); True(p.HasPending); Equal((byte?)9,p.Acknowledge()); True(!p.HasPending); });
Test("PIC EOI restores lower-priority delivery", () => { var p=new Pic8259(); p.Write(0x21,0,1); p.SetLine(0,true); Equal((byte?)8,p.Acknowledge()); p.SetLine(1,true); True(!p.HasPending); p.Write(0x20,0x20,1); Equal((byte?)9,p.Acknowledge()); });
Test("PIC cascade delivers slave vector with both EOIs", () => { var p=new Pic8259(); p.Write(0x21,0xfb,1); p.Write(0xa1,0xbf,1); p.SetLine(14,true); Equal((byte?)0x76,p.Acknowledge()); p.Write(0xa0,0x0b,1); Equal(0x40u,p.Read(0xa0,1)); p.Write(0x20,0x0b,1); Equal(4u,p.Read(0x20,1)); p.Write(0xa0,0x20,1); p.Write(0x20,0x20,1); True(!p.HasPending); });
Test("PIC initialization accepts remapped vector bases", () => { var p=new Pic8259(); p.Write(0x20,0x11,1); p.Write(0x21,0x20,1); p.Write(0x21,4,1); p.Write(0x21,1,1); p.Write(0x21,0xfe,1); p.SetLine(0,true); Equal((byte?)0x20,p.Acknowledge()); });
Test("PIT periodic IRQs run from virtual time", () => { var s=new DeviceScheduler(); int pulses=0; var p=new Pit8254(s,()=>pulses++); p.Write(0x43,0x34,1); p.Write(0x40,0xa9,1); p.Write(0x40,4,1); s.AdvanceTo(1_000_000_000); Equal(1000,pulses); });
Test("PIT reprogramming cancels prior deadline", () => { var s=new DeviceScheduler(); int pulses=0; var p=new Pit8254(s,()=>pulses++); p.Write(0x43,0x34,1); p.Write(0x40,2,1); p.Write(0x40,0,1); p.Write(0x43,0x34,1); p.Write(0x40,0,1); p.Write(0x40,0,1); s.AdvanceTo(1_000_000); Equal(0,pulses); });
Test("KBC exposes scan code and output-buffer status", () => { var m=new MemoryBus(1048576); bool high=false; var k=new Keyboard8042(m,x=>high=x); k.InjectTranslatedSet1(0x1e); True(high); Equal(5u,k.Read(0x64,1)); Equal(0x1eu,k.Read(0x60,1)); True(!high); Equal(4u,k.Read(0x64,1)); });
Test("KBC keyboard reset produces ACK and BAT", () => { var k=new Keyboard8042(new(1048576),_=>{}); k.Write(0x60,0xff,1); Equal(0xfau,k.Read(0x60,1)); Equal(0xaau,k.Read(0x60,1)); });
Test("KBC output port controls A20", () => { var m=new MemoryBus(1048576); var k=new Keyboard8042(m,_=>{}); k.Write(0x64,0xd1,1); k.Write(0x60,1,1); True(!m.A20Enabled&&!k.ResetRequested); k.Write(0x64,0xd1,1); k.Write(0x60,3,1); True(m.A20Enabled); });
Test("timer wakes HLT and guest handler returns with IRET", () =>
{
    using var m=PcMachine.CreateDemo(); m.Cpu.SetSegment(Segment.Cs,0); m.Cpu.Eip=0x1000; m.Cpu.SetRegister(Register.Sp,0x9000);
    new byte[]{0xfb,0xf4,0xb8,0x34,0x12,0xf4,0xeb,0xfd}.CopyTo(m.Memory.Ram,0x1000);
    new byte[]{0x50,0xff,0x06,0x00,0x06,0xb0,0x20,0xe6,0x20,0x58,0xcf}.CopyTo(m.Memory.Ram,0x2000);
    m.Memory.WriteWord(0x20,0x2000); m.Memory.WriteWord(0x22,0); m.Pic.Write(0x21,0xfe,1);
    m.Pit.Write(0x43,0x34,1); m.Pit.Write(0x40,0x20,1); m.Pit.Write(0x40,0,1);
    for(int i=0;i<150;i++) { m.RunSteps(1); if(m.Memory.ReadWord(0x600)>0 && m.Cpu.Eip==0x1005) break; }
    Equal<string?>(null,m.Fault); True(m.Memory.ReadWord(0x600)>0); Equal(0x1234u,m.Cpu.GetRegister(Register.Ax)); Equal(0x9000u,m.Cpu.GetRegister(Register.Sp));
});
Test("PCM ring buffer preserves channels and fills underflow with silence", () => { var b=new AudioRingBuffer(48000,2,2); b.Write(new(0,48000,2,[0.25f,0.5f])); float[] samples=new float[4]; b.Read(samples); True(samples.AsSpan().SequenceEqual(new float[]{0.25f,0.5f,0,0})); Equal(1L,b.SilentFrames); });
Test("PCM overflow drops oldest complete stereo frames", () => { var b=new AudioRingBuffer(48000,2,2); b.Write(new(0,48000,2,[1,2,3,4,5,6])); float[] samples=new float[4]; b.Read(samples); True(samples.AsSpan().SequenceEqual(new float[]{3,4,5,6})); Equal(1L,b.DroppedFrames); });
Test("configuration commit power cycles same medium with new RAM", () => { using var s=new MachineSession(PcMachine.CreateDemo()); var plan=new ConfigurationPlan(1,new MachineProfile{RamMiB=16},true); var t=s.Enqueue(new(1,"c","CommitConfigurationChange",JsonSerializer.SerializeToElement(plan,IpcProtocol.JsonOptions))); s.PumpCommands(); True(t.Result.Success); Equal(16,s.Machine.Profile.RamMiB); Equal(2L,s.Revision); s.Machine.RunSteps(100000); Equal((ushort)0xbeef,s.Machine.Memory.ReadWord(0x500)); });
Test("stale configuration commit cannot change machine", () => { using var s=new MachineSession(PcMachine.CreateDemo()); var plan=new ConfigurationPlan(0,new MachineProfile{RamMiB=16},true); var t=s.Enqueue(new(1,"c","CommitConfigurationChange",JsonSerializer.SerializeToElement(plan,IpcProtocol.JsonOptions))); s.PumpCommands(); Equal("StaleConfiguration",t.Result.ErrorCode); Equal(8,s.Machine.Profile.RamMiB); });
Test("invalid disk change leaves current machine intact", () => { using var s=new MachineSession(PcMachine.CreateDemo()); var old=s.Machine; var t=s.Enqueue(new(1,"c","PowerCycleReadOnlyDisk",JsonSerializer.SerializeToElement(new{path="bad.iso"}))); s.PumpCommands(); True(!t.Result.Success); True(ReferenceEquals(old,s.Machine)); });
Test("disposed session completes queued and subsequent requests", () => { var s=new MachineSession(PcMachine.CreateDemo()); var a=s.Enqueue(new(1,"a","Pause",default)); s.Dispose(); var b=s.Enqueue(new(1,"b","Ping",default)); Equal("SessionStopped",a.Result.ErrorCode); Equal("SessionStopped",b.Result.ErrorCode); });

Test("32-bit address over real-mode limit enters guest GP at original IP", () => { var (c,m,_)=Rig(0x67,0xa1,0,0,1,0); m.WriteWord(52,0x2000); c.Step(); Equal(0x2000u,c.Eip); Equal((ushort)0x1000,m.ReadWord(0x8ffa)); });
Test("32-bit near branch over real-mode limit enters guest GP instead of truncating", () => { var (c,m,_)=Rig(0x66,0xe9,0,0,1,0); m.WriteWord(52,0x2000); c.Step(); Equal(0x2000u,c.Eip); Equal((ushort)0x1000,m.ReadWord(0x8ffa)); });
Test("word access crossing segment end faults before writing", () => { var (c,m,_)=Rig(0xa3,0xff,0xff); m.WriteWord(52,0x2000); c.SetRegister(Register.Ax,0xbeef); c.Step(); Equal(0x2000u,c.Eip); Equal((byte)0,m.ReadByte(0xffff)); });

Test("SHL sets carry overflow and zero",()=>{var(c,_,_)=Rig(0xb0,0x80,0xd0,0xe0,0xf4);Run(c);Equal(0u,c.GetRegister(Register.Ax)&255);True((c.Eflags&(X86Cpu.Carry|X86Cpu.Overflow|X86Cpu.Zero))==(X86Cpu.Carry|X86Cpu.Overflow|X86Cpu.Zero));});
Test("SAR retains the sign bit",()=>{var(c,_,_)=Rig(0xb0,0x80,0xc0,0xf8,4,0xf4);Run(c);Equal(0xf8u,c.GetRegister(Register.Ax)&255);});
Test("ROL rotates high bit into low bit and carry",()=>{var(c,_,_)=Rig(0xb0,0x80,0xd0,0xc0,0xf4);Run(c);Equal(1u,c.GetRegister(Register.Ax)&255);True((c.Eflags&X86Cpu.Carry)!=0);});
Test("RCR consumes carry",()=>{var(c,_,_)=Rig(0xb0,0,0xf9,0xd0,0xd8,0xf4);Run(c);Equal(0x80u,c.GetRegister(Register.Ax)&255);True((c.Eflags&X86Cpu.Carry)==0);});
Test("zero shift count preserves flags",()=>{var(c,_,_)=Rig(0xb0,0x81,0xc0,0xe0,0,0xf4);c.Eflags=0x8d7;Run(c);Equal(0x8d7u,c.Eflags);Equal(0x81u,c.GetRegister(Register.Ax)&255);});
Test("MUL byte produces AX",()=>{var(c,_,_)=Rig(0xb0,20,0xb3,20,0xf6,0xe3,0xf4);Run(c);Equal(400u,c.GetRegister(Register.Ax));True((c.Eflags&X86Cpu.Carry)!=0);});
Test("IMUL signed word sign extends high half",()=>{var(c,_,_)=Rig(0xb8,0xfe,0xff,0xbb,3,0,0xf7,0xeb,0xf4);Run(c);Equal(0xfffau,c.GetRegister(Register.Ax));Equal(0xffffu,c.GetRegister(Register.Dx));True((c.Eflags&X86Cpu.Overflow)==0);});
Test("DIV byte returns quotient AL and remainder AH",()=>{var(c,_,_)=Rig(0xb8,0xe9,3,0xb3,10,0xf6,0xf3,0xf4);Run(c);Equal(0x0164u,c.GetRegister(Register.Ax));});
Test("IDIV truncates toward zero",()=>{var(c,_,_)=Rig(0xb8,0xf9,0xff,0x99,0xbb,3,0,0xf7,0xfb,0xf4);Run(c);Equal(0xfffeu,c.GetRegister(Register.Ax));Equal(0xffffu,c.GetRegister(Register.Dx));});
Test("divide by zero delivers vector zero at original instruction",()=>{var(c,m,_)=Rig(0xf6,0xf0);m.WriteWord(0,0x2000);m.WriteWord(2,0);c.Step();Equal(0x2000u,c.Eip);Equal((ushort)0x1000,m.ReadWord(0x8ffa));Equal(0u,c.GetRegister(Register.Ax));});
Test("DIV overflow delivers fault without changing dividend",()=>{var(c,m,_)=Rig(0xf7,0xf1);m.WriteWord(0,0x2000);m.WriteWord(2,0);c.SetRegister(Register.Dx,1);c.SetRegister(Register.Cx,1);c.Step();Equal(0x2000u,c.Eip);Equal(1u,c.GetRegister(Register.Dx));Equal(0u,c.GetRegister(Register.Ax));});
Test("NEG minimum signed word sets overflow",()=>{var(c,_,_)=Rig(0xb8,0,0x80,0xf7,0xd8,0xf4);Run(c);Equal(0x8000u,c.GetRegister(Register.Ax));True((c.Eflags&(X86Cpu.Overflow|X86Cpu.Carry))==(X86Cpu.Overflow|X86Cpu.Carry));});
Test("far CALL and RETF restore CS and stack",()=>{var(c,m,_)=Rig(0x9a,0,0x20,0,0,0xf4);m.WriteByte(0x2000,0xcb);Run(c);Equal(0x1006u,c.Eip);Equal(0x9000u,c.GetRegister(Register.Sp));});
Test("indirect far jump reads offset and segment",()=>{var(c,m,_)=Rig(0xff,0x2e,0,0x30);m.WriteWord(0x3000,0x2000);m.WriteWord(0x3002,0x10);m.WriteByte(0x2100,0xf4);Run(c);Equal((ushort)0x10,c.GetSegment(Segment.Cs));Equal(0x2001u,c.Eip);});
Test("LES loads a complete far pointer",()=>{var(c,m,_)=Rig(0xc4,0x3e,0,0x30,0xf4);m.WriteWord(0x3000,0x1234);m.WriteWord(0x3002,0x5678);Run(c);Equal(0x1234u,c.GetRegister(Register.Di));Equal((ushort)0x5678,c.GetSegment(Segment.Es));});
Test("ENTER and LEAVE restore frame and stack",()=>{var(c,_,_)=Rig(0xc8,0x10,0,0,0xc9,0xf4);c.SetRegister(Register.Bp,0x4567);Run(c);Equal(0x4567u,c.GetRegister(Register.Bp));Equal(0x9000u,c.GetRegister(Register.Sp));});
Test("MOVSX sign extends a byte to dword",()=>{var(c,_,_)=Rig(0xb0,0x81,0x66,0x0f,0xbe,0xd8,0xf4);Run(c);Equal(0xffffff81u,c.GetRegister(Register.Bx));});
Test("BSWAP reverses all four bytes",()=>{var(c,_,_)=Rig(0x66,0xb8,0x78,0x56,0x34,0x12,0x0f,0xc8,0xf4);Run(c);Equal(0x78563412u,c.GetRegister(Register.Ax));});
Test("RTC date is deterministic and uses BCD",()=>{var s=new DeviceScheduler();var r=new RtcCmos(s,8);r.Write(0x70,9,1);Equal(0x26u,r.Read(0x71,1));r.Write(0x70,7,1);Equal(0x24u,r.Read(0x71,1));s.AdvanceTo(2_000_000_000);r.Write(0x70,0,1);Equal(2u,r.Read(0x71,1));});
Test("keyboard test encoding maps digits and shift correctly",()=>{True(GuestKeyboard.Encode("1A\r\n").AsSpan().SequenceEqual(new byte[]{2,0x82,0x2a,0x1e,0x9e,0xaa,0x1c,0x9c}));});

// Boots the original project test sector before running real ROM service assertions.
static PcMachine BiosMachine()
{
    var m=PcMachine.CreateDemo();m.RunSteps(100000);True(m.Fault is null);return m;
}
// Enters an actual guest BIOS service with controlled registers and waits for its architectural return.
static void BiosCall(PcMachine m,byte vector,ushort ax)
{
    m.Cpu.SetSegment(Segment.Cs,0);m.Cpu.Eip=0x1000;m.Memory.WriteByte(0x1000,0xf4);
    m.Cpu.SetRegister(Register.Ax,ax);m.Cpu.EnterRealModeInterrupt(vector);m.RunSteps(100000);
    Equal<string?>(null,m.Fault);True(m.Cpu.Halted);
}
Test("guest BIOS INT12 reports reserved conventional memory",()=>{using var m=BiosMachine();BiosCall(m,0x12,0);Equal(639u,m.Cpu.GetRegister(Register.Ax));});
Test("guest BIOS INT15 reports actual extended RAM",()=>{using var m=BiosMachine();BiosCall(m,0x15,0x8800);Equal(7168u,m.Cpu.GetRegister(Register.Ax));True((m.Cpu.Eflags&X86Cpu.Carry)==0);});
Test("guest BIOS unsupported INT15 returns error carry",()=>{using var m=BiosMachine();BiosCall(m,0x15,0xff00);Equal(0x8600u,m.Cpu.GetRegister(Register.Ax));True((m.Cpu.Eflags&X86Cpu.Carry)!=0);});
Test("guest BIOS teletype updates text memory and cursor",()=>{using var m=BiosMachine();BiosCall(m,0x10,3);BiosCall(m,0x10,0x0e41);Equal((byte)'A',m.Memory.ReadByte(0xb8000));Equal((ushort)1,m.Memory.ReadWord(0x450));BiosCall(m,0x10,0x0e0d);BiosCall(m,0x10,0x0e0a);Equal((ushort)0x100,m.Memory.ReadWord(0x450));});
Test("guest BIOS ROM 8x16 font query returns BP and ES",()=>{using var m=BiosMachine();m.Cpu.SetRegister(Register.Bx,0x0600);BiosCall(m,0x10,0x1130);Equal(0xc000u,m.Cpu.GetRegister(Register.Bp)&0xffff);Equal((ushort)0xf000,m.Cpu.GetSegment(Segment.Es));Equal(16u,m.Cpu.GetRegister(Register.Cx));});
Test("guest BIOS keyboard store peek and get share BDA queue",()=>{using var m=BiosMachine();m.Cpu.SetRegister(Register.Cx,0x1e61);BiosCall(m,0x16,0x0500);BiosCall(m,0x16,0x0100);Equal(0x1e61u,m.Cpu.GetRegister(Register.Ax));True((m.Cpu.Eflags&X86Cpu.Zero)==0);BiosCall(m,0x16,0);Equal(0x1e61u,m.Cpu.GetRegister(Register.Ax));BiosCall(m,0x16,0x0100);True((m.Cpu.Eflags&X86Cpu.Zero)!=0);});
Test("guest BIOS EDD read uses ATA and returns original sector",()=>{using var m=BiosMachine();m.Cpu.SetRegister(Register.Dx,0x80);m.Cpu.SetRegister(Register.Si,0x3000);m.Cpu.SetSegment(Segment.Ds,0);m.Memory.WriteWord(0x3000,16);m.Memory.WriteWord(0x3002,1);m.Memory.WriteWord(0x3004,0x4000);m.Memory.WriteWord(0x3006,0);m.Memory.WriteDword(0x3008,0);m.Memory.WriteDword(0x300c,0);long before=m.Ata.ReadSectorCount;BiosCall(m,0x13,0x4200);True((m.Cpu.Eflags&X86Cpu.Carry)==0);Equal(before+1,m.Ata.ReadSectorCount);Equal((ushort)0xaa55,m.Memory.ReadWord(0x41fe));});

Test("8237 byte transfer handles page wrap and terminal count",()=>{var m=new MemoryBus(2*1048576);var d=new Dma8237(m);d.Write(0x0c,0,1);d.Write(4,0xfe,1);d.Write(4,0xff,1);d.Write(5,3,1);d.Write(5,0,1);d.Write(0x81,1,1);d.Write(0x0b,0x46,1);d.Write(0x0a,2,1);Equal(4,d.Transfer(2,new byte[]{1,2,3,4},true));Equal((byte)1,m.ReadByte(0x1fffe));Equal((byte)2,m.ReadByte(0x1ffff));Equal((byte)3,m.ReadByte(0x10000));Equal(4u,d.Read(8,1)&15);Equal(0u,d.Read(8,1)&15);Equal(0,d.Transfer(2,new byte[]{5},true));});
Test("8237 secondary channel uses word addresses",()=>{var m=new MemoryBus(2*1048576);var d=new Dma8237(m);d.Write(0xd8,0,1);d.Write(0xc4,0,1);d.Write(0xc4,0x80,1);d.Write(0xc6,0,1);d.Write(0xc6,0,1);d.Write(0x8b,3,1);d.Write(0xd6,0x45,1);d.Write(0xd4,1,1);Equal(2,d.Transfer(5,new byte[]{0x34,0x12},true));Equal((ushort)0x1234,m.ReadWord(0x30000));});
Test("8237 auto-init reloads address and count",()=>{var m=new MemoryBus(1048576);var d=new Dma8237(m);d.Write(0x0c,0,1);d.Write(2,0,1);d.Write(2,0x20,1);d.Write(3,0,1);d.Write(3,0,1);d.Write(0x0b,0x55,1);d.Write(0x0a,1,1);Equal(1,d.Transfer(1,new byte[]{1},true));Equal(1,d.Transfer(1,new byte[]{2},true));Equal((byte)2,m.ReadByte(0x2000));});
Test("8237 memory-to-device and master disable",()=>{var m=new MemoryBus(1048576);m.WriteByte(0x2000,0xab);var d=new Dma8237(m);d.Write(0x0c,0,1);d.Write(2,0,1);d.Write(2,0x20,1);d.Write(3,0,1);d.Write(3,0,1);d.Write(0x0b,0x49,1);d.Write(0x0a,1,1);d.Write(8,4,1);byte[] buffer=new byte[1];Equal(0,d.Transfer(1,buffer,false));d.Write(8,0,1);Equal(1,d.Transfer(1,buffer,false));Equal((byte)0xab,buffer[0]);});

StorageTests.AddTo(Test);
MachineStorageTests.AddTo(Test);
SnapshotTests.AddTo(Test);
SnapshotMediaTests.AddTo(Test);
SnapshotEventTests.AddTo(Test);
MediaCheckpointTests.AddTo(Test);
DmaWordTests.AddTo(Test);
SoundBlaster16Tests.AddTo(Test);
MemoryBusFastPathTests.AddTo(Test);
MemoryBusWordTransactionTests.AddTo(Test);
ShiftOptimizationTests.AddTo(Test);
CpuSystemTests.AddTo(Test);
ProtectedModeTests.AddTo(Test);
LssBoundaryTests.AddTo(Test);
GameportBiosTests.AddTo(Test);
SsContractTests.AddTo(Test);
X87EncodingCoverageTests.AddTo(Test);
PciConfigurationTests.AddTo(Test);
MemoryDecodeTests.AddTo(Test);
Audit2CpuTests.AddTo(Test);
Audit2StateMediaTests.AddTo(Test);
AtaPioSnapshotContinuationTests.AddTo(Test);
HostNativeRepeatTests.AddTo(Test);
DosCdRomDriverTests.AddTo(Test);
PciBiosTests.AddTo(Test);
Windows95S3Tests.AddTo(Test);
Windows95UndefinedOpcodeTests.AddTo(Test);
Windows98MmxProbeTests.AddTo(Test);
Windows95ProfileTests.AddTo(Test);
Windows95RamProfileTests.AddTo(Test);
Windows95S3BiosTests.AddTo(Test);
WindowsPostCascadeTests.AddTo(Test);
Windows95S3EngineTests.AddTo(Test);
S3StandardMmioColorTests.AddTo(Test);
Windows95S3MmioPixelTransferTests.AddTo(Test);
Windows95S3WordTailTests.AddTo(Test);
Windows95FdcEotTests.AddTo(Test);
Windows95Sb16CopyrightTests.AddTo(Test);
Windows95Sb16AbsentAspTests.AddTo(Test);
Windows98Sb16ResetTests.AddTo(Test);
Windows95S3CursorTests.AddTo(Test);
Windows95S3EngineImportTests.AddTo(Test);
Windows95PciPostIrqTests.AddTo(Test);
Windows95FloppyBootIrqTests.AddTo(Test);
Windows95LarCompatibilityTests.AddTo(Test);
Audit2InputTests.AddTo(Test);
KeyRepeatDelayTests.AddTo(Test);
Audit2PerformanceTests.AddTo(Test);
FxHostPcTests.AddTo(Test);
PiixPicTests.AddTo(Test);
PiixIrqBridgeTests.AddTo(Test);
PiixIrqPcTests.AddTo(Test);
PciMasterMemoryTests.AddTo(Test);
PiixMirqTests.AddTo(Test);
AtaDmaTests.AddTo(Test);
P54cInitTests.AddTo(Test);
PiixResetDomainTests.AddTo(Test);
PiixResetExternalTests.AddTo(Test);
PiixResetImportTests.AddTo(Test);
PiixResetPcTests.AddTo(Test);
S3MemoryResourceTests.AddTo(Test);
S3Trio64ImportTests.AddTo(Test);
AtapiTests.AddTo(Test);
AtapiSnapshotTests.AddTo(Test);
AtapiMediaTests.AddTo(Test);
S3Trio64Tests.AddTo(Test);
S3Trio64PcTests.AddTo(Test);
PiixIdeTests.AddTo(Test);
PiixIdePcTests.AddTo(Test);
PiixIdeReviewTests.AddTo(Test);
PiixIdeMediaValidationTests.AddTo(Test);
P54cTests.AddTo(Test);
P54cProfileTests.AddTo(Test);
P54cPcTests.AddTo(Test);
TimestampTests.AddTo(Test);
TimestampMsrTests.AddTo(Test);
SelectorStackTests.AddTo(Test);
DebugExtensionTests.AddTo(Test);
PopMemoryTests.AddTo(Test);
CpuAuditTests.AddTo(Test);
SchedulerAuditTests.AddTo(Test);
DecodedMathBatchTests.AddTo(Test);
BiosAuditBoundaryTests.AddTo(Test);
InputHostAuditTests.AddTo(Test);
CowAuditPerformanceTests.AddTo(Test);
OplAuditTests.AddTo(Test);
PrivilegeTests.AddTo(Test);
CallGateTests.AddTo(Test);
HardwareTaskTests.AddTo(Test);
LockAlignmentTests.AddTo(Test);
        DebugTests.AddTo(Test);
PagingTests.AddTo(Test);
Virtual8086Tests.AddTo(Test);
BitDescriptorTests.AddTo(Test);
X87ControlTests.AddTo(Test);
X87StateTests.AddTo(Test);
X87NumericTests.AddTo(Test);
X87ErrorTests.AddTo(Test);
X87ComparisonTests.AddTo(Test);
X87RoundingTests.AddTo(Test);
X87SquareRootTests.AddTo(Test);
X87ConstantTests.AddTo(Test);
X87PackedBcdTests.AddTo(Test);
X87ScalingTests.AddTo(Test);
X87ExtractionTests.AddTo(Test);
X87RemainderTests.AddTo(Test);
X87ExponentialTests.AddTo(Test);
X87LogarithmTests.AddTo(Test);
X87ArcTangentTests.AddTo(Test);
X87TangentTests.AddTo(Test);
X87SineCosineTests.AddTo(Test);
X87SinCosPairTests.AddTo(Test);
RepBatchTests.AddTo(Test);
ReviewPipelineTests.AddTo(Test);
CheckpointOptimizationTests.AddTo(Test);
FetchThroughputTests.AddTo(Test);
AluThroughputTests.AddTo(Test);
RomRepBatchTests.AddTo(Test);
BiosReviewTests.AddTo(Test);
ReviewDeviceTests.AddTo(Test);
BiosBlockMoveTests.AddTo(Test);
FloppyTests.AddTo(Test);
FloppyFormatBiosTests.AddTo(Test);
FloppyDmaBoundaryTests.AddTo(Test);
VgaTests.AddTo(Test);
ParallelScanoutTests.AddTo(Test);
JoinedWorkerTests.AddTo(Test);
Et4000Tests.AddTo(Test);
VbeTests.AddTo(Test);
GameportTests.AddTo(Test);
SerialTests.AddTo(Test);
ParallelTests.AddTo(Test);
MidiTests.AddTo(Test);
Test("IPC shutdown does not depend on a stopped GUI synchronization context", IpcLifecycleTests.CheckShutdown);
VgaFontTests.AddTo(Test);
Cp437FontTests.AddTo(Test);
CursorEmulationTests.AddTo(Test);
VgaTextTests.AddTo(Test);
VgaGraphicsTextTests.AddTo(Test);
SpeakerTests.AddTo(Test);
SoundBlasterTests.AddTo(Test);
SoundBlasterTests.AddHighSpeedTo(Test);
SoundBlasterTests.AddRecordingTo(Test);
SoundBlasterTests.AddAdpcmTo(Test);
SoundBlasterResourceTests.AddTo(Test);
Opl2Tests.AddTo(Test);
Opl2Tests.AddCsmTo(Test);
Opl2Tests.AddRhythmTo(Test);
Opl3Tests.AddTo(Test);
IoTimingTests.AddTo(Test);
RealtimePacerTests.AddTo(Test);
GuestFaultFastPathTests.AddTo(Test);
RtcHostClockTests.AddTo(Test);
HostClockPolicyTests.AddTo(Test);
HostClockSnapshotTests.AddTo(Test);
IntegerBatchTests.AddTo(Test);
IntegerBatchPcTests.AddTo(Test);
IntegerMemoryBatchTests.AddTo(Test);
IntegerMemoryBatchTests.AddDecodedTo(Test);
DecodedBatchTests.AddTo(Test);
RealCsBatchTests.AddTo(Test);
DecodedStackBatchTests.AddTo(Test);
TlbHitSelectionTests.AddTo(Test);
TrioEngineReadOptimizationTests.AddTo(Test);
GuestScenarioTests.AddTo(Test);
NetworkProfileTests.AddTo(Test);
NetworkMenuTests.AddTo(Test);
NetworkCardTests.AddTo(Test);
NetworkIntegrationTests.AddTo(Test);
MouseTests.AddTo(Test);
MouseEdgeAdmissionTests.AddTo(Test);
BiosApmTests.AddTo(Test);
ResetTests.AddTo(Test);

if (args.Length == 2 && args[0] == "--filter")
{
    tests.RemoveAll(t => !t.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase));
    if (tests.Count == 0) throw new ArgumentException("No tests matched the requested filter.");
}
var results=new List<object>(); int failed=0;
foreach(var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); results.Add(new{name=test.Name,passed=true}); }
    catch(Exception ex){failed++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); results.Add(new{name=test.Name,passed=false,error=ex.ToString()});}
}
string report=Path.Combine(Environment.CurrentDirectory,"artifacts","tests","results.json"); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report,JsonSerializer.Serialize(new{total=tests.Count,passed=tests.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{tests.Count-failed}/{tests.Count} passed");
return failed==0?0:1;
