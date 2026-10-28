// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Reports actual virtual/host elapsed time, including host waits; it never drives the scheduler.</summary>
public sealed class RealtimeSpeedMeter
{
    private ulong _virtualOrigin, _hostOrigin;
    private bool _active;
    /// <summary>Most recent complete host-second measurement; null while paused or warming up.</summary>
    public double? Percent { get; private set; }

    /// <summary>Starts a new measurement after replacement, pause or an explicit pacing transition.</summary>
    public void Reset() { _active = false; Percent = null; }

    /// <summary>Measures full windows without including time spent paused or changing the virtual clock.</summary>
    public void Observe(ulong virtualNow, ulong hostNow, bool running)
    {
        if (!running) { Reset(); return; }
        if (!_active || hostNow < _hostOrigin || virtualNow < _virtualOrigin)
        {
            _virtualOrigin = virtualNow; _hostOrigin = hostNow; _active = true; Percent = null; return;
        }
        ulong elapsed = hostNow - _hostOrigin;
        if (elapsed < 1_000_000_000) return;
        Percent = (double)(virtualNow - _virtualOrigin) / elapsed * 100;
        _virtualOrigin = virtualNow; _hostOrigin = hostNow;
    }
}
