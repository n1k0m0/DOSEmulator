// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual standard-MMIO PIX_TRANS bus lanes, pixel completion and preserved engine futures; not native-driver acceptance.</summary>
internal static class Windows95S3MmioPixelTransferTests
{
    private const uint High = 0xe0000000, Transfer = 0xae2e8;
    private static readonly byte[] Image = [0x80, 0x01, 0xa5, 0x5a, 0xff, 0, 0x3c, 0xc3];

    /// <summary>Requires a real bus, CPU, pixel, transfer-state or continuation result.</summary>
    private static void Check(bool value, string why = "S3 standard-MMIO pixel transfer invariant failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>One physical CPU/memory transaction, independent from the command's transfer-group size.</summary>
    private sealed record Store(uint Address, uint Value, int Width);

    /// <summary>Actual VGA/PCI/fixed-memory/dynamic-LFB resources, optionally supplied by a complete factory PC.</summary>
    private sealed class Rig
    {
        /// <summary>Optional complete owner graph used only by the full-container control.</summary>
        public readonly PcMachine? Machine;
        /// <summary>Actual flat protected CPU executing the own moffs store/read instructions.</summary>
        public readonly X86Cpu Cpu;
        /// <summary>Actual physical bus; fixed mappings resolve before any ordinary RAM path.</summary>
        public readonly MemoryBus Memory;
        /// <summary>Actual mapped legacy and enhanced I/O endpoints.</summary>
        public readonly IoPortBus Ports;
        /// <summary>Actual shared two-MiB video/engine state.</summary>
        public readonly VgaAdapter Video;
        /// <summary>Actual original Trio64 PCI and memory target.</summary>
        public readonly S3Trio64 Card;

        /// <summary>Builds a private component graph or borrows an already imported complete PC without reprogramming it.</summary>
        public Rig(PcMachine? machine = null, bool configure = true)
        {
            Machine = machine;
            if (machine is null)
            {
                Ports = new();
                (Cpu, Memory) = ProtectedModeTests.Rig([0x90], io: Ports, model: X86CpuModel.IntelP54cB5Subset);
                Video = new(null, false, true); Card = new(Memory, Video);
                Memory.Map(0xa0000, 0x20000, Card, "Own standard-MMIO PIX_TRANS"); Memory.MapDynamic(Card);
                Ports.Map(0x3b0, 0x30, Card); Ports.Map(0x46e8, 1, Card); Ports.Map(0x102, 1, Card); Ports.Map(0x4ae8, 2, Card);
                foreach (var window in S3Trio64.EnginePortWindows) Ports.Map(window.Start, window.Length, Card);
            }
            else
            { Cpu = machine.Cpu; Memory = machine.Memory; Ports = machine.Ports; Video = machine.Video; Card = machine.S3!; }
            if (!configure) return;
            Card.WriteConfiguration(4, 3, 2); Card.WriteConfiguration(0x10, High, 4);
            Ports.Write(0x46e8, 0x10, 1); Ports.Write(0x102, 1, 1); Ports.Write(0x46e8, 8, 1); Ports.Write(0x3c2, 0x67, 1);
            Cr(0x38, 0x48); Cr(0x39, 0xa5); Sr(8, 6); Sr(0, 3); Sr(1, 1); Sr(2, 15); Sr(4, 14);
            Sr(0x0a, 0x80); Sr(0x15, 0x80); Gc(5, 0); Gc(6, 5); Gc(8, 255);
            Cr(0x40, 1); Cr(0x31, 8); Cr(0x3a, 0x10); Cr(0x50, 0x40); Cr(0x58, 0x12); Cr(0x67, 0);
            Cr(1, 79); Cr(7, 0); Cr(9, 0); Cr(0x12, 31); Cr(0x13, 80); W(0x4ae8, 1); Cr(0x53, 0x10);
            W(0x42e8, 0x4000); W(0xaae8, 0xffff); W(0xaee8, 0xffff); W(0xbae8, 0x27); W(0xb6e8, 7);
            Mf(1, 0); Mf(2, 0); Mf(3, 4095); Mf(4, 4095); Mf(14, 0);
            if (machine is not null) Protected(machine);
        }

        /// <summary>Writes only the original actual indexed CRTC ports.</summary>
        public void Cr(byte index, byte value) { Ports.Write(0x3d4, index, 1); Ports.Write(0x3d5, value, 1); }
        /// <summary>Writes actual sequencer latches after their unlock.</summary>
        private void Sr(byte index, byte value) { Ports.Write(0x3c4, index, 1); Ports.Write(0x3c5, value, 1); }
        /// <summary>Writes the ordinary VGA memory controls through their real ports.</summary>
        private void Gc(byte index, byte value) { Ports.Write(0x3ce, index, 1); Ports.Write(0x3cf, value, 1); }
        /// <summary>Issues an actual word-wide enhanced-engine register transaction.</summary>
        public void W(ushort port, ushort value) => Ports.Write(port, value, 2);
        /// <summary>Issues an actual multifunction selector/value transaction.</summary>
        public void Mf(int index, int value) => W(0xbee8, (ushort)(index * 4096 + value));
        /// <summary>Reads the actual ordinary GP_STAT word without consuming any pixel-data endpoint.</summary>
        public ushort Status() => (ushort)Ports.Read(0x9ae8, 2);
        /// <summary>Sets actual current/source, destination and minus-one rectangle dimensions.</summary>
        public void Rectangle(int x, int y, int width, int height, int dx = 0, int dy = 0)
        {
            W(0x86e8, (ushort)x); W(0x82e8, (ushort)y); W(0x8ee8, (ushort)dx); W(0x8ae8, (ushort)dy);
            W(0x96e8, (ushort)(width - 1)); Mf(0, height - 1);
        }
        /// <summary>Starts the real8x8 CPU monochrome transfer, with command-latched EP80 and distinct zero/one source pixels.</summary>
        public void Begin(ushort command = 0x51b3)
        {
            W(0xa6e8, 0xff); W(0xa2e8, 0); W(0xbae8, 0x27); W(0xb6e8, 7); Mf(10, 0x80);
            Rectangle(0, 0, 8, 8); W(0x9ae8, command);
            Check(Video.TrioEngineWaiting && Video.TrioEngineUnsupportedCommand is null && Status() == 0x600);
        }
        /// <summary>Steps the real CPU through this component graph or the full emulation owner, with no injected I/O or completion.</summary>
        private void Step()
        {
            if (Machine is null) Cpu.Step();
            else Check(Machine.RunSteps(1) == 1 && Machine.Fault is null, Machine.Fault ?? "Own owner stopped.");
            Check(!Cpu.TripleFaulted);
        }
        /// <summary>Executes exactly the supplied own instructions and rejects a CPU fault/handler detour as success.</summary>
        private void Code(byte[] code, int instructions)
        {
            code.CopyTo(Memory.Ram, 0x2000); Cpu.Eip = 0x2000;
            for (int i = 0; i < instructions; i++) Step();
            Check(Cpu.Eip == 0x2000 + code.Length && Cpu.GetSegment(Segment.Cs) == 8 && !Cpu.Halted,
                "Actual own CPU memory caller faulted or stopped: " + Cpu.Position);
        }
        /// <summary>Executes actual byte/word/dword moffs stores, preserving little-endian bus lanes across MMIO boundaries.</summary>
        public void Guest(params Store[] stores)
        {
            var code = new List<byte>();
            foreach (var store in stores)
            {
                Check(store.Width is 1 or 2 or 4);
                if (store.Width == 2) code.Add(0x66);
                code.Add(store.Width == 1 ? (byte)0xb0 : (byte)0xb8);
                for (int i = 0; i < store.Width; i++) code.Add((byte)(store.Value >> (i * 8)));
                if (store.Width == 2) code.Add(0x66);
                code.Add(store.Width == 1 ? (byte)0xa2 : (byte)0xa3);
                code.AddRange(BitConverter.GetBytes(store.Address));
            }
            Code(code.ToArray(), stores.Length * 2);
        }
        /// <summary>Reads the actual write-only endpoint through a real CPU load with independently seeded register upper halves.</summary>
        public uint GuestRead(uint address, int width)
        {
            var code = new List<byte> { 0xb8, 0x78, 0x56, 0x34, 0x12 };
            if (width == 2) code.Add(0x66);
            code.Add(width == 1 ? (byte)0xa0 : (byte)0xa1); code.AddRange(BitConverter.GetBytes(address));
            Code(code.ToArray(), 2); return Cpu.GetRegister(Register.Ax);
        }
        /// <summary>Writes the same data through ordinary public physical-bus operations as a separate non-CPU control.</summary>
        public void Bus(Store store)
        {
            if (store.Width == 1) Memory.WriteByte(store.Address, (byte)store.Value);
            else if (store.Width == 2) Memory.WriteWord(store.Address, (ushort)store.Value);
            else Memory.WriteDword(store.Address, store.Value);
        }
    }

    /// <summary>Enters actual PE32 through the existing CPU decoder in a complete fresh PC, without replacing firmware services.</summary>
    private static void Protected(PcMachine machine)
    {
        var c = machine.Cpu; var m = machine.Memory; c.Reset(); c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000;
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

    /// <summary>Packs fixed independent source rows into the actual little-endian transaction width, not command groups.</summary>
    private static Store[] Packed(uint address, int width, byte[]? bytes = null)
    {
        bytes ??= Image; Check(bytes.Length % width == 0); var stores = new List<Store>();
        for (int i = 0; i < bytes.Length; i += width)
        {
            uint value = 0; for (int j = 0; j < width; j++) value += (uint)bytes[i + j] << (j * 8);
            stores.Add(new(address, value, width));
        }
        return stores.ToArray();
    }

    /// <summary>Captures every populated raw plane byte without introducing VGA read-latch effects.</summary>
    private static byte[][] Planes(Rig rig) => rig.Video.SaveState().Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
    /// <summary>Compares all physical VRAM, not only the selected rectangle or a status flag.</summary>
    private static bool Same(byte[][] a, byte[][] b) => a.Zip(b).All(p => p.First.AsSpan().SequenceEqual(p.Second));
    /// <summary>Captures only actual command progress; ordinary write-only read-latches are allowed their documented read effects.</summary>
    private static string Progress(Rig rig) => JsonSerializer.Serialize(rig.Video.SaveState().Fields.Where(p =>
        p.Key.StartsWith("_trioEngineActive", StringComparison.Ordinal) || p.Key.StartsWith("_trioEngineInput", StringComparison.Ordinal) ||
        p.Key.StartsWith("_trioEnginePixel", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value), StateValues.Json);
    /// <summary>Reads the independently specified640-byte-pitch indexed pixel from the actual high LFB decoder.</summary>
    private static byte Pixel(Rig rig, int x, int y) => rig.Memory.ReadByte(High + (uint)(y * 640 + x));
    /// <summary>Seeds the whole32x32 source/destination/guard region through actual physical framebuffer writes.</summary>
    private static void Seed(Rig rig)
    { for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) rig.Memory.WriteByte(High + (uint)(y * 640 + x), 0x69); }

    /// <summary>Checks every real pixel against fixed MSB-first row vectors and unchanged guards, with no engine-state-derived expectation.</summary>
    private static void Grid(Rig rig, int rows, bool copied = false)
    {
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            byte expected = 0x69;
            if (x < 8 && y < rows) expected = (Image[y] & (128 >> x)) != 0 ? (byte)0xff : (byte)0;
            if (copied && x is >= 16 and < 24 && y is >= 16 and < 24)
                expected = (Image[y - 16] & (128 >> (x - 16))) != 0 ? (byte)0xe7 : (byte)0x13;
            Check(Pixel(rig, x, y) == expected, "Actual indexed pixel " + x + "/" + y + " differs.");
        }
    }

    /// <summary>Requires completed CPU input before a real EP=C0 pattern command, then checks its actual foreground/background pixels.</summary>
    private static void FollowPattern(Rig rig)
    {
        Grid(rig, 8);
        Check(!rig.Video.TrioEngineWaiting && rig.Video.TrioEngineUnsupportedCommand is null && rig.Status() == 0x400,
            "Eight actual mono bytes did not complete51B3 before the next command.");
        rig.W(0xa6e8, 0xe7); rig.W(0xa2e8, 0x13); rig.Mf(10, 0xc0);
        rig.Rectangle(0, 0, 8, 8, 16, 16); rig.W(0x9ae8, 0xe0b1);
        Check(!rig.Video.TrioEngineWaiting && rig.Video.TrioEngineUnsupportedCommand is null && rig.Status() == 0x400,
            "The completed transfer left the following genuine E0B1 command blocked.");
        Grid(rig, 8, true);
    }

    /// <summary>Restores all CPU/video/card/memory state and every own RAM byte into a separate equal graph.</summary>
    private static Rig Copy(Rig source)
    {
        var result = new Rig();
        source.Memory.Ram.CopyTo(result.Memory.Ram, 0); result.Memory.LoadState(source.Memory.SaveState());
        result.Video.LoadState(source.Video.SaveState()); result.Card.LoadState(source.Card.SaveState()); result.Cpu.LoadState(source.Cpu.SaveState());
        return result;
    }
    /// <summary>Requires all explicit device/CPU fields and RAM to agree after actual future transfers.</summary>
    private static void Equal(Rig a, Rig b)
    {
        string first = JsonSerializer.Serialize(new Dictionary<string, StateBlock>
        { ["cpu"] = a.Cpu.SaveState(), ["video"] = a.Video.SaveState(), ["card"] = a.Card.SaveState(), ["memory"] = a.Memory.SaveState() }, StateValues.Json);
        string second = JsonSerializer.Serialize(new Dictionary<string, StateBlock>
        { ["cpu"] = b.Cpu.SaveState(), ["video"] = b.Video.SaveState(), ["card"] = b.Card.SaveState(), ["memory"] = b.Memory.SaveState() }, StateValues.Json);
        Check(first == second && a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Raw imported transfer future differs.");
    }

    /// <summary>Registers the bounded actual bus/CPU/MMIO routing and original8x8 transfer/next-command cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool guest in new[] { false, true }) foreach (int width in new[] { 1, 2, 4 })
            test($"M4 Win95 S3 standard MMIO actual8x8 then E0B1 CPU={guest} width={width}", () =>
            {
                var rig = new Rig(); Seed(rig); rig.Begin();
                foreach (var store in Packed(Transfer, width)) { if (guest) rig.Guest(store); else rig.Bus(store); }
                FollowPattern(rig);
            });
        for (int lane = 0; lane < 4; lane++)
        {
            int selected = lane;
            test("M4 Win95 S3 standard MMIO each actual pixel byte lane " + selected, () =>
            {
                var rig = new Rig(); Seed(rig); rig.Begin(); rig.Guest(Packed(Transfer + (uint)selected, 1)); FollowPattern(rig);
            });
        }
        test("M4 Win95 S3 standard MMIO unaligned CPU widths feed only actual valid lanes", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin();
            rig.Guest(new Store(Transfer - 1, 0x80ee, 2), new Store(Transfer + 1, 0xee5aa501, 4));
            Grid(rig, 4); Check(rig.Video.TrioEngineWaiting);
            rig.Guest(Packed(Transfer, 1, Image[4..])); FollowPattern(rig);
        });
        test("M4 Win95 S3 standard MMIO adjacent bytes never feed the active command", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin(); string before = Progress(rig); var pixels = Planes(rig);
            rig.Guest(new Store(Transfer - 1, 0xff, 1), new Store(Transfer + 4, 0xff, 1));
            Check(Progress(rig) == before && Same(pixels, Planes(rig)) && rig.Status() == 0x600);
            rig.Guest(Packed(Transfer, 1)); FollowPattern(rig);
        });
        foreach (int width in new[] { 1, 2, 4 })
            test("M4 Win95 S3 standard MMIO write-only CPU reads float and do not feed width=" + width, () =>
            {
                var rig = new Rig(); Seed(rig); rig.Begin(); string before = Progress(rig); var pixels = Planes(rig);
                uint value = rig.GuestRead(Transfer, width);
                Check(value == (width == 1 ? 0x123456ffu : width == 2 ? 0x1234ffffu : uint.MaxValue) &&
                    Progress(rig) == before && Same(pixels, Planes(rig)) && rig.Video.TrioEngineWaiting);
                rig.Guest(Packed(Transfer, 1)); FollowPattern(rig);
            });
        foreach (string gate in new[] { "MSE", "video", "MMIO", "CR40" })
            test("M4 Win95 S3 standard MMIO closed target gate retains CPU transfer " + gate, () =>
            {
                var rig = new Rig(); Seed(rig); rig.Begin(); string before = Progress(rig); var pixels = Planes(rig);
                if (gate == "MSE") rig.Card.WriteConfiguration(4, 1, 2);
                else if (gate == "video") rig.Ports.Write(0x46e8, 0, 1);
                else rig.Cr(gate == "MMIO" ? (byte)0x53 : (byte)0x40, 0);
                rig.Guest(Packed(Transfer, 4));
                Check(Progress(rig) == before && Same(pixels, Planes(rig)), "A disabled target consumed CPU data.");
                if (gate == "MSE") rig.Card.WriteConfiguration(4, 3, 2);
                else if (gate == "video") rig.Ports.Write(0x46e8, 8, 1);
                else rig.Cr(gate == "MMIO" ? (byte)0x53 : (byte)0x40, gate == "MMIO" ? (byte)0x10 : (byte)1);
                rig.Guest(Packed(Transfer, 1)); FollowPattern(rig);
            });
        test("M4 Win95 S3 standard MMIO remains a memory target when PCI IOSE is clear", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin(); rig.Card.WriteConfiguration(4, 2, 2);
            rig.Guest(Packed(Transfer, 4)); Check(!rig.Video.TrioEngineWaiting);
            rig.Card.WriteConfiguration(4, 3, 2); FollowPattern(rig);
        });
        test("M4 Win95 S3 standard MMIO ADV20 enables the same endpoint without CR53", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin(); rig.Cr(0x53, 0); rig.W(0x4ae8, 0x21);
            rig.Guest(Packed(Transfer, 2)); FollowPattern(rig);
        });
        test("M4 Win95 S3 bulk A0000 transfer remains complete and shares future standard MMIO", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin(); rig.Guest(Packed(0xa0000, 4));
            FollowPattern(rig);
        });
        test("M4 Win95 S3 high LFB with PIX_TRANS-like offset writes VRAM without feeding MMIO", () =>
        {
            var rig = new Rig(); Seed(rig); rig.Begin(); string before = Progress(rig);
            rig.Guest(new Store(High + 0xe2e8, 0x5a, 1));
            Check(Progress(rig) == before && rig.Memory.ReadByte(High + 0xe2e8) == 0x5a);
            rig.Guest(Packed(Transfer, 1)); FollowPattern(rig); Check(rig.Memory.ReadByte(High + 0xe2e8) == 0x5a);
        });
        foreach (bool partialGroup in new[] { false, true })
            test("M4 Win95 S3 actual standard MMIO partial raw import future group=" + partialGroup, () =>
            {
                var rig = new Rig(); Seed(rig); rig.Begin(partialGroup ? (ushort)0x53b3 : (ushort)0x51b3);
                byte[] data = partialGroup ? Image.SelectMany(value => new byte[] { value, 0x69 }).ToArray() : Image;
                int prefix = partialGroup ? 1 : 3;
                rig.Guest(Packed(Transfer, 1, data[..prefix])); Grid(rig, partialGroup ? 0 : 3);
                Check(rig.Video.TrioEngineWaiting); var restored = Copy(rig); Equal(rig, restored);
                foreach (var store in Packed(Transfer, 1, data[prefix..]))
                { rig.Guest(store); restored.Guest(store); Equal(rig, restored); }
                FollowPattern(rig); FollowPattern(restored); Equal(rig, restored);
            });
        test("M4 Win95 S3 standard MMIO equality rejects independent CPU engine VRAM PCI and A20 differences", () =>
        {
            var source = new Rig(); Seed(source); source.Begin();
            foreach (string difference in new[] { "CPU", "engine", "VRAM", "PCI", "A20" })
            {
                var changed = Copy(source); Equal(source, changed);
                switch (difference)
                {
                    case "CPU": changed.Cpu.SetRegister(Register.Ax, changed.Cpu.GetRegister(Register.Ax) ^ 0x10000u); break;
                    case "engine": changed.W(0xa6e8, 0x59); break;
                    case "VRAM": changed.Memory.WriteByte(High + 0x1234, 0x39); break;
                    case "PCI": changed.Card.WriteConfiguration(4, 2, 2); break;
                    case "A20": changed.Memory.A20Enabled = !changed.Memory.A20Enabled; break;
                }
                Check(source.Memory.Ram.AsSpan().SequenceEqual(changed.Memory.Ram), "Equality control changed ordinary RAM.");
                bool rejected = false;
                try { Equal(source, changed); }
                catch (InvalidOperationException error) when (error.Message == "Raw imported transfer future differs.") { rejected = true; }
                Check(rejected, "Equality lost an actual " + difference + " state difference; the state serializer may be empty.");
            }
        });
        test("M4 Win95 S3 standard MMIO full own32MiB SnapshotStore partial transfer continuation", FullContainer);
        foreach (bool guest in new[] { false, true })
            test("M4 Win95 S3 native16 standard MMIO eight55AA bytes then original XOR PatBlt CPU=" + guest, () => Native16(guest));
    }

    /// <summary>Returns an independent asymmetric RGB565 guard word, unrelated to the supplied pattern colors.</summary>
    private static ushort NativeSeed(int x, int y) => (ushort)(0x2469 + (x + 2) * 197 + y * 389);

    /// <summary>Computes a documented800-pixel/two-byte physical LFB address, independently from engine internal cursors.</summary>
    private static uint NativeAddress(int x, int y) => High + (uint)((y * 800 + x) * 2);

    /// <summary>Replays the bound16bpp source/command/mask sequence with independent whole-pixel, guard, XOR and RGB565 scanout oracles.</summary>
    private static void Native16(bool guest)
    {
        var rig = new Rig();
        rig.Cr(0x50, 0x90); rig.Cr(0x67, 0x50); rig.Cr(1, 99); rig.Cr(0x13, 200);
        for (int y = 655; y <= 664; y++) for (int x = -1; x <= 8; x++)
            rig.Memory.WriteWord(NativeAddress(x, y), NativeSeed(x, y));
        for (int y = 37; y <= 39; y++) for (int x = 19; x <= 78; x++)
            rig.Memory.WriteWord(NativeAddress(x, y), NativeSeed(x, y));
        rig.W(0xa2e8, 0x03ef); rig.W(0xa6e8, 0xffff); rig.W(0xaae8, 0xffff); rig.W(0xaee8, 0xffff);
        rig.W(0xbae8, 0x27); rig.W(0xb6e8, 7); rig.Mf(10, 0x80);
        rig.Rectangle(0, 656, 8, 8); rig.W(0x9ae8, 0x51b3);
        Check(rig.Video.TrioEngineWaiting && rig.Status() == 0x600);
        byte[] rows = [0x55, 0xaa, 0x55, 0xaa, 0x55, 0xaa, 0x55, 0xaa];
        foreach (var store in Packed(Transfer, 1, rows))
        { if (guest) rig.Guest(store); else rig.Bus(store); }
        Check(!rig.Video.TrioEngineWaiting && rig.Video.TrioEngineUnsupportedCommand is null && rig.Status() == 0x400,
            "Native16 eight actual byte stores left51B3 outstanding.");
        for (int y = 655; y <= 664; y++) for (int x = -1; x <= 8; x++)
        {
            ushort expected = NativeSeed(x, y);
            if (x is >= 0 and < 8 && y is >= 656 and < 664)
                expected = (rows[y - 656] & (128 >> x)) != 0 ? (ushort)0xffff : (ushort)0x03ef;
            Check(rig.Memory.ReadWord(NativeAddress(x, y)) == expected,
                "Native16 source/guard word differs at " + x + "/" + y);
        }
        // The original following E0B1 changes the current colors/mixes/EP, not the already stored source bitmap.
        rig.W(0xa2e8, 0); rig.W(0xa6e8, 0xffff); rig.W(0xb2e8, 0);
        rig.W(0xbae8, 0x25); rig.W(0xb6e8, 5); rig.Mf(10, 0xc0);
        rig.Mf(1, 0); rig.Mf(2, 0); rig.Mf(3, 1309); rig.Mf(4, 799);
        rig.Rectangle(0, 656, 58, 1, 20, 38); rig.W(0x9ae8, 0xe0b1);
        Check(!rig.Video.TrioEngineWaiting && rig.Video.TrioEngineUnsupportedCommand is null && rig.Status() == 0x400,
            "Native16 completed source still blocked original E0B1.");
        for (int y = 37; y <= 39; y++) for (int x = 19; x <= 78; x++)
        {
            ushort expected = NativeSeed(x, y);
            if (y == 38 && x is >= 20 and < 78 && (rows[6] & (128 >> (x & 7))) != 0)
                expected = (ushort)(expected ^ 0xffff);
            Check(rig.Memory.ReadWord(NativeAddress(x, y)) == expected,
                "Native16 globally aligned XOR/mask/guard word differs at " + x + "/" + y);
        }
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            Check(rig.Memory.ReadWord(NativeAddress(x, 656 + y)) ==
                ((rows[y] & (128 >> x)) != 0 ? 0xffff : 0x03ef), "Native16 PatBlt changed its source bitmap.");

        // View the physical offscreen source through real CRTC start/pitch and original RGB565 scanout.
        uint start = (uint)(656 * 800 * 2 / 4);
        rig.Cr(12, (byte)(start >> 8)); rig.Cr(13, (byte)start); rig.Cr(0x69, (byte)(start >> 16));
        rig.Ports.Read(0x3da, 1); rig.Ports.Write(0x3c0, 0x20, 1);
        var frame = rig.Video.Capture(0);
        Check(frame.Width == 800 && frame.Height == 32 && !rig.Video.Indexed256,
            "Native16 independent CRTC/RGB565 scanout layout differs.");
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            Check(frame.ArgbPixels[y * 800 + x] == ((rows[y] & (128 >> x)) != 0 ? 0xffffffffu : 0xff007d7bu),
                "Native16 stored RGB565 pixel did not produce the independent expected RGB value.");
    }

    /// <summary>Captures and restores a real complete private PC after standard-MMIO pixels and compares every later owner step.</summary>
    private static void FullContainer()
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
        string root = Path.GetFullPath(Path.Combine(parent, "win95-mmio-pix-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new MachineProfile { Id = MachineProfile.S3Trio64ProfileId, Name = "Own standard-MMIO continuation",
                Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId };
            using var machine = PcMachine.CreateWithDisks(profile, [new MemoryDisk(new byte[128 * 512], "Own MMIO snapshot disk"), null, null, null]);
            var source = new Rig(machine); Seed(source); source.Begin(); source.Guest(Packed(Transfer, 1, Image[..3])); Grid(source, 3);
            string saved = Path.Combine(root, "active.dossav"); SnapshotStore.Capture(machine, saved);
            using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch")); var restored = new Rig(resumed, false);
            Equal(source, restored);
            foreach (var store in Packed(Transfer, 1, Image[3..]))
            {
                source.Guest(store); restored.Guest(store); Equal(source, restored);
                Check(JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json) ==
                    JsonSerializer.Serialize(resumed.CaptureHardwareState(), StateValues.Json), "Full actual owner/scheduler state differs.");
            }
            FollowPattern(source); FollowPattern(restored); Equal(source, restored);
        }
        finally
        {
            Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win95-mmio-pix-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }
}
