// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public sealed partial class PcMachine : IDisposable, IPiixResetOwner
{
    private string _definitionId = Guid.NewGuid().ToString("N");
    private string? _definitionName;
    private readonly IReadOnlyBlockDevice? _disk;
    private readonly IReadOnlyBlockDevice?[] _disks;
    private readonly IReadOnlyList<HardDiskStatus> _hardDiskStatus;
    private readonly string _mediaName;
    private readonly bool _diskWritable;
    private bool _disposed;
    /// <summary>Validated hardware profile currently instantiated by this PC.</summary>
    public MachineProfile Profile { get; }
    /// <summary>Physical memory bus owned by this PC, including RAM, ROM and mapped devices.</summary>
    public MemoryBus Memory { get; }
    /// <summary>I/O bus owned by this PC and used by actual guest IN/OUT instructions.</summary>
    public IoPortBus Ports { get; } = new();
    /// <summary>Optional fixed 00:00.0 host-controller subset, with no implied PIIX function.</summary>
    public Intel82437Fx? HostBridge { get; }
    /// <summary>Optional owned configuration mechanism 1 and its fixed local function registry.</summary>
    public PciConfigurationBus? Pci { get; }
    /// <summary>Optional original-PIIX function-zero IRQ subset; no IDE or CF9 reset function is implied.</summary>
    public Intel82371FbIrq? PiixIrq { get; }
    /// <summary>Optional original-PIIX function-one IDE/PCI-busmaster subset, with no implied CF9 reset controller.</summary>
    public Intel82371FbIde? PiixIde { get; }
    /// <summary>Optional CF9 edge latch and explicit reset-domain sequencer, present only on the own reset validation board.</summary>
    public PiixResetController? PiixReset { get; }
    /// <summary>Virtual-time event scheduler owned by this PC.</summary>
    public DeviceScheduler Scheduler { get; } = new();
    /// <summary>CPU instance owned and stepped only by this PC's emulation owner.</summary>
    public X86Cpu Cpu { get; }
    /// <summary>AT numeric-error/IGNNE/IRQ13 platform wiring associated with this CPU.</summary>
    public FpuErrorLatch FpuError { get; }
    /// <summary>Active VGA/SVGA adapter selected by the validated profile.</summary>
    public VgaAdapter Video { get; }
    /// <summary>Fixed original-Trio64 PCI target and VGA/LFB gates of the separate S3 board.</summary>
    public S3Trio64? S3 { get; }
    /// <summary>Primary IDE channel with independent master/slave task engines.</summary>
    public AtaPioController Ata { get; }
    /// <summary>Installed SFF-8020i packet target, independent from whether its tray contains media.</summary>
    public AtapiCdRom? Atapi { get; }
    /// <summary>Owned protected cooked ISO lease; an ejected tray may retain its immutable mounted basis.</summary>
    public IReadOnlyOpticalDisc? OpticalDisc { get; }
    /// <summary>Secondary IDE channel with independent master/slave task engines.</summary>
    public AtaPioController SecondaryAta { get; }
    /// <summary>Reference-board POST, E9, A20 and reset diagnostic/control ports.</summary>
    public DevelopmentBoardPorts Board { get; }
    /// <summary>Cascaded 8259 interrupt controllers for this PC.</summary>
    public Pic8259 Pic { get; }
    /// <summary>Reference 8254 timer/counter device for this PC.</summary>
    public Pit8254 Pit { get; }
    /// <summary>Guest PIT/speaker signal source attached to the shared mixer.</summary>
    public PcSpeaker Speaker { get; }
    /// <summary>Guest-time mixer, independent from the host output device and its callbacks.</summary>
    public PcAudioMixer Audio { get; }
    /// <summary>Optional concrete SB2 DSP device selected by the profile.</summary>
    public SoundBlasterDsp? SoundBlaster { get; }
    /// <summary>Optional concrete CT1740/DSP4.05 PCM/mixer device selected by the profile.</summary>
    public SoundBlaster16? SoundBlaster16 { get; }
    /// <summary>Optional reference OPL2 device selected by the profile.</summary>
    public Opl2? Opl { get; }
    /// <summary>Optional reference YMF262/OPL3 device selected by the profile.</summary>
    public Opl3? Opl3 { get; }
    /// <summary>Shared ISA byte-cycle timing policy attached to the selected FM port aliases.</summary>
    public Isa8BitIoTiming? FmIoTiming { get; }
    /// <summary>Guest 8042 controller and bounded translated keyboard output path.</summary>
    public Keyboard8042 Keyboard { get; }
    /// <summary>Optional guest PS/2 auxiliary mouse device; host capture remains a separate policy.</summary>
    public Ps2Mouse? Mouse { get; }
    /// <summary>Optional standalone IBM analog adapter, separate from PS/2 and sound-card MIDI.</summary>
    public AnalogGameport? Gameport { get; }
    /// <summary>Optional fixed COM1 PC16550D-compatible functional adapter, with no implicit host endpoint.</summary>
    public Uart16550? Serial { get; }
    /// <summary>Optional standalone output-only IBM LPT1 card, with an explicitly connected virtual peer at most.</summary>
    public ParallelPrinterPort? Parallel { get; }
    /// <summary>Optional CT1740 MPU-UART transport, separate from FM, host MIDI and synthesis.</summary>
    public Mpu401Uart? Midi { get; }
    /// <summary>Reference CMOS/RTC device with carried battery state across cold hardware replacement.</summary>
    public RtcCmos Rtc { get; }
    /// <summary>AT byte/word DMA controller used by device-paced transfer requests.</summary>
    public Dma8237 Dma { get; }
    /// <summary>Two-drive FDC and its currently attached protected media leases.</summary>
    public Fdc82077 Floppy { get; }
    /// <summary>Most recent component fault description, or null while no fault is recorded.</summary>
    public string? Fault { get; private set; }
    /// <summary>Whether the emulation owner should execute guest work rather than remain paused.</summary>
    public bool Running { get; set; }
    private bool _keyboardResetWasAsserted;
    private ulong _resetPulseUntil;
    /// <summary>Diagnostic count of hardware reset count.</summary>
    public long HardwareResetCount { get; private set; }
    /// <summary>Diagnostic source of the most recent actual CPU reset.</summary>
    public string? LastResetSource { get; private set; }
    /// <summary>Virtual nanoseconds at which the most recent CPU reset occurred.</summary>
    public ulong LastResetTime { get; private set; }
    /// <summary>BDA warm-boot flag observed immediately before the most recent CPU reset.</summary>
    public ushort LastWarmBootFlag { get; private set; }
    /// <summary>Whether sustained controller reset or the timed reset pulse still holds the CPU.</summary>
    public bool CpuResetHeld => PiixReset?.CpuResetHeld == true || Keyboard.ResetLineAsserted || Scheduler.Now < _resetPulseUntil;
    public event Action? HardwareResetOccurred;

    /// <summary>Initializes PcMachine and connects the supplied dependencies before the component is used.</summary>
    public PcMachine(MachineProfile profile, IReadOnlyBlockDevice? disk, IReadOnlyBlockDevice? floppyA = null, IReadOnlyBlockDevice? floppyB = null)
        : this(profile, disk, floppyA, floppyB, null, null, null, null) { }

    /// <summary>Builds all four IDE slots together; ownership transfers only when construction succeeds.</summary>
    private PcMachine(MachineProfile profile, IReadOnlyBlockDevice? disk, IReadOnlyBlockDevice? floppyA, IReadOnlyBlockDevice? floppyB,
        IReadOnlyBlockDevice? disk1, IReadOnlyBlockDevice? disk2, IReadOnlyBlockDevice? disk3, IReadOnlyOpticalDisc? optical)
    {
        profile.Validate(); Profile = profile; _disk = disk;
        Pic = profile.IsPiixIrq ? new Pic8259(true) : new Pic8259();
        _disks = [disk, disk1, disk2, disk3];
        _hardDiskStatus = Array.AsReadOnly(_disks.Select((d, slot) => d is null ? null : new HardDiskStatus(slot, d.Name, d.SectorCount * 512, d is IWritableBlockDevice))
            .OfType<HardDiskStatus>().ToArray());
        _mediaName = _hardDiskStatus.FirstOrDefault()?.Name ?? "No disk";
        _diskWritable = _hardDiskStatus.Any(d => d.Writable);
        if (_disks.Any(d => d?.SectorCount > RawDiskImage.MaximumBytes / 512)) throw new ArgumentException("IDE images must fit LBA28 (128 GiB).");
        if (optical is not null && !profile.IsAtapi || profile.IsAtapi && _disks[profile.AtapiSlot] is not null)
            throw new ArgumentException("The installed optical target reserves its physical IDE position.");
        OpticalDisc = optical;
        if (profile.IsAtapi) Atapi = new(Scheduler, optical, profile.AtapiSlot & 1);
        Memory = new(profile.RamMiB * 1024 * 1024);
        Video = profile.IsTrio64 ? new(Scheduler, false, true) : new(Scheduler, profile.IsEt4000);
        if (profile.IsTrio64) S3 = new(Memory, Video);
        var geometryA = FloppyGeometry.FromId(profile.FloppyDriveA); var geometryB = FloppyGeometry.FromId(profile.FloppyDriveB);
        byte[] bios = DOSEBios.CreateBiosForBoard(profile.IsFxHost, profile.RamMiB, _disks,
            geometryA, geometryB, profile.BootDevice == "floppy-a", profile.Mouse == "ps2-two-button", profile.IsEt4000,
            profile.Gameport == AnalogGameport.ProfileId, profile.SerialPort == Uart16550.ProfileId,
            profile.ParallelPort == ParallelPrinterPort.ProfileId,
            profile.MidiInterface == Mpu401Uart.ProfileId ? (ushort)profile.MidiBasePort : (ushort)0,
            profile.MidiInterface == Mpu401Uart.ProfileId ? (byte)profile.SoundBlasterIrq : (byte)0, profile.IsPiixIde, profile.IsPiixReset, profile.IsTrio64, profile.IsPciBiosBoard, profile.IsWindows95Board);
        if (profile.IsPciBiosBoard) DOSEBios.ConfigurePciBiosInventory(bios);
        else if (profile.IsAtapi) DOSEBios.ConfigureAtapiInventory(bios);
        else if (profile.IsTrio64) DOSEBios.ConfigureTrio64Inventory(bios);
        else if (profile.IsPiixReset) DOSEBios.ConfigurePiixResetInventory(bios);
        else if (profile.IsPiixIde) DOSEBios.ConfigurePiixIdeInventory(bios);
        else if (profile.IsPiixIrq) DOSEBios.ConfigurePiixIrqInventory(bios);
        else if (profile.IsFxHost) DOSEBios.ConfigureFxHostInventory(bios);
        else if (profile.IsP54c) DOSEBios.ConfigureP54cInventory(bios);
        var rom = new RomDevice(bios);
        if (profile.IsFxHost)
        {
            HostBridge = new(Memory, bios);
            Pci = new(); Pci.RegisterFunction(0, 0, 0, HostBridge);
            if (S3 is not null) Pci.RegisterFunction(0, 3, 0, S3);
            if (profile.IsPiixIrq)
            {
                PiixIrq = new(Pic, profile.IsPiixIde); Pci.RegisterFunction(0, 7, 0, PiixIrq);
                Ports.Map(0x4d0, 2, PiixIrq);
            }
            if (profile.IsPiixReset) PiixReset = new(Scheduler, Pci, this);
            Ports.Map(0xcf8, 8, (IIoPortDevice?)PiixReset ?? Pci);
            Memory.Map(0x000c0000, 262144, HostBridge, "82437FX PAM and system ROM");
        }
        else Memory.Map(0x000f0000, 65536, rom, "System BIOS");
        Memory.Map(0xffff0000, 65536, rom, profile.IsP54c ? "P54C reset ROM alias" : "486 reset ROM alias");
        // This reference board also decodes its reset ROM when address line 20 is masked.
        Memory.Map(0xffef0000, 65536, rom, "A20-masked reset ROM alias");
        Memory.Map(0x000a0000, 131072, (IMemoryDevice?)S3 ?? Video, "VGA memory aperture");
        if (S3 is not null) Memory.MapDynamic(S3);
        // Installed DRAM is not decoded in the ISA option-memory window.
        // This board has no option ROMs or shadow RAM here. EMM386 supplies
        // UMB/EMS backing by paging real extended RAM into these addresses.
        if (!profile.IsFxHost) Memory.Map(0x000c0000, 196608, new OpenBusMemory(), "Unclaimed ISA option-memory window");
        Ata = profile.IsAtapi && profile.AtapiSlot < 2 ? new(Scheduler, disk, disk1, 0x1f0, 0x3f6, true, Atapi, profile.AtapiSlot & 1)
            : profile.IsPiixIde ? new(Scheduler, disk, disk1, 0x1f0, 0x3f6, true) : new(Scheduler, disk, disk1, 0x1f0, 0x3f6);
        SecondaryAta = profile.IsAtapi && profile.AtapiSlot >= 2 ? new(Scheduler, disk2, disk3, 0x170, 0x376, true, Atapi, profile.AtapiSlot & 1)
            : profile.IsPiixIde ? new(Scheduler, disk2, disk3, 0x170, 0x376, true) : new(Scheduler, disk2, disk3, 0x170, 0x376);
        Ata.InterruptChanged += high => SetIsaInterrupt(14, high);
        if (profile.IsPiixIde)
        {
            SecondaryAta.InterruptChanged += high => PiixIrq!.SetMirq(0, high);
            PiixIde = new(Scheduler, HostBridge!, Ata, SecondaryAta, Ports);
            Pci!.RegisterFunction(0, 7, 1, PiixIde); Ports.MapDynamic(PiixIde);
        }
        else SecondaryAta.InterruptChanged += high => SetIsaInterrupt(15, high);
        Board = new(Memory);
        Pit = new(Scheduler, () => { SetIsaInterrupt(0, true); SetIsaInterrupt(0, false); });
        Audio = new(Scheduler); Speaker = new(Scheduler, Pit.SpeakerCounter, Audio);
        if (profile.Mouse == "ps2-two-button") Mouse = new(Scheduler);
        if (profile.SerialPort == Uart16550.ProfileId)
        { Serial = new(Scheduler, high => SetIsaInterrupt(4, high)); Ports.Map(Uart16550.BasePort, 8, Serial); }
        if (profile.ParallelPort == ParallelPrinterPort.ProfileId)
        {
            Parallel = new(Scheduler, high => SetIsaInterrupt(7, high));
            for (int port = 0x378; port < 65536; port += 0x400) Ports.Map((ushort)port, 8, Parallel);
        }
        if (profile.Gameport == AnalogGameport.ProfileId)
        {
            Gameport = new(Scheduler);
            for (int port = 0x201; port < 65536; port += 0x400) Ports.Map((ushort)port, 1, Gameport);
        }
        Keyboard = new(Memory, high => SetIsaInterrupt(1, high), Mouse, high => SetIsaInterrupt(12, high));
        Rtc = new(Scheduler, profile.RamMiB, (byte)((geometryA?.CmosType ?? 0) << 4 | (geometryB?.CmosType ?? 0)));
        Dma = new(Memory);
        if (profile.IsSb16)
        {
            SoundBlaster16 = new(Scheduler, Dma, Audio, (irq, high) => SetIsaInterrupt(irq, high), (ushort)profile.SoundBlasterBasePort,
                profile.SoundBlasterIrq, profile.SoundBlasterDmaChannel, profile.SoundBlasterDma16Channel);
            Ports.Map((ushort)(profile.SoundBlasterBasePort + 4), 2, SoundBlaster16);
            foreach (ushort offset in new ushort[] { 6, 0x0a, 0x0c, 0x0e, 0x0f }) Ports.Map((ushort)(profile.SoundBlasterBasePort + offset), 1, SoundBlaster16);
            FmIoTiming = new(Scheduler); Opl3 = new(Scheduler, Audio, (ushort)profile.SoundBlasterBasePort);
            SoundBlaster16.AttachFm(Opl3);
            if (profile.MidiInterface == Mpu401Uart.ProfileId)
            {
                Midi = new(Scheduler, (ushort)profile.MidiBasePort); SoundBlaster16.AttachMidi(Midi);
                Ports.Map((ushort)profile.MidiBasePort, 2, Midi);
            }
            Ports.Map(0x388, 4, Opl3, FmIoTiming);
            Ports.Map((ushort)profile.SoundBlasterBasePort, 4, Opl3, FmIoTiming);
            Ports.Map((ushort)(profile.SoundBlasterBasePort + 8), 2, Opl3, FmIoTiming);
        }
        if (profile.SoundCard is "sb2-dsp-development" or "sb2-opl2-development")
        {
            SoundBlaster = new(Scheduler, Dma, Audio, high => SetIsaInterrupt(profile.SoundBlasterIrq, high), (ushort)profile.SoundBlasterBasePort);
            foreach (ushort offset in new ushort[] { 6, 0x0a, 0x0c, 0x0e }) Ports.Map((ushort)(profile.SoundBlasterBasePort + offset), 1, SoundBlaster);
        }
        if (profile.SoundCard is "adlib-opl2-development" or "sb2-opl2-development")
        {
            FmIoTiming = new(Scheduler);
            ushort? alias = profile.SoundCard == "sb2-opl2-development" ? (ushort)(profile.SoundBlasterBasePort + 8) : null;
            Opl = new(Scheduler, Audio, alias); Ports.Map(0x388, 2, Opl, FmIoTiming);
            if (alias is { } fmBase) Ports.Map(fmBase, 2, Opl, FmIoTiming);
        }
        Floppy = new(Scheduler, Dma, high => SetIsaInterrupt(6, high), geometryA, geometryB, floppyA, floppyB);
        if (PiixIde is { } ide)
        { Ports.Map(0x1f0, 8, ide); Ports.Map(0x3f6, 1, ide); Ports.Map(0x170, 8, ide); Ports.Map(0x376, 1, ide); }
        else
        { Ports.Map(0x1f0, 8, Ata); Ports.Map(0x3f6, 1, Ata); Ports.Map(0x170, 8, SecondaryAta); Ports.Map(0x376, 1, SecondaryAta); }
        Ports.Map(0x3f0, 6, Floppy); Ports.Map(0x3f7, 1, Floppy);
        IIoPortDevice videoPorts = (IIoPortDevice?)S3 ?? Video;
        Ports.Map(0x3b4, 2, videoPorts); Ports.Map(0x3ba, 1, videoPorts); Ports.Map(0x3c0, 16, videoPorts);
        Ports.Map(0x3d4, 2, videoPorts); Ports.Map(0x3da, 1, videoPorts);
        if (profile.IsEt4000) { Ports.Map(0x3bf, 1, Video); Ports.Map(0x3b8, 1, Video); Ports.Map(0x3d8, 1, Video); }
        if (S3 is not null)
        {
            Ports.Map(0x46e8, 1, S3); Ports.Map(0x0102, 1, S3); Ports.Map(0x4ae8, 2, S3);
            // These constructor-fixed original engine windows share the same PCI/POS-gated target.
            foreach (var (port, length) in S3Trio64.EnginePortWindows) Ports.Map(port, length, S3);
        }
        Ports.Map(0x80, 1, Board); Ports.Map(0x92, 1, Board); Ports.Map(0xe9, 1, Board);
        Ports.Map(0x20, 2, Pic); Ports.Map(0xa0, 2, Pic); Ports.Map(0x40, 4, Pit);
        Ports.Map(0x60, 1, Keyboard); Ports.Map(0x64, 1, Keyboard);
        Ports.Map(0x61, 1, Speaker);
        Ports.Map(0x70, 2, Rtc);
        Ports.Map(0, 16, Dma); Ports.Map(0xc0, 32, Dma);
        foreach (ushort port in new ushort[] { 0x81, 0x82, 0x83, 0x87, 0x89, 0x8a, 0x8b, 0x8f }) Ports.Map(port, 1, Dma);
        Cpu = profile.IsP54c
            ? new(Memory, Ports, X86CpuModel.IntelP54cB5Subset, Scheduler, (uint)profile.CpuHz)
            : new(Memory, Ports);
        if (profile.IsPiixReset) Cpu.ResetSignal();
        FpuError = new(high => SetIsaInterrupt(13, high), high => Cpu.IgnoreNumericError = high);
        Ports.Map(0xf0, 1, FpuError);
        Cpu.FpuErrorChanged += FpuError.SetFerr;
        _factoryPortCount = Ports.StatePortCount; _factoryMemoryCount = Memory.StateMappingCount;
    }

    /// <summary>Publishes an ordinary ISA source through the selected board's routing owner.</summary>
    private void SetIsaInterrupt(int irq, bool high)
    {
        if (PiixIrq is { } bridge) bridge.SetIsaLine(irq, high);
        else Pic.SetLine(irq, high);
    }

    /// <summary>Builds the explicitly identified development test-sector PC, not a DOS installation.</summary>
    public static PcMachine CreateDemo(MachineProfile? profile = null) => new(profile ?? new(), new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Built-in test sector (not DOS)"));

    /// <summary>Executes bounded guest instruction elements up to the next requested virtual deadline, servicing actual device events and resets.</summary>
    public int RunSteps(int maximum, ulong? untilVirtualNanoseconds = null)
    {
        if (_executing) throw new InvalidOperationException("The PC owner cannot reenter execution.");
        _executing = true;
        try { return RunStepsCore(maximum, untilVirtualNanoseconds); }
        finally { _executing = false; }
    }

    /// <summary>Runs instruction elements under the owner boundary guard.</summary>
    private int RunStepsCore(int maximum, ulong? untilVirtualNanoseconds)
    {
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        int executed = 0;
        while (executed < maximum && Fault is null)
        {
            ServicePiixResetBoundary();
            if (untilVirtualNanoseconds is { } deadline && Scheduler.Now >= deadline) break;
            if (PiixReset?.CpuResetHeld == true)
            {
                ulong target = PiixReset.CpuReleaseAt;
                if (untilVirtualNanoseconds is { } resetSlice) target = Math.Min(target, resetSlice);
                Scheduler.AdvanceTo(target); executed++; continue;
            }
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
            if (Cpu.TripleFaulted)
            {
                if (Profile.IsPiixReset)
                {
                    if ((PiixIrq!.ReadConfiguration(4, 1) & 8) != 0) ApplyPiixRequest(PiixResetRequest.Init, "pci-shutdown-init");
                    else { if (untilVirtualNanoseconds is { } shutdownDeadline) Scheduler.AdvanceTo(shutdownDeadline); break; }
                }
                else ResetCpu("triple-fault");
            }
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
                int budget = Cpu.HasRepContinuation ? Scheduler.StepBudget(4, (uint)Profile.CpuHz, Math.Min(maximum - executed, 4096), untilVirtualNanoseconds) : 0;
                if (budget >= 2 && Cpu.TryRunRepBatch(budget) is > 0 and var elements)
                {
                    Scheduler.AdvanceCycles((uint)elements * 4, (uint)Profile.CpuHz); executed += elements; continue;
                }
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

    /// <summary>Captures a status description without exposing mutable CPU, RAM or device objects.</summary>
    public MachineStatus GetStatus() => new(Profile.Id, Profile.Name,
        Fault is not null ? MachineRunState.Faulted : CpuResetHeld ? MachineRunState.ResetHeld : (Cpu.Halted || Profile.IsPiixReset && Cpu.TripleFaulted) ? MachineRunState.Halted : Running ? MachineRunState.Running : MachineRunState.Paused,
        Cpu.Instructions, Scheduler.Now, Cpu.Position, Fault, _mediaName, Board.DebugText, _diskWritable,
        Profile, Floppy.MediaName(0), Floppy.MediaName(1), new(HardwareResetCount, LastResetSource, LastResetTime, LastWarmBootFlag, CpuResetHeld, Board.Post10Count),
        _hardDiskStatus)
        { OpticalDisc = Profile.IsAtapi ? new(Profile.AtapiSlot, OpticalDisc?.Name, (OpticalDisc?.BlockCount ?? 0) * IReadOnlyOpticalDisc.BlockSize) : null };

    /// <summary>Emulation owner only, between instruction batches. CPU hot reset; powered devices and media keep running.</summary>
    public void HardwareReset() => ResetCpu("host-hardware");
    /// <summary>Apply explicit virtual characters and modem pins atomically on the emulation owner, without accessing host devices.</summary>
    public void SendSerialInput(SerialInput input)
    {
        input = input.ValidatedCopy();
        if (Serial is null) throw new InvalidOperationException("No COM1 adapter configured.");
        // Validate the pending capacity before changing any modem pins.
        Serial.QueueReceive(input.Data?.Select(x => (byte)x).ToArray() ?? [], (byte)input.Errors);
        byte lines = Serial.PhysicalModem;
        Serial.SetModemInputs(input.Cts ?? (lines & 16) != 0, input.Dsr ?? (lines & 32) != 0,
            input.Ri ?? (lines & 64) != 0, input.Dcd ?? (lines & 128) != 0);
    }

    /// <summary>Resets only the CPU and reset diagnostics while retaining powered devices, RAM and virtual time.</summary>
    private void ResetCpu(string source)
    {
        LastWarmBootFlag = Memory.ReadWord(0x472);
        Cpu.Reset(); Fault = null;
        HardwareResetCount++; LastResetSource = source; LastResetTime = Scheduler.Now;
        HardwareResetOccurred?.Invoke();
    }
    /// <summary>Captures the current guest scanout at the machine's actual virtual time.</summary>
    public VideoFrame CaptureFrame() => Video.Capture(Scheduler.Now);
    /// <summary>Captures the current guest scanout at the machine's actual virtual time.</summary>
    public VideoFrame CaptureFrame(VgaFrameBuffer buffer) => Video.Capture(Scheduler.Now, buffer);
    /// <summary>Prepares fresh volatile hardware while acquiring safe leases for the existing attached media.</summary>
    public PcMachine CreatePowerCycle(MachineProfile profile)
    {
        profile.Validate();
        IReadOnlyBlockDevice?[] copies = new IReadOnlyBlockDevice?[4];
        IReadOnlyBlockDevice? a = null, b = null; IReadOnlyOpticalDisc? optical = null;
        try
        {
            for (int i = 0; i < copies.Length; i++) copies[i] = _disks[i]?.Reopen();
            a = Floppy.ReopenMedia(0); b = Floppy.ReopenMedia(1);
            optical = OpticalDisc?.Reopen();
            var poweredOn = CreateWithOpticalDiscs(profile, copies, optical, a, b);
            poweredOn.SetDefinitionIdentity(_definitionId, _definitionName ?? Profile.Name);
            Rtc.CarryBatteryStateTo(poweredOn.Rtc);
            return poweredOn;
        }
        catch { foreach (var copy in copies) copy?.Dispose(); a?.Dispose(); b?.Dispose(); optical?.Dispose(); throw; }
    }
    /// <summary>Constructs two ISA IDE channels from four fixed positions, including empty slots.</summary>
    public static PcMachine CreateWithDisks(MachineProfile profile, IReadOnlyList<IReadOnlyBlockDevice?> disks,
        IReadOnlyBlockDevice? floppyA = null, IReadOnlyBlockDevice? floppyB = null)
    {
        if (disks.Count != 4) throw new ArgumentException("Exactly four IDE slot positions are required.");
        return new(profile, disks[0], floppyA, floppyB, disks[1], disks[2], disks[3], null);
    }

    /// <summary>Constructs the selected fixed packet target beside the four HDD positions and transfers all leases only on success.</summary>
    public static PcMachine CreateWithOpticalDiscs(MachineProfile profile, IReadOnlyList<IReadOnlyBlockDevice?> disks,
        IReadOnlyOpticalDisc? disc, IReadOnlyBlockDevice? floppyA = null, IReadOnlyBlockDevice? floppyB = null)
    {
        if (disks.Count != 4) throw new ArgumentException("Exactly four IDE slot positions are required.");
        return new(profile, disks[0], floppyA, floppyB, disks[1], disks[2], disks[3], disc);
    }

    /// <summary>Returns a borrowed device for preparing a replacement; callers must acquire a reopen lease before taking ownership.</summary>
    internal IReadOnlyBlockDevice? DiskAt(int slot) => _disks[slot];

    /// <summary>Preserves the saved PC's identity across cold starts; it is separate from volatile CPU/device state.</summary>
    internal void SetDefinitionIdentity(string id, string name) { _definitionId = id; _definitionName = name; }

    /// <summary>Captures hardware and file-backed media without claiming a CPU/RAM/device snapshot.</summary>
    public MachineDefinition CaptureDefinition(string? name = null, string? id = null)
    {
        var hard = _disks.Select((d, slot) => d is IFileBackedBlockDevice f ? new DiskAttachment(slot, f.Image.BasePath, f.Image.OverlayPath) : null)
            .OfType<DiskAttachment>().ToArray();
        var floppies = Enumerable.Range(0, 2).Select(slot => Floppy.MediaImage(slot) is { } f ? new DiskAttachment(slot, f.BasePath, f.OverlayPath) : null)
            .OfType<DiskAttachment>().ToArray();
        return new() { Id = id ?? _definitionId, Name = name ?? _definitionName ?? Profile.Name,
            Hardware = Profile, HardDisks = hard, Floppies = floppies,
            OpticalDiscs = OpticalDisc is IFileBackedOpticalDisc optical ? [new(Profile.AtapiSlot, optical.ImagePath)] : [],
            BuiltInTestDisk = _disk is MemoryDisk };
    }

    /// <summary>Releases all media leases after the emulation owner has stopped using the machine.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        try { Floppy.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        foreach (var disk in _disks)
        {
            try { disk?.Dispose(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        try { OpticalDisc?.Dispose(); }
        catch (Exception ex) { (errors ??= []).Add(ex); }
        if (errors is not null) throw new AggregateException("One or more disk leases could not close.", errors);
    }
}
