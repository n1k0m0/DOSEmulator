// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80SinCosResult(Float80Value Sine, Float80Value Cosine,
    ushort Exceptions = 0, bool RoundedUp = false, bool Commit = true, bool OutOfRange = false);

public static partial class Float80Math
{
    /// <summary>FSINCOS: both independently enclosed results, one architectural commit.</summary>
    public static Float80SinCosResult SinCosPair(Float80Value x, ushort control)
    {
        var sine = SineCosine(x, false, control);
        var cosine = SineCosine(x, true, control);
        // In the reference contract C1 describes the sine's rounding, rather
        // than the pushed cosine or the OR of two independent rounding bits.
        return new(sine.Number.Value, cosine.Number.Value,
            (ushort)(sine.Number.Exceptions | cosine.Number.Exceptions), sine.Number.RoundedUp,
            sine.Number.Commit && cosine.Number.Commit, sine.OutOfRange || cosine.OutOfRange);
    }
}
