// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Intel82371FbIrq
{
    /// <summary>Applies the own PIIX board's hard-reset assertion to internal configuration only; physical source pins remain independently owned.</summary>
    public void ApplyPiixHardReset()
    {
        for (int index = 0; index < _config.Length; index++) _config[index] = ResetValue(index);
        _elcr = 0;
        _pic.SetLevelMask(0);
        PublishPins();
    }
    /// <summary>Checks the complete immutable hard-reset register image without constructing a live peer or opening media.</summary>
    public static bool IsHardResetConfiguration(byte[] config) => config is { Length: 256 } && config.Where((value, index) => value != ResetValue(index)).Any() == false;
}
