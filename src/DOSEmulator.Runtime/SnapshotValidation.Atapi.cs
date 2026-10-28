// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

internal static partial class SnapshotValidation
{
    /// <summary>Binds the one installed packet child, actual channel selection and owned phase callbacks before opening media.</summary>
    private static void CheckAtapi(HardwareState state, MachineProfile profile)
    {
        string? expectedOwner = profile.IsAtapi ? "ata" + profile.AtapiSlot / 2 + "/_atapi" : null;
        var packets = state.Components.Where(v => v.Value.Type == "DOSEmulator.Hardware.AtapiCdRom" ||
            v.Key.EndsWith("/_atapi", StringComparison.Ordinal)).ToArray();
        if (packets.Length != (profile.IsAtapi ? 1 : 0) || packets.Any(v => v.Key != expectedOwner))
            throw new InvalidDataException("Packet-device topology differs from its fixed board profile.");
        for (int channel = 0; channel < 2; channel++)
        {
            string owner = "ata" + channel;
            if (!state.Components.TryGetValue(owner, out var block)) throw new InvalidDataException("Missing ATA channel.");
            var reader = new StateReader(block, "DOSEmulator.Hardware.AtaPioController");
            int index = profile.IsAtapi && profile.AtapiSlot / 2 == channel ? profile.AtapiSlot & 1 : -1;
            reader.Invariant("_packetIndex", index);
            if (index != 0) reader.Invariant("_shadowAtapiSlave", false);
            int selected = reader.Value<int>("_selected");
            if (selected is < 0 or > 1) throw new InvalidDataException("Invalid selected IDE target.");
            if (index < 0) continue;
            var packet = state.Components[expectedOwner!];
            var mediaReader = new StateReader(packet, "DOSEmulator.Hardware.AtapiCdRom");
            bool hasDisc = mediaReader.Value<bool>("HasDisc"); long blocks = mediaReader.Value<long>("DiscBlocks");
            var packetPhase = mediaReader.Value<AtapiPhase>("_phase");
            if (reader.Value<int>("_dmaDriveIndex") >= 0 && packetPhase is AtapiPhase.PacketReadyWait or AtapiPhase.PacketOut or AtapiPhase.CommandWait or AtapiPhase.DataInWait or AtapiPhase.DataIn or AtapiPhase.StatusWait)
                throw new InvalidDataException("One channel cannot own disk DMA and a non-overlapped packet transaction together.");
            AtapiCdRom.ValidateSnapshotState(packet, state.Scheduler, hasDisc ? blocks : null, index, selected == index, expectedOwner!);
            if (state.Components.TryGetValue("piix-reset", out var reset))
            {
                var resetReader = new StateReader(reset, "DOSEmulator.Hardware.PiixResetController");
                var phase = resetReader.Value<PiixResetPhase>("_phase");
                if (phase != PiixResetPhase.Idle) CheckAtapiHardReset(packet, phase == PiixResetPhase.AllAsserted);
            }
        }
        var cancelled = state.Scheduler.Cancelled.ToHashSet();
        if (state.Scheduler.Events.Any(e => !cancelled.Contains(e.Id) &&
            (e.Owner.EndsWith("/_atapi", StringComparison.Ordinal) || e.Operation == "phase") && e.Owner != expectedOwner))
            throw new InvalidDataException("Live packet callback has no installed owner.");
    }

    /// <summary>Checks the exact released or asserted packet reset image without disturbing tray mechanics or counters.</summary>
    private static void CheckAtapiHardReset(StateBlock packet, bool asserted) => AtapiCdRom.ValidateHardResetSnapshot(packet, asserted);

    /// <summary>Binds immutable image presence and 2048-byte capacity to the sealed manifest before a branch or media handle is created.</summary>
    private static void CheckAtapiMedia(HardwareState state, MachineDefinition definition, MediaGeneration[] media)
    {
        var profile = definition.Hardware;
        if (!profile.IsAtapi) return;
        string owner = "ata" + profile.AtapiSlot / 2 + "/_atapi";
        var row = media.SingleOrDefault(v => v.Device == "cdrom" && v.Slot == profile.AtapiSlot);
        bool attached = definition.OpticalDiscs.Length == 1;
        if (attached != (row is not null) || row is not null && (row.SectorSize != 2048 || row.JournalName is not null || row.JournalBytes != 0 || row.JournalSha256 is not null))
            throw new InvalidDataException("Optical generation has the wrong kind, extent or writable journal.");
        var channel = new StateReader(state.Components["ata" + profile.AtapiSlot / 2], "DOSEmulator.Hardware.AtaPioController");
        channel.Invariant("_shadowAtapiSlave", (profile.AtapiSlot & 1) == 0 && !definition.HardDisks.Any(v => v.Slot == profile.AtapiSlot + 1));
        AtapiCdRom.ValidateSnapshotState(state.Components[owner], state.Scheduler, row?.Sectors, profile.AtapiSlot & 1,
            channel.Value<int>("_selected") == (profile.AtapiSlot & 1), owner);
    }

    /// <summary>Returns only an already validated optical source, independently from regular status reads or PIC acknowledgements.</summary>
    private static bool AtapiInterrupt(HardwareState state, MachineProfile profile, int channel) =>
        profile.IsAtapi && profile.AtapiSlot / 2 == channel &&
        AtapiCdRom.SavedInterruptAsserted(state.Components["ata" + channel + "/_atapi"]);
}
