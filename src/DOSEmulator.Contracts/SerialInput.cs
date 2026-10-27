// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Bounded virtual framed characters and optional logical modem-pin changes; no host COM resource is implied.</summary>
public sealed record SerialInput(int[]? Data = null, int Errors = 0, bool? Cts = null, bool? Dsr = null, bool? Ri = null, bool? Dcd = null)
{
    /// <summary>Reject invalid byte/error values and return an owned snapshot before any pin or receiver mutation.</summary>
    public SerialInput ValidatedCopy()
    {
        if ((Errors & ~0x1c) != 0 || Data?.Length > 4096 || Data?.Any(x => (uint)x > 255) == true)
            throw new ArgumentException("Serial input requires at most 4096 bytes and only PE/FE/BI error bits.");
        return this with { Data = Data?.ToArray() };
    }
}
