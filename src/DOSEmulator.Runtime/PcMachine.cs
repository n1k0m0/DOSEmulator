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
        Memory.Map(0x000a0000, 131072, Video, "VGA memory aperture");
        Ata = new(Scheduler, disk); Board = new(Memory);
        Pit = new(Scheduler, () => { Pic.SetLine(0, true); Pic.SetLine(0, false); });
        Audio = new(Scheduler); Speaker = new(Scheduler, Pit.SpeakerCounter, Audio);
        if (profile.Mouse == "ps2-two-button") Mouse = new(Scheduler);
        Keyboard = new(Memory, high => Pic.SetLine(1, high), Mouse, high => Pic.SetLine(12, high));
        Rtc = new(Scheduler, profile.RamMiB, (byte)((geometryA?.CmosType ?? 0) << 4 | (geometryB?.CmosType ?? 0)));
        Dma = new(Memory);
        if (profile.SoundCard is "sb2-dsp-development" or "sb2-opl2-development")
        {
            SoundBlaster = new(Scheduler, Dma, Audio, high => Pic.SetLine(SoundBlasterDsp.Irq, high));
            foreach (ushort offset in new ushort[] { 6, 0x0a, 0x0c, 0x0e }) Ports.Map((ushort)(SoundBlasterDsp.BasePort + offset), 1, SoundBlaster);
        }
        if (profile.SoundCard is "adlib-opl2-development" or "sb2-opl2-development")
        {
            FmIoTiming = new(Scheduler);
            Opl = new(Scheduler, Audio); Ports.Map(0x388, 2, Opl, FmIoTiming);
            if (profile.SoundCard == "sb2-opl2-development") Ports.Map(0x228, 2, Opl, FmIoTiming);
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
    }

    public static PcMachine CreateDemo(MachineProfile? profile = null) => new(profile ?? new(), new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Built-in test sector (not DOS)"));

    public int RunSteps(int maximum, ulong? untilVirtualNanoseconds = null)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        int executed = 0;
        while (executed < maximum && Fault is null)
        {
            if (untilVirtualNanoseconds is { } deadline && Scheduler.Now >= deadline) break;
            if (Board.ResetRequested || Keyboard.ResetRequested) { Fault = "Guest requested a hardware reset; coordinated reset is not implemented yet."; break; }
            Pic.SetLine(14, Ata.InterruptAsserted);
            if (Cpu.CanAcceptMaskableInterrupt && Pic.HasPending && Pic.Acknowledge() is { } vector)
                Cpu.EnterRealModeInterrupt(vector);
            if (Cpu.Halted)
            {
                // A host time slice still clocks devices while the CPU is halted, even under CLI.
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
        Fault is not null ? MachineRunState.Faulted : Cpu.Halted ? MachineRunState.Halted : Running ? MachineRunState.Running : MachineRunState.Paused,
        Cpu.Instructions, Scheduler.Now, Cpu.Position, Fault, _disk?.Name ?? "No disk", Board.DebugText, _disk is IWritableBlockDevice,
        Profile, Floppy.MediaName(0), Floppy.MediaName(1));
    public VideoFrame CaptureFrame() => Video.Capture(Scheduler.Now);
    public PcMachine CreatePowerCycle(MachineProfile profile)
    {
        profile.Validate();
        IReadOnlyBlockDevice? copy = null, a = null, b = null;
        try { copy = _disk?.Reopen(); a = Floppy.ReopenMedia(0); b = Floppy.ReopenMedia(1); return new PcMachine(profile, copy, a, b); }
        catch { copy?.Dispose(); a?.Dispose(); b?.Dispose(); throw; }
    }
    public void Dispose() { Floppy.Dispose(); _disk?.Dispose(); }
}
