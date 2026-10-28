// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Keyboard8042
{
    /// <summary>Asserts the own RSTDRV-wired controller reset, discarding old queued input and blocking new keyboard input until release.</summary>
    public void AssertPiixReset()
    {
        _output.Clear(); _expectedController = _expectedKeyboard = 0;
        _enabled = _scanning = false; _resetPulse = false;
        _commandByte = 0x65; _outputPort = 3; UnsupportedCommand = false;
        memory.A20Enabled = true; UpdateIrq();
    }
    /// <summary>Releases the own controller interface; external PS/2 device state remains powered and is not replayed.</summary>
    public void ReleasePiixReset()
    { _enabled = _scanning = true; _commandByte = (byte)(_mouse is null ? 0x45 : 0x65); UpdateIrq(); }
}
