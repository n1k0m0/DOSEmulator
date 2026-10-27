// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

/// <summary>Host-repeat admission for translated make codes; guest keyboard timing remains separate.</summary>
public static class GuestKeyRepeatPolicy
{
    /// <summary>Allows held ordinary make codes without repeating modifiers, locks, releases or unknown mappings.</summary>
    public static bool ShouldRepeat(byte makeCode, bool held) => held && makeCode is > 0 and < 0x80 &&
        makeCode is not (0x1d or 0x2a or 0x36 or 0x38 or 0x3a or 0x45 or 0x46);
}
