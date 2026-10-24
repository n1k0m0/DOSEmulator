using DOSEmulator.Contracts;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public static class MachineFactory
{
    public static PcMachine FromImages(MachineProfile profile, string? ideImage = null, string? ideOverlay = null,
        string? floppyA = null, string? floppyAOverlay = null, string? floppyB = null, string? floppyBOverlay = null, string? bootDevice = null)
    {
        static IReadOnlyBlockDevice? Open(string? image, string? overlay)
        {
            if (overlay is not null && image is null) throw new ArgumentException("A private overlay requires its base image.");
            return image is null ? null : overlay is not null ? new CopyOnWriteDisk(image, overlay) : new ReadOnlyRawDisk(image);
        }
        IReadOnlyBlockDevice? ide = null, a = null, b = null;
        try
        {
            ide = Open(ideImage, ideOverlay); a = Open(floppyA, floppyAOverlay); b = Open(floppyB, floppyBOverlay);
            if (a is not null && profile.FloppyDriveA == "none") profile = profile with { FloppyDriveA = FloppyGeometry.IdForSectors(a.SectorCount) };
            if (b is not null && profile.FloppyDriveB == "none") profile = profile with { FloppyDriveB = FloppyGeometry.IdForSectors(b.SectorCount) };
            if (bootDevice is not null) profile = profile with { BootDevice = bootDevice };
            else if (ide is null && a is not null) profile = profile with { BootDevice = "floppy-a" };
            profile.Validate();
            return ide is null && a is null && b is null ? PcMachine.CreateDemo(profile) : new PcMachine(profile, ide, a, b);
        }
        catch { ide?.Dispose(); a?.Dispose(); b?.Dispose(); throw; }
    }
}
