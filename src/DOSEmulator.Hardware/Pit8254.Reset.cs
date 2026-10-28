// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Pit8254
{
    private bool _resetOutputLow;
    /// <summary>Stops the own PIIX timer at the current owner time and resets both implemented counter interfaces without clearing scheduler time or published pulse diagnostics.</summary>
    public void ApplyPiixHardReset()
    {
        if (_event != 0) scheduler.Cancel(_event);
        _event = 0;
        _running = false;
        _resetOutputLow = true;
        _reload = 65536;
        _access = 3; _low = 0; _mode = 3;
        _writeHigh = _readHigh = false;
        _latch = null; _statusLatch = null;
        _start = scheduler.Now; _remainder = 0;
        _unsupportedProgramming = false;
        SpeakerCounter.ApplyPiixHardReset();
    }
}

