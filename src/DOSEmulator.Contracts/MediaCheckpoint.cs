// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>
/// Versioned media-only checkpoint. Virtual time identifies the common capture boundary;
/// CPU, RAM, devices and scheduler state are deliberately absent and cannot be resumed from this record.
/// </summary>
public sealed record MediaCheckpoint(int SchemaVersion, string Kind, string Id, ulong VirtualNanoseconds,
    MachineDefinition Machine, MediaGeneration[] Media);

/// <summary>Base content identity and optional sealed journal for one physical IDE or floppy slot.</summary>
public sealed record MediaGeneration(string Device, int Slot, long Sectors, string BaseSha256,
    string? JournalName, long JournalBytes, string? JournalSha256)
{
    /// <summary>Bytes per sealed unit: legacy disk/floppy sectors are 512; cooked CD-ROM blocks are 2048.</summary>
    public int SectorSize { get; init; } = 512;
}
