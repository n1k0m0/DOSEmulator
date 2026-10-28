// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

internal sealed partial class AtaPioDrive
{
    /// <summary>Asserts the own IDE reset pin deterministically even when guest SRST was already high; old commits are cancelled without touching media.</summary>
    public void AssertPiixReset()
    { _resetActive = false; _control = 0; Reset(); Write(0x3f6, 4, 1); }
}
