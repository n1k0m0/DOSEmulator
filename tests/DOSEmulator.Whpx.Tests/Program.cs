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

var cpuMemory = new MemoryBus(1 << 20);
var cpuClock = new DeviceScheduler();
byte[] cpuProgram = [
    0x66, 0xb8, 0, 0, 0, 0, 0x0f, 0xa2,             // CPUID leaf 0
    0x66, 0xa3, 0x00, 0x20, 0x66, 0x89, 0x1e, 0x04, 0x20,
    0x66, 0x89, 0x0e, 0x08, 0x20, 0x66, 0x89, 0x16, 0x0c, 0x20,
    0x66, 0xb8, 1, 0, 0, 0, 0x0f, 0xa2,             // CPUID leaf 1
    0x66, 0xa3, 0x10, 0x20, 0x66, 0x89, 0x16, 0x14, 0x20,
    0x0f, 0x31, 0x66, 0xa3, 0x18, 0x20, 0x66, 0x89, 0x16, 0x1c, 0x20, // RDTSC
    0x66, 0xb9, 0x10, 0, 0, 0, 0x0f, 0x32,         // RDMSR TSC
    0x66, 0xa3, 0x20, 0x20, 0x66, 0x89, 0x16, 0x24, 0x20,
    0x66, 0xb8, 0x78, 0x56, 0x34, 0x12, 0x66, 0xba, 0, 0, 0, 0,
    0x0f, 0x30, 0x0f, 0x32,                        // WRMSR / RDMSR TSC
    0x66, 0xa3, 0x28, 0x20, 0x66, 0x89, 0x16, 0x2c, 0x20, 0xf4
];
cpuProgram.CopyTo(cpuMemory.Ram.AsSpan(0x1000));
using (var cpuMachine = new WhpxMappedMachine(cpuMemory, new IoPortBus(), new Pic8259(), cpuClock,
    WhpxCpuContract.IntelP54cB5Subset, 100_000_000))
{
    cpuMachine.SetEntry(0, 0x1000);
    cpuMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    uint Read32(int offset) => BitConverter.ToUInt32(cpuMemory.Ram, offset);
    ulong Read64(int offset) => (ulong)Read32(offset) | ((ulong)Read32(offset + 4) << 32);
    if (Read32(0x2000) != 1 || Read32(0x2004) != 0x756e6547 ||
        Read32(0x2008) != 0x6c65746e || Read32(0x200c) != 0x49656e69 ||
        Read32(0x2010) != 0x524 || Read32(0x2014) != 0x135 ||
        Read64(0x2018) > Read64(0x2020) || Read64(0x2028) < 0x1234_5678 ||
        Read64(0x2028) > 0x1234_5678 + 1_000_000)
        throw new InvalidOperationException("WHP CPUID/RDTSC/TSC-MSR results differ from the DOSE P54C clock contract.");
    var cpuCheckpoint = cpuMachine.CaptureCpuState();
    if (cpuCheckpoint.Contract != WhpxCpuContract.IntelP54cB5Subset || cpuCheckpoint.NominalCpuHz != 100_000_000 ||
        cpuCheckpoint.TscEpochTicks == 0)
        throw new InvalidOperationException("WHP checkpoint omitted the nominal P54C TSC epoch.");
    Console.WriteLine("PASS: WHP CPUID and RDTSC/RDMSR/WRMSR follow the DOSE P54C identity and virtual 100-MHz clock.");
}

var oldCpuMemory = new MemoryBus(1 << 20);
oldCpuMemory.Ram[0x18] = 0x00; oldCpuMemory.Ram[0x19] = 0x30;
new byte[] { 0x0f, 0xa2, 0xf4 }.CopyTo(oldCpuMemory.Ram.AsSpan(0x1000));
new byte[] { 0xc6, 0x06, 0x00, 0x21, 0x42, 0x58, 0x83, 0xc0, 0x02, 0x50, 0xcf }
    .CopyTo(oldCpuMemory.Ram.AsSpan(0x3000));
using (var oldCpuMachine = new WhpxMappedMachine(oldCpuMemory, new IoPortBus(), new Pic8259(), new DeviceScheduler(),
    WhpxCpuContract.Intel486Dx))
{
    oldCpuMachine.SetEntry(0, 0x1000);
    oldCpuMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (oldCpuMemory.Ram[0x2100] != 0x42)
        throw new InvalidOperationException("WHP exposed host CPUID to the DOSE 486 profile.");
    Console.WriteLine("PASS: 486 WHP CPUID raises the guest's #UD handler.");
}

var unknownMsrMemory = new MemoryBus(1 << 20);
unknownMsrMemory.Ram[0x34] = 0x00; unknownMsrMemory.Ram[0x35] = 0x30;
new byte[] { 0x66, 0xb9, 0x11, 0, 0, 0, 0x0f, 0x32, 0xf4 }
    .CopyTo(unknownMsrMemory.Ram.AsSpan(0x1000));
new byte[] { 0xc6, 0x06, 0x00, 0x21, 0x42, 0xf4 }
    .CopyTo(unknownMsrMemory.Ram.AsSpan(0x3000));
using (var unknownMsrMachine = new WhpxMappedMachine(unknownMsrMemory, new IoPortBus(), new Pic8259(),
    new DeviceScheduler(), WhpxCpuContract.IntelP54cB5Subset, 100_000_000))
{
    unknownMsrMachine.SetEntry(0, 0x1000);
    unknownMsrMachine.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (unknownMsrMemory.Ram[0x2100] != 0x42)
        throw new InvalidOperationException("WHP exposed an unimplemented host MSR to the DOSE P54C profile.");
    Console.WriteLine("PASS: unknown P54C MSR raises the guest's #GP handler.");
}

var sleepMemory = new MemoryBus(1 << 20);
var sleepClock = new DeviceScheduler();
var sleepPic = new Pic8259();
var sleepPorts = new IoPortBus();
sleepPorts.Map(0x20, 2, sleepPic);
sleepPic.Write(0x21, 0xfe, 1);
sleepMemory.Ram[0x20] = 0x00; sleepMemory.Ram[0x21] = 0x30;
new byte[] {
    0x66, 0xb9, 0x10, 0, 0, 0, 0x66, 0xb8, 0x78, 0x56, 0x34, 0x12,
    0x66, 0xba, 0, 0, 0, 0, 0x0f, 0x30, 0xfb, 0xf4, // WRMSR; STI; HLT
    0x0f, 0x32, 0x66, 0xa3, 0x00, 0x21, 0x66, 0x89, 0x16, 0x04, 0x21, 0xf4
}.CopyTo(sleepMemory.Ram.AsSpan(0x1000));
new byte[] { 0xb0, 0x20, 0xe6, 0x20, 0xcf }.CopyTo(sleepMemory.Ram.AsSpan(0x3000));
WhpxCpuState sleepState;
ulong wakeAt;
using (var sleeper = new WhpxMappedMachine(sleepMemory, sleepPorts, sleepPic, sleepClock,
    WhpxCpuContract.IntelP54cB5Subset, 100_000_000))
{
    sleeper.SetEntry(0, 0x1000);
    sleeper.RunUntilHalt(TimeSpan.FromSeconds(5));
    sleepState = sleeper.CaptureCpuState();
    if (!sleepState.Halted)
        throw new InvalidOperationException("WHP owner did not checkpoint the stopped HLT state.");
    wakeAt = checked(sleepClock.Now + 20_000_000);
}
var resumedSleepClock = new DeviceScheduler();
resumedSleepClock.AdvanceTo(sleepClock.Now);
var resumedSleepPic = new Pic8259();
var resumedSleepPorts = new IoPortBus();
resumedSleepPorts.Map(0x20, 2, resumedSleepPic);
resumedSleepPic.Write(0x21, 0xfe, 1);
resumedSleepClock.ScheduleAfter(wakeAt - resumedSleepClock.Now, () =>
{
    resumedSleepPic.SetLine(0, true);
    resumedSleepPic.SetLine(0, false);
});
using (var resumedSleeper = new WhpxMappedMachine(sleepMemory, resumedSleepPorts, resumedSleepPic,
    resumedSleepClock, WhpxCpuContract.IntelP54cB5Subset, 100_000_000))
{
    resumedSleeper.RestoreCpuState(sleepState);
    var stillHalted = resumedSleeper.RunUntilHalt(TimeSpan.FromSeconds(5));
    if (stillHalted.TotalExits != 0 || sleepMemory.Ram[0x2100] != 0)
        throw new InvalidOperationException("Restored HLT state executed before an interrupt.");
    resumedSleeper.RunUntilVirtualTime(wakeAt + 1_000_000, TimeSpan.FromSeconds(5));
    ulong afterWakeTsc = BitConverter.ToUInt64(sleepMemory.Ram, 0x2100);
    if (afterWakeTsc < 0x1234_5678 || afterWakeTsc > 0x1234_5678 + 10_000_000)
        throw new InvalidOperationException("Restored P54C TSC epoch or HLT wakeup differs after the PIC interrupt.");
    Console.WriteLine("PASS: WHP HLT state and written P54C TSC epoch survive a stopped checkpoint and PIC wakeup.");
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
    if (fresh.Scheduler.Now < resumeTarget || !checkpoint.Cpu.Halted || continuation.TotalExits != 0 ||
        resumed.GetRegister(0x10) != savedRip)
        throw new InvalidOperationException("Restored WHP PC did not preserve HLT while advancing guest time.");
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
        var persistedHalt = loaded.RunUntilVirtualTime(persistedTime + 1_000_000, TimeSpan.FromSeconds(5));
        if (persistedHalt.TotalExits != 0 || loaded.GetRegister(0x10) != persistedRip)
            throw new InvalidOperationException("Persistent WHP snapshot lost its stopped HLT state.");
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

string? realBootFloppy = Environment.GetEnvironmentVariable("DOSE_WHPX_REAL_BOOT_FLOPPY");
if (!string.IsNullOrWhiteSpace(realBootFloppy))
{
    var profile = new MachineProfile
    {
        Id = MachineProfile.Windows95ProfileId, Name = "WHPX isolated real Windows boot floppy probe",
        Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2
    };
    string overlay = Path.Combine(Path.GetTempPath(), "dose-whpx-floppy-" + Guid.NewGuid().ToString("N") + ".cow");
    try
    {
        using var pc = MachineFactory.FromImagesWithOptical(profile, null, floppyA: realBootFloppy,
            floppyAOverlay: overlay, bootDevice: "floppy-a");
        using var whpx = WhpxPcSession.Start(pc);
        int bootSteps = int.TryParse(Environment.GetEnvironmentVariable("DOSE_WHPX_REAL_BOOT_STEPS"), out int requestedSteps)
            ? Math.Clamp(requestedSteps, 1, 200) : 20;
        for (int step = 1; step <= bootSteps; step++)
        {
            try
            {
                var run = whpx.RunUntilVirtualTime(checked(pc.Scheduler.Now + 100_000_000), TimeSpan.FromSeconds(10), 100_000);
                if (step <= 20 || step % 10 == 0 || step == bootSteps)
                    Console.WriteLine($"REAL-BOOT-PROBE {step}: time={pc.Scheduler.Now}, POST={pc.Board.PostCode:X2}, RIP={run.Rip:X}, exits={run.TotalExits}, io={run.IoExits}, mmio={run.MmioExits}, irq={run.PicInterrupts}.");
            }
            catch (Exception error)
            {
                Console.WriteLine($"REAL-BOOT-PROBE {step}: {error.GetType().Name}: {error.Message}; time={pc.Scheduler.Now}, POST={pc.Board.PostCode:X2}, RIP={whpx.GetRegister(0x10):X}.");
                break;
            }
        }
        Console.WriteLine("REAL-BOOT-DIAGNOSTIC: " + pc.Board.DebugText.Replace('\r', ' ').Replace('\n', ' '));
        byte[] bootBytes = new byte[16];
        using (var source = File.OpenRead(realBootFloppy)) source.ReadExactly(bootBytes);
        Console.WriteLine($"REAL-BOOT-FDC: reads={pc.Floppy.ReadSectorCount}, busy={pc.Floppy.Busy}, irq={pc.Floppy.InterruptAsserted}, source={Convert.ToHexString(bootBytes)}, ram={Convert.ToHexString(pc.Memory.Ram.AsSpan(0x7c00, 16))}.");
        var realBootCpu = whpx.Capture().Cpu;
        var csState = realBootCpu.Registers.Single(register => register.Name == 0x13);
        Console.WriteLine($"REAL-BOOT-CPU: CS={(csState.High >> 32) & 0xffff:X4}, base={csState.Low:X}, RIP={whpx.GetRegister(0x10):X}, flags={whpx.GetRegister(0x11):X}, CR0={whpx.GetRegister(0x1000):X}.");
        for (uint row = 0; row < 25; row++)
        {
            var line = new string(Enumerable.Range(0, 80).Select(column =>
                (char)pc.Memory.ReadByte(0xb8000u + row * 160 + (uint)column * 2)).ToArray()).Trim();
            if (line.Length > 0) Console.WriteLine($"REAL-BOOT-SCREEN {row:D2}: {line}");
        }
        string firstRow = new(Enumerable.Range(0, 80).Select(column =>
            (char)pc.Memory.ReadByte(0xb8000u + (uint)column * 2)).ToArray());
        if (!firstRow.Contains("Microsoft Windows 98 CD-ROM Startup Menu", StringComparison.Ordinal))
            throw new InvalidOperationException("The real Windows 98 boot floppy did not reach its startup menu under WHP.");
        string snapshotRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "artifacts", "tests", "whpx-real-boot-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(snapshotRoot);
        try
        {
            string saved = Path.Combine(snapshotRoot, "menu.whpxsav");
            ulong savedTime = pc.Scheduler.Now, savedRip = whpx.GetRegister(0x10);
            WhpxSnapshotStore.Capture(whpx, saved);
            whpx.Dispose();
            using var restored = WhpxSnapshotStore.Load(saved, Path.Combine(snapshotRoot, "menu.dosebranch"));
            if (restored.Pc.Scheduler.Now != savedTime || restored.GetRegister(0x10) != savedRip ||
                !restored.Pc.Memory.Ram.AsSpan().SequenceEqual(pc.Memory.Ram) ||
                !firstRow.SequenceEqual(Enumerable.Range(0, 80).Select(column =>
                    (char)restored.Pc.Memory.ReadByte(0xb8000u + (uint)column * 2))))
                throw new InvalidOperationException("Real Windows 98 boot-menu snapshot did not restore exactly.");
            var continued = restored.RunUntilVirtualTime(checked(savedTime + 100_000_000), TimeSpan.FromSeconds(10), 100_000);
            if (continued.TotalExits == 0 || restored.Pc.Scheduler.Now < savedTime + 100_000_000)
                throw new InvalidOperationException("Restored Windows 98 boot menu did not continue under WHP.");
            Console.WriteLine("PASS: real Windows 98 boot menu and isolated WHP media/PC/CPU snapshot continuation.");
        }
        finally
        {
            if (!snapshotRoot.StartsWith(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "tests")) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to delete a non-test WHP snapshot path.");
            if (Directory.Exists(snapshotRoot)) Directory.Delete(snapshotRoot, true);
        }
    }
    finally { if (File.Exists(overlay)) File.Delete(overlay); }
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
