// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Explicit frontend input at owner boundaries; deterministic PCs and strict snapshot imports never read the host clock.</summary>
public sealed class HostClockSynchronizer
{
    private PcMachine? _previous;
    private bool _enabled, _running;
    private ulong _lastHostTime;
    /// <summary>Number of explicit calendar inputs, outside the serialized machine state.</summary>
    public long Synchronizations { get; private set; }

    /// <summary>Seeds cold/restored PCs and resumes, then corrects the RTC once per monotonic host second while running.</summary>
    public bool Update(PcMachine machine, bool enabled, DateTime localTime, ulong monotonicNanoseconds)
    {
        ArgumentNullException.ThrowIfNull(machine);
        bool changed = !ReferenceEquals(_previous, machine);
        bool resume = machine.Running && (!_running || changed);
        bool periodic = machine.Running && monotonicNanoseconds >= _lastHostTime && monotonicNanoseconds - _lastHostTime >= 1_000_000_000;
        bool apply = enabled && (changed || !_enabled || resume || periodic);
        if (apply)
        {
            machine.SetCalendar(localTime);
            _lastHostTime = monotonicNanoseconds;
            Synchronizations++;
        }
        _previous = machine; _enabled = enabled; _running = machine.Running;
        return apply;
    }
}
