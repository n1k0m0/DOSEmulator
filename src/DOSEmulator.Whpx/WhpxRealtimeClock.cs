// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;

namespace DOSEmulator.Whpx;

/// <summary>Maps an explicitly active owner window to monotonic host time, including all device/exit work.</summary>
public sealed class WhpxRealtimeClock
{
    private readonly Func<long> _counter;
    private readonly long _frequency;
    private long _hostOrigin;
    private ulong _guestOrigin, _last;
    private bool _active;

    /// <summary>Uses the host performance counter; civil time and CPU frequency never drive elapsed guest time.</summary>
    public WhpxRealtimeClock() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    /// <summary>Allows an independent monotonic counter oracle without creating any WHP or host endpoint.</summary>
    public WhpxRealtimeClock(Func<long> counter, long frequency)
    {
        ArgumentNullException.ThrowIfNull(counter);
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        _counter = counter; _frequency = frequency;
    }

    /// <summary>Reports whether an explicit active clock window currently owns the host-time origin.</summary>
    public bool Active => _active;

    /// <summary>Starts or resumes from actual saved guest time; preceding pause and snapshot work are excluded.</summary>
    public void Begin(ulong guestNanoseconds)
    {
        if (_active) throw new InvalidOperationException("A WHP real-time window is already active.");
        _hostOrigin = _counter(); _guestOrigin = _last = guestNanoseconds; _active = true;
    }

    /// <summary>Observes every elapsed host tick once, retaining subnanosecond conversion residue through the common origin.</summary>
    public ulong Observe()
    {
        if (!_active) throw new InvalidOperationException("WHP guest time is paused.");
        long current = _counter();
        if (current < _hostOrigin) throw new InvalidOperationException("The monotonic host counter moved backwards.");
        ulong elapsed = checked((ulong)((UInt128)(ulong)(current - _hostOrigin) * 1_000_000_000 / (ulong)_frequency));
        ulong value = checked(_guestOrigin + elapsed);
        if (value < _last) throw new InvalidOperationException("The monotonic host counter moved backwards.");
        return _last = value;
    }

    /// <summary>Returns the remaining real duration to a guest deadline, never a synthetic CPU-time multiplier.</summary>
    public ulong Remaining(ulong deadline)
    {
        ulong now = Observe();
        return deadline > now ? deadline - now : 0;
    }

    /// <summary>Ends the active window without advancing authoritative guest state during a later pause.</summary>
    public void End()
    {
        if (!_active) throw new InvalidOperationException("WHP guest time is already paused.");
        _active = false;
    }
}
