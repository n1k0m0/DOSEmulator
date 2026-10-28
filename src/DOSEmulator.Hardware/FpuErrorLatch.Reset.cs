// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class FpuErrorLatch
{
    /// <summary>Clears the RSTDRV-wired AT latch after CPURST has actually lowered FERR; no IRQ or acknowledgement history is rewound.</summary>
    public void ApplyPiixHardReset()
    { FerrAsserted = InterruptAsserted = IgnoreNumericError = false; interrupt(false); ignore(false); }
}
