// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>AT-style 486 external numeric-error latch. No cycle/pin propagation timing model.</summary>
public sealed partial class FpuErrorLatch(Action<bool> interrupt, Action<bool> ignore) : IIoPortDevice
{
    /// <summary>Actual FERR input/error level, independent from the AT IRQ13 latch acknowledgement.</summary>
    public bool FerrAsserted { get; private set; }
    /// <summary>Externally observable interrupt line after the component's enable/mask policy.</summary>
    public bool InterruptAsserted { get; private set; }
    /// <summary>External IGNNE input level, independent from FERR status and CR0.NE.</summary>
    public bool IgnoreNumericError { get; private set; }
    /// <summary>Count of actual numeric-error input edges observed by the AT error-latch device.</summary>
    public long ErrorEdges { get; private set; }
    /// <summary>Count of actual port-F0 acknowledgements of the AT error latch.</summary>
    public long Acknowledgements { get; private set; }

    /// <summary>Latches an actual FERR edge and preserves the AT error/IRQ13 acknowledgement contract.</summary>
    public void SetFerr(bool asserted)
    {
        if (FerrAsserted == asserted) return;
        FerrAsserted = asserted;
        if (asserted)
        {
            ErrorEdges++;
            InterruptAsserted = true;
            interrupt(true);
        }
        else
        {
            IgnoreNumericError = false;
            ignore(false);
            // Clearing the FPU alone does not clear the AT latch or PIC IRR.
        }
    }

    /// <summary>Returns the reference AT numeric-error port's implemented read behavior.</summary>
    public uint Read(ushort port, int width) => width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
    /// <summary>Acknowledges the AT error latch at byte port F0 without silently clearing x87 status or PIC state.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (port != 0xf0 || width != 1) return;
        Acknowledgements++;
        InterruptAsserted = false;
        interrupt(false);
        IgnoreNumericError = FerrAsserted;
        ignore(IgnoreNumericError);
    }
}
