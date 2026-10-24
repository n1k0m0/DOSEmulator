namespace DOSEmulator.Runtime;

/// <summary>Host pacing only: bounds catch-up slices and rebases a late host-time anchor.
/// Never advances or rewinds the machine clock, skips guest work, or changes device rates.</summary>
public sealed class RealtimePacer
{
    public const ulong MaximumSliceNanoseconds = 20_000_000;
    public const ulong MaximumCatchUpNanoseconds = 100_000_000;
    private ulong _virtualOrigin, _hostOrigin;
    private readonly ulong _lead;
    public long Resynchronizations { get; private set; }
    public RealtimePacer(ulong leadNanoseconds = 0)
    {
        if (leadNanoseconds >= MaximumCatchUpNanoseconds) throw new ArgumentOutOfRangeException(nameof(leadNanoseconds));
        _lead = leadNanoseconds;
    }
    public void Reset(ulong virtualNow, ulong hostNow) { _virtualOrigin = virtualNow; _hostOrigin = hostNow; }
    private ulong Desired(ulong hostNow)
    {
        if (hostNow < _hostOrigin) throw new ArgumentOutOfRangeException(nameof(hostNow));
        return checked(_virtualOrigin + (hostNow - _hostOrigin) + _lead);
    }
    public ulong Target(ulong virtualNow, ulong hostNow)
    {
        ulong desired = Desired(hostNow);
        if (desired > virtualNow && desired - virtualNow > MaximumCatchUpNanoseconds)
        {
            Reset(virtualNow, hostNow); Resynchronizations++; return virtualNow;
        }
        return Math.Max(virtualNow, Math.Min(desired, checked(virtualNow + MaximumSliceNanoseconds)));
    }
    public int DelayMilliseconds(ulong virtualNow, ulong hostNow)
    {
        ulong desired = Desired(hostNow);
        return desired > virtualNow && desired - virtualNow > MaximumSliceNanoseconds ? 1 : 16;
    }
}
