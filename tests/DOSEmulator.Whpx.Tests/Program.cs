// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

if (!WhpxRealModeMachine.IsAvailable())
{
    Console.WriteLine("SKIP: Windows Hypervisor Platform is unavailable.");
    return;
}

var ports = new IoPortBus();
var device = new ProofPort();
ports.Map(0xe9, 1, device);
using var machine = new WhpxRealModeMachine(ports);
// MOV AL,'A'; OUT E9,AL; IN AL,E9; MOV [2000h],AL; HLT.
byte[] program = [0xb0, 0x41, 0xe6, 0xe9, 0xe4, 0xe9, 0xa2, 0x00, 0x20, 0xf4];
machine.WriteRam(0x1000, program);
machine.SetEntry(0, 0x1000);
var result = machine.RunUntilHalt(TimeSpan.FromSeconds(5));
if (result.IoExits != 2 || result.TotalExits != 3 || device.Writes != 1 || device.Reads != 1 ||
    device.LastWrite != 0x41 || machine.ReadRam(0x2000, 1)[0] != 0x7a ||
    !machine.TakeDirtyPages().Contains(2))
    throw new InvalidOperationException("WHP/DOSE bus or RAM result differs from the own real-mode oracle.");
Console.WriteLine($"PASS: WHPX real-mode RAM, DOSE I/O bus (2 exits), dirty page, HLT; RIP={result.Rip:X}.");
machine.Dispose();

var boardMemory = new MemoryBus(1 << 20);
var board = new DevelopmentBoardPorts(boardMemory);
var boardPorts = new IoPortBus();
boardPorts.Map(0xe9, 1, board);
using var boardMachine = new WhpxRealModeMachine(boardPorts);
boardMachine.WriteRam(0x1010, [0xb0, (byte)'W', 0xe6, 0xe9, 0xf4]);
boardMachine.SetEntry(0x100, 0x10);
var boardResult = boardMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
if (boardResult.IoExits != 1 || board.DebugText != "W")
    throw new InvalidOperationException("Actual DOSE development-board I/O did not receive the WHP exit.");
Console.WriteLine("PASS: WHPX exit reached the actual DOSE E9 board device.");
boardMachine.Dispose();

var mappedBus = new MemoryBus(1 << 20);
var mappedDevice = new ProofMemory();
mappedBus.Map(0xa0000, 4096, mappedDevice, "synthetic VGA aperture");
var firmware = new byte[65536];
firmware[0] = 0x5a;
mappedBus.Map(0xf0000, 65536, new RomDevice(firmware), "synthetic BIOS");
var mappedPorts = new IoPortBus();
var mappedPic = new Pic8259();
mappedPorts.Map(0x20, 2, mappedPic);
mappedPorts.Map(0xa0, 2, mappedPic);
var mappedClock = new DeviceScheduler();
using (var mapped = new WhpxMappedMachine(mappedBus, mappedPorts, mappedPic, mappedClock))
{
    // ES=A000h; write/read a device byte, then read F000:0000 ROM into RAM.
    byte[] mappedProgram = [
        0xb8, 0x00, 0xa0, 0x8e, 0xc0,
        0x26, 0xc6, 0x06, 0x00, 0x00, 0x33,
        0x26, 0xa0, 0x00, 0x00, 0xa2, 0x00, 0x20,
        0xb8, 0x00, 0xf0, 0x8e, 0xd8, 0xa0, 0x00, 0x00,
        0xbb, 0x00, 0x00, 0x8e, 0xdb, 0xa2, 0x01, 0x20, 0xf4
    ];
    mappedBus.Ram.AsSpan(0x1000, mappedProgram.Length).Clear();
    mappedProgram.CopyTo(mappedBus.Ram.AsSpan(0x1000));
    mapped.SetEntry(0, 0x1000);
    mapped.RestoreCpuState(mapped.CaptureCpuState());
    var mappedResult = mapped.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (mappedDevice.Reads != 1 || mappedDevice.Writes != 1 || mappedDevice.Value != 0x33 ||
        mappedBus.Ram[0x2000] != 0x33 || mappedBus.Ram[0x2001] != 0x5a || mappedResult.MmioExits < 2 || mappedClock.Now == 0)
        throw new InvalidOperationException($"Mapped WHP result differs: reads={mappedDevice.Reads}, writes={mappedDevice.Writes}, value={mappedDevice.Value:X2}, RAM={mappedBus.Ram[0x2000]:X2}/{mappedBus.Ram[0x2001]:X2}, MMIO={mappedResult.MmioExits}, clock={mappedClock.Now}, RIP={mappedResult.Rip:X}.");
    Console.WriteLine($"PASS: mapped DOSE MMIO, BIOS ROM, RAM and clock ({mappedResult.MmioExits} MMIO exits).");
}

var irqMemory = new MemoryBus(1 << 20);
var irqPorts = new IoPortBus();
var irqPic = new Pic8259();
irqPorts.Map(0x20, 2, irqPic);
irqPorts.Map(0xa0, 2, irqPic);
var irqClock = new DeviceScheduler();
irqMemory.Ram[0x20] = 0x00;
irqMemory.Ram[0x21] = 0x30;
new byte[] { 0xfb, 0xf4, 0xf4 }.CopyTo(irqMemory.Ram.AsSpan(0x1000));
new byte[] { 0xc6, 0x06, 0x00, 0x21, 0x42, 0xcf }.CopyTo(irqMemory.Ram.AsSpan(0x3000));
irqPic.Write(0x21, 0xfe, 1);
irqPic.SetLine(0, true);
using (var irqMachine = new WhpxMappedMachine(irqMemory, irqPorts, irqPic, irqClock))
{
    irqMachine.SetEntry(0, 0x1000);
    var irqResult = irqMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (irqResult.PicInterrupts != 1 || irqMemory.Ram[0x2100] != 0x42)
        throw new InvalidOperationException($"WHP/PIC interrupt failed: count={irqResult.PicInterrupts}, memory={irqMemory.Ram[0x2100]:X2}, RIP={irqResult.Rip:X}.");
    Console.WriteLine("PASS: DOSE PIC IRQ0 entered and returned from a WHP real-mode handler.");
}

var timedMemory = new MemoryBus(1 << 20);
var timedPic = new Pic8259();
timedPic.Write(0x21, 0xfe, 1);
timedMemory.Ram[0x20] = 0;
timedMemory.Ram[0x21] = 0x30;
new byte[] { 0xfb, 0xf4, 0xf4 }.CopyTo(timedMemory.Ram.AsSpan(0x1000));
new byte[] { 0xc6, 0x06, 0x00, 0x21, 0x67, 0xcf }.CopyTo(timedMemory.Ram.AsSpan(0x3000));
var timedClock = new DeviceScheduler();
timedClock.ScheduleAfter(10_000_000, () => timedPic.SetLine(0, true));
using (var timedMachine = new WhpxMappedMachine(timedMemory, new IoPortBus(), timedPic, timedClock))
{
    timedMachine.SetEntry(0, 0x1000);
    var timedResult = timedMachine.RunUntilVirtualTime(30_000_000, TimeSpan.FromSeconds(5));
    if (timedClock.Now < 30_000_000 || timedMemory.Ram[0x2100] != 0x67 || timedResult.PicInterrupts != 1)
        throw new InvalidOperationException("WHP HLT did not clock and deliver the scheduled PIC event.");
    Console.WriteLine("PASS: halted WHP guest wakes on scheduled DOSE PIC event.");
}

var a20Memory = new MemoryBus(2 << 20);
var a20Board = new DevelopmentBoardPorts(a20Memory);
var a20Ports = new IoPortBus();
a20Ports.Map(0x92, 1, a20Board);
new byte[] {
    0xb0, 0x00, 0xe6, 0x92,             // A20 off
    0xb8, 0xff, 0xff, 0x8e, 0xc0,       // ES=FFFF
    0x26, 0xc6, 0x06, 0x10, 0x00, 0x55, // [ES:0010] -> physical zero
    0xb0, 0x02, 0xe6, 0x92,             // A20 on
    0x26, 0xc6, 0x06, 0x10, 0x00, 0x66, // [ES:0010] -> physical 1 MiB
    0xf4
}.CopyTo(a20Memory.Ram.AsSpan(0x1000));
using (var a20Machine = new WhpxMappedMachine(a20Memory, a20Ports, new Pic8259(), new DeviceScheduler()))
{
    a20Machine.SetEntry(0, 0x1000);
    var a20Result = a20Machine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (a20Result.IoExits != 2 || a20Memory.Ram[0] != 0x55 || a20Memory.Ram[0x100000] != 0x66)
        throw new InvalidOperationException("WHP A20 remapping differs from DOSE physical bus semantics.");
    Console.WriteLine("PASS: A20 transitions remap the WHP physical alias correctly.");
}

var stringMemory = new MemoryBus(1 << 20);
var stringPorts = new IoPortBus();
var stringDevice = new ProofPort();
stringPorts.Map(0xe9, 1, stringDevice);
// DX=E9, CX=3, DI=2000; REP INSB; HLT.
new byte[] { 0xba, 0xe9, 0x00, 0xb9, 0x03, 0x00, 0xbf, 0x00, 0x20, 0xf3, 0x6c, 0xf4 }
    .CopyTo(stringMemory.Ram.AsSpan(0x1000));
using (var stringMachine = new WhpxMappedMachine(stringMemory, stringPorts, new Pic8259(), new DeviceScheduler()))
{
    stringMachine.SetEntry(0, 0x1000);
    var stringResult = stringMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (stringResult.IoExits < 1 || stringDevice.Reads != 3 ||
        !stringMemory.Ram.AsSpan(0x2000, 3).SequenceEqual(new byte[] { 0x7a, 0x7a, 0x7a }))
        throw new InvalidOperationException("WHP REP INSB did not traverse the DOSE port and RAM bus.");
    Console.WriteLine("PASS: WHP REP INSB traverses the DOSE port and RAM bus.");
}

var pamMemory = new MemoryBus(8 << 20);
var pamFirmware = new byte[65536];
pamFirmware[0x100] = 0x5a;
var pamHost = new Intel82437Fx(pamMemory, pamFirmware);
pamMemory.Map(0xc0000, 0x40000, pamHost, "real 430FX PAM");
var pamPorts = new IoPortBus();
pamPorts.Map(0x123, 1, new PamSwitch(pamHost));
new byte[] {
    0xb8, 0x00, 0xf0, 0x8e, 0xc0, // ES=F000
    0xba, 0x23, 0x01, 0xb0, 0x30, 0xee, // switch PAM0 to shadow read/write
    0x26, 0xc6, 0x06, 0x00, 0x01, 0x66,
    0x26, 0xa0, 0x00, 0x01, 0xa2, 0x00, 0x20, 0xf4
}.CopyTo(pamMemory.Ram.AsSpan(0x1000));
using (var pamMachine = new WhpxMappedMachine(pamMemory, pamPorts, new Pic8259(), new DeviceScheduler()))
{
    pamMachine.SetEntry(0, 0x1000);
    var pamResult = pamMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (pamMemory.Ram[0xf0100] != 0x66 || pamMemory.Ram[0x2000] != 0x66 || pamResult.MmioExits == 0)
        throw new InvalidOperationException("WHP PAM shadow read/write did not follow the real 430FX decoder.");
    Console.WriteLine("PASS: real 430FX PAM shadow updates WHP firmware pages and RAM.");
}

if (Environment.GetEnvironmentVariable("DOSE_WHPX_BOOT_PROBE") == "1")
{
    using var demo = PcMachine.CreateDemo();
    using var accelerated = WhpxPcSession.Start(demo);
    bool cpuOwnerRejected = false;
    try { demo.RunSteps(1); }
    catch (InvalidOperationException) { cpuOwnerRejected = true; }
    if (!cpuOwnerRejected) throw new InvalidOperationException("Software CPU ran while WHP owned the PC.");
    try
    {
        var boot = accelerated.RunUntilHalt(TimeSpan.FromSeconds(5), 10000);
        Console.WriteLine($"BOOT-PROBE: exits={boot.TotalExits}, io={boot.IoExits}, mmio={boot.MmioExits}, irq={boot.PicInterrupts}, RIP={boot.Rip:X}, POST={demo.Board.Post10Count}, next={demo.Scheduler.NextEventTime}.");
    }
    catch (Exception error)
    {
        Console.WriteLine($"BOOT-PROBE: {error.GetType().Name}: {error.Message}; POST={demo.Board.Post10Count}; RIP={accelerated.GetRegister(0x10):X}.");
    }
    var checkpoint = accelerated.Capture();
    ulong savedRip = accelerated.GetRegister(0x10);
    accelerated.Dispose();
    using var fresh = PcMachine.CreateDemo();
    using var resumed = WhpxPcSession.Resume(fresh, checkpoint);
    if (fresh.Scheduler.Now != demo.Scheduler.Now || fresh.Board.Post10Count != demo.Board.Post10Count ||
        resumed.GetRegister(0x10) != savedRip ||
        !fresh.Memory.Ram.AsSpan().SequenceEqual(demo.Memory.Ram))
        throw new InvalidOperationException("WHP PC checkpoint did not round-trip on a fresh device graph.");
    ulong resumeTarget = checked(fresh.Scheduler.Now + 1_000_000);
    var continuation = resumed.RunUntilVirtualTime(resumeTarget, TimeSpan.FromSeconds(5));
    if (fresh.Scheduler.Now < resumeTarget || continuation.TotalExits == 0)
        throw new InvalidOperationException("Restored WHP PC did not continue advancing guest time.");
    Console.WriteLine("PASS: full DOSE device graph, scheduler, RAM and WHP CPU register checkpoint resumed.");

    string ownedRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
        "artifacts", "tests", "whpx-persist-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(ownedRoot);
    try
    {
        string saved = Path.Combine(ownedRoot, "own.whpxsav");
        ulong persistedTime = fresh.Scheduler.Now, persistedRip = resumed.GetRegister(0x10);
        WhpxSnapshotStore.Capture(resumed, saved);
        resumed.Dispose();
        using var loaded = WhpxSnapshotStore.Load(saved, Path.Combine(ownedRoot, "own.dosebranch"));
        if (loaded.Pc.Scheduler.Now != persistedTime || loaded.GetRegister(0x10) != persistedRip ||
            loaded.Pc.Board.Post10Count != fresh.Board.Post10Count ||
            !loaded.Pc.Memory.Ram.AsSpan().SequenceEqual(fresh.Memory.Ram))
            throw new InvalidOperationException("Persistent WHP media/PC/CPU checkpoint did not restore.");
        Console.WriteLine("PASS: sealed WHPX snapshot restored media, device graph, RAM and CPU state.");
        string cpuPath = Path.Combine(saved, "cpu.json");
        byte[] corrupted = File.ReadAllBytes(cpuPath);
        corrupted[^1] ^= 1;
        File.WriteAllBytes(cpuPath, corrupted);
        bool rejected = false;
        try { using var invalid = WhpxSnapshotStore.Load(saved, Path.Combine(ownedRoot, "invalid.dosebranch")); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected || Directory.Exists(Path.Combine(ownedRoot, "invalid.dosebranch")))
            throw new InvalidOperationException("Corrupt WHPX CPU state was not rejected before media branching.");
        Console.WriteLine("PASS: corrupted WHPX CPU sidecar rejected before media branching.");
    }
    finally
    {
        if (!ownedRoot.StartsWith(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "tests")) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete a non-test WHP snapshot path.");
        if (Directory.Exists(ownedRoot)) Directory.Delete(ownedRoot, true);
    }
}

if (Environment.GetEnvironmentVariable("DOSE_WHPX_WIN_PROBE") == "1")
{
    var profile = new MachineProfile
    {
        Id = MachineProfile.Windows95ProfileId, Name = "WHPX isolated Windows95 board probe",
        Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2
    };
    using var pc = PcMachine.CreateDemo(profile);
    using var whpx = WhpxPcSession.Start(pc);
    try
    {
        var winResult = whpx.RunUntilVirtualTime(200_000_000, TimeSpan.FromSeconds(30), 10000);
        Console.WriteLine($"WIN-BOARD-PROBE: POST={pc.Board.Post10Count}, code={pc.Board.PostCode:X2}, exits={winResult.TotalExits}, io={winResult.IoExits}, mmio={winResult.MmioExits}, irq={winResult.PicInterrupts}, RIP={winResult.Rip:X}.");
    }
    catch (Exception error)
    {
        Console.WriteLine($"WIN-BOARD-PROBE: {error.GetType().Name}: {error.Message}; POST={pc.Board.Post10Count}, RIP={whpx.GetRegister(0x10):X}.");
    }
    var winCheckpoint = whpx.Capture();
    ulong winRip = whpx.GetRegister(0x10);
    whpx.Dispose();
    using var restoredPc = PcMachine.CreateDemo(profile);
    using var restoredWhpx = WhpxPcSession.Resume(restoredPc, winCheckpoint);
    if (restoredPc.Board.Post10Count != pc.Board.Post10Count ||
        restoredPc.Scheduler.Now != pc.Scheduler.Now || restoredWhpx.GetRegister(0x10) != winRip ||
        !restoredPc.Memory.Ram.AsSpan().SequenceEqual(pc.Memory.Ram))
        throw new InvalidOperationException("WHP Windows95 board checkpoint did not round-trip.");
    Console.WriteLine("PASS: Windows95 board WHP CPU, devices, scheduler and RAM checkpoint round-tripped.");
}

sealed class ProofPort : IIoPortDevice
{
    public int Writes { get; private set; }
    public int Reads { get; private set; }
    public uint LastWrite { get; private set; }
    public uint Read(ushort port, int width) { Reads++; return 0x7a; }
    public void Write(ushort port, uint value, int width) { Writes++; LastWrite = value; }
}

sealed class ProofMemory : IMemoryDevice
{
    public byte Value { get; private set; }
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public byte ReadByte(uint offset) { Reads++; return Value; }
    public void WriteByte(uint offset, byte value) { Writes++; Value = value; }
}

sealed class PamSwitch(Intel82437Fx host) : IIoPortDevice
{
    public uint Read(ushort port, int width) => 0xff;
    public void Write(ushort port, uint value, int width) => host.WriteConfiguration(0x59, value, width);
}
