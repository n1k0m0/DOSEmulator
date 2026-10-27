// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>A persistent PC configuration. It describes hardware and media, not running CPU or device state.</summary>
public sealed record MachineDefinition
{
    /// <summary>Persisted format version; unsupported versions are rejected before adopting the configuration.</summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Stable GUID identifying this saved PC independently from the hardware profile key.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Human-readable name of this configured component or media binding.</summary>
    public string Name { get; init; } = "My DOS PC";
    /// <summary>Hardware configuration recreated when this saved PC is cold-started.</summary>
    public MachineProfile Hardware { get; init; } = new();
    /// <summary>Fixed physical IDE-slot bindings; an omitted slot has no attached disk.</summary>
    public DiskAttachment[] HardDisks { get; init; } = [];
    /// <summary>Removable media bindings for the two configured floppy-drive positions.</summary>
    public DiskAttachment[] Floppies { get; init; } = [];
    /// <summary>Whether this PC explicitly uses the original project boot-sector fixture instead of an installed OS image.</summary>
    public bool BuiltInTestDisk { get; init; }

    /// <summary>Rejects unknown versions, conflicting slots and malformed media before opening files.</summary>
    public void Validate()
    {
        if (SchemaVersion != 1 || !Guid.TryParseExact(Id, "N", out _)) throw new ArgumentException("Unknown machine format or invalid machine ID.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128) throw new ArgumentException("Machine name must contain 1..128 characters.");
        ArgumentNullException.ThrowIfNull(Hardware); Hardware.Validate();
        ValidateSlots(HardDisks, 4); ValidateSlots(Floppies, 2);
        if (BuiltInTestDisk && HardDisks.Length != 0) throw new ArgumentException("The built-in test disk cannot replace an attached hard disk.");
    }

    /// <summary>Checks slot identity independently for IDE disks and removable floppy media.</summary>
    private static void ValidateSlots(DiskAttachment[] media, int slots)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Length > slots || media.Any(m => m is null || m.Slot < 0 || m.Slot >= slots || string.IsNullOrWhiteSpace(m.ImagePath)) ||
            media.Select(m => m.Slot).Distinct().Count() != media.Length)
            throw new ArgumentException("Invalid or duplicate media slot.");
    }
}

/// <summary>A raw base image and an optional private sector journal at a fixed controller slot.</summary>
public sealed record DiskAttachment(int Slot, string ImagePath, string? OverlayPath = null);

/// <summary>Guest-visible IDE slot and capacity, suitable for UI/IPC without exposing mutable devices.</summary>
public sealed record HardDiskStatus(int Slot, string Name, long Bytes, bool Writable);
