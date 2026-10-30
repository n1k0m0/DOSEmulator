// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Host pacing only: bounds catch-up slices and rebases a late host-time anchor.
/// Never advances or rewinds the machine clock, skips guest work, or changes device rates.</summary>
public sealed class RealtimePacer
{
    public const ulong MaximumSliceNanoseconds = 20_000_000;
    public const ulong MaximumCatchUpNanoseconds = 100_000_000;
    private ulong _virtualOrigin, _hostOrigin;
    private readonly ulong _lead;
    private readonly ulong _maximumCatchUp;
    /// <summary>Diagnostic count of resynchronizations.</summary>
    public long Resynchronizations { get; private set; }
    /// <summary>Initializes RealtimePacer and connects the supplied dependencies before the component is used.</summary>
    public RealtimePacer(ulong leadNanoseconds = 0, ulong maximumCatchUpNanoseconds = MaximumCatchUpNanoseconds)
    {
        if (maximumCatchUpNanoseconds is < MaximumSliceNanoseconds or > 60_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(maximumCatchUpNanoseconds));
        if (leadNanoseconds >= maximumCatchUpNanoseconds) throw new ArgumentOutOfRangeException(nameof(leadNanoseconds));
        _lead = leadNanoseconds; _maximumCatchUp = maximumCatchUpNanoseconds;
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset(ulong virtualNow, ulong hostNow) { _virtualOrigin = virtualNow; _hostOrigin = hostNow; }
    /// <summary>Computes the nominal guest target from the monotonic host origin and allowed audio lead.</summary>
    private ulong Desired(ulong hostNow)
    {
        if (hostNow < _hostOrigin) throw new ArgumentOutOfRangeException(nameof(hostNow));
        return checked(_virtualOrigin + (hostNow - _hostOrigin) + _lead);
    }
    /// <summary>Computes the guest-time execution target from the host pacing origin and bounded audio lead.</summary>
    public ulong Target(ulong virtualNow, ulong hostNow)
    {
        ulong desired = Desired(hostNow);
        if (desired > virtualNow && desired - virtualNow > _maximumCatchUp)
        {
            Reset(virtualNow, hostNow); Resynchronizations++; return virtualNow;
        }
        return Math.Max(virtualNow, Math.Min(desired, checked(virtualNow + MaximumSliceNanoseconds)));
    }
    /// <summary>Chooses a bounded host wait without changing the guest's nominal timing model.</summary>
    public int DelayMilliseconds(ulong virtualNow, ulong hostNow)
        => DelayMilliseconds(virtualNow, hostNow, false);
    /// <summary>Coalesces sub-millisecond idle work into one host wait; an executing CPU behind its target never sleeps here.</summary>
    public int DelayMilliseconds(ulong virtualNow, ulong hostNow, bool processorHalted)
    {
        ulong desired = Desired(hostNow);
        if (desired > virtualNow) return processorHalted && desired - virtualNow < 1_000_000 ? 1 : 0;
        if (desired == virtualNow) return 1;
        return (int)Math.Clamp((virtualNow - desired + 999_999) / 1_000_000, 1UL, 16UL);
    }
}
