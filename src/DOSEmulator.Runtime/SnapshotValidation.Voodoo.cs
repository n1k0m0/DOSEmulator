// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

namespace DOSEmulator.Runtime;

internal static partial class SnapshotValidation
{
    /// <summary>Checks the complete additional-card shape, active completion and reset domain before opening media branches.</summary>
    private static void CheckVoodoo(HardwareState state, MachineProfile profile)
    {
        var cancelled = state.Scheduler.Cancelled.ToHashSet();
        var live = state.Scheduler.Events.Where(e => e.Owner == "voodoo" && !cancelled.Contains(e.Id)).ToArray();
        if (!profile.IsVoodoo)
        { if (state.Components.ContainsKey("voodoo") || live.Length != 0) throw new InvalidDataException("Unexpected additional SST-1 card."); return; }
        if (!state.Components.TryGetValue("voodoo", out var block)) throw new InvalidDataException("Missing additional SST-1 card.");
        VoodooGraphics.ValidateSavedState(block); var r = new StateReader(block, "DOSEmulator.Hardware.VoodooGraphics");
        long token = r.Value<long>("_event"); ulong deadline = r.Value<ulong>("_deadline");
        if (token == 0 ? live.Length != 0 : live.Length != 1 || live[0].Id != token || live[0].Operation != "complete" || live[0].Payload is null || live[0].Payload.Length != 0 ||
            live[0].Time != deadline || deadline <= state.Scheduler.Now || deadline - state.Scheduler.Now > 4_300_000_000)
            throw new InvalidDataException("SST-1 completion token or virtual deadline differs from its queued command.");
        var reset = new StateReader(state.Components["piix-reset"], "DOSEmulator.Hardware.PiixResetController");
        if (reset.Value<PiixResetPhase>("_phase") != PiixResetPhase.Idle && (token != 0 || r.Value<int>("_front") != 0 || r.Value<byte[]>("_config")[4] != 0))
            throw new InvalidDataException("SST-1 work or PCI target remained active inside the held reset domain.");
    }
}
