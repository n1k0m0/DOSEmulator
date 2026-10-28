// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class DevelopmentBoardPorts
{
    /// <summary>Resets only the own board's port-92 latch, retaining already published POST and E9 diagnostics.</summary>
    public void ApplyPiixHardReset()
    { _port92 = 2; ResetRequested = false; memory.A20Enabled = true; }
}
