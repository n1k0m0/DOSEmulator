// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class AtaPioController
{
    /// <summary>Asserts both devices' own board reset pins with a deterministic selected-drive latch, retaining their original media peers.</summary>
    public void AssertPiixReset()
    {
        foreach (var drive in _drives) drive.AssertPiixReset();
        _atapi?.ApplyPiixHardReset(); _selected = 0; _atapi?.Select(_packetIndex == 0); UpdateInterrupt();
    }
}
