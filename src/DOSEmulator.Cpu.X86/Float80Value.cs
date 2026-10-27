// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

/// <summary>Raw x87 extended format: explicit 64-bit significand and sign/exponent.</summary>
public readonly record struct Float80Value(ulong Significand, ushort SignExponent)
{
    /// <summary>Raw extended exponent field with the sign bit removed.</summary>
    public int Exponent => SignExponent & 0x7fff;
    /// <summary>Whether the raw extended sign bit is set, including signed zero.</summary>
    public bool Negative => (SignExponent & 0x8000) != 0;
    /// <summary>Whether the raw extended significand's explicit integer bit is set.</summary>
    public bool IntegerBit => (Significand & 0x8000000000000000) != 0;
    /// <summary>Legacy x87 tag classification derived from the raw value without normalizing its bits.</summary>
    public int Tag => Exponent == 0 && Significand == 0 ? 1 :
        Exponent == 0 || Exponent == 0x7fff || !IntegerBit ? 2 : 0;
    /// <summary>Exact raw extended encoding of positive one.</summary>
    public static Float80Value One => new(0x8000000000000000, 0x3fff);
    /// <summary>Exact raw extended encoding of positive zero.</summary>
    public static Float80Value Zero => default;
    /// <summary>Reference raw extended indefinite quiet-NaN encoding.</summary>
    public static Float80Value Indefinite => new(0xc000000000000000, 0xffff);
}
