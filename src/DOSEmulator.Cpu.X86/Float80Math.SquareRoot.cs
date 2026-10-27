// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    /// <summary>FSQRT, with exact integer remainder and a single rounding to PC/RC.</summary>
    public static Float80Result SquareRoot(Float80Value value, ushort control)
    {
        if (Unsupported(value)) return Special(Float80Value.Indefinite, Invalid, control);
        if (Nan(value)) return Special(value with { Significand = value.Significand | QuietBit }, Signaling(value) ? Invalid : (ushort)0, control);
        if (Zero(value)) return new(value); // Includes negative zero.
        if (value.Negative) return Special(Float80Value.Indefinite, Invalid, control);
        if (Infinity(value)) return new(value);
        ushort flags = DenormalOperand(value) ? Denormal : (ushort)0;
        if ((flags & ~control) != 0) return new(value, flags, false, false);

        int precision = PrecisionBits(control);
        int scale = Scale(value);
        // Arithmetic shift is floor division, including negative odd exponents.
        int exponent = (checked((int)new BigInteger(value.Significand).GetBitLength()) - 1 + scale) >> 1;
        int shift = scale - 2 * (exponent - precision + 1);
        BigInteger numerator = value.Significand, denominator = BigInteger.One;
        if (shift >= 0) numerator <<= shift; else denominator <<= -shift;
        BigInteger significand = IntegerSquareRoot(numerator / denominator);
        bool inexact = significand * significand * denominator != numerator;
        BigInteger midpoint = (significand << 1) + 1;
        int comparison = (numerator << 2).CompareTo(midpoint * midpoint * denominator);
        bool up = inexact && ((control >> 10 & 3) switch
        {
            0 => comparison > 0 || comparison == 0 && !significand.IsEven,
            2 => true,
            _ => false
        });
        if (up) significand++;
        if (significand == BigInteger.One << precision) { significand >>= 1; exponent++; }
        // Every finite positive extended input has a normal, finite square root.
        var result = new Float80Value((ulong)(significand << (64 - precision)), (ushort)(exponent + Bias));
        return new(result, (ushort)(flags | (inexact ? Precision : 0)), up);
    }

    /// <summary>Computes the floor integer square root with a monotone Newton iteration and no floating-point approximation.</summary>
    private static BigInteger IntegerSquareRoot(BigInteger value)
    {
        if (value.IsZero) return BigInteger.Zero;
        BigInteger current = BigInteger.One << checked((int)((value.GetBitLength() + 1) / 2));
        while (true)
        {
            BigInteger next = (current + value / current) >> 1;
            if (next >= current) return current;
            current = next;
        }
    }
}
