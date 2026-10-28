// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class SoundBlaster16
{
    /// <summary>Asserts the own ISA reset wiring, cancelling old DSP work and restoring configured jumpers while retaining prior PCM integration.</summary>
    public void AssertPiixReset(int irq, int dma8, int dma16)
    {
        _resetHigh = false; Reset(true);
        _xbus05Control = 0;
        _interruptSources = 0; UpdateIrq();
        Irq = irq; Dma8Channel = dma8; Dma16Channel = dma16;
        _mixerIndex = 0; ResetMixer(); Array.Clear(_parameters);
    }
    /// <summary>Deasserts ISA reset through the existing DSP ready/AA delay after the minimum high interval.</summary>
    public void ReleasePiixReset() => Reset(false);
}
