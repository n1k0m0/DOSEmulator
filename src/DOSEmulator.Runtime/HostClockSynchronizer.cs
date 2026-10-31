// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Explicit frontend input at owner boundaries; deterministic PCs and strict snapshot imports never read the host clock.</summary>
public sealed class HostClockSynchronizer
{
    private PcMachine? _previous;
    private bool _enabled, _running;
    private ulong _lastHostTime;
    private long _lastResumeGeneration;
    /// <summary>Number of explicit calendar inputs, outside the serialized machine state.</summary>
    public long Synchronizations { get; private set; }

    /// <summary>Uses the active session's CPU boundary, including retained native RAM between GUI slices.</summary>
    public bool Update(MachineSession session, bool enabled, DateTime localTime, ulong monotonicNanoseconds)
    {
        ArgumentNullException.ThrowIfNull(session);
        return UpdateCore(session.Machine, enabled, localTime, monotonicNanoseconds, session.ResumeGeneration, session.SetCalendar);
    }

    /// <summary>Seeds cold/restored PCs and resumes, then corrects the RTC once per monotonic host second while running.</summary>
    public bool Update(PcMachine machine, bool enabled, DateTime localTime, ulong monotonicNanoseconds) =>
        Update(machine, enabled, localTime, monotonicNanoseconds, null);

    /// <summary>Also observes successful session resume transitions coalesced within one owner pump; generations never enter saved hardware.</summary>
    public bool Update(PcMachine machine, bool enabled, DateTime localTime, ulong monotonicNanoseconds, long? resumeGeneration)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return UpdateCore(machine, enabled, localTime, monotonicNanoseconds, resumeGeneration, machine.SetCalendar);
    }

    /// <summary>Commits policy history only after the selected owner accepts the explicit calendar input.</summary>
    private bool UpdateCore(PcMachine machine, bool enabled, DateTime localTime, ulong monotonicNanoseconds, long? resumeGeneration, Action<DateTime> setCalendar)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (resumeGeneration is < 0) throw new ArgumentOutOfRangeException(nameof(resumeGeneration));
        bool changed = !ReferenceEquals(_previous, machine);
        long previousGeneration = changed ? 0 : _lastResumeGeneration;
        bool queuedResume = resumeGeneration is { } generation && generation > previousGeneration;
        bool resume = machine.Running && (!_running || changed || queuedResume);
        bool periodic = machine.Running && monotonicNanoseconds >= _lastHostTime && monotonicNanoseconds - _lastHostTime >= 1_000_000_000;
        bool apply = enabled && (changed || !_enabled || resume || periodic);
        if (apply)
        {
            setCalendar(localTime);
            _lastHostTime = monotonicNanoseconds;
            Synchronizations++;
        }
        _previous = machine; _enabled = enabled; _running = machine.Running;
        // Rebind a replacement PC's supplied baseline; stale observations of the
        // same PC must neither manufacture a resume nor lower the accepted token.
        if (changed) _lastResumeGeneration = resumeGeneration ?? 0;
        else if (resumeGeneration is { } current && current > _lastResumeGeneration) _lastResumeGeneration = current;
        return apply;
    }
}
