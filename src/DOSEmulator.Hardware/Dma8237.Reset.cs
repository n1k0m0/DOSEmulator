// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Dma8237
{
    /// <summary>Masks all own PIIX DMA channels and clears requests/status/byte sequencing; undefined address/count/page/mode values use explicit zero defaults without touching RAM.</summary>
    public void ApplyPiixHardReset()
    {
        Array.Clear(_highByte); Array.Clear(_status); Array.Clear(_command);
        foreach (var channel in _channels)
        {
            channel.Address = channel.BaseAddress = channel.Count = channel.BaseCount = 0;
            channel.Page = channel.Mode = 0;
            channel.Masked = true;
            channel.Requested = channel.TerminalCount = false;
        }
    }
}
