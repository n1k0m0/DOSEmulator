namespace DOSEmulator.Core;

/// <summary>Nanosecond virtual time, stable ordering and integer clock conversion with carried remainder.</summary>
public sealed class DeviceScheduler
{
    private readonly PriorityQueue<(long Id, Action Callback), (ulong Time, long Id)> _events = new();
    private readonly HashSet<long> _cancelled = [];
    private long _sequence;
    private ulong _cycleRemainder;
    public ulong Now { get; private set; }
    public ulong? NextEventTime => _events.TryPeek(out _, out var key) ? key.Time : null;

    public long ScheduleAfter(ulong nanoseconds, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        long id = checked(++_sequence);
        _events.Enqueue((id, callback), (checked(Now + nanoseconds), id));
        return id;
    }
    public void Cancel(long id) => _cancelled.Add(id);

    public void AdvanceCycles(uint cycles, uint frequency)
    {
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        ulong numerator = checked((ulong)cycles * 1_000_000_000UL + _cycleRemainder);
        _cycleRemainder = numerator % frequency;
        AdvanceTo(checked(Now + numerator / frequency));
    }

    public void AdvanceTo(ulong target)
    {
        if (target < Now) throw new ArgumentOutOfRangeException(nameof(target));
        int count = 0;
        while (_events.TryPeek(out _, out var key) && key.Time <= target)
        {
            if (++count > 1_000_000) throw new InvalidOperationException("Device event loop failed to advance virtual time.");
            var item = _events.Dequeue();
            Now = key.Time;
            if (!_cancelled.Remove(item.Id)) item.Callback();
        }
        Now = target;
    }
}
