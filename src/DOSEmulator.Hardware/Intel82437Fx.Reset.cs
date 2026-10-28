// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Intel82437Fx
{
    /// <summary>Checks the complete immutable hard-reset register image without constructing a live peer or opening media.</summary>
    public static bool IsHardResetConfiguration(byte[] config) => config is { Length: 256 } && config.Where((value, index) => value != ResetValue(index)).Any() == false;
}
