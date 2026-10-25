namespace DOSEmulator.Contracts;

public sealed record MachineProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "virtual486-dev-v1";
    public string Name { get; init; } = "Virtual 486 AT - Development";
    public string Cpu { get; init; } = "i486dx-development";
    public int CpuHz { get; init; } = 33_000_000;
    public int RamMiB { get; init; } = 8;
    public string FloppyDriveA { get; init; } = "none";
    public string FloppyDriveB { get; init; } = "none";
    public string BootDevice { get; init; } = "ide";
    public string SoundCard { get; init; } = "none";
    public int SoundBlasterBasePort { get; init; } = 0x220;
    public int SoundBlasterIrq { get; init; } = 5;
    public int SoundBlasterDmaChannel { get; init; } = 1;
    public string Mouse { get; init; } = "none";

    public string GetBlasterEnvironment() => $"A{SoundBlasterBasePort:X3} I{SoundBlasterIrq} D{SoundBlasterDmaChannel} T3";

    public void Validate()
    {
        if (SchemaVersion != 1 || Id != "virtual486-dev-v1" || Cpu != "i486dx-development")
            throw new ArgumentException("Only the explicitly limited Virtual486 development profile is implemented.");
        if (CpuHz != 33_000_000)
            throw new ArgumentException("This development profile has a fixed nominal clock of 33 MHz.");
        if (RamMiB is not (4 or 8 or 16))
            throw new ArgumentException("Development RAM must be 4, 8 or 16 MiB.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128)
            throw new ArgumentException("A profile name must contain 1 to 128 characters.");
        foreach (string drive in new[] { FloppyDriveA, FloppyDriveB })
            if (drive is not ("none" or "360k" or "720k" or "1200k" or "1440k" or "2880k")) throw new ArgumentException("Unknown floppy drive geometry.");
        if (BootDevice is not ("ide" or "floppy-a") || (BootDevice == "floppy-a" && FloppyDriveA == "none"))
            throw new ArgumentException("Boot device must be ide or an installed floppy-a drive.");
        if (FloppyDriveB != "none" && FloppyDriveA == "none")
            throw new ArgumentException("Drive B requires an installed drive A on this reference platform.");
        if (SoundCard is not ("none" or "sb2-dsp-development" or "adlib-opl2-development" or "sb2-opl2-development"))
            throw new ArgumentException("Unknown development sound card. Supported: none, sb2-dsp-development, adlib-opl2-development, sb2-opl2-development.");
        if (SoundBlasterBasePort is not (0x220 or 0x240))
            throw new ArgumentException("SB2 base port must be 220h or 240h.");
        if (SoundBlasterIrq is not (3 or 5 or 7 or 9))
            throw new ArgumentException("SB2 IRQ must be 3, 5, 7 or AT IRQ9; reserved board IRQs cannot be shared.");
        if (SoundBlasterDmaChannel != 1)
            throw new ArgumentException("This SB2 board uses DMA1. Other DMA wiring belongs to a later card profile; DMA2 is reserved for floppy.");
        if (Mouse is not ("none" or "ps2-two-button")) throw new ArgumentException("Mouse must be none or ps2-two-button.");
    }
}

public enum MachineRunState { Paused, Running, Halted, Faulted, Stopped, ResetHeld }

public sealed record MachineResetStatus(long Count, string? Source, ulong AtNanoseconds, ushort WarmBootFlag, bool Held, long Post10Count = 0);

public sealed record MachineStatus(
    string ProfileId, string ProfileName, MachineRunState State, long Instructions,
    ulong VirtualNanoseconds, string Position, string? Fault, string MediaName, string DebugOutput,
    bool DiskWritable = false, MachineProfile? Configuration = null,
    string FloppyAMedia = "Empty", string FloppyBMedia = "Empty", MachineResetStatus? Reset = null);

public sealed record HostSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Filter { get; init; } = "Sharp";
    public string Upscale { get; init; } = "None";
    public bool CorrectAspect { get; init; } = true;
    public bool VSync { get; init; } = true;
    public bool AudioMuted { get; init; }
    public int MasterVolume { get; init; } = 80;
    public int SpeakerVolume { get; init; } = 100;
    public int DigitalVolume { get; init; } = 100;
    public int FmVolume { get; init; } = 100;
}

public sealed record ConfigurationPlan(long ExpectedRevision, MachineProfile Requested, bool RequiresPowerCycle);
