// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Mpu401Uart
{
    /// <summary>Resets internal UART/command state without a command acknowledgement or rewinding already started outer MIDI frames.</summary>
    public void ApplyPiixHardReset()
    {
        if (_commandEvent != 0) { _clock.Cancel(_commandEvent); _commandEvent = 0; }
        if (_transmitEvent != 0) { _clock.Cancel(_transmitEvent); _transmitEvent = 0; }
        if (_transmitting) AbortedOutputBytes++;
        if (_holding is not null) AbortedOutputBytes++;
        _transmitting = _busy = false;
        _holding = null; _transmitByte = 0; _transmitStart = 0;
        unchecked { _generation++; }
        UartMode = false; _received.Clear();
        UpdateInterrupt();
    }
}