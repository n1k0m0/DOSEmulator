// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Runtime;
using System.Diagnostics;

namespace DOSEmulator.Whpx;

/// <summary>In-memory WHP checkpoint at a stopped PC owner boundary; media branching remains with SnapshotStore.</summary>
public sealed record WhpxPcCheckpoint(HardwareState Hardware, byte[] Ram, WhpxCpuState Cpu);

/// <summary>
/// Optional experimental pairing of an intact DOSE PC device graph with WHP execution.
/// It never changes PcMachine.RunSteps or any Windows launcher selection.
/// </summary>
public sealed class WhpxPcSession : IDisposable
{
    /// <summary>Maps only a validated constructor-selected CPU model to its distinct native checkpoint contract.</summary>
    internal static WhpxCpuContract ContractFor(DOSEmulator.Contracts.MachineProfile profile) =>
        profile.IsP55c ? WhpxCpuContract.IntelP55cMmxSubset : profile.IsP54c ? WhpxCpuContract.IntelP54cB5Subset : WhpxCpuContract.Intel486Dx;
    private readonly PcMachine _pc;
    private readonly WhpxMappedMachine _whpx;
    private readonly object _gate = new();
    private readonly bool _ownsPc;
    private bool _disposed;

    /// <summary>Reserves a stopped DOSE graph and binds an independent native CPU without changing the software CPU image.</summary>
    private WhpxPcSession(PcMachine pc, bool setResetEntry, bool ownsPc = false,
        uint virtualTimeScale = 1)
    {
        _pc = pc;
        _ownsPc = ownsPc;
        pc.AcquireExternalCpuOwner();
        WhpxMappedMachine? engine = null;
        try
        {
            engine = new WhpxMappedMachine(pc.Memory, pc.Ports, pc.Pic, pc.Scheduler,
                ContractFor(pc.Profile), pc.Profile.IsPentium ? (uint)pc.Profile.CpuHz : 0, virtualTimeScale);
            if (setResetEntry) engine.SetEntry(0xf000, 0xfff0);
            pc.MarkExternalCpuImage();
            _whpx = engine;
            engine.ServiceHardwareBoundary = pc.ServiceExternalResetBoundary;
            pc.HardwareResetOccurred += ResetNativeCpu;
        }
        catch
        {
            engine?.Dispose();
            pc.ReleaseExternalCpuOwner();
            throw;
        }
    }

    /// <summary>Uses the actual native selector and IP for frontend and IPC status.</summary>
    public string Position { get { lock (_gate) { ThrowIfDisposed(); return _whpx.Position; } } }
    /// <summary>Stops the active host-time origin only on a real menu pause, capture or disposal.</summary>
    public void Pause() { lock (_gate) { ThrowIfDisposed(); _whpx.PauseRealtime(); _pc.Audio.AdvanceTo(_pc.Scheduler.Now); } }
    /// <summary>Mirrors completed physical reset edges into the real CPU without executing the stale software image.</summary>
    private void ResetNativeCpu() => _whpx.ResetProcessor(_pc.LastResetSource == "cf9-init");

    /// <summary>Returns the graph borrowed by this native session; its software CPU registers are not the native register file.</summary>
    public PcMachine Pc => _pc;
    /// <summary>Reads a selected authoritative native register at a stopped CPU boundary.</summary>
    public ulong GetRegister(uint name) { lock (_gate) { ThrowIfDisposed(); return _whpx.GetRegister(name); } }

    /// <summary>Reads the authoritative stopped native register file without copying the entire guest RAM.</summary>
    public WhpxCpuState CaptureCpuState() { lock (_gate) { ThrowIfDisposed(); return _whpx.CaptureCpuState(); } }

    /// <summary>Attaches WHP only when explicitly requested by an experimental caller.</summary>
    public static WhpxPcSession Start(PcMachine pc, uint virtualTimeScale = 1)
    {
        ArgumentNullException.ThrowIfNull(pc);
        RequireFreshPc(pc);
        return new(pc, true, virtualTimeScale: virtualTimeScale);
    }

    /// <summary>Rejects CPU reset over an already running device timeline or a detached native register image.</summary>
    private static void RequireFreshPc(PcMachine pc)
    {
        pc.RequireSoftwareCpu();
        if (pc.Scheduler.Now != 0 || pc.Cpu.Instructions != 0 || pc.Board.Post10Count != 0 || pc.HardwareResetCount != 0)
            throw new InvalidOperationException("WHP start/resume requires a fresh PC device graph; CPU-engine migration is unsupported.");
    }

    /// <summary>Rebuilds the PC's validated device graph before restoring the independent WHP CPU register file.</summary>
    public static WhpxPcSession Resume(PcMachine freshPc, WhpxPcCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(freshPc);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Hardware is null || checkpoint.Ram is null || checkpoint.Cpu is null)
            throw new InvalidDataException("Incomplete WHP PC checkpoint.");
        RequireFreshPc(freshPc);
        WhpxMappedMachine.ValidateCpuState(checkpoint.Cpu);
        var contract = ContractFor(freshPc.Profile);
        uint frequency = freshPc.Profile.IsPentium ? (uint)freshPc.Profile.CpuHz : 0;
        if (checkpoint.Cpu.Contract != contract || checkpoint.Cpu.NominalCpuHz != frequency)
            throw new InvalidDataException("WHP checkpoint CPU contract differs from the fresh PC profile.");
        freshPc.RestoreHardwareState(checkpoint.Hardware, checkpoint.Ram);
        var session = new WhpxPcSession(freshPc, false,
            virtualTimeScale: checkpoint.Cpu.VirtualTimeScale);
        try { session._whpx.RestoreCpuState(checkpoint.Cpu); return session; }
        catch { session.Dispose(); throw; }
    }

    /// <summary>Adopts a media-branched PC already validated by SnapshotStore.</summary>
    internal static WhpxPcSession AttachLoaded(PcMachine restoredPc, WhpxCpuState cpu, bool ownsPc = true)
    {
        var session = new WhpxPcSession(restoredPc, false, ownsPc,
            cpu.VirtualTimeScale);
        try { session._whpx.RestoreCpuState(cpu); return session; }
        catch { session.Dispose(); throw; }
    }

    /// <summary>Captures the existing media/PC snapshot and the stopped WHP CPU under one session owner lock.</summary>
    internal (SnapshotManifest Hardware, WhpxCpuState Cpu) CaptureWithMedia(string hardwarePath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _whpx.PauseRealtime(); _whpx.RequireCheckpointBoundary();
            var cpu = _whpx.CaptureCpuState();
            var hardware = SnapshotStore.CaptureExternalCpu(_pc, hardwarePath);
            return (hardware, cpu);
        }
    }

    /// <summary>Runs native code to a stopped HLT exit and advances audio to the resulting device time.</summary>
    public WhpxMappedRunResult RunUntilHalt(TimeSpan timeout, int maximumExits = 10000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try { return _whpx.RunUntilHalt(timeout, maximumExits); }
            finally { _pc.Audio.AdvanceTo(_pc.Scheduler.Now); }
        }
    }

    /// <summary>Runs to the requested real-time device deadline and advances audio at the same boundary.</summary>
    public WhpxMappedRunResult RunUntilVirtualTime(ulong deadline, TimeSpan timeout, int maximumExits = 10000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try { return _whpx.RunUntilVirtualTime(deadline, timeout, maximumExits); }
            finally { _pc.Audio.AdvanceTo(_pc.Scheduler.Now); }
        }
    }

    /// <summary>Runs one continuous real-time window, counting audio and observation work as elapsed guest wall time.
    /// Bus-writes-only observers must use MemoryBus writes/notifications for RAM stores; direct untracked byte-array writes require the default full reconciliation.</summary>
    public WhpxRealtimeRunResult RunRealtime(TimeSpan duration, Action<WhpxRealtimeObservation>? observe = null,
        int observationHz = 60, CancellationToken cancellation = default, bool busWritesOnly = false, bool retainOwnerWindow = false)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        if (observationHz is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(observationHz));
        lock (_gate)
        {
            ThrowIfDisposed();
            ulong startedAt = _pc.Scheduler.Now;
            ulong until = checked(startedAt + (ulong)duration.Ticks * 100);
            ulong quantum = 1_000_000_000UL / (uint)observationHz;
            ulong next = startedAt;
            long hostStart = Stopwatch.GetTimestamp();
            int io = 0, mmio = 0, irq = 0, exits = 0, observations = 0;
            if (retainOwnerWindow && !busWritesOnly) throw new ArgumentException("Retained owners must use bus writes.");
            bool completed = false;
            _whpx.BeginRealtimeWindow(busWritesOnly);
            try
            {
                while (_pc.Scheduler.Now < until)
                {
                    cancellation.ThrowIfCancellationRequested();
                    next = Math.Min(until, checked(next + quantum));
                    var part = _whpx.RunUntilVirtualTime(next, TimeSpan.FromSeconds(30), 2_000_000);
                    io += part.IoExits; mmio += part.MmioExits; irq += part.PicInterrupts; exits += part.TotalExits;
                    _pc.Audio.AdvanceTo(_pc.Scheduler.Now);
                    if (observe is not null)
                    {
                        observe(new(_pc.Scheduler.Now, part.Rip, io, mmio, irq, exits));
                        observations++;
                    }
                }
                completed = true;
            }
            finally { _whpx.EndRealtimeWindow(retainOwnerWindow && completed); _pc.Audio.AdvanceTo(_pc.Scheduler.Now); }
            ulong hostNs = (ulong)((UInt128)(ulong)(Stopwatch.GetTimestamp() - hostStart) * 1_000_000_000 / (ulong)Stopwatch.Frequency);
            return new(startedAt, _pc.Scheduler.Now, hostNs, io, mmio, irq, exits, observations,
                hostNs == 0 ? 0 : 100d * (_pc.Scheduler.Now - startedAt) / hostNs);
        }
    }

    /// <summary>Sets only the RTC at a completed native owner slice without pausing active time or publishing the RAM mirror.</summary>
    public void SetCalendar(DateTime calendar)
    {
        lock (_gate)
        {
            ThrowIfDisposed(); _whpx.RequireDeviceBoundary();
            _pc.Rtc.SetCalendar(calendar);
        }
    }

    /// <summary>Captures the full DOSE device graph, guest RAM and WHP register file; no media files are copied.</summary>
    public WhpxPcCheckpoint Capture()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _whpx.PauseRealtime(); _whpx.RequireCheckpointBoundary();
            var cpu = _whpx.CaptureCpuState();
            var hardware = _pc.CaptureHardwareState();
            return new(hardware, (byte[])_pc.Memory.Ram.Clone(), cpu);
        }
    }

    /// <summary>Rejects further owner operations after this native session has released its resources.</summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Stops the native engine before releasing its CPU reservation and any owned restored PC.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pc.HardwareResetOccurred -= ResetNativeCpu;
            _whpx.Dispose();
            _pc.ReleaseExternalCpuOwner();
            if (_ownsPc) _pc.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>Stopped-owner observation; exit counts are not a count of guest retired instructions.</summary>
public readonly record struct WhpxRealtimeObservation(ulong VirtualNanoseconds, ulong Rip, int IoExits,
    int MmioExits, int PicInterrupts, int TotalExits);

/// <summary>Actual active-window wall-clock ratio, requiring independent workload progress before a realtime performance claim.</summary>
public sealed record WhpxRealtimeRunResult(ulong StartedAt, ulong EndedAt, ulong HostNanoseconds,
    int IoExits, int MmioExits, int PicInterrupts, int TotalExits, int Observations, double ClockFollowPercent);
