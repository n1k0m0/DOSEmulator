// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Runtime;

namespace DOSEmulator.Whpx;

/// <summary>Connects the native CPU to the same queued owner, menus, input and media lifecycle as the software frontend.</summary>
public sealed class WhpxExecutionFactory : IMachineExecutionFactory
{
    /// <summary>Stable command-line and settings identifier.</summary>
    public string Engine => "whpx";
    /// <summary>Admits the qualified P54C profiles and checks the host before replacing a live owner.</summary>
    public void ValidateProfile(MachineProfile profile)
    {
        profile.Validate();
        if (!profile.IsP54c) throw new InvalidOperationException("WHPX requires a Pentium profile. Select SOFTWARE before choosing the 486 platform.");
        if (!WhpxRealModeMachine.IsAvailable()) throw new PlatformNotSupportedException("Windows Hypervisor Platform is unavailable. Enable it in Windows Features or start with --engine software.");
    }
    /// <summary>Starts one fresh native CPU without taking over the caller's PC disposal responsibility.</summary>
    public IMachineExecution Create(PcMachine machine) { ValidateProfile(machine.Profile); return new Execution(machine); }
    /// <summary>Loads the native typed CPU and full hardware/media branch without software-engine migration.</summary>
    public IMachineExecution Load(string source, string destination)
    {
        if (!WhpxRealModeMachine.IsAvailable()) throw new PlatformNotSupportedException("Windows Hypervisor Platform is unavailable.");
        var prepared = WhpxSnapshotStore.PrepareLoad(source, destination, requireP54c: true);
        return new Execution(prepared.Pc, prepared.Cpu, Path.GetFullPath(destination));
    }

    /// <summary>Owns native resources and exposes only operations safe at a stopped queued-owner boundary.</summary>
    private sealed class Execution(PcMachine machine, WhpxCpuState? restored = null, string? preparedBranch = null) : IMachineExecution
    {
        private WhpxPcSession? _session;
        private WhpxCpuState? _restored = restored;
        private string? _preparedBranch = preparedBranch;
        private string? _fault;
        private long _resetCount = machine.HardwareResetCount;
        /// <summary>The powered device graph stays with the common session through native partition replacement.</summary>
        public PcMachine Machine => machine;
        /// <summary>Creates only one native partition at a time in this GUI process.</summary>
        public void Activate()
        {
            if (_session is not null) return;
            try
            {
                _session = _restored is null ? WhpxPcSession.Start(machine) : WhpxPcSession.AttachLoaded(machine, _restored, ownsPc: false);
                _preparedBranch = null;
            }
            catch { AbandonPreparedBranch(); throw; }
        }
        /// <summary>Retains the stopped CPU and graph so failed replacement can restore the original session.</summary>
        public void Suspend()
        {
            if (_session is null) return;
            _session.Pause(); _restored = _session.CaptureCpuState(); _session.Dispose(); _session = null;
        }
        /// <summary>Reports actual native CS:RIP and errors while unavailable retirement counts remain explicitly marked.</summary>
        public MachineStatus GetStatus()
        {
            if (_resetCount != machine.HardwareResetCount) { _resetCount = machine.HardwareResetCount; _fault = null; }
            var status = machine.GetStatus();
            return status with { Position = _session!.Position, Instructions = 0, CpuEngine = "whpx", InstructionCountAvailable = false, Fault = _fault,
                State = _fault is not null ? MachineRunState.Faulted : !machine.Running ? MachineRunState.Paused : machine.CpuResetHeld ? MachineRunState.ResetHeld : MachineRunState.Running };
        }
        /// <summary>Includes owner-side conversion and between-slice work in one retained active clock origin.</summary>
        public void RunRealtime(TimeSpan duration, Action observe, CancellationToken cancellation)
        {
            try { _session!.RunRealtime(duration, _ => observe(), 120, cancellation, busWritesOnly: true, retainOwnerWindow: true); }
            catch (Exception error) when (error is not OperationCanceledException)
            { _fault = error.Message; machine.Running = false; _session!.Pause(); }
        }
        /// <summary>Publishes coherent paused RAM and excludes subsequent host pause time.</summary>
        public void Pause() => _session!.Pause();
        /// <summary>Uses the native owner's device boundary rather than the software full-snapshot RAM contract.</summary>
        public void SetCalendar(DateTime calendar) => _session!.SetCalendar(calendar);
        /// <summary>Saves the authoritative native CPU with its complete hardware and media.</summary>
        public object CaptureSnapshot(string path) => WhpxSnapshotStore.Capture(_session!, path);
        /// <summary>Releases only CPU resources; the common session owns PC and network disposal.</summary>
        public void Dispose() { _session?.Dispose(); _session = null; AbandonPreparedBranch(); }

        /// <summary>Closes only a never-activated restored PC before discarding its newly created branch, leaving the previous owner to resume.</summary>
        private void AbandonPreparedBranch()
        {
            if (_preparedBranch is not { } branch) return;
            _preparedBranch = null;
            try { machine.Dispose(); }
            finally { WhpxSnapshotStore.DiscardPreparedBranch(branch); }
        }
    }
}
