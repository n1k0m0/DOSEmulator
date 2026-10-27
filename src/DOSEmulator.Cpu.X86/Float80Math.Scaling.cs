// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    /// <summary>FSCALE: truncate ST(1) toward zero, then scale ST(0) at full extended precision.</summary>
    public static Float80Result ScaleByPowerOfTwo(Float80Value value, Float80Value power, ushort control)
    {
        if (Unsupported(value) || Unsupported(power)) return Special(Float80Value.Indefinite, Invalid, control);
        // Use the same independently tested two-source NaN selection; no finite
        // addition is performed on this branch.
        if (Nan(value) || Nan(power)) return Calculate(value, power, Float80Operation.Add, control);
        if (Infinity(power) && (power.Negative ? Infinity(value) : Zero(value)))
            return Special(Float80Value.Indefinite, Invalid, control);
        ushort flags = DenormalOperand(value) || DenormalOperand(power) ? Denormal : (ushort)0;
        if ((flags & ~control) != 0) return new(value, flags, false, false);
        if (Infinity(power)) return new(power.Negative ? SignedZero(value.Negative) : SignedInfinity(value.Negative), flags);
        if (Zero(value) || Infinity(value)) return new(value, flags);

        int factor = TruncatedScaleFactor(power);
        // FSCALE is not controlled by PC. Only the exponent changes until
        // denormalization is needed; RC still controls that final rounding.
        var rounded = Quantize(value.Significand, BigInteger.One, Scale(value) + factor, value.Negative, 64, Emin, Emax, control, true);
        // Unlike elementary arithmetic, FSCALE can exceed the exponent range
        // even after the unmasked response's 24576 adjustment. The 486 response
        // is signed infinity/zero, including precision, independently of RC.
        if (rounded.Exponent > Emax)
            return new(SignedInfinity(value.Negative), (ushort)(flags | rounded.Flags | Precision), true);
        if (rounded.Exponent < Emin)
            return new(SignedZero(value.Negative), (ushort)(flags | rounded.Flags | Precision));
        return new(ToExtended(rounded, value.Negative, 64), (ushort)(flags | rounded.Flags), rounded.Up, rounded.Commit);
    }

    /// <summary>Truncates ST1 toward zero and bounds an exponent adjustment beyond all relevant extended ranges.</summary>
    private static int TruncatedScaleFactor(Float80Value power)
    {
        int shift = Scale(power);
        if (power.Significand == 0 || shift < -63) return 0;
        // +/-65536 already exceeds every finite extended exponent and every
        // recoverable unmasked exponent response. Larger values are equivalent;
        // avoid allocating an integer with an enormous number of exponent bits.
        BigInteger magnitude = shift >= 17 ? 65536 : shift >= 0 ? (BigInteger)power.Significand << shift : power.Significand >> -shift;
        int bounded = (int)BigInteger.Min(magnitude, 65536);
        return power.Negative ? -bounded : bounded;
    }
}
