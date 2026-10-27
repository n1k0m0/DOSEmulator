// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Independent byte-bus, cached-translation and REP oracles for chipset-controlled RAM decoding.</summary>
internal static class MemoryDecodeTests
{
    private const int Bytes = 2 * 1048576;
    /// <summary>Requires an observed result rather than an implementation-specific branch choice.</summary>
    private static void Check(bool value, string why = "RAM decode contract failed.") { if (!value) throw new Exception(why); }
    /// <summary>Recognizes validation failures without treating an unrelated access violation as success.</summary>
    private static void Invalid(Action action)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; }
        throw new Exception("Invalid RAM decode was accepted.");
    }
    private sealed class Device : IMemoryDevice
    {
        public readonly List<string> Events = [];
        private readonly Dictionary<uint, byte> _bytes = [];
        /// <summary>Supplies deterministic peripheral bytes and records exactly the requested byte transfer.</summary>
        public byte ReadByte(uint offset) { Events.Add("R" + offset); return _bytes.GetValueOrDefault(offset, (byte)(offset * 17 + 5)); }
        /// <summary>Stores only device bytes and records the original byte order.</summary>
        public void WriteByte(uint offset, byte value) { Events.Add("W" + offset + ":" + value); _bytes[offset] = value; }
    }
    // Deliberately literal oracle: translated byte, explicit mapping, electrical decode, backing byte.
    // It contains no page classification, span, wide access or CPU translation shortcut.
    private sealed class Model(bool a20, uint top, uint holeStart, uint holeLength)
    {
        public readonly byte[] Ram = new byte[Bytes];
        public readonly List<(uint Start, uint Length, Device Device)> Maps = [];
        /// <summary>Evaluates one address against the explicit external wiring contract.</summary>
        private uint Address(uint value) => a20 ? value : value & 0xffefffff;
        /// <summary>Decodes installed DRAM without aliasing undispatched high rows into lower rows.</summary>
        private bool Driven(uint at) => at < top && !(at >= holeStart && (ulong)at < (ulong)holeStart + holeLength);
        /// <summary>Observes one ordered byte with mapped devices taking precedence over chipset DRAM decode.</summary>
        public byte Read(uint at)
        {
            at = Address(at);
            foreach (var item in Maps) if (at >= item.Start && (ulong)at < (ulong)item.Start + item.Length) return item.Device.ReadByte(at - item.Start);
            return Driven(at) ? Ram[at] : (byte)255;
        }
        /// <summary>Routes a byte to the device or a driven DRAM row; unclaimed writes have no destination.</summary>
        public void Write(uint at, byte value)
        {
            at = Address(at);
            foreach (var item in Maps) if (at >= item.Start && (ulong)at < (ulong)item.Start + item.Length) { item.Device.WriteByte(at - item.Start, value); return; }
            if (Driven(at)) Ram[at] = value;
        }
    }
    /// <summary>Fills distinct pages and offsets so aliasing or a hidden-row write cannot pass a zero-filled oracle.</summary>
    private static void Seed(byte[] ram) { for (int i = 0; i < ram.Length; i++) ram[i] = (byte)(i * 11 + (i >> 12) * 7 + 0x29); }
    /// <summary>Compares wide bus effects with individual independent byte transactions at every relevant boundary.</summary>
    private static void Compare(bool a20, uint top, uint holeStart, uint holeLength, int width)
    {
        var bus = new MemoryBus(Bytes) { A20Enabled = a20 }; var model = new Model(a20, top, holeStart, holeLength);
        Seed(bus.Ram); bus.Ram.CopyTo(model.Ram, 0); bus.SetDecodedRamBytes(top); bus.SetRamHole(holeStart, holeLength);
        var devices = new List<(Device Actual, Device Expected)>();
        foreach (var (start, length) in new (uint, uint)[] { (0x2100, 3), (0x5fff, 2), (0x101000, 4096), (0x180001, 3), (0xfffffffe, 2) })
        {
            var actual = new Device(); var expected = new Device(); bus.Map(start, length, actual, "Own decode probe"); model.Maps.Add((start, length, expected)); devices.Add((actual, expected));
        }
        var addresses = new HashSet<uint>();
        foreach (uint edge in new uint[] { 0, top, holeStart, unchecked(holeStart + holeLength), 0x1000, 0x2100, 0x2103, 0x5fff, 0x6000, 0x6001, 0xfffff, 0x100000, 0x101000, 0x102000, 0x180001, 0x180004, Bytes, uint.MaxValue })
            for (int delta = -4; delta <= 4; delta++) addresses.Add(unchecked(edge + (uint)delta));
        foreach (uint address in addresses)
        {
            uint expected = 0;
            for (int i = 0; i < width; i++) expected |= (uint)model.Read(unchecked(address + (uint)i)) << (8 * i);
            uint actual = width == 2 ? bus.ReadWord(address) : bus.ReadDword(address);
            Check(actual == expected, $"Decode read {address:X8}/{width}: {actual:X8} != {expected:X8}");
            uint written = unchecked(address * 0x9e3779b9u + 0x87654321);
            for (int i = 0; i < width; i++) model.Write(unchecked(address + (uint)i), (byte)(written >> (8 * i)));
            if (width == 2) bus.WriteWord(address, (ushort)written); else bus.WriteDword(address, written);
            foreach (var pair in devices) Check(pair.Actual.Events.SequenceEqual(pair.Expected.Events), "Device byte order or count changed.");
        }
        Check(bus.Ram.AsSpan().SequenceEqual(model.Ram), "A hidden DRAM row, wrapped byte or mapped byte was written.");
    }
    private sealed class CallbackDevice(Action action) : IMemoryDevice
    {
        /// <summary>Changes decode during the first genuine device byte rather than before a wide access is checked.</summary>
        public byte ReadByte(uint offset) { action(); return 0x11; }
        /// <summary>Applies the same side effect at the first byte of an ordered store.</summary>
        public void WriteByte(uint offset, byte value) => action();
    }
    /// <summary>Registers boundary oracles without asserting unimplemented cache or DRAM electrical timing.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool a20 in new[] { false, true }) foreach (int width in new[] { 2, 4 })
        foreach (uint top in new uint[] { 0, 1, 2, 3, 4095, 4096, 4097, 0x100001, Bytes })
            test($"430FX RAM decode literal byte bus top={top:X8} width={width} A20={a20}", () => Compare(a20, top, 0, 0, width));
        foreach (bool a20 in new[] { false, true }) foreach (int width in new[] { 2, 4 })
        foreach (var (start, length) in new (uint, uint)[] { (0, 4096), (0x5000, 0x2000), (0x80000, 0x20000), (0xf0000, 0x20000), (0xfffff000, 4096) })
            test($"430FX RAM decode literal hole={start:X8}+{length:X8} width={width} A20={a20}", () => Compare(a20, Bytes, start, length, width));
        foreach (uint top in new uint[] { 0, 1, 4095, 4096, 4097, 0x100001, Bytes })
            test("430FX RAM decode all inspection fastpaths respect top " + top.ToString("X8"), () =>
            {
                var m = new MemoryBus(Bytes); Seed(m.Ram); m.SetDecodedRamBytes(top);
                foreach (uint at in new uint[] { 0, top == 0 ? 0 : top - 1, top, 0x1fffff, uint.MaxValue })
                {
                    bool expected = at < top;
                    Check(m.IsPlainRamByte(at) == expected);
                    Check(m.TryReadRamByte(at, out byte read) == expected && read == (expected ? m.Ram[at] : 0));
                    Check(m.TryGetRamSpan(at, 1, out var span) == expected && span.Length == (expected ? 1 : 0));
                    Check(m.TryGetCodeSpan(at, 1, out var code) == expected && code.Length == (expected ? 1 : 0));
                    Check(m.TryPeekByte(at, out byte peek) && peek == (expected ? m.Ram[at] : 255));
                }
                foreach (int width in new[] { -1, 0, 1, 2, 4, 4096 })
                {
                    bool expected = width > 0 && (uint)width <= top;
                    Check(m.TryGetRamSpan(0, width, out _) == expected && m.TryGetCodeSpan(0, width, out _) == expected);
                }
                if (top > 0) Check(!m.TryGetRamSpan(top - 1, 2, out _) && !m.TryGetCodeSpan(top - 1, 2, out _));
            });
        foreach (int tail in new[] { 1, 2, 3, 4095 })
            test("430FX RAM decode hole covers the last partial installed page bytes=" + tail, () =>
            {
                var m = new MemoryBus(1048576 + tail); Seed(m.Ram); byte[] original = m.Ram.AsSpan(1048576).ToArray();
                m.SetRamHole(0x100000, 4096);
                for (int i = 0; i < tail; i++)
                {
                    uint at = 0x100000u + (uint)i;
                    Check(m.ReadByte(at) == 255 && !m.IsPlainRamByte(at) && !m.TryReadRamByte(at, out _));
                    Check(!m.TryGetRamSpan(at, 1, out _) && !m.TryGetCodeSpan(at, 1, out _));
                    Check(m.TryPeekByte(at, out byte value) && value == 255); m.WriteByte(at, 0);
                }
                Check(m.ReadWord(0x100000) == 65535 && m.ReadDword(0x100000) == uint.MaxValue);
                m.WriteWord(0x100000, 0); m.WriteDword(0x100000, 0);
                Check(m.Ram.AsSpan(1048576).SequenceEqual(original), "A hole failed to cover the partial last DRAM page.");
                m.SetRamHole(0, 0); Check(m.TryGetRamSpan(0x100000, tail, out var restored) && restored.SequenceEqual(original));
            });
        test("430FX RAM decode restores visibility without clearing or aliasing installed storage", () =>
        {
            var m = new MemoryBus(Bytes); Seed(m.Ram); byte[] before = m.Ram.ToArray();
            Check(m.DecodedRamBytes == Bytes); m.SetDecodedRamBytes(0);
            m.WriteDword(0, 0); m.WriteDword(0x100000, 0); Check(m.ReadDword(0) == uint.MaxValue);
            m.SetDecodedRamBytes(Bytes); Check(m.Ram.SequenceEqual(before) && m.ReadDword(0x100000) != 0);
            m.SetRamHole(0x100000, 4096); m.WriteDword(0x100000, 0); Check(m.ReadDword(0x100000) == uint.MaxValue);
            m.SetRamHole(0, 0); Check(m.Ram.SequenceEqual(before) && m.TryGetRamSpan(0x100000, 4096, out _));
        });
        test("430FX RAM decode invalid public settings preserve the previous valid wiring", () =>
        {
            var m = new MemoryBus(Bytes); m.SetDecodedRamBytes(0x100000); m.SetRamHole(0x80000, 0x20000);
            Invalid(() => m.SetDecodedRamBytes(Bytes + 1)); Invalid(() => m.SetDecodedRamBytes(uint.MaxValue));
            foreach (var (start, length) in new (uint, uint)[] { (1, 4096), (4096, 1), (4096, 0), (0xfffff000, 8192) }) Invalid(() => m.SetRamHole(start, length));
            Check(m.DecodedRamBytes == 0x100000 && m.RamHoleStart == 0x80000 && m.RamHoleLength == 0x20000);
        });
        test("430FX RAM decode keeps mapped ROM and MMIO above DRAM top and inside a hole", () =>
        {
            var m = new MemoryBus(Bytes); m.SetDecodedRamBytes(0); m.SetRamHole(0x80000, 0x20000);
            var d = new Device(); m.Map(0x81000, 4096, d, "Own hidden-page peripheral"); m.Map(0x180000, 4, new RomDevice([0x12, 0x34, 0x56, 0x78]), "Own high ROM");
            Check(m.ReadDword(0x180000) == 0x78563412 && m.TryGetCodeSpan(0x180000, 4, out var rom) && rom.SequenceEqual(new byte[] { 0x12, 0x34, 0x56, 0x78 }));
            Check(!m.TryGetRamSpan(0x180000, 4, out _)); m.WriteDword(0x180000, 0);
            Check(m.ReadDword(0x180000) == 0x78563412 && m.Ram[0x180000] == 0);
            m.WriteWord(0x81000, 0x1234); Check(m.ReadWord(0x81000) == 0x1234 && d.Events.SequenceEqual(new[] { "W0:52", "W1:18", "R0", "R1" }));
            Check(!m.TryPeekByte(0x81000, out _) && d.Events.Count == 4);
        });
        test("430FX RAM decode updates later bytes after a device changes top during a wide read or write", () =>
        {
            foreach (bool write in new[] { false, true }) foreach (bool shrinking in new[] { false, true })
            {
                var m = new MemoryBus(Bytes); m.Ram[0x2001] = 0x22; m.Ram[0x2002] = 0x33; m.Ram[0x2003] = 0x44;
                m.SetDecodedRamBytes(shrinking ? (uint)Bytes : 0u); m.Map(0x2000, 1, new CallbackDevice(() => m.SetDecodedRamBytes(shrinking ? 0u : Bytes)), "Own decode transition");
                if (write)
                {
                    m.WriteDword(0x2000, 0x87654321);
                    Check(m.Ram[0x2001] == (shrinking ? 0x22 : 0x43) && m.Ram[0x2002] == (shrinking ? 0x33 : 0x65) && m.Ram[0x2003] == (shrinking ? 0x44 : 0x87));
                }
                else Check(m.ReadDword(0x2000) == (shrinking ? 0xffffff11u : 0x44332211u));
            }
        });
        foreach (bool hole in new[] { false, true })
            test("430FX RAM decode after-load rebuilds cached page classification hole=" + hole, () =>
            {
                var source = new MemoryBus(Bytes); source.SetDecodedRamBytes(0x180000); source.SetRamHole(hole ? 0x80000u : 0, hole ? 0x20000u : 0);
                var restored = new MemoryBus(Bytes); restored.LoadState(source.SaveState());
                Check(restored.DecodedRamBytes == 0x180000 && restored.RamHoleStart == source.RamHoleStart && restored.RamHoleLength == source.RamHoleLength);
                Check(restored.TryGetRamSpan(0x80000, 0x20000, out _) == !hole && restored.TryReadRamByte(0x80000, out _) == !hole);
                Check(!restored.TryGetRamSpan(0x17ffff, 2, out _));
                restored.SetRamHole(0, 0); Check(restored.TryGetRamSpan(0x80000, 0x20000, out _));
            });
        foreach (string field in new[] { "DecodedRamBytes", "_ramHoleStart", "_ramHoleLength" })
            test("430FX RAM decode direct state load rejects malformed " + field, () =>
            {
                var original = new MemoryBus(Bytes); var state = original.SaveState();
                state.Fields[field] = JsonSerializer.SerializeToElement(field == "DecodedRamBytes" ? uint.MaxValue : 1u, StateValues.Json);
                Invalid(() => new MemoryBus(Bytes).LoadState(state));
            });
        foreach (bool hole in new[] { false, true })
            test("430FX RAM decode warm paging TLB cannot retain a hidden physical row hole=" + hole, () =>
            {
                var (c, m) = PagingTests.Create([0xa1, 0, 0, 3, 0]); PagingTests.Pte(m, 0x30000, 0x180000); m.WriteDword(0x180000, 0x12345678);
                c.Step(); c.Eip = 0x2000; c.Step(); Check(c.GetRegister(Register.Ax) == 0x12345678 && c.TlbHits > 0);
                if (hole) m.SetRamHole(0x180000, 4096); else m.SetDecodedRamBytes(0x100000);
                c.Eip = 0x2000; c.Step(); Check(c.GetRegister(Register.Ax) == uint.MaxValue && !c.TripleFaulted);
                c.SetRegister(Register.Ax, 0xabcdef01); PagingTests.At(c, m, [0xa3, 0, 0, 3, 0], 0x2000);
                Check(m.Ram[0x180000] == 0x78 && m.Ram[0x180003] == 0x12);
                if (hole) m.SetRamHole(0, 0); else m.SetDecodedRamBytes(Bytes);
                c.Eip = 0x2005; new byte[] { 0xa1, 0, 0, 3, 0 }.CopyTo(m.Ram, 0x2005); c.Step(); Check(c.GetRegister(Register.Ax) == 0x12345678);
            });
        test("430FX RAM decode hidden physical RAM does not bypass cached page write protection", () =>
        {
            var (c, m) = PagingTests.Create([0xa1, 0, 0, 3, 0]); PagingTests.Pte(m, 0x30000, 0x180000, 5); m.WriteDword(0x180000, 0x12345678); c.Step();
            m.SetDecodedRamBytes(0x100000); new byte[] { 0xa3, 0, 0, 3, 0 }.CopyTo(m.Ram, 0x2000); c.Eip = 0x2000;
            ProtectedModeTests.Fault(c, m, 14, 3); Check(c.Cr2 == 0x30000 && m.Ram[0x180000] == 0x78);
        });
        foreach (bool paging in new[] { false, true }) foreach (bool destination in new[] { false, true }) foreach (bool hole in new[] { false, true })
            test($"430FX RAM decode REP checks every source/destination byte paging={paging} destination={destination} hole={hole}", () =>
            {
                using var m = RepBatchTests.Machine([0xf3, 0xa5, 0xf4], paging); uint source = destination ? 0x28000u : 0x37ffcu, target = destination ? 0x37ffcu : 0x28000u;
                new byte[] { 0xf3, 0xa5, 0xf4 }.CopyTo(m.Memory.Ram, 0x2000);
                // Keep the actual GDT/IDT/page tables intact: only seed the two data ranges after fixture construction.
                for (int i = 0; i < 4096; i++) { m.Memory.Ram[0x28000 + i] = (byte)(i * 13 + 3); m.Memory.Ram[0x37ffc + i] = (byte)(i * 17 + 5); }
                byte[] hidden = m.Memory.Ram.AsSpan(0x38000, 4092).ToArray(); byte[] first = m.Memory.Ram.AsSpan((int)source, 4).ToArray();
                if (hole) m.Memory.SetRamHole(0x38000, 4096); else m.Memory.SetDecodedRamBytes(0x38000);
                m.Cpu.SetRegister(Register.Si, source); m.Cpu.SetRegister(Register.Di, target); m.Cpu.SetRegister(Register.Cx, 1024); m.RunSteps(1030);
                Check(m.Fault is null && m.Cpu.Halted && m.Cpu.GetRegister(Register.Cx) == 0);
                Check(m.Memory.Ram.AsSpan((int)target, 4).SequenceEqual(first));
                if (destination) Check(m.Memory.Ram.AsSpan(0x38000, 4092).SequenceEqual(hidden), "REP store wrote an undecoded physical row.");
                else Check(m.Memory.Ram.AsSpan((int)target + 4, 4092).IndexOfAnyExcept((byte)255) < 0, "REP load read hidden backing RAM.");
            });
    }
}
