using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct Float80Comparison(ushort Conditions, ushort Exceptions = 0, bool Commit = true);

public static partial class Float80Math
{
    /// <summary>Exact extended-format comparison; C3/C2/C0, no precision rounding.</summary>
    public static Float80Comparison Compare(Float80Value left, Float80Value right, bool quiet, ushort control, bool memoryDenormal = false)
    {
        bool invalid = Unsupported(left) || Unsupported(right) ||
            (quiet ? Signaling(left) || Signaling(right) : Nan(left) || Nan(right));
        // Reference contract: invalid reports unordered even when the pop is suppressed.
        if (invalid) return new(0x4500, Invalid, (control & Invalid) != 0);
        if (Nan(left) || Nan(right)) return new(0x4500);
        ushort exceptions = DenormalOperand(left) || DenormalOperand(right) || memoryDenormal ? Denormal : (ushort)0;
        int relation;
        if (Zero(left) && Zero(right)) relation = 0;
        else if (left.Negative != right.Negative) relation = left.Negative ? -1 : 1;
        else
        {
            if (Infinity(left) || Infinity(right)) relation = Infinity(left) ? (Infinity(right) ? 0 : 1) : -1;
            else
            {
                int ls = Scale(left), rs = Scale(right), common = Math.Min(ls, rs);
                relation = ((BigInteger)left.Significand << (ls - common)).CompareTo((BigInteger)right.Significand << (rs - common));
            }
            if (left.Negative) relation = -relation;
        }
        // An unmasked denormal reports the relation, but suppresses the pop.
        return new((ushort)(relation < 0 ? 0x100 : relation == 0 ? 0x4000 : 0), exceptions, (exceptions & ~control) == 0);
    }
}
