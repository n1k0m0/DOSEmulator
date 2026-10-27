// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Numerics;

namespace DOSEmulator.Cpu.X86;

public readonly record struct PackedBcdResult(ulong Low, ushort High, ushort Exceptions = 0,
    bool RoundedUp = false, bool Commit = true);

public static partial class Float80Math
{
    /// <summary>FBLD: eighteen decimal digits and a sign; conversion is exact even at PC24.</summary>
    public static Float80Value LoadPackedBcd(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 10) throw new ArgumentException("Packed BCD has ten bytes.", nameof(bytes));
        long magnitude = 0;
        // A..F digits have architecturally undefined results. The reference model
        // deterministically accumulates their nibble values without raising IE.
        for (int i = 8; i >= 0; i--) magnitude = magnitude * 100 + (bytes[i] >> 4) * 10 + (bytes[i] & 15);
        var value = FromInteger(magnitude);
        return value with { SignExponent = (ushort)(value.SignExponent | ((bytes[9] & 0x80) != 0 ? 0x8000 : 0)) };
    }

    /// <summary>FBSTP: RC rounding before the 18-digit range check; no DE or PC reduction.</summary>
    public static PackedBcdResult StorePackedBcd(Float80Value value, ushort control)
    {
        // Packages the format's masked invalid/indefinite response without committing an unmasked conversion.
        PackedBcdResult InvalidResult() => new(0xc000000000000000, 0xffff, Invalid, false, (control & Invalid) != 0);
        if (Unsupported(value) || Nan(value) || Infinity(value)) return InvalidResult();
        var rounded = RoundInteger(value.Significand, BigInteger.One, Scale(value), value.Negative, control >> 10 & 3);
        if (rounded.Value >= 1_000_000_000_000_000_000) return InvalidResult();
        ulong integer = (ulong)rounded.Value, low = 0;
        ushort high = (ushort)(value.Negative ? 0x8000 : 0);
        for (int digit = 0; digit < 18; digit++)
        {
            uint nibble = (uint)(integer % 10); integer /= 10;
            if (digit < 16) low |= (ulong)nibble << (digit * 4);
            else high |= (ushort)(nibble << ((digit - 16) * 4));
        }
        // Precision is a post-completion exception: memory and pop still commit
        // when PM=0. Invalid with IM=0 suppresses both effects instead.
        return new(low, high, rounded.Inexact ? Precision : (ushort)0, rounded.Up);
    }
}
