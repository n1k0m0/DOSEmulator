// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>PCI physical DRAM grants with independent A20, hole, mapping and every-bank PAM oracles; no MMIO is invoked.</summary>
internal static class PciMasterMemoryTests
{
    /// <summary>Requires an actual backing byte, refusal or no-side-effect invariant.</summary>
    private static void Check(bool value, string why = "PCI physical DRAM contract failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Counts actual callbacks so refused DMA grants cannot hide CPU-bus MMIO reads or writes.</summary>
    private sealed class ObservedMemory : IMemoryDevice
    {
        public int Reads, Writes;
        /// <summary>Publishes a read only if the ordinary mapped bus really contacts this synthetic target.</summary>
        public byte ReadByte(uint offset) { Reads++; return 0xa5; }
        /// <summary>Publishes a write only if the ordinary mapped bus really contacts this synthetic target.</summary>
        public void WriteByte(uint offset, byte value) { Writes++; }
    }

    /// <summary>Binds the same PAM controller mapping used by the actual board and explicitly programs its DRB ceiling.</summary>
    private sealed class HostRig
    {
        public readonly MemoryBus Memory = new(32 * 1048576);
        public readonly Intel82437Fx Host;
        /// <summary>Creates a private patterned F ROM and leaves PAM at its genuine disabled reset value.</summary>
        public HostRig()
        {
            byte[] rom = Enumerable.Range(0, 65536).Select(v => (byte)(v ^ (v >> 8) ^ 0x5a)).ToArray();
            Host = new(Memory, rom); Memory.Map(0xc0000, 0x40000, Host, "Own PAM shadow");
            Host.WriteConfiguration(0x64, 8, 1);
        }
    }

    /// <summary>Programs one independently numbered 16-KiB expansion bank or the sole 64-KiB F bank.</summary>
    private static void Pam(Intel82437Fx host, int bank, byte attributes)
    {
        if (bank == 12) { host.WriteConfiguration(0x59, (uint)attributes << 4, 1); return; }
        int address = 0x5a + bank / 2, shift = (bank & 1) * 4;
        uint old = host.ReadConfiguration(address, 1);
        host.WriteConfiguration(address, (old & ~(15u << shift)) | ((uint)attributes << shift), 1);
    }

    /// <summary>Captures fixed host data only, allowing a refusal/read to prove no status or source mutation.</summary>
    private static string State(HostRig rig) => JsonSerializer.Serialize(rig.Host.SaveState(), StateValues.Json);

    /// <summary>Registers physical address, complete-range and host target-selection cases without starting a guest or touching media.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PCI physical span preserves A20 while the old CPU and ISA RAM APIs still fold it", () =>
        {
            var memory = new MemoryBus(4 * 1048576) { A20Enabled = false };
            memory.Ram[0x1234] = 0x31; memory.Ram[0x101234] = 0x72;
            Check(memory.ReadByte(0x101234) == 0x31);
            Check(memory.TryGetRamSpan(0x101234, 1, out var old) && old[0] == 0x31);
            Check(memory.TryGetPhysicalRamSpan(0x101234, 1, null, out var physical) && physical[0] == 0x72);
            physical[0] = 0x94;
            Check(memory.Ram[0x101234] == 0x94 && memory.Ram[0x1234] == 0x31 && !memory.A20Enabled);
            Check(memory.TryGetPhysicalRamSpan(0xfffff, 2, null, out var crossing) && crossing.Length == 2);
            Check(!memory.TryGetRamSpan(0xfffff, 2, out _));
        });
        foreach (var range in new (uint Address, int Length)[]
        {
            (0, 0), (0x100, -1), (uint.MaxValue, 2), (0xfffffff0, 32),
            (0x1fffff, 2), (0x200000, 1), (0x1000, int.MaxValue)
        })
            test($"M4 PCI physical span rejects complete invalid range {range.Address:X8}/{range.Length}", () =>
            {
                var memory = new MemoryBus(2 * 1048576);
                memory.Ram[0x1000] = 0x5a;
                Check(!memory.TryGetPhysicalRamSpan(range.Address, range.Length, null, out var bytes) && bytes.IsEmpty);
                Check(memory.Ram[0x1000] == 0x5a && memory.A20Enabled);
            });
        foreach (uint first in new uint[] { 0x1fffff, 0x200000, 0x200001 })
            test("M4 PCI physical span obeys decoded DRB end independently from installed storage " + first, () =>
            {
                var memory = new MemoryBus(4 * 1048576); memory.Ram[0x200000] = 0x6c;
                memory.SetDecodedRamBytes(0x200000);
                bool expected = first < 0x200000;
                Check(memory.TryGetPhysicalRamSpan(first, 1, null, out var bytes) == expected && bytes.Length == (expected ? 1 : 0));
                Check(memory.Ram[0x200000] == 0x6c);
                memory.SetDecodedRamBytes(0x400000);
                Check(memory.TryGetPhysicalRamSpan(0x200000, 1, null, out var visible) && visible[0] == 0x6c);
            });
        foreach (var range in new (uint Address, int Length, bool Allowed)[]
        {
            (0x7ffff, 1, true), (0x7ffff, 2, false), (0x80000, 1, false),
            (0x9ffff, 1, false), (0x9ffff, 2, false), (0xa0000, 1, true),
            (0x70000, 0x40000, false)
        })
            test($"M4 PCI physical span checks the entire forwarded hole {range.Address:X}/{range.Length}", () =>
            {
                var memory = new MemoryBus(2 * 1048576); memory.SetRamHole(0x80000, 0x20000);
                Check(memory.TryGetPhysicalRamSpan(range.Address, range.Length, null, out var bytes) == range.Allowed);
                Check(range.Allowed || bytes.IsEmpty);
            });
        foreach (var range in new (uint Address, int Length, bool Allowed)[]
        {
            (0x1fff, 1, true), (0x1fff, 2, false), (0x2000, 1, false),
            (0x201f, 1, false), (0x201f, 2, false), (0x2020, 1, true),
            (0x1000, 0x3000, false)
        })
            test($"M4 PCI physical span refuses any foreign partial-page mapping {range.Address:X}/{range.Length}", () =>
            {
                var memory = new MemoryBus(2 * 1048576); var target = new ObservedMemory();
                memory.Map(0x2000, 32, target, "Side-effect sentinel");
                Check(memory.TryGetPhysicalRamSpan(range.Address, range.Length, null, out var bytes) == range.Allowed);
                Check(range.Allowed || bytes.IsEmpty);
                Check(target.Reads == 0 && target.Writes == 0);
            });
        test("M4 PCI physical span permits only the exact named mapping instance across every covered mapping", () =>
        {
            var memory = new MemoryBus(2 * 1048576);
            var permitted = new ObservedMemory(); var other = new ObservedMemory();
            memory.Map(0x2000, 16, permitted, "Permitted shadow");
            memory.Map(0x2010, 16, other, "Foreign adjacent target");
            memory.Ram[0x2000] = 0x63;
            Check(memory.TryGetPhysicalRamSpan(0x1ff0, 32, permitted, out var shadow) && shadow[16] == 0x63);
            shadow[16] = 0x8b;
            Check(!memory.TryGetPhysicalRamSpan(0x1ff0, 48, permitted, out var rejected) && rejected.IsEmpty);
            Check(!memory.TryGetPhysicalRamSpan(0x2000, 1, other, out _));
            Check(permitted.Reads == 0 && permitted.Writes == 0 && other.Reads == 0 && other.Writes == 0 &&
                memory.Ram[0x2000] == 0x8b);
        });
        foreach (bool write in new[] { false, true })
            test("M4 PCI host MAE gates PCI DRAM without disabling CPU RAM write=" + write, () =>
            {
                var rig = new HostRig(); rig.Memory.Ram[0x101234] = 0x72; rig.Memory.A20Enabled = false;
                Check(rig.Host.TryGetPciMemorySpan(0x101234, 1, write, out var enabled) && enabled[0] == 0x72);
                rig.Host.WriteConfiguration(4, 4, 1); string before = State(rig);
                Check(!rig.Host.TryGetPciMemorySpan(0x101234, 1, write, out var disabled) && disabled.IsEmpty);
                rig.Memory.WriteByte(0x1234, 0x4d);
                Check(rig.Memory.Ram[0x1234] == 0x4d && rig.Memory.Ram[0x101234] == 0x72 && State(rig) == before);
            });
        foreach (int bank in Enumerable.Range(0, 13)) foreach (byte attributes in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })
            test($"M4 PCI host PAM bank {bank} independently accepts RE and WE with CE={attributes >> 2} attributes={attributes}", () =>
            {
                var rig = new HostRig(); Pam(rig.Host, bank, attributes);
                uint address = bank == 12 ? 0xf0000u : 0xc0000u + (uint)bank * 0x4000;
                int length = bank == 12 ? 65536 : 16384;
                rig.Memory.Ram[address] = 0x37; rig.Memory.Ram[address + (uint)length - 1] = 0x92;
                string before = State(rig);
                bool read = rig.Host.TryGetPciMemorySpan(address, length, false, out var input);
                Check(read == ((attributes & 1) != 0) && (read ? input[0] == 0x37 && input[^1] == 0x92 : input.IsEmpty));
                bool write = rig.Host.TryGetPciMemorySpan(address, length, true, out var output);
                Check(write == ((attributes & 2) != 0) && output.Length == (write ? length : 0));
                if (write) { output[0] = 0x4c; output[^1] = 0xa8; }
                Check(rig.Memory.Ram[address] == (write ? 0x4c : 0x37) &&
                    rig.Memory.Ram[address + (uint)length - 1] == (write ? 0xa8 : 0x92));
                Check(State(rig) == before, "PAM grant mutated configuration or PCI status.");
            });
        foreach (bool write in new[] { false, true }) foreach (int denied in new[] { 0, 6, 11, 12 })
            test($"M4 PCI host rejects an interior denied PAM bank without partial grant write={write} bank={denied}", () =>
            {
                var rig = new HostRig(); for (int bank = 0; bank < 13; bank++) Pam(rig.Host, bank, 3);
                Pam(rig.Host, denied, write ? (byte)1 : (byte)2);
                string before = State(rig); rig.Memory.Ram[0xc0000] = 0x49;
                Check(!rig.Host.TryGetPciMemorySpan(0xbffff, 0x40002, write, out var bytes) && bytes.IsEmpty);
                Check(rig.Memory.Ram[0xc0000] == 0x49 && State(rig) == before);
            });
        test("M4 PCI host F ROM CPU fallback never becomes a successful PCI read target", () =>
        {
            var rig = new HostRig(); rig.Memory.Ram[0xf0000] = 0x69;
            Check(rig.Memory.ReadByte(0xf0000) == 0x5a);
            Check(!rig.Host.TryGetPciMemorySpan(0xf0000, 1, false, out var absent) && absent.IsEmpty);
            Pam(rig.Host, 12, 1);
            Check(rig.Host.TryGetPciMemorySpan(0xf0000, 1, false, out var ram) && ram[0] == 0x69);
            Check(rig.Memory.ReadByte(0xf0000) == 0x69);
        });
        foreach (bool write in new[] { false, true })
            test("M4 PCI host refuses device and high-ROM mappings without contacting them write=" + write, () =>
            {
                var rig = new HostRig(); var target = new ObservedMemory();
                rig.Memory.Map(0x110000, 0x100, target, "Synthetic PCI memory exclusion");
                rig.Memory.Map(0xffff0000, 65536, new RomDevice(new byte[65536]), "Reset alias");
                string before = State(rig);
                Check(!rig.Host.TryGetPciMemorySpan(0x10ffff, 0x102, write, out var mmio) && mmio.IsEmpty);
                Check(!rig.Host.TryGetPciMemorySpan(0xffff0000, 1, write, out var rom) && rom.IsEmpty);
                Check(target.Reads == 0 && target.Writes == 0 && State(rig) == before);
            });
        foreach (int hole in new[] { 1, 2 }) foreach (bool write in new[] { false, true })
            test($"M4 PCI host HEN{hole} rejects a complete DMA grant without RAM remapping write={write}", () =>
            {
                var rig = new HostRig(); uint start = hole == 1 ? 0x80000u : 0xf00000u;
                int length = hole == 1 ? 0x20000 : 0x100000;
                rig.Memory.Ram[start] = 0x7d; rig.Host.WriteConfiguration(0x57, 1u | ((uint)hole << 6), 1);
                Check(!rig.Host.TryGetPciMemorySpan(start - 1, length + 2, write, out var denied) && denied.IsEmpty);
                Check(rig.Host.TryGetPciMemorySpan(start - 1, 1, write, out _));
                Check(rig.Host.TryGetPciMemorySpan(start + (uint)length, 1, write, out _));
                rig.Host.WriteConfiguration(0x57, 1, 1);
                Check(rig.Host.TryGetPciMemorySpan(start, 1, write, out var visible) && visible[0] == 0x7d);
            });
        test("M4 PCI host reset restores MAE DRB and PAM gates while preserving shadow bytes", () =>
        {
            var rig = new HostRig(); Pam(rig.Host, 12, 3); rig.Memory.Ram[0xf0000] = 0xbc;
            rig.Host.WriteConfiguration(4, 4, 1); rig.Host.ResetConfiguration();
            Check(rig.Host.MemoryAccessEnabled && rig.Memory.DecodedRamBytes == 8 * 1048576);
            Check(!rig.Host.TryGetPciMemorySpan(0x800000, 1, false, out _));
            Check(!rig.Host.TryGetPciMemorySpan(0xf0000, 1, false, out _));
            Check(rig.Memory.Ram[0xf0000] == 0xbc && rig.Host.HasMemoryBinding(rig.Memory));
        });
    }
}
