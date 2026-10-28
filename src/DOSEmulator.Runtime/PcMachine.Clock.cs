// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Runtime;

public sealed partial class PcMachine
{
    /// <summary>Applies explicit civil calendar input only between complete owner batches, without advancing guest execution.</summary>
    public void SetCalendar(DateTime calendar)
    {
        RequireSnapshotBoundary();
        Rtc.SetCalendar(calendar);
    }
}
