// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Real fixed-aperture CPU word tails, preserved byte/DWORD grouping and full32MiB futures; no native-driver or FIFO-timing acceptance.</summary>
internal static class Windows95S3WordTailTests
{
    /// <summary>Independently selected high VRAM target, separate from the legacy PIX_TRANS endpoint.</summary>
    private const uint High = 0xe0000000, Transfer = 0xae2e8;
    /// <summary>Sixteen asymmetric two-byte MSB-first rows, transmitted as little-endian CPU words.</summary>
    private static readonly byte[][] Rows =
    [
        [0x80, 0x01], [0x0f, 0xf0], [0xa5, 0x5a], [0x3c, 0xc3],
        [0x55, 0xaa], [0x69, 0x96], [0xf0, 0x0f], [0x7e, 0x81],
        [0x18, 0xe7], [0xc6, 0x39], [0x42, 0xbd], [0x24, 0xdb],
        [0x99, 0x66], [0xe1, 0x1e], [0x87, 0x78], [0x5a, 0xa5]
    ];

    /// <summary>Requires an actual byte, pixel, state or instruction-boundary result.</summary>
    private static void Check(bool value, string why = "S3 word-tail invariant failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Separate one-byte fixed target used to prove that an operand spanning two mappings keeps both byte effects.</summary>
    private sealed class ByteTarget : IMemoryDevice
    {
        /// <summary>Actual ordered writes seen only by the separate mapped target.</summary>
        public readonly List<(uint Offset, byte Value)> Writes = [];
        /// <summary>Returns a harmless fixed pulled-up value; no product state is injected.</summary>
        public byte ReadByte(uint offset) => 0xff;
        /// <summary>Records only the actual bus write delivered to this target.</summary>
        public void WriteByte(uint offset, byte value) => Writes.Add((offset, value));
    }

    /// <summary>Original S3 resources and a real protected CPU, optionally belonging to a full factory PC.</summary>
    private sealed class Rig
    {
        /// <summary>Optional full owner used for the complete snapshot continuations.</summary>
        public readonly PcMachine? Machine;
        /// <summary>Actual CPU executing own byte/word/DWORD moffs stores.</summary>
        public readonly X86Cpu Cpu;
        /// <summary>Actual memory bus resolving fixed targets, A20 and dynamic high VRAM.</summary>
        public readonly MemoryBus Memory;
        /// <summary>Actual legacy/enhanced port bus.</summary>
        public readonly IoPortBus Ports;
        /// <summary>Actual common scanout and engine VRAM.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Actual PCI/fixed-aperture/dynamic-high S3 adapter.</summary>
        public readonly S3Trio64 Card;
        /// <summary>Independent second device at the optional first mapping's final byte boundary.</summary>
        public readonly ByteTarget Boundary = new();
        /// <summary>Independently programmed pixel byte count, not read from an active engine cursor.</summary>
        public readonly int Depth;
        /// <summary>Independently programmed graphics pitch in pixels.</summary>
        public readonly int Pitch;
        /// <summary>Fixed externally specified zero/one colors for the chosen mode.</summary>
        public readonly uint Background, Foreground;

        /// <summary>Creates own resources or binds an already restored PC without reprogramming its captured state.</summary>
        public Rig(bool native16 = false, bool splitMapping = false, PcMachine? machine = null, bool configure = true)
        {
            Machine = machine; Depth = native16 ? 2 : 1; Pitch = native16 ? 800 : 640;
            Background = native16 ? 0x03efu : 0x17u; Foreground = native16 ? 0xffffu : 0xe2u;
            if (machine is null)
            {
                Ports = new(); (Cpu, Memory) = ProtectedModeTests.Rig([0x90], io: Ports, model: X86CpuModel.IntelP54cB5Subset);
                Video = new(null, false, true); Card = new(Memory, Video);
                Memory.Map(0xa0000, splitMapping ? 0xe2e9u : 0x20000u, Card, "Own word-tail fixed VGA target");
                if (splitMapping) Memory.Map(Transfer + 1, 1, Boundary, "Own distinct final-byte target");
                Memory.MapDynamic(Card);
                Ports.Map(0x3b0, 0x30, Card); Ports.Map(0x46e8, 1, Card); Ports.Map(0x102, 1, Card); Ports.Map(0x4ae8, 2, Card);
                foreach (var window in S3Trio64.EnginePortWindows) Ports.Map(window.Start, window.Length, Card);
            }
            else { Cpu = machine.Cpu; Memory = machine.Memory; Ports = machine.Ports; Video = machine.Video; Card = machine.S3!; }
            if (!configure) return;
            Card.WriteConfiguration(4, 3, 2); Card.WriteConfiguration(0x10, High, 4);
            Ports.Write(0x46e8, 0x10, 1); Ports.Write(0x102, 1, 1); Ports.Write(0x46e8, 8, 1); Ports.Write(0x3c2, 0x67, 1);
            Cr(0x38, 0x48); Cr(0x39, 0xa5); Sr(8, 6); Sr(0, 3); Sr(1, 1); Sr(2, 15); Sr(4, 14);
            Sr(0x0a, 0x80); Sr(0x15, 0x80); Gc(5, 0); Gc(6, 5); Gc(8, 255);
            Cr(0x40, 1); Cr(0x31, 8); Cr(0x3a, 0x10); Cr(0x50, native16 ? (byte)0x90 : (byte)0x40);
            Cr(0x58, 0x12); Cr(0x67, native16 ? (byte)0x50 : (byte)0);
            Cr(1, native16 ? (byte)99 : (byte)79); Cr(7, 0); Cr(9, 0); Cr(0x12, 31);
            Cr(0x13, native16 ? (byte)200 : (byte)80); W(0x4ae8, 1); Cr(0x53, 0x10);
            W(0x42e8, 0x4000); W(0xaae8, 0xffff); W(0xaee8, 0xffff);
            W(0xa2e8, (ushort)Background); W(0xa6e8, (ushort)Foreground); W(0xbae8, 0x27); W(0xb6e8, 7);
            Mf(1, 0); Mf(2, 0); Mf(3, 1023); Mf(4, Pitch - 1); Mf(14, 0);
            if (machine is not null) Protected(machine);
        }

        /// <summary>Programs an actual indexed CRTC latch.</summary>
        public void Cr(byte index, byte value) { Ports.Write(0x3d4, index, 1); Ports.Write(0x3d5, value, 1); }
        /// <summary>Programs the actual unlocked sequencer.</summary>
        private void Sr(byte index, byte value) { Ports.Write(0x3c4, index, 1); Ports.Write(0x3c5, value, 1); }
        /// <summary>Programs actual ordinary VGA memory controls.</summary>
        private void Gc(byte index, byte value) { Ports.Write(0x3ce, index, 1); Ports.Write(0x3cf, value, 1); }
        /// <summary>Writes one actual enhanced word register.</summary>
        public void W(ushort port, ushort value) => Ports.Write(port, value, 2);
        /// <summary>Writes an actual multifunction selector and its payload.</summary>
        public void Mf(int index, int value) => W(0xbee8, (ushort)(index * 4096 + value));
        /// <summary>Starts the explicitly dimensioned mono CPU command at own physical8/4.</summary>
        public void Begin(int width, int height, ushort command = 0x55b3, bool mono = true)
        {
            Mf(10, mono ? 0x80 : 0); W(0xbae8, mono ? (ushort)0x27 : (ushort)0x47);
            W(0x86e8, 8); W(0x82e8, 4); W(0x96e8, (ushort)(width - 1)); Mf(0, height - 1); W(0x9ae8, command);
            Check(Video.TrioEngineWaiting && Video.TrioEngineUnsupportedCommand is null);
        }

        /// <summary>Executes one real CPU instruction through the full owner when available.</summary>
        private void Step()
        {
            if (Machine is null) Cpu.Step();
            else Check(Machine.RunSteps(1) == 1 && Machine.Fault is null, Machine.Fault ?? "Own owner stopped.");
            Check(!Cpu.TripleFaulted && !Cpu.Halted);
        }
        /// <summary>Uses actual public byte/word/DWORD memory writes or an actual CPU moffs instruction of exactly that width.</summary>
        public void Store(uint address, uint value, int width, bool guest = true)
        {
            Check(width is 1 or 2 or 4);
            if (!guest)
            {
                if (width == 1) Memory.WriteByte(address, (byte)value);
                else if (width == 2) Memory.WriteWord(address, (ushort)value);
                else Memory.WriteDword(address, value);
                return;
            }
            var code = new List<byte>(); if (width == 2) code.Add(0x66);
            code.Add(width == 1 ? (byte)0xb0 : (byte)0xb8);
            for (int i = 0; i < width; i++) code.Add((byte)(value >> (i * 8)));
            if (width == 2) code.Add(0x66);
            code.Add(width == 1 ? (byte)0xa2 : (byte)0xa3); code.AddRange(BitConverter.GetBytes(address));
            code.CopyTo(Memory.Ram, 0x2000); Cpu.Eip = 0x2000; Step(); Step();
            Check(Cpu.Eip == 0x2000u + code.Count && Cpu.GetSegment(Segment.Cs) == 8, "Actual store left the own caller.");
        }
        /// <summary>Returns the independently programmed physical pixel location, without an engine cursor.</summary>
        public uint Address(int x, int y) => High + (uint)(((y + 4) * Pitch + x + 8) * Depth);
        /// <summary>Reads a whole real stored pixel through the normal physical VRAM path.</summary>
        public uint Pixel(int x, int y) => Depth == 1 ? Memory.ReadByte(Address(x, y)) : Memory.ReadWord(Address(x, y));
        /// <summary>Seeds a whole pixel through the actual physical LFB.</summary>
        public void Pixel(int x, int y, uint value)
        { if (Depth == 1) Memory.WriteByte(Address(x, y), (byte)value); else Memory.WriteWord(Address(x, y), (ushort)value); }
    }

    /// <summary>Enters real PE32 on a complete own PC using actual LGDT/LIDT/CR0/far-jump instructions.</summary>
    private static void Protected(PcMachine machine)
    {
        var c = machine.Cpu; var m = machine.Memory; c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000; c.Eflags = 2;
        c.SetRegister(Register.Sp, 0x9000);
        ProtectedModeTests.Descriptor(m, 8, 0, 0xfffff, 0x9a, 12); ProtectedModeTests.Descriptor(m, 16, 0, 0xfffff, 0x92, 12);
        for (int i = 0; i < 256; i++) ProtectedModeTests.Gate(m, (byte)i);
        m.WriteByte(0x6000, 0xf4); m.WriteWord(0x300, 0xff); m.WriteDword(0x302, 0x400);
        m.WriteWord(0x310, 0x7ff); m.WriteDword(0x312, 0x8000);
        new byte[] { 0x0f, 0x01, 0x16, 0, 3, 0x66, 0x0f, 0x01, 0x1e, 0x10, 3,
            0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0 }.CopyTo(m.Ram, 0x1000);
        Check(machine.RunSteps(5) == 5 && c.ProtectedMode && c.Eip == 0x2000 && machine.Fault is null);
        c.SetSegment(Segment.Ss, 16); c.SetSegment(Segment.Ds, 16); c.SetSegment(Segment.Es, 16); c.SetRegister(Register.Sp, 0x18000);
    }

    /// <summary>Provides independent asymmetric guard data, distinct from command cursor state.</summary>
    private static uint SeedValue(Rig r, int x, int y) => r.Depth == 1
        ? (uint)((0x2b + (x + 2) * 17 + (y + 2) * 43) & 255)
        : (uint)((0x3469 + (x + 2) * 197 + (y + 2) * 389) & 65535);
    /// <summary>Seeds all image and surrounding guard pixels through real VRAM writes.</summary>
    private static void Seed(Rig r, int width, int height)
    { for (int y = -1; y <= height; y++) for (int x = -1; x <= width; x++) r.Pixel(x, y, SeedValue(r, x, y)); }
    /// <summary>Checks every pixel and every surrounding guard from fixed external bitmap rows and actual completed extents.</summary>
    private static void Grid(Rig r, int width, int height, byte[][] rows, int doneRows, int doneColumns = 0)
    {
        for (int y = -1; y <= height; y++) for (int x = -1; x <= width; x++)
        {
            uint expected = SeedValue(r, x, y);
            if (x >= 0 && x < width && y >= 0 && y < height && (y < doneRows || y == doneRows && x < doneColumns))
                expected = (rows[y][x / 8] & (128 >> (x & 7))) != 0 ? r.Foreground : r.Background;
            Check(r.Pixel(x, y) == expected, "Stored pixel/guard differs at " + x + "/" + y);
        }
    }
    /// <summary>Packs the externally specified two bitmap bytes as a little-endian CPU word.</summary>
    private static ushort Word(byte[] row) => (ushort)(row[0] | row[1] << 8);
    /// <summary>Reads one explicit raw field only as supplemental transfer-progress evidence.</summary>
    private static T Field<T>(Rig r, string name) => r.Video.SaveState().Fields[name].Deserialize<T>(StateValues.Json)!;
    /// <summary>Captures all explicit engine fields in a real dictionary, avoiding ignored anonymous readonly properties.</summary>
    private static string Engine(Rig r) => JsonSerializer.Serialize(r.Video.SaveState().Fields.Where(p =>
        p.Key.StartsWith("_trioEngine", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value), StateValues.Json);
    /// <summary>Requires true completion and then an actual independent synchronous drawing effect from the following command.</summary>
    private static void FollowingFill(Rig r)
    {
        Check(!r.Video.TrioEngineWaiting && r.Video.TrioEngineUnsupportedCommand is null, "Actual word pixels did not complete the CPU command.");
        r.Pixel(90, 24, 0x69); r.Pixel(89, 24, 0x36); r.Pixel(91, 24, 0x96);
        r.Mf(10, 0); r.W(0xa6e8, 0x5a); r.W(0xbae8, 0x27);
        r.W(0x86e8, 98); r.W(0x82e8, 28); r.W(0x96e8, 0); r.Mf(0, 0); r.W(0x9ae8, 0x40b1);
        Check(!r.Video.TrioEngineWaiting && r.Video.TrioEngineUnsupportedCommand is null &&
            r.Pixel(90, 24) == 0x5a && r.Pixel(89, 24) == 0x36 && r.Pixel(91, 24) == 0x96,
            "Completed mono words did not admit the following real one-pixel fill.");
    }

    /// <summary>Requires the complete explicit PC graph, clock and all32MiB to agree after actual future effects.</summary>
    private static void Equal(PcMachine a, PcMachine b)
    {
        var first = a.CaptureHardwareState(); var second = b.CaptureHardwareState();
        Check(JsonSerializer.Serialize(first.Components, StateValues.Json) == JsonSerializer.Serialize(second.Components, StateValues.Json) &&
            JsonSerializer.Serialize(first.Scheduler, StateValues.Json) == JsonSerializer.Serialize(second.Scheduler, StateValues.Json) &&
            first.MemoryMap == second.MemoryMap && first.PortMap == second.PortMap && first.ParallelSink == second.ParallelSink &&
            first.SerialEcho == second.SerialEcho && first.AdcSource == second.AdcSource && first.Adc16Source == second.Adc16Source &&
            a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Full CPU/RAM/device/scheduler word-tail continuation differs.");
    }

    /// <summary>Preserves a full own PC after an actual completed word row or during a byte/word partial group before a tail.</summary>
    private static void FullFuture(bool partial)
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
        string root = Path.GetFullPath(Path.Combine(parent, "win95-s3-word-tail-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new MachineProfile { Id = MachineProfile.Windows95ProfileId, Name = "Own S3 word-tail continuation",
                Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId,
                OpticalDrive = MachineProfile.AtapiCdRomId };
            using var source = PcMachine.CreateWithDisks(profile, [new MemoryDisk(new byte[128 * 512], "Own word-tail disk"), null, null, null]);
            var a = new Rig(native16: true, machine: source); int width = partial ? 48 : 16, height = partial ? 2 : 3;
            byte[][] rows = partial ? [[0xa5, 0x5a, 0xc3, 0x3c, 0x80, 0x22], [0x0f, 0xf0, 0x69, 0x96, 0x55, 0xaa]]
                : [[0x80, 0x01], [0x5a, 0xa5], [0xc3, 0x3c]];
            Seed(a, width, height); a.Begin(width, height);
            if (partial) { a.Store(Transfer, 0x3cc35aa5, 4); a.Store(Transfer, 0x80, 1); Grid(a, width, height, rows, 0, 32); }
            else { a.Store(Transfer, 0x0180, 2); Grid(a, width, height, rows, 1); }
            Check(a.Video.TrioEngineWaiting);
            string saved = Path.Combine(root, "active.dossav"); SnapshotStore.Capture(source, saved);
            using var restored = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
            var b = new Rig(native16: true, machine: restored, configure: false); Equal(source, restored);
            (uint Value, int Width)[] tail = partial ? [(0x1122u, 2), (0x33u, 1), (0x9669f00fu, 4), (0xaa55u, 2)]
                : [(0xa55au, 2), (0x3cc3u, 2)];
            for (int i = 0; i < tail.Length; i++)
            {
                a.Store(Transfer, tail[i].Value, tail[i].Width); b.Store(Transfer, tail[i].Value, tail[i].Width);
                Equal(source, restored);
                if (partial && i == 0) Check(Field<byte>(a, "_trioEngineInputBytes") == 3 && Field<int>(a, "_trioEnginePixelIndex") == 32);
            }
            Grid(a, width, height, rows, height); Grid(b, width, height, rows, height); Equal(source, restored);
            FollowingFill(a); FollowingFill(b); Equal(source, restored);
        }
        finally
        {
            Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win95-s3-word-tail-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }

    /// <summary>Registers meaningful actual word effects and separate controls for all preserved fallback/group boundaries.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool guest in new[] { false, true }) foreach (bool native16 in new[] { false, true })
            test("M4 Win95 S3 BUS32 SWP1 actual16x16 sixteenWORDs CPU=" + guest + " native16=" + native16, () =>
            {
                var r = new Rig(native16); Seed(r, 16, 16); r.Begin(16, 16);
                for (int y = 0; y < 16; y++)
                { r.Store(Transfer, Word(Rows[y]), 2, guest); Grid(r, 16, 16, Rows, y + 1); Check(r.Video.TrioEngineWaiting == (y != 15)); }
                FollowingFill(r);
            });
        test("M4 Win95 S3 BUS32 SWP1 actualDWORD32pixels plus WORD16pixel tail per48pixel row", () =>
        {
            var r = new Rig(true); byte[][] rows = [[0xa5, 0x5a, 0xc3, 0x3c, 0x80, 0x01], [0x0f, 0xf0, 0x69, 0x96, 0x55, 0xaa]];
            Seed(r, 48, 2); r.Begin(48, 2);
            r.Store(Transfer, 0x3cc35aa5, 4); Grid(r, 48, 2, rows, 0, 32);
            r.Store(Transfer, 0x0180, 2); Grid(r, 48, 2, rows, 1);
            r.Store(Transfer, 0x9669f00f, 4); r.Store(Transfer, 0xaa55, 2); Grid(r, 48, 2, rows, 2); FollowingFill(r);
        });
        foreach (int width in new[] { 1, 33 })
            test("M4 Win95 S3 BUS32 SWP1 exact WORDtail padding does not spill beyond width=" + width, () =>
            {
                var r = new Rig(); byte[][] rows = width == 1 ? [[0x80]] : [[0xa5, 0x5a, 0xc3, 0x3c, 0x80]];
                Seed(r, width, 1); r.Begin(width, 1); if (width == 33) r.Store(Transfer, 0x3cc35aa5, 4);
                r.Store(Transfer, 0xff80, 2); Grid(r, width, 1, rows, 1); FollowingFill(r);
            });
        test("M4 Win95 S3 BUS32 eightDWORDs retain per-row padding and write only eight16pixel rows", () =>
        {
            var r = new Rig(); byte[][] rows = Rows.Where((_, i) => i % 2 == 0).Concat(Rows.Skip(8)).ToArray();
            Seed(r, 16, 16); r.Begin(16, 16);
            for (int i = 0; i < 8; i++) r.Store(Transfer, (uint)(Word(Rows[i * 2]) | (uint)Word(Rows[i * 2 + 1]) << 16), 4);
            Grid(r, 16, 16, rows, 8); Check(r.Video.TrioEngineWaiting && Field<int>(r, "_trioEnginePixelIndex") == 128);
            for (int y = 8; y < 16; y++) r.Store(Transfer, Word(rows[y]), 2);
            Grid(r, 16, 16, rows, 16); FollowingFill(r);
        });
        test("M4 Win95 S3 BUS32 thirtytwoBYTEs remain eight complete groups instead of sixteenWORD tails", () =>
        {
            var r = new Rig(); byte[][] rows = Rows.Where((_, i) => i % 2 == 0).Concat(Rows.Skip(8)).ToArray();
            Seed(r, 16, 16); r.Begin(16, 16);
            foreach (byte value in Rows.SelectMany(row => row)) r.Store(Transfer, value, 1);
            Grid(r, 16, 16, rows, 8); Check(r.Video.TrioEngineWaiting && Field<int>(r, "_trioEnginePixelIndex") == 128);
            for (int y = 8; y < 16; y++) r.Store(Transfer, Word(rows[y]), 2);
            Grid(r, 16, 16, rows, 16); FollowingFill(r);
        });
        test("M4 Win95 S3 SWP0 actualWORDs preserve four-byte high-first grouping", () =>
        {
            var r = new Rig(); byte[][] rows = [[0x56, 0x78], [0x9a, 0xbc]];
            Seed(r, 16, 2); r.Begin(16, 2, 0x45b3); r.Store(Transfer, 0x1234, 2);
            Grid(r, 16, 2, rows, 0); Check(Field<byte>(r, "_trioEngineInputBytes") == 2);
            r.Store(Transfer, 0x5678, 2); Grid(r, 16, 2, rows, 1);
            r.Store(Transfer, 0xdead, 2); r.Store(Transfer, 0x9abc, 2); Grid(r, 16, 2, rows, 2); FollowingFill(r);
        });
        test("M4 Win95 S3 WORDtail never bypasses an actual preceding zero-valued BYTE partial group", () =>
        {
            var r = new Rig(); byte[][] rows = [[0xa5, 0x5a, 0xc3, 0x3c, 0x00, 0x22]];
            Seed(r, 48, 1); r.Begin(48, 1); r.Store(Transfer, 0x3cc35aa5, 4); r.Store(Transfer, 0, 1);
            Check(Field<byte>(r, "_trioEngineInputBytes") == 1 && Field<uint>(r, "_trioEngineInputData") == 0);
            r.Store(Transfer, 0x1122, 2); Grid(r, 48, 1, rows, 0, 32);
            Check(Field<byte>(r, "_trioEngineInputBytes") == 3 && Field<uint>(r, "_trioEngineInputData") == 0x112200);
            r.Store(Transfer, 0x33, 1); Grid(r, 48, 1, rows, 1); FollowingFill(r);
        });
        test("M4 Win95 S3 WORD with seventeen remaining pixels stays incomplete until its fullDWORD group", () =>
        {
            var r = new Rig(); byte[][] rows = [[0x80, 0x01, 0xc3]];
            Seed(r, 17, 1); r.Begin(17, 1); r.Store(Transfer, 0x0180, 2);
            Grid(r, 17, 1, rows, 0); Check(Field<byte>(r, "_trioEngineInputBytes") == 2);
            r.Store(Transfer, 0x3cc3, 2); Grid(r, 17, 1, rows, 1); FollowingFill(r);
        });
        test("M4 Win95 S3 through-plane CPU pixels retain ordinary WORD partials and four-byte grouping", () =>
        {
            var r = new Rig(); Seed(r, 4, 1); r.Begin(4, 1, 0x55b1, false);
            r.Store(Transfer, 0x2211, 2); Check(r.Video.TrioEngineWaiting && Field<byte>(r, "_trioEngineInputBytes") == 2);
            for (int x = -1; x <= 4; x++) Check(r.Pixel(x, 0) == SeedValue(r, x, 0));
            r.Store(Transfer, 0x4433, 2); Check(!r.Video.TrioEngineWaiting);
            for (int x = -1; x <= 4; x++) Check(r.Pixel(x, 0) == (x is >= 0 and < 4 ? (uint)(0x11 * (x + 1)) : SeedValue(r, x, 0)));
            FollowingFill(r);
        });
        test("M4 Win95 S3 physically odd WORD retains ordered BYTE fallbacks", () =>
        {
            var r = new Rig(); byte[][] rows = [[0x80, 0x01]];
            Seed(r, 16, 1); r.Begin(16, 1); r.Store(Transfer + 1, 0x0180, 2);
            Grid(r, 16, 1, rows, 0); Check(Field<byte>(r, "_trioEngineInputBytes") == 2);
            r.Store(Transfer + 1, 0x3cc3, 2); Grid(r, 16, 1, rows, 1); FollowingFill(r);
        });
        test("M4 Win95 S3 upper standard PIX_TRANS WORD lane retains ordered BYTE partials", () =>
        {
            var r = new Rig(); byte[][] rows = [[0x80, 0x01]];
            Seed(r, 16, 1); r.Begin(16, 1); r.Store(Transfer + 2, 0x0180, 2);
            Grid(r, 16, 1, rows, 0); Check(Field<byte>(r, "_trioEngineInputBytes") == 2 && Field<uint>(r, "_trioEngineInputData") == 0x0180);
            r.Store(Transfer + 2, 0x3cc3, 2); Grid(r, 16, 1, rows, 1); FollowingFill(r);
        });
        test("M4 Win95 S3 aligned lower bulk WORDs share exact tail completion with standard PIX_TRANS", () =>
        {
            var r = new Rig(true); byte[][] rows = [[0x80, 0x01], [0x5a, 0xa5]];
            Seed(r, 16, 2); r.Begin(16, 2); r.Store(0xa0000, 0x0180, 2);
            Grid(r, 16, 2, rows, 1); r.Store(Transfer, 0xa55a, 2); Grid(r, 16, 2, rows, 2); FollowingFill(r);
        });
        test("M4 Win95 S3 WORD across two fixed mappings performs each real BYTE effect once", () =>
        {
            var r = new Rig(splitMapping: true); Seed(r, 16, 1); r.Begin(16, 1); r.Store(Transfer, 0x0180, 2);
            Grid(r, 16, 1, [[0x80, 0x01]], 0);
            Check(r.Video.TrioEngineWaiting && Field<byte>(r, "_trioEngineInputBytes") == 1 &&
                Field<uint>(r, "_trioEngineInputData") == 0x80 && r.Boundary.Writes.SequenceEqual(new[] { (0u, (byte)1) }),
                "A fixed mapping boundary was consumed as a whole S3 WORD or duplicated a byte.");
        });
        foreach (string gate in new[] { "MSE", "video", "MMIO", "CR40" })
            test("M4 Win95 S3 WORDtail retains actual target gate " + gate, () =>
            {
                var r = new Rig(); Seed(r, 16, 1); r.Begin(16, 1);
                Check(r.Memory.ReadByte(0xa9ae8) == 0 && Field<bool>(r, "_trioEngineReadValid"));
                string before = Engine(r);
                if (gate == "MSE") r.Card.WriteConfiguration(4, 1, 2);
                else if (gate == "video") r.Ports.Write(0x46e8, 0, 1);
                else r.Cr(gate == "MMIO" ? (byte)0x53 : (byte)0x40, 0);
                r.Store(Transfer, 0x0180, 2); Check(Engine(r) == before, "Disabled word target changed engine progress.");
                if (gate == "MSE") r.Card.WriteConfiguration(4, 3, 2);
                else if (gate == "video") r.Ports.Write(0x46e8, 8, 1);
                else r.Cr(gate == "MMIO" ? (byte)0x53 : (byte)0x40, gate == "MMIO" ? (byte)0x10 : (byte)1);
                Grid(r, 16, 1, [[0x80, 0x01]], 0); r.Store(Transfer, 0x0180, 2);
                Grid(r, 16, 1, [[0x80, 0x01]], 1); FollowingFill(r);
            });
        test("M4 Win95 S3 WORDtail uses MSE independently from a cleared PCI IOSE", () =>
        {
            var r = new Rig(); Seed(r, 16, 1); r.Begin(16, 1); r.Card.WriteConfiguration(4, 2, 2);
            r.Store(Transfer, 0x0180, 2); r.Card.WriteConfiguration(4, 3, 2); Grid(r, 16, 1, [[0x80, 0x01]], 1); FollowingFill(r);
        });
        test("M4 Win95 S3 CPU WORD accepts the actual fixed PIX_TRANS target after physical A20 translation", () =>
        {
            var r = new Rig(); Seed(r, 16, 1); r.Begin(16, 1); r.Memory.A20Enabled = false;
            r.Store(Transfer | 0x100000, 0x0180, 2); Grid(r, 16, 1, [[0x80, 0x01]], 1); FollowingFill(r);
        });
        test("M4 Win95 S3 highLFB WORD with PIX_TRANS-like offset stays ordinary VRAM", () =>
        {
            var r = new Rig(); Seed(r, 16, 1); r.Begin(16, 1); string before = Engine(r);
            r.Store(High + 0xe2e8, 0x0180, 2); Check(Engine(r) == before && r.Memory.ReadWord(High + 0xe2e8) == 0x0180);
            Grid(r, 16, 1, [[0x80, 0x01]], 0); r.Store(Transfer, 0x0180, 2); Grid(r, 16, 1, [[0x80, 0x01]], 1); FollowingFill(r);
        });
        test("M4 Win95 S3 neighboring aligned WORDs never feed the PIX_TRANS tail", () =>
        {
            var r = new Rig(); Seed(r, 16, 1); r.Begin(16, 1); string before = Engine(r);
            r.Store(Transfer - 2, 0x0180, 2); r.Store(Transfer + 4, 0x0180, 2);
            Check(Engine(r) == before); Grid(r, 16, 1, [[0x80, 0x01]], 0);
            r.Store(Transfer, 0x0180, 2); Grid(r, 16, 1, [[0x80, 0x01]], 1); FollowingFill(r);
        });
        foreach (bool partial in new[] { false, true })
            test("M4 Win95 S3 WORDtail full32MiB actual SnapshotStore continuation partialBYTE=" + partial, () => FullFuture(partial));
    }
}
