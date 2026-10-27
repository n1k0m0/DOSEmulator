// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System;

namespace SharpPixels;

/// <summary>Monotonic host-repeat admission. Missed periods are discarded; zero interval disables repeats.</summary>
internal sealed class KeyRepeatClock
{
    private long _last;
    private bool _armed;

    /// <summary>Discards the held-key deadline when focus, input ownership or the repeat context is lost.</summary>
    public void Reset() { _last = 0; _armed = false; }

    /// <summary>Starts a fresh interval after an actual key event or completed callback, including a modal stall.</summary>
    public void Restart(long monotonicMilliseconds)
    {
        if (monotonicMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
        _last = monotonicMilliseconds; _armed = true;
    }

    /// <summary>Allows at most one batch now and reanchors from now rather than accumulating historical deadlines.</summary>
    public bool TryTake(long monotonicMilliseconds, uint intervalMilliseconds, bool enabled)
    {
        if (monotonicMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
        if (!enabled || intervalMilliseconds == 0) { Reset(); return false; }
        if (!_armed || monotonicMilliseconds < _last) { Restart(monotonicMilliseconds); return false; }
        if (monotonicMilliseconds - _last < intervalMilliseconds) return false;
        _last = monotonicMilliseconds; return true;
    }
}
