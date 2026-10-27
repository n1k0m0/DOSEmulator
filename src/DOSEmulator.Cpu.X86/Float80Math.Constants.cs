// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public static partial class Float80Math
{
    /// <summary>Observable extended constants, rounded by RC without PC reduction or PE.</summary>
    public static Float80Value LoadConstant(int index, ushort control)
    {
        // Floors and nearest increments describe the documented reference model.
        // They do not claim to recover every bit of an original chip's internal ROM.
        (ulong Floor, ushort Exponent, bool NearestUp, bool Inexact) constant = index switch
        {
            0 => (0x8000000000000000, 0x3fff, false, false), // 1
            1 => (0xd49a784bcd1b8afe, 0x4000, false, true),  // log2(10)
            2 => (0xb8aa3b295c17f0bb, 0x3fff, true, true),   // log2(e)
            3 => (0xc90fdaa22168c234, 0x4000, true, true),   // pi
            4 => (0x9a209a84fbcff798, 0x3ffd, true, true),   // log10(2)
            5 => (0xb17217f7d1cf79ab, 0x3ffe, true, true),   // ln(2)
            6 => (0, 0, false, false),                     // +0
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
        var (floor, exponent, nearestUp, inexact) = constant;
        int rounding = control >> 10 & 3;
        return new(floor + (rounding == 2 && inexact || rounding == 0 && nearestUp ? 1ul : 0), exponent);
    }
}
