// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class ParallelPrinterPort
{
    /// <summary>Resets only board output latches and informs the existing peer; external pins and already accepted printer bytes retain their ownership.</summary>
    public void ApplyPiixHardReset()
    {
        bool changed = _data != 0 || _control != 0;
        if (_data != 0) DataChangedAt = scheduler.Now;
        _data = _control = 0;
        UpdateInterrupt();
        if (changed) OutputsChanged?.Invoke(new(scheduler.Now, _data, _control));
    }
}