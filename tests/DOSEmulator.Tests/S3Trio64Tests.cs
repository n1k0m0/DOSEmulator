// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Actual PCI/wake gates, shared VGA/LFB storage and independently specified original-Trio64 pixels.</summary>
internal static class S3Trio64Tests
{
    /// <summary>Requires an independently specified original register, byte, pixel or unchanged-state result.</summary>
    private static void Check(bool value, string why = "Original Trio64 device invariant") { if (!value) throw new Exception(why); }
    /// <summary>Accepts only the intended atomic API transaction rejection.</summary>
    private static void Invalid(Action action)
    { bool seen = false; try { action(); } catch (ArgumentException) { seen = true; } Check(seen, "Invalid Trio64 transaction accepted"); }
    private sealed class Rig
    {
        public readonly DeviceScheduler Clock = new();
        public readonly MemoryBus Memory = new(2 * 1048576);
        public readonly VgaAdapter Video;
        public readonly S3Trio64 Card;
        /// <summary>Builds one bound card with its real fixed VGA aperture and one dynamic high-memory target.</summary>
        public Rig()
        { Video = new(Clock, false, true); Card = new(Memory, Video); Memory.Map(0xa0000, 0x20000, Card, "Own S3 legacy"); Memory.MapDynamic(Card); }
        /// <summary>Writes one actual indexed CRTC port pair.</summary>
        public void Cr(byte index, byte value) { Card.Write(0x3d4, index, 1); Card.Write(0x3d5, value, 1); }
        /// <summary>Reads one actual indexed CRTC data byte, including lock answers.</summary>
        public byte Cr(byte index) { Card.Write(0x3d4, index, 1); return (byte)Card.Read(0x3d5, 1); }
        /// <summary>Writes one real sequencer register through its unlock rules.</summary>
        public void Sr(byte index, byte value) { Card.Write(0x3c4, index, 1); Card.Write(0x3c5, value, 1); }
        /// <summary>Writes one normal graphics-controller register.</summary>
        public void Gc(byte index, byte value) { Card.Write(0x3ce, index, 1); Card.Write(0x3cf, value, 1); }
        /// <summary>Performs real setup/POS wake and establishes standard color VGA controls.</summary>
        public void Wake()
        {
            Card.WriteConfiguration(4, 3, 2); Card.Write(0x46e8, 0x10, 1); Card.Write(0x102, 1, 1); Card.Write(0x46e8, 8, 1);
            Card.Write(0x3c2, 0x67, 1); Sr(0, 3); Sr(1, 1); Sr(2, 15); Sr(3, 0); Sr(4, 2);
            Cr(0x38, 0x48); Cr(0x39, 0xa5); Sr(8, 6);
        }
        /// <summary>Enables a selected high LAW and the documented two-control raw-memory bypass.</summary>
        public void Linear(int size = 2, uint bar = 0xe0000000)
        {
            Wake(); Card.WriteConfiguration(0x10, bar, 4); Cr(0x40, 1); Cr(0x31, 8); Cr(0x58, (byte)(0x10 | size));
            Sr(0x0a, 0x80); Sr(0x15, 0x80); Sr(4, 14); Gc(5, 0); Card.Write(0x4ae8, 1, 2);
        }
    }
    /// <summary>Captures all raw plane storage, independently from pixels, CPU bank or active aperture.</summary>
    private static byte[][] Planes(VgaAdapter video) => video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Requires exact retained storage without ignoring currently hidden VRAM.</summary>
    private static bool SamePlanes(byte[][] a, byte[][] b) => a.Length == b.Length && a.Zip(b).All(v => v.First.AsSpan().SequenceEqual(v.Second));
    /// <summary>Reconstructs one six-bit-DAC ARGB color without consulting the renderer's cache.</summary>
    private static uint Color(int index)
    {
        uint red = (uint)((index * 3 + 7) & 63), green = (uint)((index * 5 + 11) & 63), blue = (uint)((index * 7 + 13) & 63);
        return 0xff000000u | ((red * 4) + red / 16) << 16 | ((green * 4) + green / 16) << 8 | ((blue * 4) + blue / 16);
    }
    /// <summary>Registers original identification, real decode/alias, bank-disabled VGA and complete-pixel checks.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 S3 original PCI identities and cold gates do not expose another chip or an option ROM", () =>
        {
            var r = new Rig(); Check(r.Card.ReadConfiguration(0, 4) == 0x88115333 && r.Card.ReadConfiguration(8, 4) == 0x03000000);
            Check(r.Card.ReadConfiguration(4, 4) == 0x02000000 && r.Card.ReadConfiguration(0x30, 4) == 0x000c0000);
            Check(!r.Card.IoSpaceEnabled && !r.Card.MemorySpaceEnabled && !r.Video.TrioVideoEnabled && r.Video.VideoMemoryBytes == 0x200000 && !r.Card.LinearEnabled);
            Check(r.Card.Read(0x3cc, 1) == 255 && r.Memory.ReadByte(0xa0000) == 255);
            r.Card.WriteConfiguration(0x30, 1, 1); Check(r.Memory.ReadByte(0xc0000) == 0, "No option-ROM image is installed; unrelated installed RAM keeps its bytes");
        });
        foreach (int width in new[] { 0, 3, 8 })
            test("M4 S3 invalid PCI and I/O API widths reject atomically " + width, () =>
            {
                var r = new Rig(); r.Wake(); string pci = JsonSerializer.Serialize(r.Card.SaveState(), StateValues.Json), video = JsonSerializer.Serialize(r.Video.SaveState(), StateValues.Json);
                Invalid(() => r.Card.WriteConfiguration(4, uint.MaxValue, width)); Invalid(() => r.Card.ReadConfiguration(4, width)); Invalid(() => r.Card.Write(0x3d4, 0xa539, width)); Invalid(() => r.Card.Read(0x3d4, width));
                Check(pci == JsonSerializer.Serialize(r.Card.SaveState(), StateValues.Json) && video == JsonSerializer.Serialize(r.Video.SaveState(), StateValues.Json));
            });
        foreach (int command in new[] { 0, 1, 2, 3, 0x20, 0x21, 0x22, 0x23 })
            test("M4 S3 independent PCI memory IO and snoop target gates " + command, () =>
            {
                var r = new Rig(); r.Linear(); r.Memory.WriteByte(0xe0000011, 0x69); r.Card.WriteConfiguration(4, (uint)command, 2);
                Check(r.Card.Read(0x3cc, 1) == ((command & 1) != 0 ? 0x67u : 255u));
                Check(r.Memory.ReadByte(0xe0000011) == ((command & 2) != 0 ? 0x69 : 255));
                byte[][] before = Planes(r.Video); if ((command & 2) == 0) { r.Memory.WriteByte(0xe0000011, 0xa7); Check(SamePlanes(before, Planes(r.Video))); }
            });
        test("M4 S3 normal video stays asleep until the complete original setup POS wake sequence", () =>
        {
            var r = new Rig(); r.Card.WriteConfiguration(4, 3, 2); Check(r.Card.Read(0x3cc, 1) == 255);
            r.Card.Write(0x102, 1, 1); Check(r.Video.TrioPos == 0 && !r.Video.TrioVideoEnabled);
            r.Card.Write(0x46e8, 0x10, 2); Check(r.Video.TrioSetup == 0);
            r.Card.Write(0x46e8, 0x10, 1); r.Card.Write(0x102, 1, 1); Check(r.Card.Read(0x102, 1) == 1 && !r.Video.TrioVideoEnabled);
            r.Card.Write(0x46e8, 8, 1); Check(r.Video.TrioVideoEnabled && r.Card.Read(0x3cc, 1) == 0);
        });
        test("M4 S3 CRTC identities locks straps and full byte index are independent from stored BAR", () =>
        {
            var r = new Rig(); r.Wake(); r.Cr(0x39, 0); r.Cr(0x38, 0); Check(r.Cr(0x2d) == 0x88 && r.Cr(0x2e) == 0x11 && r.Cr(0x31) == 255);
            r.Cr(0x31, 0x09); r.Cr(0x38, 0x48); Check(r.Cr(0x31) == 0 && r.Cr(0x30) == 0xe1);
            r.Cr(0x59, 0xe0); r.Cr(0x39, 0xa0); Check(r.Cr(0x59) == 0); r.Cr(0x59, 0xe0); Check(r.Card.BarBase == 0xe0000000);
            byte strap = r.Cr(0x36); r.Cr(0x36, 0); Check(r.Cr(0x36) == strap); r.Cr(0x39, 0xa5); r.Cr(0x36, 0); Check(r.Cr(0x36) == 2);
            r.Cr(0xff, 0xa7); Check(r.Card.Read(0x3d4, 1) == 255 && r.Card.Read(0x3d5, 1) == 255);
        });
        test("M4 S3 BAR probing shares CR59 and only CR5A bit7 while preserving its low seven bits", () =>
        {
            var r = new Rig(); r.Wake(); r.Cr(0x5a, 0x21); r.Card.WriteConfiguration(0x10, uint.MaxValue, 4);
            Check(r.Card.ReadConfiguration(0x10, 4) == 0xff800000 && r.Cr(0x59) == 255 && r.Cr(0x5a) == 0xa1);
            r.Card.WriteConfiguration(0x13, 0xe0, 1); Check(r.Card.BarBase == 0xe0800000 && r.Cr(0x5a) == 0xa1);
            r.Cr(0x5a, 0x35); Check(r.Card.ReadConfiguration(0x10, 4) == 0xe0000000 && r.Video.TrioLinearBase == 0xe0350000);
        });
        foreach (int size in new[] { 0, 1, 2, 3 })
            test("M4 S3 law extent differs from eight MiB BAR and two MiB population size " + size, () =>
            {
                var r = new Rig(); r.Linear(size); uint length = size == 0 ? 0x10000u : size == 1 ? 0x100000u : size == 2 ? 0x200000u : 0x400000u;
                Check(r.Card.BarBase == 0xe0000000 && r.Card.LinearLength == length && r.Card.LinearEnabled && r.Video.VideoMemoryBytes == 0x200000);
                r.Memory.WriteByte(0xe0000000 + length - 1, 0x69); Check(r.Memory.ReadByte(0xe0000000 + length - 1) == (length <= 0x200000 ? 0x69 : 255));
                byte[][] before = Planes(r.Video); r.Memory.WriteByte(0xe0000000 + length, 0xa7); Check(r.Memory.ReadByte(0xe0000000 + length) == 255 && SamePlanes(before, Planes(r.Video)));
            });
        test("M4 S3 four MiB law has explicit FF drop unpopulated tail rather than wrapping two MiB", () =>
        {
            var r = new Rig(); r.Linear(3); r.Memory.WriteByte(0xe0000000, 0x13); r.Memory.WriteByte(0xe01fffff, 0x57); byte[][] before = Planes(r.Video);
            r.Memory.WriteByte(0xe0200000, 0x9b); r.Memory.WriteByte(0xe03fffff, 0xdf);
            Check(r.Memory.ReadByte(0xe0200000) == 255 && r.Memory.ReadByte(0xe03fffff) == 255 && SamePlanes(before, Planes(r.Video)) && r.Memory.ReadByte(0xe0000000) == 0x13);
        });
        foreach (uint bar in new uint[] { 0, 0x00800000, 0xff800000 })
            test("M4 S3 whole eight MiB conflicted resource stays inert BAR " + bar.ToString("X8"), () =>
            {
                var r = new Rig(); r.Memory.Map(0xffff0000, 65536, new RomDevice(new byte[65536]), "Own top ROM");
                if (bar == 0x00800000) r.Memory.Map(0x00f00000, 4096, new RomDevice(new byte[4096]), "Own conflict outside LAW");
                r.Linear(2, bar); byte[][] before = Planes(r.Video); Check(!r.Card.LinearEnabled);
                r.Memory.WriteByte(bar + 0x11, 0xa7); Check(SamePlanes(before, Planes(r.Video)), "Inert resource touched VRAM");
            });
        test("M4 S3 canonical banked chain4 and raw high LFB expose the same literal physical64K region", () =>
        {
            var r = new Rig(); r.Wake(); r.Cr(0x31, 9); r.Cr(0x6a, 1); r.Sr(4, 14); r.Gc(5, 0); r.Gc(6, 5); r.Gc(8, 255);
            foreach (var pair in new[] { (0u,(byte)0x13), (1u,(byte)0x57), (2u,(byte)0x9b), (3u,(byte)0xdf) }) r.Memory.WriteByte(0xa0000 + pair.Item1, pair.Item2);
            byte[][] planes = Planes(r.Video); Check(planes[0][0x4000] == 0x13 && planes[1][0x4000] == 0x57 && planes[2][0x4000] == 0x9b && planes[3][0x4000] == 0xdf);
            r.Linear(); Check(r.Memory.ReadDword(0xe0010000) == 0xdf9b5713);
        });
        test("M4 S3 bank-disabled standard planar VGA uses four actual masks and read planes", () =>
        {
            var r = new Rig(); r.Wake(); r.Cr(0x31, 0); r.Cr(0x6a, 63); r.Sr(4, 6); r.Sr(2, 15); r.Gc(5, 2); r.Gc(6, 5); r.Gc(8, 255);
            r.Memory.WriteByte(0xa1234, 0x0a); r.Gc(5, 0);
            for (int p = 0; p < 4; p++) { r.Gc(4, (byte)p); Check(r.Memory.ReadByte(0xa1234) == ((p & 1) != 0 ? 255 : 0)); }
            byte[][] planes = Planes(r.Video); Check(planes[1][0x1234] == 255 && planes[3][0x1234] == 255 && planes[0][0x1234] == 0 && planes[2][0x1234] == 0);
        });
        test("M4 S3 bank-disabled text odd even returns real characters from shared planes", () =>
        {
            var r = new Rig(); r.Wake(); r.Cr(0x31, 0); r.Cr(0x6a, 63); r.Sr(4, 2); r.Sr(2, 3); r.Gc(5, 0x10); r.Gc(6, 0x0e);
            r.Memory.WriteWord(0xb8000, 0x1e41); r.Memory.WriteWord(0xb8002, 0x2f42);
            Check(r.Video.ReadText().StartsWith("AB", StringComparison.Ordinal)); byte[][] planes = Planes(r.Video);
            Check(planes[0][0] == 0x41 && planes[1][0] == 0x1e && planes[0][1] == 0x42 && planes[1][1] == 0x2f);
        });
        test("M4 S3 independent complete packed pixel oracle covers pitch padding palette and display start", () =>
        {
            var r = new Rig(); r.Linear(); r.Cr(1, 39); r.Cr(7, 0); r.Cr(9, 0); r.Cr(0x12, 199); r.Cr(0x13, 42); r.Cr(0x3a, 0x10); r.Cr(0x50, 0); r.Cr(0x67, 0); r.Cr(12, 0); r.Cr(13, 32);
            r.Card.Write(0x3c8, 0, 1); for (int i = 0; i < 256; i++) { r.Card.Write(0x3c9, (uint)((i * 3 + 7) & 63), 1); r.Card.Write(0x3c9, (uint)((i * 5 + 11) & 63), 1); r.Card.Write(0x3c9, (uint)((i * 7 + 13) & 63), 1); }
            r.Card.Read(0x3da, 1); r.Card.Write(0x3c0, 0x20, 1);
            for (int y = 0; y < 200; y++) for (int x = 0; x < 336; x++) r.Memory.WriteByte(0xe0000080u + (uint)(y * 336 + x), x < 320 ? (byte)((x * 3 + y * 5 + (x >> 2)) & 255) : (byte)0xf1);
            var frame = r.Video.Capture(0); Check(frame.Width == 320 && frame.Height == 200 && frame.ArgbPixels.Length == 64000);
            for (int y = 0; y < 200; y++) for (int x = 0; x < 320; x++) Check(frame.ArgbPixels[y * 320 + x] == Color((x * 3 + y * 5 + (x >> 2)) & 255), "Pixel " + x + "/" + y);
        });
    }
}

