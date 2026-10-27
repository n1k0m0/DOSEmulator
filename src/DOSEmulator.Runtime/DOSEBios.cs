// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.IO.Compression;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

public static partial class DOSEBios
{
    /// <summary>Builds the original one-disk-compatible ROM API through the complete fixed-slot configuration generator.</summary>
    public static byte[] CreateBios(int ramMiB, long sectors, bool writable = false,
        FloppyGeometry? floppyA = null, FloppyGeometry? floppyB = null, bool bootFloppy = false, bool ps2Mouse = false, bool et4000 = false, bool gameport = false, bool serial = false, bool parallel = false,
        ushort midiPort = 0, byte midiIrq = 0)
        => CreateBiosCore(ramMiB, [sectors, 0, 0, 0], [writable, false, false, false], floppyA, floppyB, bootFloppy, ps2Mouse, et4000, gameport, serial, parallel, midiPort, midiIrq);

    /// <summary>Builds a dense BIOS drive map while retaining physical primary/secondary master/slave routes.</summary>
    public static byte[] CreateBiosForDisks(int ramMiB, IReadOnlyList<IReadOnlyBlockDevice?> disks,
        FloppyGeometry? floppyA = null, FloppyGeometry? floppyB = null, bool bootFloppy = false, bool ps2Mouse = false, bool et4000 = false, bool gameport = false, bool serial = false, bool parallel = false,
        ushort midiPort = 0, byte midiIrq = 0)
        => CreateBiosCore(ramMiB, disks.Select(d => d?.SectorCount ?? 0).ToArray(), disks.Select(d => d is IWritableBlockDevice).ToArray(),
            floppyA, floppyB, bootFloppy, ps2Mouse, et4000, gameport, serial, parallel, midiPort, midiIrq);

    /// <summary>Chooses only the explicit board firmware variant while retaining the public legacy ROM builder ABI.</summary>
    internal static byte[] CreateBiosForBoard(bool fxHost, int ramMiB, IReadOnlyList<IReadOnlyBlockDevice?> disks,
        FloppyGeometry? floppyA, FloppyGeometry? floppyB, bool bootFloppy, bool ps2Mouse, bool et4000, bool gameport, bool serial, bool parallel,
        ushort midiPort, byte midiIrq)
        => CreateBiosCore(ramMiB, disks.Select(d => d?.SectorCount ?? 0).ToArray(), disks.Select(d => d is IWritableBlockDevice).ToArray(),
            floppyA, floppyB, bootFloppy, ps2Mouse, et4000, gameport, serial, parallel, midiPort, midiIrq, fxHost);

    /// <summary>Patches immutable ROM configuration, including translated CHS geometry for disks larger than 504 MiB.</summary>
    private static byte[] CreateBiosCore(int ramMiB, long[] capacities, bool[] writableDrives,
        FloppyGeometry? floppyA, FloppyGeometry? floppyB, bool bootFloppy, bool ps2Mouse, bool et4000, bool gameport, bool serial, bool parallel, ushort midiPort, byte midiIrq, bool fxHost = false)
    {
        if (midiPort != 0 && (midiPort is not (0x300 or 0x330) || midiIrq is not (5 or 7 or 9 or 10)))
            throw new ArgumentException("Unsupported CT1740 MIDI inventory resources.");
        long sectors = capacities.FirstOrDefault(c => c > 0);
        int first = Array.FindIndex(capacities, c => c > 0);
        bool writable = first >= 0 && writableDrives[first];
        using var compressed = new MemoryStream(Convert.FromBase64String(fxHost ? FxHostCompressedImage : CompressedImage));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        byte[] rom = new byte[65536]; gzip.ReadExactly(rom);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0xe000), (ushort)Math.Min(0xfc00, (ramMiB - 1) * 1024));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0xe002), (uint)Math.Min(0x10000000, sectors));
        rom[0xe006] = writable ? (byte)1 : (byte)0;
        rom[0xe007] = floppyA?.CmosType ?? 0; rom[0xe008] = floppyB?.CmosType ?? 0;
        rom[0xe009] = bootFloppy ? (byte)0 : (byte)0x80;
        rom[0xe00a] = (byte)(floppyA?.SectorsPerTrack ?? 0); rom[0xe00b] = (byte)(floppyB?.SectorsPerTrack ?? 0);
        rom[0xe00c] = floppyA?.DataRate ?? 0; rom[0xe00d] = floppyB?.DataRate ?? 0;
        rom[0xe00e] = (byte)((floppyA is null ? 0 : 1) + (floppyB is null ? 0 : 1));
        rom[0xe00f] = ps2Mouse ? (byte)1 : (byte)0;
        rom[0xe010] = et4000 ? (byte)1 : (byte)0;
        rom[0xe015] = gameport ? (byte)1 : (byte)0;
        rom[0xe016] = serial ? (byte)1 : (byte)0;
        rom[0xe017] = parallel ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0xe018), midiPort);
        rom[0xe01a] = midiIrq;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0xe011), checked((uint)ramMiB * 1024));
        int index = 0;
        for (int slot = 0; slot < capacities.Length; slot++)
        {
            long count = capacities[slot];
            if (count <= 0) continue;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0xe040 + index * 4), (uint)count);
            rom[0xe050 + index] = writableDrives[slot] ? (byte)1 : (byte)0;
            rom[0xe058 + index] = (byte)slot;
            int heads = new[] { 16, 32, 64, 128, 255 }.FirstOrDefault(h => count <= 1024L * h * 63, 255);
            rom[0xe05c + index] = (byte)heads;
            if (index < 2)
            {
                var table = rom.AsSpan(0xe060 + index * 16, 16); table.Clear();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(table, (ushort)Math.Clamp(count / (heads * 63), 1, 1024));
                table[2] = (byte)heads; table[14] = 63;
            }
            index++;
        }
        rom[0xe054] = (byte)index;
        rom[0xe024] = (byte)(floppyA?.SectorsPerTrack ?? 18); rom[0xe034] = (byte)(floppyB?.SectorsPerTrack ?? 18);
        rom[0xe027] = FormatGap(floppyA); rom[0xe037] = FormatGap(floppyB);
        VgaPalette.Planar().CopyTo(rom, 0xe100); VgaPalette.Indexed().CopyTo(rom, 0xe400);
        Cp437Font.Table(8).CopyTo(rom.AsSpan(0xa000));
        Cp437Font.Table(14).CopyTo(rom.AsSpan(0xa800));
        Cp437Font.Table(16).CopyTo(rom.AsSpan(0xc000));
        return rom;
    }
    /// <summary>Identifies only the selected own P54C platform in the immutable ROM inventory without changing the 486 builder ABI or bytes.</summary>
    internal static void ConfigureP54cInventory(byte[] rom)
    {
        ReplaceInventory(rom, "  |  486 / AT   |   Original firmware - 64 KiB ROM"u8,
            "  | P54C / AT   |   Original firmware - 64 KiB ROM"u8);
        ReplaceInventory(rom, "Configured CPU: 486DX / AT development profile"u8,
            "Configured CPU: P54C B5 / DOSE AT subset"u8);
    }

    /// <summary>Labels the actual host-validation board without claiming PIIX, historical board firmware or Windows acceptance.</summary>
    internal static void ConfigureFxHostInventory(byte[] rom)
    {
        ReplaceInventory(rom, "  |  486 / AT   |   Original firmware - 64 KiB ROM"u8,
            " | P54C / FXhost | Original firmware - 64 KiB ROM"u8);
        ReplaceInventory(rom, "Configured CPU: 486DX / AT development profile"u8,
            "Configured CPU: P54C B5 / 430FX host subset"u8);
    }

    /// <summary>Replaces one exact own inventory string within its original capacity, retaining all ROM pointers and terminators.</summary>
    private static void ReplaceInventory(Span<byte> rom, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        int offset = rom.IndexOf(before);
        if (after.Length > before.Length || offset < 0 || offset + before.Length >= rom.Length ||
            rom[offset + before.Length] != 0 || rom[(offset + before.Length)..].IndexOf(before) >= 0)
            throw new InvalidOperationException("The selected DOSEBios inventory does not fit its expected own ROM layout.");
        var destination = rom.Slice(offset, before.Length);
        destination.Fill((byte)' '); after.CopyTo(destination);
    }

    /// <summary>Selects the reference format-gap byte for the configured native floppy geometry.</summary>
    private static byte FormatGap(FloppyGeometry? geometry) => geometry?.CmosType switch
    {
        1 or 3 => 0x50, 2 => 0x54, 5 => 0x53, _ => 0x6c
    };
}
