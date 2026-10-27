// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Nanosecond virtual time, stable ordering and integer clock conversion with carried remainder.</summary>
public sealed partial class DeviceScheduler
{
    private readonly PriorityQueue<(long Id, Action Callback, object? Owner, string? Operation, Func<System.Text.Json.JsonElement[]>? Payload), (ulong Time, long Id)> _events = new();
    private readonly HashSet<long> _cancelled = [];
    private int _dispatchDepth;
    /// <summary>Whether a callback is currently executing instead of a resumable owner boundary.</summary>
    public bool IsDispatching => _dispatchDepth != 0;
    private long _sequence;
    private ulong _cycleRemainder;
    private uint _cachedCycles, _cachedFrequency;
    private ulong _cycleWhole, _cycleFraction;
    private ulong? _nextEventTime;
    /// <summary>Current monotonic virtual time in nanoseconds.</summary>
    public ulong Now { get; private set; }
    /// <summary>Earliest queued virtual device deadline in nanoseconds, or null when no event is scheduled.</summary>
    public ulong? NextEventTime => _nextEventTime;
    /// <summary>Caches the next queue deadline so ordinary CPU steps do not repeatedly inspect the priority queue.</summary>
    private void RefreshDeadline() => _nextEventTime = _events.TryPeek(out _, out var key) ? key.Time : null;

    /// <summary>Queues a virtual-time callback with stable equal-deadline ordering and returns its cancellation token.</summary>
    public long ScheduleAfter(ulong nanoseconds, Action callback, object? stateOwner = null, string? operation = null, Func<System.Text.Json.JsonElement[]>? payload = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        long id = checked(++_sequence);
        _events.Enqueue((id, callback, stateOwner, operation, payload), (checked(Now + nanoseconds), id));
        RefreshDeadline();
        return id;
    }
    /// <summary>Cancels the identified or currently scheduled component operation before it can publish another completion.</summary>
    public void Cancel(long id)
    {
        // Keep ordinary cancellation cheap, but do not let repeated device
        // reprogramming retain an unbounded heap of callbacks that cannot fire.
        if (_cancelled.Add(id) && (_cancelled.Count & 1023) == 0 &&
            (_cancelled.Count >= _events.Count / 2 || _events.Count > StateEventLimit))
            CompactCancelled();
    }
    /// <summary>Removes only dead queued callbacks, preserving all live deadlines, tokens and stable priority keys.</summary>
    private void CompactCancelled()
    {
        var live = _events.UnorderedItems.Where(v => !_cancelled.Contains(v.Element.Id)).ToArray();
        _events.Clear();
        foreach (var entry in live) _events.Enqueue(entry.Element, entry.Priority);
        // Already returned token IDs are never reused. Unknown future or invalid
        // negative IDs keep their former semantics and remain snapshot-invalid.
        _cancelled.RemoveWhere(id => id >= 0 && id <= _sequence);
        RefreshDeadline();
    }

    /// <summary>Samples completed virtual core ticks at an owner boundary, retaining the cycle-to-nanosecond phase.</summary>
    public ulong ClockTicks(uint frequency)
    {
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (IsDispatching) throw new InvalidOperationException("Clock ticks require an owner boundary outside a dispatched callback.");
        UInt128 numerator = (UInt128)Now * frequency;
        // AdvanceCycles rounds Now down and retains the exact remaining fraction. Restoring
        // that phase avoids losing one TSC tick at 33/90 MHz and never mutates clock caches.
        if (_cachedFrequency != 0) numerator += (UInt128)_cycleRemainder * frequency / _cachedFrequency;
        return unchecked((ulong)(numerator / 1_000_000_000));
    }

    /// <summary>Conservative budget ending before the next event or caller deadline.</summary>
    public int StepBudget(uint clocksPerStep, uint frequency, int maximum, ulong? deadline = null)
    {
        if (clocksPerStep == 0 || frequency == 0 || maximum < 0) throw new ArgumentOutOfRangeException();
        ulong? stop = _nextEventTime;
        if (deadline is { } d && (stop is null || d < stop)) stop = d;
        if (stop is not { } end) return maximum;
        if (end <= Now) return 0;
        UInt128 numerator = (UInt128)(end - Now) * frequency;
        if (numerator < _cycleRemainder) return 0;
        UInt128 steps = (numerator - _cycleRemainder) / ((ulong)clocksPerStep * 1_000_000_000);
        return (int)UInt128.Min((UInt128)(uint)maximum, steps);
    }

    /// <summary>Converts guest cycles to nanoseconds with integer carried remainder and runs due device events.</summary>
    public void AdvanceCycles(uint cycles, uint frequency)
    {
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (_cachedFrequency != frequency || _cachedCycles != cycles)
        {
            ulong delta = (ulong)cycles * 1_000_000_000UL;
            _cycleWhole = delta / frequency; _cycleFraction = delta % frequency;
            _cachedFrequency = frequency; _cachedCycles = cycles;
            ulong numerator = checked(delta + _cycleRemainder);
            _cycleRemainder = numerator % frequency;
            AdvanceTo(checked(Now + numerator / frequency)); return;
        }
        ulong remainder = _cycleRemainder + _cycleFraction;
        ulong carry = remainder >= frequency ? 1UL : 0;
        _cycleRemainder = remainder - carry * frequency;
        AdvanceTo(checked(Now + _cycleWhole + carry));
    }

    /// <summary>Runs due callbacks in deadline/order sequence and advances the virtual clock without host-time dependence.</summary>
    public void AdvanceTo(ulong target)
    {
        if (target < Now) throw new ArgumentOutOfRangeException(nameof(target));
        int count = 0;
        while (_nextEventTime is { } deadline && deadline <= target)
        {
            if (++count > 1_000_000) throw new InvalidOperationException("Device event loop failed to advance virtual time.");
            var item = _events.Dequeue();
            RefreshDeadline();
            Now = deadline;
            if (!_cancelled.Remove(item.Id))
            {
                _dispatchDepth++;
                try { item.Callback(); } finally { _dispatchDepth--; }
            }
        }
        Now = target;
    }
}
