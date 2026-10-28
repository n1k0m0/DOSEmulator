// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class Pic8259
{
    /// <summary>Clears the own PIIX PIC's internal service/configuration latches without inventing edges on retained external input levels.</summary>
    public void ApplyPiixHardReset()
    {
        if (!_supportsLevelInputs) throw new InvalidOperationException("PIIX reset requires the explicitly capable PIC board.");
        ResetPiixChip(_master, 8);
        ResetPiixChip(_slave, 0x70);
        _master.Lines &= 0xfb;
        RefreshCascade();
    }

    /// <summary>Uses named deterministic values for undefined ICW/vector state; defined reset modes and masks are inactive.</summary>
    private static void ResetPiixChip(Chip chip, byte vector)
    {
        chip.Vector = vector;
        chip.Mask = 0xff;
        chip.Irr = chip.Isr = chip.LevelMask = 0;
        chip.InitStep = 0;
        chip.NeedsIcw4 = chip.Single = chip.AutoEoi = chip.ReadIsr = false;
    }
}
