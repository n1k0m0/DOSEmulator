namespace DOSEmulator.Hardware;

public sealed record FloppyGeometry(int Cylinders, int Heads, int SectorsPerTrack, byte CmosType, byte DataRate)
{
    public long SectorCount => (long)Cylinders * Heads * SectorsPerTrack;
    public static FloppyGeometry? FromId(string id) => id switch
    {
        "none" => null,
        "360k" => new(40, 2, 9, 1, 2),
        "1200k" => new(80, 2, 15, 2, 0),
        "720k" => new(80, 2, 9, 3, 2),
        "1440k" => new(80, 2, 18, 4, 0),
        "2880k" => new(80, 2, 36, 5, 3),
        _ => throw new ArgumentException("Unknown floppy drive type.")
    };
    public static string IdForSectors(long sectors) => sectors switch
    {
        720 => "360k", 2400 => "1200k", 1440 => "720k", 2880 => "1440k", 5760 => "2880k",
        _ => throw new ArgumentException("Supported raw floppy sizes: 360, 720, 1200, 1440 or 2880 KiB.")
    };
}
