// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

public sealed record MachineProfile
{
    /// <summary>Own DOSE AT validation platform; this identifier does not name a historical PCI chipset.</summary>
    public const string P54cProfileId = "dose-p54c-at-dev-v1";
    /// <summary>Explicitly limited P54C B5 CPU implementation, separate from a complete Pentium platform claim.</summary>
    public const string P54cCpuId = "p54c-b5-subset-development";
    /// <summary>Fixed nominal virtual core/TSC rate for the DOSE P54C validation profile, without historical cycle calibration.</summary>
    public const int P54cCpuHz = 90_000_000;
    /// <summary>Explicit 82437FX-host validation board; PIIX and Windows integration remain separate.</summary>
    public const string FxHostProfileId = "dose-430fx-host-dev-v1";
    /// <summary>Fixed nominal P54C core rate of this host-controller board, without cycle calibration.</summary>
    public const int FxHostCpuHz = 100_000_000;
    /// <summary>Fixed installed capacity validated with the host-controller firmware and snapshot graph.</summary>
    public const int FxHostRamMiB = 32;
    /// <summary>Own 430FX/PIRQ validation board; this identifier excludes IDE, reset and full PIIX acceptance.</summary>
    public const string PiixIrqProfileId = "dose-430fx-piix-irq-dev-v1";
    /// <summary>Own PIIX IDE/PCI busmaster validation board; full chipset reset and Windows acceptance remain separate.</summary>
    public const string PiixIdeProfileId = "dose-430fx-piix-ide-dev-v1";
    /// <summary>Own CF9/INIT/reset-domain board; complete chipset and Windows acceptance remain separate.</summary>
    public const string PiixResetProfileId = "dose-430fx-piix-reset-dev-v1";
    /// <summary>Own reset-capable 430FX board with a fixed, limited original Trio64 PCI adapter.</summary>
    public const string S3Trio64ProfileId = "dose-430fx-piix-s3-dev-v1";
    /// <summary>Explicit original 86C764 PCI adapter with two MiB; acceleration and complete Windows support remain separate.</summary>
    public const string S3Trio64VideoId = "s3-trio64-86c764-pci-2m-development";
    /// <summary>Own fixed S3/PIIX board with one separately identified SFF-8020i PIO CD-ROM target.</summary>
    public const string AtapiProfileId = "dose-430fx-piix-s3-atapi-dev-v1";
    /// <summary>Original 1995 twelve-byte packet/PIO subset, without DMA, overlap or CD audio synthesis.</summary>
    public const string AtapiCdRomId = "sff8020i-rev25-pio-development";
    /// <summary>Separate own PCI firmware board, retaining the prior ATAPI profile and its ROM bytes.</summary>
    public const string PciBiosProfileId = "dose-430fx-piix-s3-atapi-pcibios-dev-v1";
    /// <summary>Own Windows95 development candidate with the optional S3 firmware services; installation acceptance remains separate.</summary>
    public const string Windows95ProfileId = "dose-430fx-piix-s3-win95-dev-v1";
    /// <summary>Separate own development candidate at a fixed nominal 133 MHz, without historical cycle calibration.</summary>
    public const string Windows95Profile133Id = "dose-430fx-piix-s3-win95-133-dev-v1";
    /// <summary>Nominal owner/TSC rate reserved for the separate 133 MHz development identifier.</summary>
    public const int Windows95Cpu133Hz = 133_000_000;
    /// <summary>Whether this explicitly identified candidate selects the new S3-VBE firmware while retaining all fixed chipset peers.</summary>
    public bool IsWindows95Board => (Id is Windows95ProfileId or Windows95Profile133Id) && Cpu == P54cCpuId;
    /// <summary>Whether this fixed board exposes the own PCI BIOS and BIOS32 firmware interfaces.</summary>
    public bool IsPciBiosBoard => (Id == PciBiosProfileId || IsWindows95Board) && Cpu == P54cCpuId;
    /// <summary>Whether this is the explicitly validated packet-device board tuple.</summary>
    public bool IsAtapiBoard => (Id == AtapiProfileId || IsPciBiosBoard) && Cpu == P54cCpuId;
    /// <summary>Whether one installed packet target is selected, independently from inserted media.</summary>
    public bool IsAtapi => OpticalDrive == AtapiCdRomId;
    /// <summary>Whether this is a separately validated board containing the fixed Trio64 tuple.</summary>
    public bool IsS3Trio64Board => (Id == S3Trio64ProfileId || IsAtapiBoard) && Cpu == P54cCpuId;
    /// <summary>Whether the profile explicitly selects the original two-MiB Trio64 PCI adapter.</summary>
    public bool IsTrio64 => VideoCard == S3Trio64VideoId;
    /// <summary>Whether the board explicitly wires CF9 to the P54C and the reviewed PCI/ISA reset domains.</summary>
    public bool IsPiixReset => (Id == PiixResetProfileId || IsS3Trio64Board) && Cpu == P54cCpuId;
    /// <summary>Whether this tuple constructs both original PIIX functions and DMA-capable ATA channels.</summary>
    public bool IsPiixIde => (Id == PiixIdeProfileId || IsPiixReset) && Cpu == P54cCpuId;
    /// <summary>Whether this tuple contains the explicitly capable PIC/PIRQ bridge, including the separate IDE board.</summary>
    public bool IsPiixIrq => (Id == PiixIrqProfileId || IsPiixIde) && Cpu == P54cCpuId;
    /// <summary>Whether the board contains the fixed 82437FX host controller.</summary>
    public bool IsFxHost => (Id == FxHostProfileId || IsPiixIrq) && Cpu == P54cCpuId;
    /// <summary>Whether the profile selects one matching board and the limited P54C CPU implementation.</summary>
    public bool IsP54c => (Id == P54cProfileId || IsFxHost) && Cpu == P54cCpuId;
    /// <summary>Selects a complete supported CPU/platform/clock tuple for a later validated cold power cycle, preserving user names and devices.</summary>
    public MachineProfile WithDevelopmentCpu(bool useP54c)
    {
        if (IsFxHost)
        {
            if (!useP54c) throw new InvalidOperationException(IsWindows95Board
                ? "The Windows95 development board retains its fixed P54C, nominal clock and 32 MiB tuple."
                : "The 430FX-host board has a fixed P54C/100-MHz/32-MiB tuple.");
            return this;
        }
        return this with
        {
            Id = useP54c ? P54cProfileId : "virtual486-dev-v1",
            Cpu = useP54c ? P54cCpuId : "i486dx-development",
            CpuHz = useP54c ? P54cCpuHz : 33_000_000
        };
    }
    /// <summary>Persisted format version; unsupported versions are rejected before adopting the configuration.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Identifier of the explicitly implemented development hardware profile.</summary>
    public string Id { get; init; } = "virtual486-dev-v1";
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    public string Name { get; init; } = "DOSE 486 AT - Development";
    /// <summary>Selected validated guest CPU model identifier, not a live mutable CPU instance.</summary>
    public string Cpu { get; init; } = "i486dx-development";
    /// <summary>Nominal guest clock frequency in hertz; it is not a claim of historical cycle calibration.</summary>
    public int CpuHz { get; init; } = 33_000_000;
    /// <summary>Installed guest RAM in binary mebibytes.</summary>
    public int RamMiB { get; init; } = 8;
    /// <summary>Configured physical floppy-A geometry identifier, independent from its currently inserted medium.</summary>
    public string FloppyDriveA { get; init; } = "none";
    /// <summary>Configured physical floppy-B geometry identifier, independent from its currently inserted medium.</summary>
    public string FloppyDriveB { get; init; } = "none";
    /// <summary>Configured cold-boot source; current reference choices are IDE or installed floppy A.</summary>
    public string BootDevice { get; init; } = "ide";
    /// <summary>Explicit sound-card model identifier, validated together with its port/IRQ/DMA resources.</summary>
    public string SoundCard { get; init; } = "none";
    /// <summary>Configured ISA DSP/mixer base I/O port.</summary>
    public int SoundBlasterBasePort { get; init; } = 0x220;
    /// <summary>Configured physical ISA sound-card IRQ number before guest PIC remapping.</summary>
    public int SoundBlasterIrq { get; init; } = 5;
    /// <summary>Configured sound-card byte-DMA channel.</summary>
    public int SoundBlasterDmaChannel { get; init; } = 1;
    /// <summary>Configured sound-card word-DMA channel for the concrete SB16 model.</summary>
    public int SoundBlasterDma16Channel { get; init; } = 5;
    /// <summary>Selected guest mouse model identifier, independent from host capture ownership.</summary>
    public string Mouse { get; init; } = "none";
    /// <summary>Optional standalone analog adapter; this does not automatically open a host gamepad or MIDI endpoint.</summary>
    public string Gameport { get; init; } = "none";
    /// <summary>Optional fixed COM1 adapter; its peer remains disconnected until explicitly supplied.</summary>
    public string SerialPort { get; init; } = "none";
    /// <summary>Optional standalone IBM printer adapter; no host printer or virtual peer is implied.</summary>
    public string ParallelPort { get; init; } = "none";
    /// <summary>Explicit optional CT1740 MPU-UART jumper selection; no MIDI endpoint or synthesizer is implied.</summary>
    public string MidiInterface { get; init; } = "none";
    /// <summary>Jumper-selected MIDI data/status pair, independent from the DSP/mixer base.</summary>
    public int MidiBasePort { get; init; } = 0x330;
    /// <summary>Installed optical drive model, distinct from an empty or loaded tray.</summary>
    public string OpticalDrive { get; init; } = "none";
    /// <summary>One of the four physical IDE positions reserved for the installed packet target.</summary>
    public int AtapiSlot { get; init; } = 2;
    private string _videoCard = DOSEIdentity.VgaModel;
    /// <summary>Canonical adapter ID; older saved PCs normalize the former reference-VGA spelling while loading.</summary>
    public string VideoCard
    {
        get => _videoCard;
        init => _videoCard = value == "reference-vga-development" ? DOSEIdentity.VgaModel : value;
    }
    /// <summary>Whether the selected profile instantiates the limited ET4000AX ISA reference adapter.</summary>
    public bool IsEt4000 => VideoCard == "et4000ax-isa-1m-development";

    /// <summary>Whether the selected profile instantiates the concrete CT1740/DSP4.05 sound-card model.</summary>
    public bool IsSb16 => SoundCard == "sb16-ct1740-dsp405-development";
    /// <summary>Builds the DOS BLASTER variable for the selected concrete card and its configured resources.</summary>
    public string GetBlasterEnvironment() => IsSb16
        ? $"A{SoundBlasterBasePort:X3} I{SoundBlasterIrq} D{SoundBlasterDmaChannel} H{SoundBlasterDma16Channel}" + (MidiInterface == "none" ? "" : $" P{MidiBasePort:X3}") + " T6"
        : $"A{SoundBlasterBasePort:X3} I{SoundBlasterIrq} D{SoundBlasterDmaChannel} T3";

    /// <summary>Rejects unsupported CPU/platform/clock tuples before optional drive-geometry inference opens any media.</summary>
    public void ValidateCpuSelection()
    {
        if (SchemaVersion != 1 || !(Id == "virtual486-dev-v1" && Cpu == "i486dx-development" || IsP54c))
            throw new ArgumentException("Use a matching implemented DOSE platform and CPU model.");
        int expectedHz = IsWindows95Board && Id == Windows95Profile133Id ? Windows95Cpu133Hz :
            IsFxHost ? FxHostCpuHz : IsP54c ? P54cCpuHz : 33_000_000;
        if (CpuHz != expectedHz)
            throw new ArgumentException("The selected development CPU requires its fixed nominal virtual clock.");
        if (IsFxHost ? RamMiB != FxHostRamMiB : RamMiB is not (4 or 8 or 16))
            throw new ArgumentException("Use 32 MiB for the 430FX-host board, or 4/8/16 MiB for an existing AT board.");
        if (IsAtapi != IsAtapiBoard || OpticalDrive is not ("none" or AtapiCdRomId) || AtapiSlot is < 0 or > 3 || !IsAtapi && AtapiSlot != 2)
            throw new ArgumentException("The packet CD-ROM requires its fixed S3/PIIX board and one valid IDE slot.");
        if (IsTrio64 != IsS3Trio64Board)
            throw new ArgumentException("The original Trio64 adapter requires its fixed 430FX/PIIX/S3 board tuple.");
    }

    /// <summary>Rejects unsupported hardware identifiers, sizes and conflicting DOSE platform resource selections.</summary>
    public void Validate()
    {
        ValidateCpuSelection();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128)
            throw new ArgumentException("A profile name must contain 1 to 128 characters.");
        foreach (string drive in new[] { FloppyDriveA, FloppyDriveB })
            if (drive is not ("none" or "360k" or "720k" or "1200k" or "1440k" or "2880k")) throw new ArgumentException("Unknown floppy drive geometry.");
        if (BootDevice is not ("ide" or "floppy-a") || (BootDevice == "floppy-a" && FloppyDriveA == "none"))
            throw new ArgumentException("Boot device must be ide or an installed floppy-a drive.");
        if (FloppyDriveB != "none" && FloppyDriveA == "none")
            throw new ArgumentException("Drive B requires an installed drive A on the DOSE platform.");
        if (SoundCard is not ("none" or "sb2-dsp-development" or "adlib-opl2-development" or "sb2-opl2-development" or "sb16-ct1740-dsp405-development"))
            throw new ArgumentException("Unknown development sound card.");
        if (IsSb16)
        {
            if (SoundBlasterBasePort is not (0x220 or 0x240 or 0x260 or 0x280) || SoundBlasterIrq is not (5 or 7 or 9 or 10) ||
                SoundBlasterDmaChannel is not (0 or 1 or 3) || SoundBlasterDma16Channel is not (5 or 6 or 7))
                throw new ArgumentException("CT1740 requires a supported base, IRQ, DMA8 and DMA16 resource set.");
        }
        else
        {
            if (SoundBlasterBasePort is not (0x220 or 0x240))
                throw new ArgumentException("SB2 base port must be 220h or 240h.");
            if (SoundBlasterIrq is not (3 or 5 or 7 or 9))
                throw new ArgumentException("SB2 IRQ must be 3, 5, 7 or AT IRQ9; reserved board IRQs cannot be shared.");
            if (SoundBlasterDmaChannel != 1)
                throw new ArgumentException("This SB2 board uses DMA1. Other DMA wiring belongs to a later card profile; DMA2 is reserved for floppy.");
        }
        if (Mouse is not ("none" or "ps2-two-button")) throw new ArgumentException("Mouse must be none or ps2-two-button.");
        if (Gameport is not ("none" or "ibm-game-control-development")) throw new ArgumentException("Unknown development gameport.");
        if (SerialPort is not ("none" or "pc16550d-com1-development")) throw new ArgumentException("Unknown development COM adapter.");
        if (ParallelPort is not ("none" or "ibm-printer-adapter-development")) throw new ArgumentException("Unknown development LPT adapter.");
        if (MidiInterface is not ("none" or "ct1740-mpu401-uart-development") || MidiBasePort is not (0x300 or 0x330) || (MidiInterface != "none" && !IsSb16))
            throw new ArgumentException("MPU-UART requires the CT1740 card and a 300h/330h jumper selection.");
        if (ParallelPort != "none" && SoundBlasterIrq == 7 && (IsSb16 || SoundCard is "sb2-dsp-development" or "sb2-opl2-development"))
            throw new ArgumentException("The IBM LPT adapter reserves IRQ7; select a different sound-card IRQ before enabling it.");
        if (VideoCard is not ("dose-vga-development" or "et4000ax-isa-1m-development" or S3Trio64VideoId)) throw new ArgumentException("Unknown development video card.");
    }
}

public enum MachineRunState { Paused, Running, Halted, Faulted, Stopped, ResetHeld }

public sealed record MachineResetStatus(long Count, string? Source, ulong AtNanoseconds, ushort WarmBootFlag, bool Held, long Post10Count = 0);

public sealed record MachineStatus(
    string ProfileId, string ProfileName, MachineRunState State, long Instructions,
    ulong VirtualNanoseconds, string Position, string? Fault, string MediaName, string DebugOutput,
    bool DiskWritable = false, MachineProfile? Configuration = null,
    string FloppyAMedia = "Empty", string FloppyBMedia = "Empty", MachineResetStatus? Reset = null,
    IReadOnlyList<HardDiskStatus>? HardDisks = null)
{
    /// <summary>Configured read-only optical basis, independent from the drive's guest-controlled tray state.</summary>
    public OpticalDiscStatus? OpticalDisc { get; init; }
}

/// <summary>Installed packet target and protected ISO capacity for UI/IPC; no mutable device is exposed.</summary>
public sealed record OpticalDiscStatus(int Slot, string? Name, long Bytes);

public sealed record HostSettings
{
    /// <summary>Explicit host local calendar input at startup, restore and resume; deterministic headless runs remain opt-in.</summary>
    public bool SynchronizeClock { get; init; } = true;
    /// <summary>Persisted format version; unsupported versions are rejected before adopting the configuration.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Host presentation filter; it does not modify the guest's video registers or VRAM.</summary>
    public string Filter { get; init; } = "Sharp";
    /// <summary>Host-only pixel upscaler selection, separate from the guest video mode.</summary>
    public string Upscale { get; init; } = "None";
    /// <summary>Whether host presentation uses a corrected four-to-three display aspect.</summary>
    public bool CorrectAspect { get; init; } = true;
    /// <summary>Whether host presentation synchronizes buffer swaps to the display.</summary>
    public bool VSync { get; init; } = true;
    /// <summary>Host output mute preference, separate from guest DSP/mixer state.</summary>
    public bool AudioMuted { get; init; }
    /// <summary>Host master playback gain expressed as a percentage.</summary>
    public int MasterVolume { get; init; } = 80;
    /// <summary>Host source gain for the PC speaker expressed as a percentage.</summary>
    public int SpeakerVolume { get; init; } = 100;
    /// <summary>Host source gain for digital PCM expressed as a percentage.</summary>
    public int DigitalVolume { get; init; } = 100;
    /// <summary>Host source gain for FM synthesis expressed as a percentage.</summary>
    public int FmVolume { get; init; } = 100;
}

public sealed record ConfigurationPlan(long ExpectedRevision, MachineProfile Requested, bool RequiresPowerCycle);
