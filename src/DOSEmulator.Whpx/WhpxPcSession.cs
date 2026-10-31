// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Runtime;

namespace DOSEmulator.Whpx;

/// <summary>In-memory WHP checkpoint at a stopped PC owner boundary; media branching remains with SnapshotStore.</summary>
public sealed record WhpxPcCheckpoint(HardwareState Hardware, byte[] Ram, WhpxCpuState Cpu);

/// <summary>
/// Optional experimental pairing of an intact DOSE PC device graph with WHP execution.
/// It never changes PcMachine.RunSteps or any Windows launcher selection.
/// </summary>
public sealed class WhpxPcSession : IDisposable
{
    private readonly PcMachine _pc;
    private readonly WhpxMappedMachine _whpx;
    private readonly object _gate = new();
    private readonly bool _ownsPc;
    private bool _disposed;

    private WhpxPcSession(PcMachine pc, bool setResetEntry, bool ownsPc = false)
    {
        _pc = pc;
        _ownsPc = ownsPc;
        pc.AcquireExternalCpuOwner();
        WhpxMappedMachine? engine = null;
        try
        {
            engine = new WhpxMappedMachine(pc.Memory, pc.Ports, pc.Pic, pc.Scheduler,
                pc.Profile.IsP54c ? WhpxCpuContract.IntelP54cB5Subset : WhpxCpuContract.Intel486Dx,
                pc.Profile.IsP54c ? (uint)pc.Profile.CpuHz : 0);
            if (setResetEntry) engine.SetEntry(0xf000, 0xfff0);
            _whpx = engine;
        }
        catch
        {
            engine?.Dispose();
            pc.ReleaseExternalCpuOwner();
            throw;
        }
    }

    public PcMachine Pc => _pc;
    public ulong GetRegister(uint name) { lock (_gate) { ThrowIfDisposed(); return _whpx.GetRegister(name); } }

    /// <summary>Attaches WHP only when explicitly requested by an experimental caller.</summary>
    public static WhpxPcSession Start(PcMachine pc) => new(pc ?? throw new ArgumentNullException(nameof(pc)), true);

    /// <summary>Rebuilds the PC's validated device graph before restoring the independent WHP CPU register file.</summary>
    public static WhpxPcSession Resume(PcMachine freshPc, WhpxPcCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(freshPc);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Hardware is null || checkpoint.Ram is null || checkpoint.Cpu is null)
            throw new InvalidDataException("Incomplete WHP PC checkpoint.");
        freshPc.RestoreHardwareState(checkpoint.Hardware, checkpoint.Ram);
        var session = new WhpxPcSession(freshPc, false);
        try { session._whpx.RestoreCpuState(checkpoint.Cpu); return session; }
        catch { session.Dispose(); throw; }
    }

    /// <summary>Adopts a media-branched PC already validated by SnapshotStore.</summary>
    internal static WhpxPcSession AttachLoaded(PcMachine restoredPc, WhpxCpuState cpu)
    {
        var session = new WhpxPcSession(restoredPc, false, true);
        try { session._whpx.RestoreCpuState(cpu); return session; }
        catch { session.Dispose(); throw; }
    }

    /// <summary>Captures the existing media/PC snapshot and the stopped WHP CPU under one session owner lock.</summary>
    internal (SnapshotManifest Hardware, WhpxCpuState Cpu) CaptureWithMedia(string hardwarePath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var cpu = _whpx.CaptureCpuState();
            var hardware = SnapshotStore.Capture(_pc, hardwarePath);
            return (hardware, cpu);
        }
    }

    public WhpxMappedRunResult RunUntilHalt(TimeSpan timeout, int maximumExits = 10000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try { return _whpx.RunUntilHalt(timeout, maximumExits); }
            finally { _pc.Audio.AdvanceTo(_pc.Scheduler.Now); }
        }
    }

    public WhpxMappedRunResult RunUntilVirtualTime(ulong deadline, TimeSpan timeout, int maximumExits = 10000)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try { return _whpx.RunUntilVirtualTime(deadline, timeout, maximumExits); }
            finally { _pc.Audio.AdvanceTo(_pc.Scheduler.Now); }
        }
    }

    /// <summary>Captures the full DOSE device graph, guest RAM and WHP register file; no media files are copied.</summary>
    public WhpxPcCheckpoint Capture()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var cpu = _whpx.CaptureCpuState();
            var hardware = _pc.CaptureHardwareState();
            return new(hardware, (byte[])_pc.Memory.Ram.Clone(), cpu);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _whpx.Dispose();
            _pc.ReleaseExternalCpuOwner();
            if (_ownsPc) _pc.Dispose();
            _disposed = true;
        }
    }
}
