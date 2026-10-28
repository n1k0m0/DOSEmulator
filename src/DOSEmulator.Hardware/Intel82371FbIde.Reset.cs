// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Intel82371FbIde
{
    /// <summary>Stops only this function's PCI grants and restores its internal reset registers; ATA media, staging and device reset belong to their board peers.</summary>
    public void ApplyPiixHardReset()
    {
        for (int channel = 0; channel < _channels.Length; channel++)
        {
            CancelEvent(channel);
            var state = _channels[channel];
            ClearCursor(state);
            state.Command = state.Status = 0;
            state.Prdt = 0;
            state.IrqHigh = Ata(channel).InterruptAsserted;
        }
        for (int index = 0; index < _config.Length; index++) _config[index] = ResetValue(index);
    }
    /// <summary>Checks the complete immutable hard-reset register image without constructing a live peer or opening media.</summary>
    public static bool IsHardResetConfiguration(byte[] config) => config is { Length: 256 } && config.Where((value, index) => value != ResetValue(index)).Any() == false;
}
