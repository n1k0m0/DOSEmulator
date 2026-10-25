using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    /// <summary>FRNDINT: round to an integral extended value using RC, independently of PC.</summary>
    public static Float80Result RoundToInteger(Float80Value value, ushort control)
    {
        if (Unsupported(value)) return Special(Float80Value.Indefinite, Invalid, control);
        if (Nan(value)) return Special(value with { Significand = value.Significand | QuietBit }, Signaling(value) ? Invalid : (ushort)0, control);
        if (Infinity(value) || Zero(value)) return new(value);
        ushort flags = DenormalOperand(value) ? Denormal : (ushort)0;
        if ((flags & ~control) != 0) return new(value, flags, false, false);
        int scale = Scale(value);
        if (scale >= 0) return new(value);
        var rounded = RoundInteger(value.Significand, BigInteger.One, scale, value.Negative, control >> 10 & 3);
        Float80Value result;
        if (rounded.Value.IsZero) result = SignedZero(value.Negative);
        else
        {
            int bits = checked((int)rounded.Value.GetBitLength());
            result = new((ulong)(rounded.Value << (64 - bits)), (ushort)(Bias + bits - 1 | (value.Negative ? 0x8000 : 0)));
        }
        return new(result, (ushort)(flags | (rounded.Inexact ? Precision : 0)), rounded.Up);
    }
}
