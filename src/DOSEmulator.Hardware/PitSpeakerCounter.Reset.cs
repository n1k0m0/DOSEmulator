// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class PitSpeakerCounter
{
    /// <summary>Integrates the old signal before clearing the own PIIX counter-two latches and its pending load; no published audio or external time is rewound.</summary>
    public void ApplyPiixHardReset()
    {
        BeforeChange?.Invoke();
        CancelLoad();
        _control = 0x36;
        _low = 0;
        _writeHigh = _readHigh = _latchHigh = _gate = _running = _loaded = false;
        _fixedOutput = false;
        _startsHigh = true;
        _reload = _programmed = 65536;
        _originTick = NowTick;
        _pausedElapsed = 0;
        _frozenCount = 0;
        _latchedCount = null; _latchedStatus = null;
        UnsupportedProgramming = false;
    }
}
