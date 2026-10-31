// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Whpx;

/// <summary>Mirrors DOSE's physical RAM and executable firmware while leaving MMIO to instruction emulation.</summary>
internal sealed unsafe class WhpxPhysicalMemory : IDisposable
{
    private const int Page = 4096;
    private readonly nint _partition;
    private readonly MemoryBus _bus;
    private readonly byte* _ram;
    private readonly List<(uint Start, uint Length, IMemoryDevice Device, nint Copy)> _firmware = [];
    private readonly Dictionary<uint, (nint Source, uint Flags)> _mapped = [];
    private readonly List<(uint Start, nint[] Sources, ulong[] Bits)> _dirtyRuns = [];
    private readonly HashSet<int> _hostTouchedPages = [];
    private readonly Dictionary<uint, byte[]> _hostFirmwareDecode = [];
    private bool _firmwareInitialized;
    private (bool A20, uint Decoded, uint HoleStart, uint HoleLength)? _topology;
    private bool _disposed;

    internal WhpxPhysicalMemory(nint partition, MemoryBus bus)
    {
        _partition = partition;
        _bus = bus;
        _ram = (byte*)NativeMemory.AlignedAlloc((nuint)bus.Ram.Length, Page);
        if (_ram is null) throw new OutOfMemoryException();
        try
        {
            bus.Ram.AsSpan().CopyTo(new Span<byte>(_ram, bus.Ram.Length));
            foreach (var (start, length, device) in bus.PhysicalMappings)
            {
                if (device is not (RomDevice or Intel82437Fx)) continue;
                if ((start & (Page - 1)) != 0 || (length & (Page - 1)) != 0)
                    throw new NotSupportedException("Executable firmware mapping is not page aligned.");
                byte* copy = (byte*)NativeMemory.AlignedAlloc(length, Page);
                if (copy is null) throw new OutOfMemoryException();
                _firmware.Add((start, length, device, (nint)copy));
            }
            bus.PhysicalRamWrite += OnHostRamWrite;
            Refresh(true);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Guest RAM is copied before a device callback; device DMA and PAM changes are copied back before resuming.</summary>
    internal void PullGuestRam()
    {
        foreach (var run in _dirtyRuns)
        {
            Array.Clear(run.Bits);
            WhpxNative.Check(WhpxNative.WHvQueryGpaRangeDirtyBitmap(_partition, run.Start,
                (ulong)run.Sources.Length * Page, run.Bits, (uint)(run.Bits.Length * sizeof(ulong))),
                "query mapped dirty RAM");
            for (int index = 0; index < run.Sources.Length; index++)
            {
                if ((run.Bits[index >> 6] & (1UL << (index & 63))) == 0) continue;
                byte* source = (byte*)run.Sources[index];
                int offset = checked((int)(source - _ram));
                new ReadOnlySpan<byte>(source, Page).CopyTo(_bus.Ram.AsSpan(offset, Page));
            }
        }
    }

    private void OnHostRamWrite(uint address, int length)
    {
        int first = (int)(address >> 12), last = (int)((address + (uint)length - 1) >> 12);
        for (int page = first; page <= last; page++) _hostTouchedPages.Add(page);
    }

    internal void Refresh(bool includeExternalChanges = false)
    {
        // Device DMA may have changed managed RAM since the previous guest exit.
        // Compare pages and copy only changed ones; never overwrite guest-dirtied
        // pages before PullGuestRam has reconciled them.
        IEnumerable<int> pages = includeExternalChanges ? Enumerable.Range(0, (_bus.Ram.Length + Page - 1) / Page) : _hostTouchedPages;
        foreach (int page in pages)
        {
            int offset = page * Page;
            int length = Math.Min(Page, _bus.Ram.Length - offset);
            var native = new Span<byte>(_ram + offset, length);
            var managed = _bus.Ram.AsSpan(offset, length);
            if (!native.SequenceEqual(managed)) managed.CopyTo(native);
        }
        foreach (var (start, length, device, copy) in _firmware)
        {
            bool all = !_firmwareInitialized;
            if (device is Intel82437Fx host)
            {
                byte[] config = [
                    (byte)host.ReadConfiguration(0x57, 1),
                    (byte)host.ReadConfiguration(0x59, 1), (byte)host.ReadConfiguration(0x5a, 1),
                    (byte)host.ReadConfiguration(0x5b, 1), (byte)host.ReadConfiguration(0x5c, 1),
                    (byte)host.ReadConfiguration(0x5d, 1), (byte)host.ReadConfiguration(0x5e, 1),
                    (byte)host.ReadConfiguration(0x5f, 1), (byte)host.ReadConfiguration(0x64, 1)
                ];
                if (!_hostFirmwareDecode.TryGetValue(start, out var previous) || !previous.AsSpan().SequenceEqual(config))
                    all = true;
                _hostFirmwareDecode[start] = config;
            }
            if (all)
                for (uint offset = 0; offset < length; offset++) ((byte*)copy)[offset] = _bus.ReadByte(start + offset);
            else if (device is Intel82437Fx)
                foreach (int page in _hostTouchedPages)
                {
                    uint physical = (uint)page * Page;
                    if (physical < start || (ulong)physical + Page > (ulong)start + length) continue;
                    for (uint offset = 0; offset < Page; offset++)
                        ((byte*)copy)[physical - start + offset] = _bus.ReadByte(physical + offset);
                }
        }
        _firmwareInitialized = true;
        _hostTouchedPages.Clear();

        var topology = (_bus.A20Enabled, _bus.DecodedRamBytes, _bus.RamHoleStart, _bus.RamHoleLength);
        if (_topology == topology) return;

        // Reconcile A20, PAM/DRAM decode, and ROM aliases after owner-side I/O. Page
        // mappings are replaced only when their backing or access permissions change.
        var wanted = new Dictionary<uint, (nint Source, uint Flags)>();
        uint ramEnd = (uint)Math.Max(_bus.Ram.Length, 2 * 1024 * 1024);
        for (uint page = 0; page < ramEnd; page += Page) Add(page);
        foreach (var (start, length, _, _) in _firmware)
            for (ulong page = start; page < (ulong)start + length; page += Page) Add((uint)page);

        // A 32-MiB PC exposes thousands of pages. WHP accepts contiguous GPA and
        // host ranges in one call, which also avoids exhausting its mapping table.
        uint[] removed = _mapped.Where(item => !wanted.TryGetValue(item.Key, out var next) || next != item.Value)
            .Select(item => item.Key).Order().ToArray();
        for (int first = 0; first < removed.Length;)
        {
            int after = first + 1;
            while (after < removed.Length && removed[after] == removed[after - 1] + Page) after++;
            ulong bytes = (ulong)(after - first) * Page;
            WhpxNative.Check(WhpxNative.WHvUnmapGpaRange(_partition, removed[first], bytes),
                $"unmap physical range 0x{removed[first]:X}+0x{bytes:X}");
            for (int index = first; index < after; index++) _mapped.Remove(removed[index]);
            first = after;
        }
        var added = wanted.Where(item => !_mapped.ContainsKey(item.Key)).OrderBy(item => item.Key).ToArray();
        for (int first = 0; first < added.Length;)
        {
            int after = first + 1;
            while (after < added.Length && added[after].Key == added[after - 1].Key + Page &&
                   added[after].Value.Flags == added[first].Value.Flags &&
                   added[after].Value.Source == added[after - 1].Value.Source + Page) after++;
            ulong bytes = (ulong)(after - first) * Page;
            WhpxNative.Check(WhpxNative.WHvMapGpaRange(_partition, added[first].Value.Source,
                added[first].Key, bytes, added[first].Value.Flags),
                $"map physical range 0x{added[first].Key:X}+0x{bytes:X}");
            for (int index = first; index < after; index++) _mapped[added[index].Key] = added[index].Value;
            first = after;
        }
        _dirtyRuns.Clear();
        var dirtyPages = _mapped.Where(item => (item.Value.Flags & 8) != 0).OrderBy(item => item.Key).ToArray();
        for (int first = 0; first < dirtyPages.Length;)
        {
            int after = first + 1;
            while (after < dirtyPages.Length && dirtyPages[after].Key == dirtyPages[after - 1].Key + Page) after++;
            var sources = new nint[after - first];
            for (int index = first; index < after; index++) sources[index - first] = dirtyPages[index].Value.Source;
            _dirtyRuns.Add((dirtyPages[first].Key, sources, new ulong[(sources.Length + 63) / 64]));
            first = after;
        }
        _topology = topology;

        void Add(uint guest)
        {
            uint physical = _bus.A20Enabled ? guest : guest & ~0x0010_0000u;
            if (_bus.TryGetPhysicalRamSpan(physical, Page, null, out _))
            {
                wanted[guest] = ((nint)(_ram + physical), WhpxNative.ReadWriteExecuteDirty);
                return;
            }
            foreach (var item in _firmware)
                if (physical >= item.Start && (ulong)physical + Page <= (ulong)item.Start + item.Length)
                {
                    wanted[guest] = (item.Copy + (int)(physical - item.Start), 0x5); // read + execute; writes exit to bus
                    return;
                }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus.PhysicalRamWrite -= OnHostRamWrite;
        foreach (var (_, _, _, copy) in _firmware) NativeMemory.AlignedFree((void*)copy);
        NativeMemory.AlignedFree(_ram);
    }
}
