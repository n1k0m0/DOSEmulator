// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Uart16550
{
    private long _receiveGeneration;

    /// <summary>Resets the own PIIX board's UART latches and outgoing work, retaining modem pins and the independent external receive timeline.</summary>
    public void ApplyPiixHardReset()
    {
        Cancel(ref _startEvent); Cancel(ref _txEvent); Cancel(ref _timeoutEvent);
        Cancel(ref _threEvent); Cancel(ref _breakEvent);
        unchecked { _receiveGeneration++; }
        Array.Clear(_rx); _rxHead = _rxCount = 0; _tx.Clear();
        _ier = _lcr = _mcr = _fcr = _scratch = _modemDelta = 0;
        _effectiveModem = _physicalModem;
        _divisor = 0;
        _overrun = _timeout = _txBusy = _burst = _breakDelivered = false;
        _thre = _threPending = true;
        _txRemainder = 0;
        UpdateInterrupt();
    }
}