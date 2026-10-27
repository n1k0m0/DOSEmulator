// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System;

namespace SharpPixels;

/// <summary>Monotonic host-repeat admission. Missed periods are discarded; zero interval disables repeats.</summary>
internal sealed class KeyRepeatClock
{
    private long _last;
    private bool _armed;
    private bool _repeating;

    /// <summary>Discards the held-key deadline when focus, input ownership or the repeat context is lost.</summary>
    public void Reset() { _last = 0; _armed = false; _repeating = false; }

    /// <summary>Starts a fresh initial-delay window after an actual key event or input-context change.</summary>
    public void Restart(long monotonicMilliseconds)
    {
        if (monotonicMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
        _last = monotonicMilliseconds; _armed = true; _repeating = false;
    }

    /// <summary>Preserves the former equal initial/interval contract for callers without a separate initial delay.</summary>
    public bool TryTake(long monotonicMilliseconds, uint intervalMilliseconds, bool enabled) =>
        TryTake(monotonicMilliseconds, intervalMilliseconds, intervalMilliseconds, enabled);

    /// <summary>Allows the first held-key repeat only after its initial delay, then one batch per interval without catch-up debt.</summary>
    public bool TryTake(long monotonicMilliseconds, uint initialDelayMilliseconds, uint intervalMilliseconds, bool enabled)
    {
        if (monotonicMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
        if (!enabled || intervalMilliseconds == 0) { Reset(); return false; }
        if (!_armed || monotonicMilliseconds < _last) { Restart(monotonicMilliseconds); return false; }
        if (monotonicMilliseconds - _last < (_repeating ? intervalMilliseconds : initialDelayMilliseconds)) return false;
        _last = monotonicMilliseconds; _repeating = true; return true;
    }
    /// <summary>Anchors an admitted batch at callback completion without restarting the initial delay or reviving cleared held keys.</summary>
    public void CompleteRepeat(long monotonicMilliseconds)
    {
        if (monotonicMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
        if (_armed) _last = monotonicMilliseconds;
    }

}
