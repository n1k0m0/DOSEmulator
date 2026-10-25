using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public sealed class PcMachine : IDisposable
{
    private readonly IReadOnlyBlockDevice? _disk;
    public MachineProfile Profile { get; }
    public MemoryBus Memory { get; }
    public IoPortBus Ports { get; } = new();
    public DeviceScheduler Scheduler { get; } = new();
    public X86Cpu Cpu { get; }
    public FpuErrorLatch FpuError { get; }
    public VgaAdapter Video { get; }
    public AtaPioController Ata { get; }
    public DevelopmentBoardPorts Board { get; }
    public Pic8259 Pic { get; } = new();
    public Pit8254 Pit { get; }
    public PcSpeaker Speaker { get; }
    public PcAudioMixer Audio { get; }
    public SoundBlasterDsp? SoundBlaster { get; }
    public Opl2? Opl { get; }
    public Isa8BitIoTiming? FmIoTiming { get; }
    public Keyboard8042 Keyboard { get; }
    public Ps2Mouse? Mouse { get; }
    public RtcCmos Rtc { get; }
    public Dma8237 Dma { get; }
    public Fdc82077 Floppy { get; }
    public string? Fault { get; private set; }
    public bool Running { get; set; }
    private bool _keyboardResetWasAsserted;
    private ulong _resetPulseUntil;
    public long HardwareResetCount { get; private set; }
    public string? LastResetSource { get; private set; }
    public ulong LastResetTime { get; private set; }
    public ushort LastWarmBootFlag { get; private set; }
    public bool CpuResetHeld => Keyboard.ResetLineAsserted || Scheduler.Now < _resetPulseUntil;
    public event Action? HardwareResetOccurred;

    public PcMachine(MachineProfile profile, IReadOnlyBlockDevice? disk, IReadOnlyBlockDevice? floppyA = null, IReadOnlyBlockDevice? floppyB = null)
    {
        profile.Validate(); Profile = profile; _disk = disk;
        Memory = new(profile.RamMiB * 1024 * 1024);
        Video = new(Scheduler);
        var geometryA = FloppyGeometry.FromId(profile.FloppyDriveA); var geometryB = FloppyGeometry.FromId(profile.FloppyDriveB);
        byte[] bios = ReferenceFirmware.CreateBios(profile.RamMiB, disk?.SectorCount ?? 0, disk is IWritableBlockDevice,
            geometryA, geometryB, profile.BootDevice == "floppy-a", profile.Mouse == "ps2-two-button");
        var rom = new RomDevice(bios);
        Memory.Map(0x000f0000, 65536, rom, "System BIOS");
        Memory.Map(0xffff0000, 65536, rom, "486 reset ROM alias");
        // This reference board also decodes its reset ROM when address line 20 is masked.
        Memory.Map(0xffef0000, 65536, rom, "A20-masked reset ROM alias");
        Memory.Map(0x000a0000, 131072, Video, "VGA memory aperture");
        // Installed DRAM is not decoded in the ISA option-memory window.
        // This board has no option ROMs or shadow RAM here. EMM386 supplies
        // UMB/EMS backing by paging real extended RAM into these addresses.
        Memory.Map(0x000c0000, 196608, new OpenBusMemory(), "Unclaimed ISA option-memory window");
        Ata = new(Scheduler, disk); Board = new(Memory);
        Pit = new(Scheduler, () => { Pic.SetLine(0, true); Pic.SetLine(0, false); });
        Audio = new(Scheduler); Speaker = new(Scheduler, Pit.SpeakerCounter, Audio);
        if (profile.Mouse == "ps2-two-button") Mouse = new(Scheduler);
        Keyboard = new(Memory, high => Pic.SetLine(1, high), Mouse, high => Pic.SetLine(12, high));
        Rtc = new(Scheduler, profile.RamMiB, (byte)((geometryA?.CmosType ?? 0) << 4 | (geometryB?.CmosType ?? 0)));
        Dma = new(Memory);
        if (profile.SoundCard is "sb2-dsp-development" or "sb2-opl2-development")
        {
            SoundBlaster = new(Scheduler, Dma, Audio, high => Pic.SetLine(profile.SoundBlasterIrq, high), (ushort)profile.SoundBlasterBasePort);
            foreach (ushort offset in new ushort[] { 6, 0x0a, 0x0c, 0x0e }) Ports.Map((ushort)(profile.SoundBlasterBasePort + offset), 1, SoundBlaster);
        }
        if (profile.SoundCard is "adlib-opl2-development" or "sb2-opl2-development")
        {
            FmIoTiming = new(Scheduler);
            ushort? alias = profile.SoundCard == "sb2-opl2-development" ? (ushort)(profile.SoundBlasterBasePort + 8) : null;
            Opl = new(Scheduler, Audio, alias); Ports.Map(0x388, 2, Opl, FmIoTiming);
            if (alias is { } fmBase) Ports.Map(fmBase, 2, Opl, FmIoTiming);
        }
        Floppy = new(Scheduler, Dma, high => Pic.SetLine(6, high), geometryA, geometryB, floppyA, floppyB);
        Ports.Map(0x1f0, 8, Ata); Ports.Map(0x3f6, 1, Ata);
        Ports.Map(0x3f0, 6, Floppy); Ports.Map(0x3f7, 1, Floppy);
        Ports.Map(0x3b4, 2, Video); Ports.Map(0x3ba, 1, Video); Ports.Map(0x3c0, 16, Video);
        Ports.Map(0x3d4, 2, Video); Ports.Map(0x3da, 1, Video);
        Ports.Map(0x80, 1, Board); Ports.Map(0x92, 1, Board); Ports.Map(0xe9, 1, Board);
        Ports.Map(0x20, 2, Pic); Ports.Map(0xa0, 2, Pic); Ports.Map(0x40, 4, Pit);
        Ports.Map(0x60, 1, Keyboard); Ports.Map(0x64, 1, Keyboard);
        Ports.Map(0x61, 1, Speaker);
        Ports.Map(0x70, 2, Rtc);
        Ports.Map(0, 16, Dma); Ports.Map(0xc0, 32, Dma);
        foreach (ushort port in new ushort[] { 0x81, 0x82, 0x83, 0x87, 0x89, 0x8a, 0x8b, 0x8f }) Ports.Map(port, 1, Dma);
        Cpu = new(Memory, Ports);
        FpuError = new(high => Pic.SetLine(13, high), high => Cpu.IgnoreNumericError = high);
        Ports.Map(0xf0, 1, FpuError);
        Cpu.FpuErrorChanged += FpuError.SetFerr;
    }

    public static PcMachine CreateDemo(MachineProfile? profile = null) => new(profile ?? new(), new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Built-in test sector (not DOS)"));

    public int RunSteps(int maximum, ulong? untilVirtualNanoseconds = null)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        int executed = 0;
        while (executed < maximum && Fault is null)
        {
            if (untilVirtualNanoseconds is { } deadline && Scheduler.Now >= deadline) break;
            bool fastReset = Board.ConsumeResetRequest(), keyboardPulse = Keyboard.ConsumeResetPulse();
            bool keyboardLine = Keyboard.ResetLineAsserted;
            if (fastReset || keyboardPulse || (keyboardLine && !_keyboardResetWasAsserted))
            {
                var sources = new List<string>();
                if (fastReset) sources.Add("port92");
                if (keyboardPulse) sources.Add("kbc-pulse");
                if (keyboardLine && !_keyboardResetWasAsserted) sources.Add("kbc-output-port");
                if (keyboardPulse) _resetPulseUntil = checked(Scheduler.Now + 6_000);
                ResetCpu(string.Join("+", sources));
            }
            _keyboardResetWasAsserted = keyboardLine;
            if (CpuResetHeld)
            {
                if (Scheduler.Now < _resetPulseUntil)
                {
                    ulong release = untilVirtualNanoseconds is { } slice ? Math.Min(_resetPulseUntil, slice) : _resetPulseUntil;
                    Scheduler.AdvanceTo(release); executed++; continue;
                }
                if (untilVirtualNanoseconds is { } heldDeadline) Scheduler.AdvanceTo(heldDeadline);
                break;
            }
            Pic.SetLine(14, Ata.InterruptAsserted);
            if (Cpu.TripleFaulted) { ResetCpu("triple-fault"); }
            try
            {
                if (Cpu.CanAcceptMaskableInterrupt && Pic.HasPending && Pic.Acknowledge() is { } vector)
                    Cpu.EnterHardwareInterrupt(vector);
            }
            catch (Exception ex) when (ex is UnsupportedInstructionException or InvalidOperationException or IOException)
            {
                Fault = ex.Message; Running = false; break;
            }
            if (Cpu.Halted || Cpu.WaitingForNumericError)
            {
                // HLT and external numeric-error waits still clock devices, even under CLI.
                // Without a time target, CLI/HLT remains a useful headless test-program stop.
                if (!Cpu.CanAcceptMaskableInterrupt || Scheduler.NextEventTime is not { } next)
                {
                    if (untilVirtualNanoseconds is { } haltedDeadline) Scheduler.AdvanceTo(haltedDeadline);
                    break;
                }
                if (untilVirtualNanoseconds is { } wakeDeadline && next >= wakeDeadline) { Scheduler.AdvanceTo(wakeDeadline); break; }
                Scheduler.AdvanceTo(next); executed++; continue;
            }
            try
            {
                uint cycles = Cpu.Step();
                Scheduler.AdvanceCycles(cycles, (uint)Profile.CpuHz);
                executed++;
            }
            catch (Exception ex) when (ex is UnsupportedInstructionException or InvalidOperationException or IOException)
            {
                Fault = ex.Message; Running = false;
            }
        }
        Audio.AdvanceTo(Scheduler.Now);
        return executed;
    }

    public MachineStatus GetStatus() => new(Profile.Id, Profile.Name,
        Fault is not null ? MachineRunState.Faulted : CpuResetHeld ? MachineRunState.ResetHeld : Cpu.Halted ? MachineRunState.Halted : Running ? MachineRunState.Running : MachineRunState.Paused,
        Cpu.Instructions, Scheduler.Now, Cpu.Position, Fault, _disk?.Name ?? "No disk", Board.DebugText, _disk is IWritableBlockDevice,
        Profile, Floppy.MediaName(0), Floppy.MediaName(1), new(HardwareResetCount, LastResetSource, LastResetTime, LastWarmBootFlag, CpuResetHeld, Board.Post10Count));

    /// <summary>Emulation owner only, between instruction batches. CPU hot reset; powered devices and media keep running.</summary>
    public void HardwareReset() => ResetCpu("host-hardware");

    private void ResetCpu(string source)
    {
        LastWarmBootFlag = Memory.ReadWord(0x472);
        Cpu.Reset(); Fault = null;
        HardwareResetCount++; LastResetSource = source; LastResetTime = Scheduler.Now;
        HardwareResetOccurred?.Invoke();
    }
    public VideoFrame CaptureFrame() => Video.Capture(Scheduler.Now);
    public PcMachine CreatePowerCycle(MachineProfile profile)
    {
        profile.Validate();
        IReadOnlyBlockDevice? copy = null, a = null, b = null;
        try
        {
            copy = _disk?.Reopen(); a = Floppy.ReopenMedia(0); b = Floppy.ReopenMedia(1);
            var poweredOn = new PcMachine(profile, copy, a, b);
            Rtc.CarryBatteryStateTo(poweredOn.Rtc);
            return poweredOn;
        }
        catch { copy?.Dispose(); a?.Dispose(); b?.Dispose(); throw; }
    }
    public void Dispose() { Floppy.Dispose(); _disk?.Dispose(); }
}
