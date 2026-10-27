// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80TangentResult(Float80Result Number, Float80Value Pushed, bool OutOfRange = false);

public static partial class Float80Math
{
    /// <summary>FPTAN: outward integer argument reduction and a single full-precision rounding.</summary>
    public static Float80TangentResult Tangent(Float80Value x, ushort control)
    {
        if (Unsupported(x) || Infinity(x))
            return new(Special(Float80Value.Indefinite, Invalid, control), Float80Value.Indefinite);
        if (Nan(x))
        {
            var quiet = x with { Significand = x.Significand | QuietBit };
            return new(Special(quiet, Signaling(x) ? Invalid : (ushort)0, control), quiet);
        }
        if (x.Exponent >= Bias + 63) return new(new(x, Commit: false), x, true);
        ushort initial = DenormalOperand(x) ? Denormal : (ushort)0;
        if ((initial & ~control) != 0) return new(new(x, initial, false, false), Float80Value.One);
        if (Zero(x)) return new(new(x), Float80Value.One);

        BigInteger n = x.Significand, d = BigInteger.One;
        int scale = Scale(x);
        if (scale >= 0) n <<= scale; else d <<= -scale;
        if ((n << 64) < d)
        {
            // For 0 < z < 2^-64, z+z^3/3 < tan(z) < z+z^3.
            // Keep these strict rational endpoints: fixed point around z would
            // erase the direction at exact representable tiny operands.
            BigInteger nn = n*n, dd = d*d;
            var a = EncodeLogBound(n*(3*dd+nn), 3*d*dd, 0, x.Negative, control, initial);
            var b = EncodeLogBound(n*(dd+nn), d*dd, 0, x.Negative, control, initial);
            if (a == b) return new(a, Float80Value.One);
        }
        for (int bits = 256; ; bits = checked(bits + 128))
        {
            BigInteger unit = BigInteger.One << bits;
            var pi = PiBounds.GetOrAdd(bits, ComputePiBounds);
            // Select the same nearest multiple of pi/2 at both endpoints.
            // The pi enclosure is refined before any ambiguous reduction.
            BigInteger kl = (4*n*unit + d*pi.High)/(2*d*pi.High);
            BigInteger kh = (4*n*unit + d*pi.Low)/(2*d*pi.Low);
            if (kl != kh) continue;
            BigInteger lo = n*unit/d - CeilingDivide(kl*pi.High, 2);
            BigInteger hi = CeilingDivide(n*unit, d) - kl*pi.Low/2;
            if (lo <= 0 && hi >= 0) continue;
            // Leave enough integer units for a strictly positive sine bound
            // before dividing to form cotangent near a reduction boundary.
            if (BigInteger.Abs(lo) < 8 || BigInteger.Abs(hi) < 8) continue;
            var low = TangentReducedBound(lo, unit, kl.IsEven, control, x.Negative, initial, false);
            var high = TangentReducedBound(hi, unit, kl.IsEven, control, x.Negative, initial, true);
            if (low == high) return new(low, Float80Value.One);
        }
    }

    /// <summary>Combines reduced sin/cos bounds into a quadrant-selected tangent endpoint with directed rounding.</summary>
    private static Float80Result TangentReducedBound(BigInteger point, BigInteger unit, bool even,
        ushort control, bool negative, ushort initial, bool upper)
    {
        bool belowZero = point.Sign < 0;
        BigInteger r = BigInteger.Abs(point);
        var s = TrigSeriesBounds(r, unit, true);
        var c = TrigSeriesBounds(r, unit, false);
        BigInteger sl = r*s.Low/unit, sh = CeilingDivide(r*s.High, unit);
        // tan(r) or -cot(r); either function is strictly increasing on the
        // chosen branch. Reverse magnitude bounds for negative endpoints.
        bool resultNegative = belowZero ^ !even;
        bool magnitudeUpper = upper ^ resultNegative;
        BigInteger numerator = even ? (magnitudeUpper ? sh : sl) : (magnitudeUpper ? c.High : c.Low);
        BigInteger denominator = even ? (magnitudeUpper ? c.Low : c.High) : (magnitudeUpper ? sl : sh);
        return EncodeLogBound(numerator, denominator, 0, negative ^ resultNegative, control, initial);
    }

    // sin(r)/r or cos(r), |r| <= pi/4. Terms and every integer division
    // round outwards; the alternating omitted tail is below two units.
    private static (BigInteger Low, BigInteger High) TrigSeriesBounds(BigInteger r, BigInteger unit, bool sine)
    {
        BigInteger ul = r*r/unit, uh = CeilingDivide(r*r, unit);
        BigInteger tl = unit, th = unit, sl = unit, sh = unit;
        for (int j = 1; ; j++)
        {
            BigInteger divisor = (BigInteger)(2*j)*(2*j+(sine ? 1 : -1))*unit;
            tl = tl*ul/divisor; th = CeilingDivide(th*uh, divisor);
            if ((j&1) != 0) { sl -= th; sh -= tl; } else { sl += tl; sh += th; }
            if (th <= 1) return (sl-2, sh+2);
        }
    }
}
