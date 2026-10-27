// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections.Concurrent;
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    private static readonly ConcurrentDictionary<int, (BigInteger Low, BigInteger High)> LogTwoBounds = new();
    /// <summary>Computes an outward-rounded positive integer quotient for interval upper bounds.</summary>
    private static BigInteger CeilingDivide(BigInteger n, BigInteger d) => (n + d - 1) / d;

    /// <summary>F2XM1: integer interval evaluation of 2^x-1, with full extended precision.</summary>
    public static Float80Result Exp2MinusOne(Float80Value source, ushort control)
    {
        if (Unsupported(source)) return Special(Float80Value.Indefinite, Invalid, control);
        if (Nan(source)) return Special(source with { Significand = source.Significand | QuietBit }, Signaling(source) ? Invalid : (ushort)0, control);
        if (Zero(source)) return new(source);
        // Finite values outside [-1,1] are architecturally undefined. Keep their
        // raw bits under a documented deterministic diagnostic policy.
        if (Infinity(source)) return new(source.Negative ? FromInteger(-1) : source);
        if (source.Exponent > Bias || source.Exponent == Bias && source.Significand != IntegerBit)
            return new(source, Precision);
        ushort flags = DenormalOperand(source) ? Denormal : (ushort)0;
        if ((flags & ~control) != 0) return new(source, flags, false, false);
        // Nonzero finite F2XM1 sets PE in this reference model, including the
        // exact mathematical endpoints. C1 follows our mathematical rounding,
        // not an unobservable implementation's internal polynomial error.
        if (source.Exponent == Bias)
            return new(source.Negative ? new(IntegerBit, 0xbffe) : Float80Value.One, Precision);

        for (int bits = 192; ; bits = checked(bits + 128))
        {
            BigInteger unit = BigInteger.One << bits;
            var (low, high) = Exp2RatioBounds(source, bits, unit);
            var a = EncodeExpBound((BigInteger)source.Significand * low, unit, Scale(source), source.Negative, control, flags);
            var b = EncodeExpBound((BigInteger)source.Significand * high, unit, Scale(source), source.Negative, control, flags);
            if (a == b) return a;
        }
    }

    /// <summary>Rounds one exponential interval endpoint to the raw extended result and exception contract.</summary>
    private static Float80Result EncodeExpBound(BigInteger n, BigInteger d, int scale, bool negative, ushort control, ushort flags)
    {
        int exponent = FloorLog(n, d) + scale;
        var unbounded = RoundInteger(n, d, scale - (exponent - 63), negative, control >> 10 & 3);
        if (unbounded.Value == BigInteger.One << 64) exponent++;
        var rounded = Quantize(n, d, scale, negative, 64, Emin, Emax, control, true);
        return new(ToExtended(rounded, negative, 64), (ushort)(flags | Precision | (exponent < Emin ? Underflow : 0)), rounded.Up);
    }

    // H(x)=(2^x-1)/x = ln(2) * sum((x ln(2))^k/(k+1)!).
    // Computing H avoids catastrophic cancellation or a fixed-point zero when
    // x is as small as 2^-16445. Every integer operation rounds outwards.
    private static (BigInteger Low, BigInteger High) Exp2RatioBounds(Float80Value source, int bits, BigInteger unit)
    {
        var log = LogTwoBounds.GetOrAdd(bits, ComputeLogTwoBounds);
        int shift = Scale(source) + bits;
        BigInteger raw = source.Significand;
        BigInteger xLow = shift >= 0 ? raw << shift : raw >> -shift;
        BigInteger xHigh = shift >= 0 ? xLow : CeilingDivide(raw, BigInteger.One << -shift);
        BigInteger tLow = log.Low * xLow / unit, tHigh = CeilingDivide(log.High * xHigh, unit);
        BigInteger termLow = unit, termHigh = unit, sumLow = unit, sumHigh = unit;
        for (int k = 1; ; k++)
        {
            termLow = termLow * tLow / (unit * (k + 1));
            termHigh = CeilingDivide(termHigh * tHigh, unit * (k + 1));
            if (source.Negative && (k & 1) != 0) { sumLow -= termHigh; sumHigh -= termLow; }
            else { sumLow += termLow; sumHigh += termHigh; }
            if (termHigh <= 1) break;
        }
        // |x ln(2)|<0.7. Once the current upper term is <= one unit,
        // the remaining geometric/alternating tail is strictly < two units.
        return (log.Low * (sumLow - 2) / unit, CeilingDivide(log.High * (sumHigh + 2), unit));
    }

    /// <summary>Builds reconstructible integer lower/upper bounds on ln(2) at the requested binary scale.</summary>
    private static (BigInteger Low, BigInteger High) ComputeLogTwoBounds(int bits)
    {
        BigInteger unit = BigInteger.One << bits, power = 3, low = 0;
        int terms = 0;
        for (int k = 0; ; k++)
        {
            BigInteger term = 2 * unit / ((2 * k + 1) * power);
            if (term.IsZero) break;
            low += term; terms++; power *= 9;
        }
        // ln(2)=2 atanh(1/3). Each floor loses <1 unit; after the
        // first omitted term, the tail is bounded by its geometric ratio 1/9.
        return (low, low + terms + 2);
    }
}
