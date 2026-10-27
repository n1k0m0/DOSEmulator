// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80Remainder(Float80Value Value, ushort Exceptions = 0, ushort Conditions = 0,
    bool Commit = true, ushort ConditionMask = 0x4700);

public static partial class Float80Math
{
    /// <summary>One architecturally visible FPREM/FPREM1 reduction. No host floats or PC/RC rounding.</summary>
    public static Float80Remainder Remainder(Float80Value dividend, Float80Value divisor, bool nearest, ushort control)
    {
        if (Unsupported(dividend) || Unsupported(divisor)) return RemainderSpecial(Special(Float80Value.Indefinite, Invalid, control));
        if (Nan(dividend) || Nan(divisor)) return RemainderSpecial(Calculate(dividend, divisor, Float80Operation.Add, control));
        if (Infinity(dividend) || Zero(divisor)) return RemainderSpecial(Special(Float80Value.Indefinite, Invalid, control));
        ushort flags = DenormalOperand(dividend) || DenormalOperand(divisor) ? Denormal : (ushort)0;
        if ((flags & ~control) != 0) return new(dividend, flags, Commit: false, ConditionMask: 0x600);
        if (Zero(dividend)) return new(dividend, flags);
        if (Infinity(divisor))
        {
            var unchanged = Quantize(dividend.Significand, BigInteger.One, Scale(dividend), dividend.Negative, 64, Emin, Emax, control, true);
            return new(ToExtended(unchanged, dividend.Negative, 64), (ushort)(flags | unchanged.Flags));
        }

        int leftScale = Scale(dividend), rightScale = Scale(divisor);
        int difference = 63 - BitOperations.LeadingZeroCount(dividend.Significand) + leftScale -
                         (63 - BitOperations.LeadingZeroCount(divisor.Significand) + rightScale);
        bool partial = difference >= 64;
        // AMD's documented reference reduction: N=32+(D mod 32), within Intel's
        // implementation-dependent 32..63 interval. A partial step remains one
        // guest instruction, so interrupts/debug/context switches can intervene.
        int factor = partial ? difference - (32 + (difference & 31)) : 0;
        int unit = Math.Min(leftScale, rightScale + factor);
        BigInteger left = (BigInteger)dividend.Significand << (leftScale - unit);
        BigInteger right = (BigInteger)divisor.Significand << (rightScale + factor - unit);
        BigInteger quotient = BigInteger.DivRem(left, right, out BigInteger remainder);
        if (nearest && !partial && (remainder * 2 > right || remainder * 2 == right && !quotient.IsEven))
        { quotient++; remainder -= right; }
        // Condition bits describe the magnitude of the quotient, independently
        // of operand signs. A zero remainder retains the dividend's sign.
        int q = (int)(quotient & 7);
        ushort conditions = partial ? (ushort)0x400 : (ushort)(((q & 4) << 6) | ((q & 2) << 13) | ((q & 1) << 9));
        bool negative = dividend.Negative ^ (remainder.Sign < 0);
        if (remainder.IsZero) return new(SignedZero(dividend.Negative), flags, conditions);
        var rounded = Quantize(BigInteger.Abs(remainder), BigInteger.One, unit, negative, 64, Emin, Emax, control, true);
        return new(ToExtended(rounded, negative, 64), (ushort)(flags | rounded.Flags), conditions, rounded.Commit);
    }

    /// <summary>Converts a special-value response into the documented remainder condition-code/commit contract.</summary>
    private static Float80Remainder RemainderSpecial(Float80Result result) =>
        new(result.Value, result.Exceptions, Commit: result.Commit, ConditionMask: 0x600);
}
