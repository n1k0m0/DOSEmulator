// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

public sealed partial class DeviceScheduler
{
    private const int StateEventLimit = 100000;
    /// <summary>Captures the bounded queue including remaining cancellation entries; no callbacks or guest time advance.</summary>
    public SchedulerState SaveState(IReadOnlyDictionary<object, string> owners)
    {
        if (IsDispatching) throw new InvalidOperationException("Scheduler snapshots require an owner boundary outside a callback.");
        if (_events.Count > StateEventLimit || _cancelled.Count > StateEventLimit)
            throw new InvalidOperationException("Scheduler queue exceeds the snapshot resource limit.");
        var result = new List<ScheduledStateEvent>();
        foreach (var (item, priority) in _events.UnorderedItems.OrderBy(v => v.Priority))
        {
            if (item.Owner is null || item.Operation is null || !owners.TryGetValue(item.Owner, out string? name))
                throw new InvalidOperationException("Pending opaque callback cannot be included in a hardware snapshot.");
            result.Add(new(item.Id, priority.Time, name, item.Operation, item.Payload?.Invoke() ?? []));
        }
        var state = new SchedulerState(Now, _sequence, _cycleRemainder, _cachedCycles, _cachedFrequency, _cycleWhole, _cycleFraction,
            _cancelled.Order().ToArray(), result.ToArray());
        CheckState(state);
        return state;
    }

    /// <summary>Uses the same clock/token/size limits before both publication and import of a scheduler snapshot.</summary>
    private static void CheckState(SchedulerState state)
    {
        if (state.Sequence < 0 || state.Events is null || state.Cancelled is null || state.Events.Length > StateEventLimit ||
            state.Cancelled.Length > StateEventLimit || state.CachedFrequency == 0 && state.Remainder != 0 ||
            state.CachedFrequency != 0 && state.Remainder >= state.CachedFrequency ||
            state.Events.Any(v => v is null || v.Id <= 0 || v.Id > state.Sequence || v.Time < state.Now || v.Payload is null) ||
            state.Events.Select(v => v.Id).Distinct().Count() != state.Events.Length ||
            state.Cancelled.Any(v => v < 0 || v > state.Sequence) || state.Cancelled.Distinct().Count() != state.Cancelled.Length)
            throw new InvalidDataException("Invalid scheduler clock/token/deadline state.");
        if (state.CachedFrequency != 0 && (state.Whole != (ulong)state.CachedCycles * 1_000_000_000UL / state.CachedFrequency ||
            state.Fraction != (ulong)state.CachedCycles * 1_000_000_000UL % state.CachedFrequency))
            throw new InvalidDataException("Invalid scheduler conversion cache.");
    }

    /// <summary>Validates and binds every event before replacing a newly created scheduler's constructor events.</summary>
    public void LoadState(SchedulerState state, IReadOnlyDictionary<string, IStateObject> owners)
    {
        CheckState(state);
        var restored = state.Events.Select(v =>
        {
            if (!owners.TryGetValue(v.Owner, out var owner) || owner is not IScheduledStateObject component)
                throw new InvalidDataException("Unknown scheduled state owner.");
            return (Event: v, Owner: component, Callback: component.BindStateEvent(v.Operation, v.Payload));
        }).ToArray();
        _events.Clear(); _cancelled.Clear();
        Now = state.Now; _sequence = state.Sequence; _cycleRemainder = state.Remainder;
        _cachedCycles = state.CachedCycles; _cachedFrequency = state.CachedFrequency;
        _cycleWhole = state.Whole; _cycleFraction = state.Fraction;
        foreach (long id in state.Cancelled) _cancelled.Add(id);
        foreach (var (item, owner, callback) in restored)
            _events.Enqueue((item.Id, callback, owner, item.Operation, () => item.Payload), (item.Time, item.Id));
        RefreshDeadline();
    }
}
