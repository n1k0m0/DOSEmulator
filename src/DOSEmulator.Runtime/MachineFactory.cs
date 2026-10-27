// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

/// <summary>Prepares complete PCs and transfers media ownership only after all configuration checks succeed.</summary>
public static class MachineFactory
{
    /// <summary>Preserves legacy launch options and additionally accepts the other three fixed IDE slots.</summary>
    public static PcMachine FromImages(MachineProfile profile, string? ideImage = null, string? ideOverlay = null,
        string? floppyA = null, string? floppyAOverlay = null, string? floppyB = null, string? floppyBOverlay = null, string? bootDevice = null,
        string? disk1 = null, string? overlay1 = null, string? disk2 = null, string? overlay2 = null, string? disk3 = null, string? overlay3 = null)
    {
        var hard = new List<DiskAttachment>(); var floppy = new List<DiskAttachment>();
        // Adds one explicit image/overlay binding and rejects an orphan journal before opening any media.
        void Add(List<DiskAttachment> list, int slot, string? image, string? overlay)
        {
            if (overlay is not null && image is null) throw new ArgumentException("A private overlay requires its base image.");
            if (image is not null) list.Add(new(slot, image, overlay));
        }
        Add(hard, 0, ideImage, ideOverlay); Add(hard, 1, disk1, overlay1); Add(hard, 2, disk2, overlay2); Add(hard, 3, disk3, overlay3);
        Add(floppy, 0, floppyA, floppyAOverlay); Add(floppy, 1, floppyB, floppyBOverlay);
        if (bootDevice is not null) profile = profile with { BootDevice = bootDevice };
        else if (hard.Count == 0 && floppyA is not null) profile = profile with { BootDevice = "floppy-a" };
        var definition = new MachineDefinition { Name = profile.Name, Hardware = profile, HardDisks = hard.ToArray(), Floppies = floppy.ToArray(),
            BuiltInTestDisk = hard.Count == 0 && floppy.Count == 0 };
        return FromDefinition(definition, inferFloppyTypes: true);
    }

    /// <summary>Prepares a cold start. Matching active overlays use pending leases so a failed load leaves the old PC usable.</summary>
    public static PcMachine FromDefinition(MachineDefinition definition, PcMachine? previous = null, bool inferFloppyTypes = false)
    {
        // Legacy CLI floppy inference must precede profile validation of floppy boot.
        var profile = definition.Hardware;
        // Reject a mismatched CPU/profile/clock tuple before even inspecting an inferred floppy image.
        ArgumentNullException.ThrowIfNull(profile); profile.ValidateCpuSelection();
        if (inferFloppyTypes)
        {
            foreach (var item in definition.Floppies)
            {
                using var image = new ReadOnlyRawDisk(item.ImagePath);
                if (item.Slot == 0 && profile.FloppyDriveA == "none") profile = profile with { FloppyDriveA = FloppyGeometry.IdForSectors(image.SectorCount) };
                if (item.Slot == 1 && profile.FloppyDriveB == "none") profile = profile with { FloppyDriveB = FloppyGeometry.IdForSectors(image.SectorCount) };
            }
            definition = definition with { Hardware = profile };
        }
        definition = MachineDefinitionStore.Resolve(definition, Environment.CurrentDirectory);
        profile = definition.Hardware with { Name = definition.Name };
        IReadOnlyBlockDevice?[] disks = new IReadOnlyBlockDevice?[4], floppies = new IReadOnlyBlockDevice?[2];
        try
        {
            foreach (var item in definition.HardDisks)
            {
                var old = previous is null ? null : Enumerable.Range(0, 4).Select(previous.DiskAt)
                    .FirstOrDefault(d => Matches(item, (d as IFileBackedBlockDevice)?.Image));
                disks[item.Slot] = Open(item, old);
            }
            foreach (var item in definition.Floppies)
            {
                // A saved PC may move either active floppy to the other slot. Reopen the matching old medium,
                // not the destination slot: its pending lease must survive until the old PC is released.
                int oldSlot = previous is null ? -1 : Enumerable.Range(0, 2)
                    .FirstOrDefault(slot => Matches(item, previous.Floppy.MediaImage(slot)), -1);
                floppies[item.Slot] = oldSlot >= 0 ? previous!.Floppy.ReopenMedia(oldSlot) : Open(item, null);
            }
            if (definition.BuiltInTestDisk) disks[0] = new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Built-in test sector (not DOS)");
            var machine = PcMachine.CreateWithDisks(profile, disks, floppies[0], floppies[1]);
            machine.SetDefinitionIdentity(definition.Id, definition.Name);
            previous?.Rtc.CarryBatteryStateTo(machine.Rtc);
            return machine;
        }
        catch
        {
            foreach (var media in disks.Concat(floppies)) media?.Dispose();
            throw;
        }
    }

    /// <summary>Reuses a matching protected journal lease or opens a distinct read-only base/private overlay pair.</summary>
    private static IReadOnlyBlockDevice Open(DiskAttachment item, IReadOnlyBlockDevice? previous) =>
        Matches(item, (previous as IFileBackedBlockDevice)?.Image) ? previous!.Reopen() :
        item.OverlayPath is null ? new ReadOnlyRawDisk(item.ImagePath) : new CopyOnWriteDisk(item.ImagePath, item.OverlayPath);

    /// <summary>Tests both image and journal identity; equal bases with different overlays are independent installations.</summary>
    private static bool Matches(DiskAttachment item, BlockDeviceImage? old) => old is not null &&
        MachineDefinitionStore.Same(item.ImagePath, old.BasePath) && (item.OverlayPath is null && old.OverlayPath is null ||
        item.OverlayPath is not null && old.OverlayPath is not null && MachineDefinitionStore.Same(item.OverlayPath, old.OverlayPath));
}
