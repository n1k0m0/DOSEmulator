// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Runtime.InteropServices;
using System.Numerics;
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
    private sealed record MapRun(uint Start, ulong Bytes, nint Source, uint Flags);
    private sealed record MapPlan(MapRun[] Runs);
    private WhpxRamView? _view;
    private bool _sharedRam;
    private readonly Dictionary<(bool A20, uint Decoded, uint HoleStart, uint HoleLength), MapPlan> _plans = [];
    private MapPlan? _activePlan;
    private readonly HashSet<int> _hostTouchedPages = [];
    private readonly Dictionary<uint, UInt128> _hostFirmwareDecode = [];
    private bool _firmwareInitialized;
    private (bool A20, uint Decoded, uint HoleStart, uint HoleLength)? _topology;
    private bool _disposed;

    /// <summary>Allocates aligned CPU RAM and executable own firmware copies, then binds only its own RAM-change observer.</summary>
    internal WhpxPhysicalMemory(nint partition, MemoryBus bus)
    {
        _partition = partition;
        _bus = bus;
        _ram = (byte*)NativeMemory.AlignedAlloc((nuint)bus.Ram.Length, Page);
        if (_ram is null) throw new OutOfMemoryException();
        try
        {
            _view = new WhpxRamView(_ram, bus.Ram.Length);
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
            Refresh();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Shares the complete stopped image for one native execution window; devices then see every CPU store immediately.</summary>
    internal void ActivateSharedRam()
    {
        if (_sharedRam) return;
        _bus.AttachExternalRam(_view!.Memory); _sharedRam = true;
    }

    /// <summary>Returns the full native image to the managed stopped-owner API before snapshots, raw observers or disposal.</summary>
    internal void DeactivateSharedRam()
    {
        if (!_sharedRam) return;
        _bus.DetachExternalRam(_view!.Memory); _sharedRam = false;
    }

    /// <summary>Marks device-written physical pages needed by executable PAM shadow reconciliation.</summary>
    private void OnHostRamWrite(uint address, int length)
    {
        int first = (int)(address >> 12), last = (int)((address + (uint)length - 1) >> 12);
        for (int page = first; page <= last; page++) _hostTouchedPages.Add(page);
    }

    /// <summary>Reconciles executable firmware and bounded A20/DRB mapping plans while RAM remains shared with devices.</summary>
    internal void Refresh()
    {
        // RAM is shared while active. Only the separately executable PAM/ROM
        // copies need owner-side reconciliation before the vCPU resumes.
        foreach (var (start, length, device, copy) in _firmware)
        {
            bool all = !_firmwareInitialized;
            if (device is Intel82437Fx host)
            {
                UInt128 config = host.ReadConfiguration(0x57, 1) |
                    ((UInt128)host.ReadConfiguration(0x59, 4) << 8) |
                    ((UInt128)host.ReadConfiguration(0x5d, 2) << 40) |
                    ((UInt128)host.ReadConfiguration(0x5f, 1) << 56) |
                    ((UInt128)host.ReadConfiguration(0x64, 1) << 64);
                if (!_hostFirmwareDecode.TryGetValue(start, out var previous) || previous != config)
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

        // HIMEM repeatedly toggles A20 while probing RAM. Reuse exact immutable
        // page classifications instead of rebuilding tens of thousands of rows,
        // and never issue a device-write notification while merely inspecting RAM.
        if (!_plans.TryGetValue(topology, out var plan))
        {
            var wanted = new Dictionary<uint, (nint Source, uint Flags)>();
            uint ramEnd = (uint)Math.Max(_bus.Ram.Length, 2 * 1024 * 1024);
            for (uint page = 0; page < ramEnd; page += Page) Add(page, wanted);
            foreach (var (start, length, _, _) in _firmware)
                for (ulong page = start; page < (ulong)start + length; page += Page) Add((uint)page, wanted);
            var ordered = wanted.OrderBy(item => item.Key).ToArray();
            var runs = new List<MapRun>();
            for (int first = 0; first < ordered.Length;)
            {
                int after = first + 1;
                while (after < ordered.Length && ordered[after].Key == ordered[after - 1].Key + Page &&
                       ordered[after].Value.Flags == ordered[first].Value.Flags &&
                       ordered[after].Value.Source == ordered[after - 1].Value.Source + Page) after++;
                runs.Add(new(ordered[first].Key, (ulong)(after - first) * Page,
                    ordered[first].Value.Source, ordered[first].Value.Flags));
                first = after;
            }
            plan = new(runs.ToArray());
            if (_plans.Count == 4) _plans.Clear(); // Guest DRB/hole programming cannot grow an unbounded cache.
            _plans.Add(topology, plan);
        }
        // Every mapping addresses the same authoritative DRAM. Switching A20
        // aliases or DRB holes cannot discard a CPU store or a device DMA write.
        if (_activePlan is { } previousPlan)
            foreach (var run in previousPlan.Runs)
                WhpxNative.Check(WhpxNative.WHvUnmapGpaRange(_partition, run.Start, run.Bytes), "unmap physical topology");
        foreach (var run in plan.Runs)
            WhpxNative.Check(WhpxNative.WHvMapGpaRange(_partition, run.Source, run.Start, run.Bytes, run.Flags), "map physical topology");
        _activePlan = plan; _topology = topology;

        // Classifies one translated physical page as decoded DRAM or executable own firmware.
        void Add(uint guest, Dictionary<uint, (nint Source, uint Flags)> wanted)
        {
            uint physical = _bus.A20Enabled ? guest : guest & ~0x0010_0000u;
            if (_bus.TryGetReadOnlyPhysicalRamSpan(physical, Page, null, out _))
            {
                wanted[guest] = ((nint)(_ram + physical), 0x7);
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

    /// <summary>Detaches the complete RAM image before releasing its view, observers and aligned allocations.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeactivateSharedRam();
        _bus.PhysicalRamWrite -= OnHostRamWrite;
        if (_view is not null) ((IDisposable)_view).Dispose();
        foreach (var (_, _, _, copy) in _firmware) NativeMemory.AlignedFree((void*)copy);
        NativeMemory.AlignedFree(_ram);
    }
}
