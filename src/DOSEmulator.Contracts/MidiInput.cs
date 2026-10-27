// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Explicit outer MIDI serial bytes, not guest receive-register or synthesizer state.</summary>
public sealed record MidiInput(int[] Data)
{
    /// <summary>Validate the complete packet and own its bytes before any queue or line can change.</summary>
    public MidiInput ValidatedCopy()
    {
        if (Data is null || Data.Length is < 1 or > 4096 || Data.Any(v => (uint)v > 255))
            throw new ArgumentException("MIDI input requires 1..4096 byte values.");
        return this with { Data = Data.ToArray() };
    }
}
