// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80Extraction(Float80Value Significand, Float80Value Exponent, ushort Exceptions = 0, bool Commit = true);

public static partial class Float80Math
{
    /// <summary>Exact FXTRACT pair. Precision and rounding controls cannot shorten either result.</summary>
    public static Float80Extraction Extract(Float80Value source, ushort control)
    {
        if (Unsupported(source)) return new(Float80Value.Indefinite, Float80Value.Indefinite, Invalid, (control & Invalid) != 0);
        if (Nan(source))
        {
            ushort flags = Signaling(source) ? Invalid : (ushort)0;
            var quiet = source with { Significand = source.Significand | QuietBit };
            return new(quiet, quiet, flags, (flags & ~control) == 0);
        }
        if (Infinity(source)) return new(source, SignedInfinity(false));
        if (Zero(source)) return new(source, SignedInfinity(true), ZeroDivide, (control & ZeroDivide) != 0);

        ushort exceptions = DenormalOperand(source) ? Denormal : (ushort)0;
        int leading = BitOperations.LeadingZeroCount(source.Significand);
        var significand = new Float80Value(source.Significand << leading, (ushort)(Bias | (source.Negative ? 0x8000 : 0)));
        var exponent = FromInteger(Math.Max(1, source.Exponent) - Bias - leading);
        return new(significand, exponent, exceptions, (exceptions & ~control) == 0);
    }
}
