using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public enum Float80Operation { Add, Subtract, Multiply, Divide }
public readonly record struct Float80Result(Float80Value Value, ushort Exceptions = 0, bool RoundedUp = false, bool Commit = true);
public readonly record struct Float80Encoding(ulong Bits, ushort Exceptions = 0, bool RoundedUp = false, bool Commit = true);

/// <summary>Exact integer/rational elementary x87 arithmetic. No host floating-point operations.</summary>
public static partial class Float80Math
{
    private const ushort Invalid = 1, Denormal = 2, ZeroDivide = 4, Overflow = 8, Underflow = 16, Precision = 32;
    private const ulong IntegerBit = 0x8000000000000000, QuietBit = 0x4000000000000000;
    private const int Bias = 16383, Emin = -16382, Emax = 16383;
    private static bool Unsupported(Float80Value v) => v.Exponent != 0 && !v.IntegerBit;
    private static bool Nan(Float80Value v) => v.Exponent == 0x7fff && v.IntegerBit && v.Significand != IntegerBit;
    private static bool Signaling(Float80Value v) => Nan(v) && (v.Significand & QuietBit) == 0;
    private static bool Infinity(Float80Value v) => v.Exponent == 0x7fff && v.Significand == IntegerBit;
    private static bool Zero(Float80Value v) => v.Exponent == 0 && v.Significand == 0;
    private static bool DenormalOperand(Float80Value v) => v.Exponent == 0 && v.Significand != 0;
    private static int Scale(Float80Value v) => Math.Max(1, v.Exponent) - Bias - 63;
    private static Float80Value SignedZero(bool negative) => new(0, negative ? (ushort)0x8000 : (ushort)0);
    private static Float80Value SignedInfinity(bool negative) => new(IntegerBit, (ushort)(0x7fff | (negative ? 0x8000 : 0)));
    private static Float80Result Special(Float80Value value, ushort flags, ushort control) => new(value, flags, false, (flags & ~control & 7) == 0);
    private static int PrecisionBits(ushort control) => (control >> 8 & 3) switch { 0 => 24, 2 => 53, _ => 64 };

    public static Float80Result Calculate(Float80Value left, Float80Value right, Float80Operation operation, ushort control, bool memoryDenormal = false)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (Unsupported(left) || Unsupported(right)) return Special(Float80Value.Indefinite, Invalid, control);
        if (Nan(left) || Nan(right))
        {
            bool sn = Signaling(left) || Signaling(right);
            Float80Value chosen;
            if (!Nan(right)) chosen = left;
            else if (!Nan(left)) chosen = right;
            else if (Signaling(left) != Signaling(right)) chosen = Signaling(left) ? right : left;
            else if (left.Significand != right.Significand) chosen = left.Significand > right.Significand ? left : right;
            else chosen = left.Negative ? right : left;
            return Special(chosen with { Significand = chosen.Significand | QuietBit }, sn ? Invalid : (ushort)0, control);
        }
        // NaN selection precedes subtraction's sign inversion.
        if (operation == Float80Operation.Subtract) right = right with { SignExponent = (ushort)(right.SignExponent ^ 0x8000) };
        bool add = operation is Float80Operation.Add or Float80Operation.Subtract;
        bool negative = left.Negative ^ right.Negative;
        if (add && Infinity(left) && Infinity(right) && left.Negative != right.Negative ||
            operation == Float80Operation.Multiply && (Zero(left) && Infinity(right) || Infinity(left) && Zero(right)) ||
            operation == Float80Operation.Divide && (Zero(left) && Zero(right) || Infinity(left) && Infinity(right)))
            return Special(Float80Value.Indefinite, Invalid, control);
        if (operation == Float80Operation.Divide && !Infinity(left) && Zero(right))
            return Special(SignedInfinity(negative), ZeroDivide, control);
        ushort initial = DenormalOperand(left) || DenormalOperand(right) || memoryDenormal ? Denormal : (ushort)0;
        if ((initial & ~control) != 0) return Special(left, initial, control);
        if (Infinity(left) || Infinity(right))
        {
            Float80Value value = add ? (Infinity(left) ? left : right) :
                operation == Float80Operation.Divide && Infinity(right) ? SignedZero(negative) : SignedInfinity(negative);
            return new(value, initial);
        }
        BigInteger numerator, denominator = BigInteger.One;
        int scale;
        if (add)
        {
            int ls = Scale(left), rs = Scale(right); scale = Math.Min(ls, rs);
            BigInteger a = (BigInteger)left.Significand << (ls - scale), b = (BigInteger)right.Significand << (rs - scale);
            numerator = (left.Negative ? -a : a) + (right.Negative ? -b : b);
            negative = numerator.Sign < 0;
            if (numerator.IsZero) negative = Zero(left) && Zero(right) && left.Negative == right.Negative ? left.Negative : (control >> 10 & 3) == 1;
            numerator = BigInteger.Abs(numerator);
        }
        else if (operation == Float80Operation.Multiply)
        { numerator = (BigInteger)left.Significand * right.Significand; scale = Scale(left) + Scale(right); }
        else
        { numerator = left.Significand; denominator = right.Significand; scale = Scale(left) - Scale(right); }
        if (numerator.IsZero) return new(SignedZero(negative), initial);
        var rounded = Quantize(numerator, denominator, scale, negative, PrecisionBits(control), Emin, Emax, control, true);
        return new(ToExtended(rounded, negative, PrecisionBits(control)), (ushort)(initial | rounded.Flags), rounded.Up, rounded.Commit);
    }

    private readonly record struct Rounded(BigInteger Significand, int Exponent, bool Subnormal, ushort Flags, bool Up, bool Commit = true, bool Infinite = false);
    private static int FloorLog(BigInteger n, BigInteger d)
    {
        int k = checked((int)(n.GetBitLength() - d.GetBitLength()));
        if (k >= 0 ? n < d << k : n << -k < d) k--;
        return k;
    }
    private static (BigInteger Value, bool Inexact, bool Up) RoundInteger(BigInteger n, BigInteger d, int scale, bool negative, int rc)
    {
        if (scale >= 0) n <<= scale; else d <<= -scale;
        BigInteger q = BigInteger.DivRem(n, d, out BigInteger rem);
        bool up = !rem.IsZero && rc switch
        {
            0 => rem * 2 > d || rem * 2 == d && !q.IsEven,
            1 => negative,
            2 => !negative,
            _ => false
        };
        return (up ? q + 1 : q, !rem.IsZero, up);
    }
    private static Rounded Quantize(BigInteger n, BigInteger d, int scale, bool negative, int p, int emin, int emax, ushort control, bool stack)
    {
        int rc = control >> 10 & 3, exponent = FloorLog(n, d) + scale;
        var normal = RoundInteger(n, d, scale - (exponent - p + 1), negative, rc);
        if (normal.Value == BigInteger.One << p) { normal.Value >>= 1; exponent++; }
        if (exponent > emax)
        {
            if ((control & Overflow) == 0)
                return stack ? new(normal.Value, exponent - 24576, false, (ushort)(Overflow | (normal.Inexact ? Precision : 0)), normal.Up) :
                    new(0, 0, false, Overflow, false, false);
            bool infinite = rc == 0 || rc == 1 && negative || rc == 2 && !negative;
            return new((BigInteger.One << p) - 1, emax, false, Overflow | Precision, infinite, true, infinite);
        }
        if (exponent < emin)
        {
            if ((control & Underflow) == 0)
                return stack ? new(normal.Value, exponent + 24576, false, (ushort)(Underflow | (normal.Inexact ? Precision : 0)), normal.Up) :
                    new(0, 0, false, Underflow, false, false);
            var tiny = RoundInteger(n, d, scale - (emin - p + 1), negative, rc);
            bool subnormal = tiny.Value < BigInteger.One << (p - 1);
            // x87 tests tininess after precision rounding with an unbounded
            // exponent, before denormalization. Rounding back to minimum-normal
            // during denormalization can therefore still signal UE+PE.
            return new(tiny.Value, emin, subnormal, tiny.Inexact ? (ushort)(Underflow | Precision) : (ushort)0, tiny.Up);
        }
        return new(normal.Value, exponent, false, normal.Inexact ? Precision : (ushort)0, normal.Up);
    }
    private static Float80Value ToExtended(Rounded value, bool negative, int p)
    {
        if (value.Infinite) return SignedInfinity(negative);
        return new((ulong)(value.Significand << (64 - p)), (ushort)((value.Subnormal ? 0 : value.Exponent + Bias) | (negative ? 0x8000 : 0)));
    }
    public static Float80Value FromInteger(long value)
    {
        BigInteger absolute = BigInteger.Abs(new BigInteger(value));
        if (absolute.IsZero) return Float80Value.Zero;
        int bits = (int)absolute.GetBitLength();
        return new((ulong)(absolute << (64 - bits)), (ushort)(Bias + bits - 1 | (value < 0 ? 0x8000 : 0)));
    }
    private static Float80Value DecodeBinary(ulong bits, int width, out bool denormal)
    {
        if (width is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(width));
        int p = width == 32 ? 24 : 53, ebits = width == 32 ? 8 : 11, bias = (1 << (ebits - 1)) - 1;
        bool negative = (bits & 1ul << (width - 1)) != 0;
        int e = (int)(bits >> (p - 1) & (uint)((1 << ebits) - 1));
        ulong fraction = bits & ((1ul << (p - 1)) - 1);
        denormal = e == 0 && fraction != 0;
        if (e == (1 << ebits) - 1) return new(IntegerBit | fraction << (64 - p), (ushort)(0x7fff | (negative ? 0x8000 : 0)));
        if (e == 0 && fraction == 0) return SignedZero(negative);
        ulong sig = e == 0 ? fraction : fraction | 1ul << (p - 1);
        int shift = BitOperations.LeadingZeroCount(sig);
        return new(sig << shift, (ushort)(Math.Max(1, e) - bias - (p - 1) + 63 - shift + Bias | (negative ? 0x8000 : 0)));
    }
    public static Float80Result LoadBinary(ulong bits, int width, ushort control)
    {
        var value = DecodeBinary(bits, width, out bool denormal);
        if (Signaling(value)) return Special(value with { Significand = value.Significand | QuietBit }, Invalid, control);
        // FLD pushes a normalized denormal even when DM is unmasked (486 contract).
        return new(value, denormal ? Denormal : (ushort)0);
    }
    public static Float80Result CalculateBinaryMemory(Float80Value left, ulong bits, int width, Float80Operation operation, ushort control, bool reverse = false)
    {
        var right = DecodeBinary(bits, width, out bool denormal);
        return reverse ? Calculate(right, left, operation, control, denormal) : Calculate(left, right, operation, control, denormal);
    }
    public static Float80Encoding StoreBinary(Float80Value value, int width, ushort control)
    {
        if (width is not (32 or 64)) throw new ArgumentOutOfRangeException(nameof(width));
        int p = width == 32 ? 24 : 53, ebits = width == 32 ? 8 : 11, bias = (1 << (ebits - 1)) - 1;
        ulong sign = value.Negative ? 1ul << (width - 1) : 0, allExponent = ((1ul << ebits) - 1) << (p - 1);
        if (Unsupported(value)) return new((1ul << (width - 1)) | allExponent | 1ul << (p - 2), Invalid, false, (control & Invalid) != 0);
        if (Nan(value)) return new(sign | allExponent | (value.Significand & ~IntegerBit | QuietBit) >> (64 - p), Signaling(value) ? Invalid : (ushort)0, false, !Signaling(value) || (control & Invalid) != 0);
        if (Infinity(value)) return new(sign | allExponent);
        if (Zero(value)) return new(sign);
        var r = Quantize(value.Significand, BigInteger.One, Scale(value), value.Negative, p, 1 - bias, bias, control, false);
        ulong fraction = (ulong)r.Significand & ((1ul << (p - 1)) - 1);
        ulong encoded = r.Infinite ? allExponent : (r.Subnormal ? 0 : (ulong)(r.Exponent + bias) << (p - 1)) | fraction;
        return new(sign | encoded, r.Flags, r.Up, r.Commit);
    }
    public static Float80Encoding StoreInteger(Float80Value value, int width, ushort control)
    {
        if (width is not (16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(width));
        ulong indefinite = 1ul << (width - 1);
        if (Unsupported(value) || Nan(value) || Infinity(value)) return new(indefinite, Invalid, false, (control & Invalid) != 0);
        var rounded = RoundInteger(value.Significand, BigInteger.One, Scale(value), value.Negative, control >> 10 & 3);
        BigInteger signed = value.Negative ? -rounded.Value : rounded.Value, bound = BigInteger.One << (width - 1);
        if (signed < -bound || signed >= bound) return new(indefinite, Invalid, false, (control & Invalid) != 0);
        ulong bits = (ulong)(signed.Sign < 0 ? signed + (BigInteger.One << width) : signed);
        return new(bits, rounded.Inexact ? Precision : (ushort)0, rounded.Up);
    }
}
