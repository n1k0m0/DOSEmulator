using System.IO.Compression;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public static partial class ReferenceFirmware
{
    public static byte[] CreateBios(int ramMiB, long sectors, bool writable = false,
        FloppyGeometry? floppyA = null, FloppyGeometry? floppyB = null, bool bootFloppy = false, bool ps2Mouse = false)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(CompressedImage));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        byte[] rom = new byte[65536]; gzip.ReadExactly(rom);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0xe000), (ushort)Math.Min(0xfc00, (ramMiB - 1) * 1024));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0xe002), (uint)Math.Min(0xfffffff, sectors));
        rom[0xe006] = writable ? (byte)1 : (byte)0;
        rom[0xe007] = floppyA?.CmosType ?? 0; rom[0xe008] = floppyB?.CmosType ?? 0;
        rom[0xe009] = bootFloppy ? (byte)0 : (byte)0x80;
        rom[0xe00a] = (byte)(floppyA?.SectorsPerTrack ?? 0); rom[0xe00b] = (byte)(floppyB?.SectorsPerTrack ?? 0);
        rom[0xe00c] = floppyA?.DataRate ?? 0; rom[0xe00d] = floppyB?.DataRate ?? 0;
        rom[0xe00e] = (byte)((floppyA is null ? 0 : 1) + (floppyB is null ? 0 : 1));
        rom[0xe00f] = ps2Mouse ? (byte)1 : (byte)0;
        rom[0xe024] = (byte)(floppyA?.SectorsPerTrack ?? 18); rom[0xe034] = (byte)(floppyB?.SectorsPerTrack ?? 18);
        rom[0xe027] = FormatGap(floppyA); rom[0xe037] = FormatGap(floppyB);
        VgaPalette.Planar().CopyTo(rom, 0xe100); VgaPalette.Indexed().CopyTo(rom, 0xe400);
        Cp437Font.Table(8).CopyTo(rom.AsSpan(0xa000));
        Cp437Font.Table(14).CopyTo(rom.AsSpan(0xa800));
        Cp437Font.Table(16).CopyTo(rom.AsSpan(0xc000));
        return rom;
    }
    private static byte FormatGap(FloppyGeometry? geometry) => geometry?.CmosType switch
    {
        1 or 3 => 0x50, 2 => 0x54, 5 => 0x53, _ => 0x6c
    };
}
