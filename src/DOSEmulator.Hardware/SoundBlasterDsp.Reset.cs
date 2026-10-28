// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class SoundBlasterDsp
{
    /// <summary>Asserts the own ISA reset signal, including the complete hardware parameter reset rather than the high-speed DSP escape policy.</summary>
    public void AssertPiixReset() { HighSpeed = false; _resetHigh = false; Reset(true); }
    /// <summary>Releases the own ISA reset signal through the real DSP readiness deadline.</summary>
    public void ReleasePiixReset() => Reset(false);
}
