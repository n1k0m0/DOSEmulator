// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Hardware;

internal static class DmaWordTests
{
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value) { if (!value) throw new Exception("DMA word contract failed."); }
    /// <summary>Returns the independent AT word-DMA address-register port for the requested channel.</summary>
    private static ushort AddressPort(int channel) => (ushort)(0xc0 + (channel - 4) * 4);
    /// <summary>Returns the independent AT word-DMA page-register port for the requested channel.</summary>
    private static ushort PagePort(int channel) => channel switch { 5 => 0x8b, 6 => 0x89, 7 => 0x8a, _ => throw new ArgumentOutOfRangeException() };
    /// <summary>Extracts the requested byte, word or doubleword, including the AH/CH/DH/BH aliases.</summary>
    private static ushort ReadRegister(Dma8237 dma, ushort port)
    {
        dma.Write(0xd8, 0, 1);
        return (ushort)(dma.Read(port, 1) | dma.Read(port, 1) << 8);
    }
    /// <summary>Programs the actual guest-visible DMA registers with the case's address, count, page and mode.</summary>
    private static void Program(Dma8237 dma, int channel, ushort address, ushort count, byte page, byte mode)
    {
        dma.Write(0xd4, (uint)(4 | (channel & 3)), 1); dma.Write(0xd8, 0, 1);
        ushort port = AddressPort(channel);
        dma.Write(port, (byte)address, 1); dma.Write(port, (byte)(address >> 8), 1);
        dma.Write((ushort)(port + 2), (byte)count, 1); dma.Write((ushort)(port + 2), (byte)(count >> 8), 1);
        dma.Write(PagePort(channel), page, 1); dma.Write(0xd6, (uint)(mode | (channel & 3)), 1);
        dma.Write(0xd4, (uint)(channel & 3), 1);
    }
    /// <summary>Registers the named dma word regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int channel in new[] { 5, 6, 7 })
        foreach (bool write in new[] { false, true })
        foreach (int service in new[] { 0, 0x40, 0x80 })
        foreach (bool decrement in new[] { false, true })
        foreach (bool auto in new[] { false, true })
            test($"DMA{channel} word {(write ? "write" : "read")} service {service:X2} decrement {decrement} auto {auto}", () =>
            {
                var memory = new MemoryBus(2 * 1048576); var dma = new Dma8237(memory);
                ushort first = decrement ? (ushort)1 : (ushort)0xfffe;
                byte mode = (byte)((write ? 4 : 8) | service | (decrement ? 0x20 : 0) | (auto ? 0x10 : 0));
                Program(dma, channel, first, 2, 3, mode);
                // Three-word blocks wrap inside one 128-KiB page; page bit zero is not an address bit.
                for (int block = 0; block < (auto ? 2 : 1); block++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        ushort index = unchecked((ushort)(first + (decrement ? -i : i)));
                        uint physical = 0x20000u + (uint)index * 2;
                        ushort expected = (ushort)(0x1234 + block * 0x100 + i);
                        if (!write) memory.WriteWord(physical, expected);
                        DmaReadResult result;
                        if (write) { result = dma.TryWriteWord(channel, expected); Check(memory.ReadWord(physical) == expected); }
                        else { result = dma.TryReadWord(channel, out ushort actual); Check(actual == expected); }
                        Check(result == DmaReadResult.Transferred && dma.IsTerminalCount(channel) == (i == 2));
                        ushort next = i == 2 && auto ? first : unchecked((ushort)(index + (decrement ? -1 : 1)));
                        Check(ReadRegister(dma, AddressPort(channel)) == next);
                        Check(ReadRegister(dma, (ushort)(AddressPort(channel) + 2)) == (i == 2 && auto ? 2 : unchecked((ushort)(1 - i))));
                        Check(dma.Read(PagePort(channel), 1) == 3);
                    }
                    Check((dma.Read(0xd0, 1) & 15) == (1u << (channel & 3)) && (dma.Read(0xd0, 1) & 15) == 0);
                }
                Check(((dma.Read(0xde, 1) >> (channel & 3)) & 1) == (auto ? 0u : 1u));
                if (!auto)
                {
                    Check(dma.TryReadWord(channel, out ushort blocked) == DmaReadResult.Blocked && blocked == 0);
                    Check(dma.TryWriteWord(channel, 0xf00d) == DmaReadResult.Blocked && dma.IsTerminalCount(channel));
                }
            });
        foreach (int channel in new[] { 5, 6, 7 })
        foreach (bool write in new[] { false, true })
        foreach (int mode in new[] { 0, 4, 8, 12, 0xc4, 0xc8 })
            test($"DMA{channel} word mode {mode:X2} {(write ? "input" : "output")} grants only matching data transfers", () =>
            {
                var memory = new MemoryBus(1048576); var device = new ByteDevice(); memory.Map(0x22000, 2, device, "DMA observer");
                var dma = new Dma8237(memory); Program(dma, channel, 0x1000, 0, 2, (byte)mode);
                DmaReadResult result = write ? dma.TryWriteWord(channel, 0x1234) : dma.TryReadWord(channel, out _);
                bool valid = mode == (write ? 4 : 8);
                Check(result == (valid ? DmaReadResult.Transferred : DmaReadResult.InvalidMode));
                Check(device.Accesses.Count == (valid ? 2 : 0));
                Check(ReadRegister(dma, AddressPort(channel)) == (valid ? 0x1001 : 0x1000));
                Check(ReadRegister(dma, (ushort)(AddressPort(channel) + 2)) == (valid ? 0xffff : 0));
                Check(dma.IsTerminalCount(channel) == valid);
                if (valid) Check(device.Accesses.Select(x => x.Offset).SequenceEqual(new uint[] { 0, 1 }));
            });
        foreach (int channel in new[] { 5, 6, 7 })
        foreach (bool write in new[] { false, true })
        foreach (bool disabled in new[] { false, true })
            test($"DMA{channel} word {(write ? "write" : "read")} {(disabled ? "controller disable" : "mask")} preserves pending transfer", () =>
            {
                var memory = new MemoryBus(1048576); var dma = new Dma8237(memory);
                memory.WriteWord(0x22000, 0xabcd); Program(dma, channel, 0x1000, 0, 2, (byte)(write ? 4 : 8));
                if (disabled) dma.Write(0xd0, 4, 1); else dma.Write(0xd4, (uint)(4 | (channel & 3)), 1);
                Check(dma.TryReadWord(channel, out ushort value) == DmaReadResult.Blocked && value == 0);
                Check(dma.TryWriteWord(channel, 0x1234) == DmaReadResult.Blocked);
                Check(memory.ReadWord(0x22000) == 0xabcd && ReadRegister(dma, AddressPort(channel)) == 0x1000);
                Check(ReadRegister(dma, (ushort)(AddressPort(channel) + 2)) == 0 && !dma.IsTerminalCount(channel));
                if (disabled) dma.Write(0xd0, 0, 1); else dma.Write(0xd4, (uint)(channel & 3), 1);
                Check((write ? dma.TryWriteWord(channel, 0x1234) : dma.TryReadWord(channel, out value)) == DmaReadResult.Transferred);
                Check(memory.ReadWord(0x22000) == (write ? 0x1234 : 0xabcd));
                // Master clear resets both readable TC and the device-facing completion indication.
                dma.Write(0xda, 0, 1);
                Check(!dma.IsTerminalCount(channel) && dma.Read(0xd0, 1) == 0 && dma.Read(0xde, 1) == 15);
            });
        foreach (int channel in new[] { -1, 0, 1, 2, 3, 4, 8, int.MaxValue })
            test($"DMA word rejects non-word channel {channel}", () =>
            {
                var dma = new Dma8237(new MemoryBus(1048576));
                Throws(() => dma.TryReadWord(channel, out _)); Throws(() => dma.TryWriteWord(channel, 0));
            });
        test("DMA word count FFFF means 65536 grants, with a fixed page and no early TC", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var dma = new Dma8237(memory); Program(dma, 7, 0, 0xffff, 5, 0x44);
            for (int i = 0; i < 65536; i++)
            {
                Check(dma.TryWriteWord(7, (ushort)(i ^ 0x5a5a)) == DmaReadResult.Transferred);
                Check(dma.IsTerminalCount(7) == (i == 65535));
            }
            for (int i = 0; i < 65536; i++) Check(memory.ReadWord((uint)(0x40000 + i * 2)) == (ushort)(i ^ 0x5a5a));
            Check(ReadRegister(dma, 0xcc) == 0 && ReadRegister(dma, 0xce) == 0xffff && dma.Read(0x8a, 1) == 5);
        });
        test("DMA low and high controllers have independent flipflops, masks, disable and TC", () =>
        {
            var memory = new MemoryBus(1048576); var dma = new Dma8237(memory);
            dma.Write(0x0c, 0, 1); dma.Write(4, 0x34, 1); Program(dma, 5, 0x1000, 0, 2, 0x44); dma.Write(4, 0x12, 1);
            dma.Write(5, 0, 1); dma.Write(5, 0, 1); dma.Write(0x81, 0, 1); dma.Write(0x0b, 0x46, 1); dma.Write(0x0a, 2, 1);
            dma.Write(0xd0, 4, 1); Check(dma.TryWriteByte(2, 0x39) == DmaReadResult.Transferred);
            Check(memory.ReadByte(0x1234) == 0x39 && (dma.Read(8, 1) & 15) == 4 && dma.Read(0xd0, 1) == 0);
            dma.Write(0xd0, 0, 1); Check(dma.TryWriteWord(5, 0x3412) == DmaReadResult.Transferred);
            Check((dma.Read(0xd0, 1) & 15) == 2 && dma.IsTerminalCount(2));
            dma.Write(0xda, 0, 1); Check(dma.IsTerminalCount(2) && !dma.IsTerminalCount(5));
        });
        test("DMA word MMIO is little-endian, observes mappings per byte, and cannot overwrite ROM", () =>
        {
            var memory = new MemoryBus(1048576); var device = new ByteDevice(); memory.Map(0x22001, 1, device, "partial word");
            var dma = new Dma8237(memory); Program(dma, 5, 0x1000, 0, 2, 0x44);
            Check(dma.TryWriteWord(5, 0xab34) == DmaReadResult.Transferred && memory.ReadByte(0x22000) == 0x34);
            Check(device.Accesses.Single() == (0u, (byte)0xab, true));
            memory.Map(0x24000, 2, new RomDevice([0x78, 0x56]), "ROM"); Program(dma, 5, 0x2000, 0, 2, 0x44);
            Check(dma.TryWriteWord(5, 0xabcd) == DmaReadResult.Transferred && memory.ReadWord(0x24000) == 0x5678);
            Program(dma, 5, 0x2000, 0, 2, 0x48); Check(dma.TryReadWord(5, out ushort value) == DmaReadResult.Transferred && value == 0x5678);
        });
        test("DMA word high page uses the existing physical bus A20 policy and unpopulated bus", () =>
        {
            var memory = new MemoryBus(2 * 1048576); var dma = new Dma8237(memory);
            memory.A20Enabled = false; Program(dma, 6, 0x1234, 0, 0x11, 0x44);
            Check(dma.TryWriteWord(6, 0xbeef) == DmaReadResult.Transferred && memory.ReadWord(0x2468) == 0xbeef);
            memory.A20Enabled = true; Program(dma, 6, 0, 0, 0xff, 0x48);
            Check(dma.TryReadWord(6, out ushort value) == DmaReadResult.Transferred && value == 0xffff);
        });
        test("DMA word fixture grants one word per device deadline and retries blocked requests", () =>
        {
            var memory = new MemoryBus(1048576); var dma = new Dma8237(memory); var clock = new DeviceScheduler();
            var fixture = new DmaWordFixture(dma, clock); Program(dma, 5, 0x1000, 1, 2, 0x44);
            fixture.Write(0x301, 5, 1); fixture.Write(0x302, 0x1234, 2); fixture.Write(0x304, 4, 2); fixture.Write(0x300, 2, 1);
            clock.AdvanceTo(99_999); Check(fixture.Beats.Count == 0 && memory.ReadWord(0x22000) == 0);
            dma.Write(0xd4, 5, 1); clock.AdvanceTo(100_000); Check(fixture.Beats.Single().Result == "Blocked");
            dma.Write(0xd4, 1, 1); clock.AdvanceTo(199_999); Check(memory.ReadWord(0x22000) == 0);
            clock.AdvanceTo(200_000); Check(memory.ReadWord(0x22000) == 0x1234 && !dma.IsTerminalCount(5));
            clock.AdvanceTo(400_000); Check(memory.ReadWord(0x22002) == 0x1235 && dma.IsTerminalCount(5));
            Check(fixture.Beats.Select(x => x.Time).SequenceEqual(new ulong[] { 100_000, 200_000, 300_000, 400_000 }));
            Check(fixture.Beats.Select(x => x.Result).SequenceEqual(new[] { "Blocked", "Transferred", "Transferred", "Blocked" }));
            Check(fixture.Read(0x30a, 2) == 2 && fixture.Read(0x30c, 2) == 2 && fixture.Read(0x308, 1) == 0);
        });
    }
    /// <summary>Requires a regression action to reject its input with the selected exception type.</summary>
    private static void Throws(Action action)
    {
        try { action(); } catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected invalid DMA channel rejection.");
    }
    private sealed class ByteDevice : IMemoryDevice
    {
        /// <summary>Number of timed ISA accesses observed by this timing policy.</summary>
        public List<(uint Offset, byte Value, bool Write)> Accesses { get; } = [];
        /// <summary>Returns a controlled mapped-device byte and records the test observation/fault hook.</summary>
        public byte ReadByte(uint offset) { Accesses.Add((offset, 0x39, false)); return 0x39; }
        /// <summary>Applies the controlled mapped-device write and records its test observation/fault hook.</summary>
        public void WriteByte(uint offset, byte value) => Accesses.Add((offset, value, true));
    }
}
