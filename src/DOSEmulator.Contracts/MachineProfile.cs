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
    public string Mouse { get; init; } = "none";

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
        if (Mouse is not ("none" or "ps2-two-button")) throw new ArgumentException("Mouse must be none or ps2-two-button.");
    }
}

public enum MachineRunState { Paused, Running, Halted, Faulted, Stopped }

public sealed record MachineStatus(
    string ProfileId, string ProfileName, MachineRunState State, long Instructions,
    ulong VirtualNanoseconds, string Position, string? Fault, string MediaName, string DebugOutput,
    bool DiskWritable = false, MachineProfile? Configuration = null,
    string FloppyAMedia = "Empty", string FloppyBMedia = "Empty");

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
