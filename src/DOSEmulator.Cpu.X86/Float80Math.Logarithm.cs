// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    /// <summary>FYL2X/FYL2XP1: fused, full-precision integer interval logarithm and multiplication.</summary>
    public static Float80Result Log2Multiply(Float80Value x, Float80Value y, bool plusOne, ushort control)
    {
        if (Unsupported(x) || Unsupported(y) || Nan(x) || Nan(y))
            return Calculate(x, y, Float80Operation.Add, control); // Shared two-source NaN/unsupported contract only.

        BigInteger n = x.Significand, d = BigInteger.One;
        int scale = Scale(x);
        if (scale >= 0) n <<= scale; else d <<= -scale;
        if (x.Negative) n = -n;
        if (plusOne) n += d;
        if (x.Negative && Infinity(x) || !Infinity(x) && n.Sign < 0 ||
            n.IsZero && Zero(y) || (plusOne ? Zero(x) : n == d) && Infinity(y) || Infinity(x) && Zero(y))
            return Special(Float80Value.Indefinite, Invalid, control);
        bool logNegative = plusOne ? x.Negative : n < d;
        bool negative = logNegative ^ y.Negative;
        if (n.IsZero)
            return Special(SignedInfinity(!y.Negative), Infinity(y) ? (ushort)0 : ZeroDivide, control);

        ushort initial = DenormalOperand(x) || DenormalOperand(y) ? Denormal : (ushort)0;
        if ((initial & ~control) != 0) return new(y, initial, false, false);
        if (Infinity(x) || Infinity(y)) return new(SignedInfinity(negative), initial);
        if (Zero(y) || (plusOne ? Zero(x) : n == d)) return new(SignedZero(negative), initial);

        // log2 of an exact power of two is integral. Resolve it explicitly:
        // outward transcendental bounds would straddle an exact rounding boundary.
        int k = FloorLog(n, d);
        BigInteger mn = k < 0 ? n << -k : n, md = k >= 0 ? d << k : d;
        if (mn == md)
            return EncodeLogBound(BigInteger.Abs(k) * y.Significand, BigInteger.One, Scale(y), negative, control, initial);

        for (int bits = 192; ; bits = checked(bits + 128))
        {
            var logTwo = LogTwoBounds.GetOrAdd(bits, ComputeLogTwoBounds);
            BigInteger unit = BigInteger.One << bits;
            Float80Result a, b;
            if (plusOne && (x.Exponent < Bias - 1 || x.Exponent == Bias - 1 && x.Significand == IntegerBit))
            {
                // ln(1+x)/x = 2/(2+x) * sum((x/(2+x))^(2j)/(2j+1)).
                // Keep x as a separate exact factor, even for 2^-16445.
                var h = LogOnePlusRatioBounds(x, bits, unit);
                BigInteger product = (BigInteger)x.Significand * y.Significand;
                a = EncodeLogBound(product * h.Low, logTwo.High, Scale(x) + Scale(y), negative, control, initial);
                b = EncodeLogBound(product * h.High, logTwo.Low, Scale(x) + Scale(y), negative, control, initial);
            }
            else
            {
                var log = LogMantissaBounds(mn, md, unit);
                BigInteger lo = k * logTwo.High + log.Low, hi = k * logTwo.Low + log.High;
                a = EncodeLogBound(BigInteger.Abs(lo) * y.Significand, logTwo.High, Scale(y), negative, control, initial);
                b = EncodeLogBound(BigInteger.Abs(hi) * y.Significand, logTwo.Low, Scale(y), negative, control, initial);
            }
            if (a == b) return a;
        }
    }

    /// <summary>Rounds the complete logarithmic-product bound without prematurely rounding its factors.</summary>
    private static Float80Result EncodeLogBound(BigInteger n, BigInteger d, int scale, bool negative, ushort control, ushort initial)
    {
        int exponent = FloorLog(n, d) + scale;
        if (RoundInteger(n, d, scale - (exponent - 63), negative, control >> 10 & 3).Value == BigInteger.One << 64) exponent++;
        var r = Quantize(n, d, scale, negative, 64, Emin, Emax, control, true);
        // Like the reference x87 transcendental path, nonzero finite logarithm
        // products report PE even when a particular power-of-two case is exact.
        return new(ToExtended(r, negative, 64), (ushort)(initial | r.Flags | Precision | (exponent < Emin ? Underflow : 0)), r.Up, r.Commit);
    }

    /// <summary>Bounds ln(m) for a normalized rational mantissa using an outward-rounded atanh series.</summary>
    private static (BigInteger Low, BigInteger High) LogMantissaBounds(BigInteger n, BigInteger d, BigInteger unit)
    {
        // 1 <= n/d < 2, hence 0 <= z=(n-d)/(n+d) < 1/3.
        BigInteger zl = (n - d) * unit / (n + d), zh = CeilingDivide((n - d) * unit, n + d);
        BigInteger ul = zl * zl / unit, uh = CeilingDivide(zh * zh, unit);
        BigInteger tl = zl, th = zh, sl = zl, sh = zh;
        for (int j = 1; ; j++)
        {
            tl = tl * ul / unit; th = CeilingDivide(th * uh, unit);
            sl += tl / (2 * j + 1); sh += CeilingDivide(th, 2 * j + 1);
            if (th <= 1) break;
        }
        return (sl * 2, (sh + 2) * 2);
    }

    /// <summary>Bounds ln(1+x)/x while retaining tiny x explicitly instead of rounding 1+x to one.</summary>
    private static (BigInteger Low, BigInteger High) LogOnePlusRatioBounds(Float80Value x, int bits, BigInteger unit)
    {
        int shift = Scale(x) + bits;
        BigInteger xl = shift >= 0 ? (BigInteger)x.Significand << shift : (BigInteger)x.Significand >> -shift;
        BigInteger xh = shift >= 0 ? xl : CeilingDivide(x.Significand, BigInteger.One << -shift);
        BigInteger dl = 2 * unit + (x.Negative ? -xh : xl), dh = 2 * unit + (x.Negative ? -xl : xh);
        BigInteger zl = xl * unit / dh, zh = CeilingDivide(xh * unit, dl);
        BigInteger ul = zl * zl / unit, uh = CeilingDivide(zh * zh, unit);
        BigInteger tl = unit, th = unit, sl = unit, sh = unit;
        for (int j = 1; ; j++)
        {
            tl = tl * ul / unit; th = CeilingDivide(th * uh, unit);
            sl += tl / (2 * j + 1); sh += CeilingDivide(th, 2 * j + 1);
            if (th <= 1) break;
        }
        // Here z^2 <= 1/9. The omitted positive tail is < two units.
        return (2 * unit * sl / dh, CeilingDivide(2 * unit * (sh + 2), dl));
    }
}
