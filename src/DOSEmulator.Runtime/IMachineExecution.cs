// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;

namespace DOSEmulator.Runtime;

/// <summary>Optional CPU owner used by the common queued session without a Runtime-to-native assembly dependency.</summary>
public interface IMachineExecution : IDisposable
{
    /// <summary>Device graph reserved by this CPU owner.</summary>
    PcMachine Machine { get; }
    /// <summary>Activates the prepared owner only after the previous native partition has released its reservation.</summary>
    void Activate();
    /// <summary>Stops and saves native registers while retaining powered devices for transactional replacement rollback.</summary>
    void Suspend();
    /// <summary>Reports the authoritative CPU position rather than the unused software register image.</summary>
    MachineStatus GetStatus();
    /// <summary>Runs one bounded real-time owner window; observers complete before the next CPU slice.</summary>
    void RunRealtime(TimeSpan duration, Action observe, CancellationToken cancellation);
    /// <summary>Applies explicit RTC input between completed native slices without copying or detaching shared RAM.</summary>
    void SetCalendar(DateTime calendar);
    /// <summary>Captures the engine-specific complete CPU and media checkpoint.</summary>
    object CaptureSnapshot(string path);
    /// <summary>Freezes the active clock and exposes coherent RAM at an explicit UI pause.</summary>
    void Pause();
}

/// <summary>Prepares native owners before replacing a live PC; failed admission retains the previous session.</summary>
public interface IMachineExecutionFactory
{
    /// <summary>Stable explicit frontend engine selection.</summary>
    string Engine { get; }
    /// <summary>Validates profile admission before changing hardware or opening replacement media.</summary>
    void ValidateProfile(MachineProfile profile);
    /// <summary>Reserves a fresh PC for execution.</summary>
    IMachineExecution Create(PcMachine machine);
    /// <summary>Loads only this engine's typed checkpoint into a new independent branch.</summary>
    IMachineExecution Load(string source, string destination);
}
