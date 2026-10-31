// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Runtime;

/// <summary>Independent mode-zero terminal-count, wrap and pending/completed event restoration oracles.</summary>
internal static class PitModeZeroTests
{
    /// <summary>Checks the first legal terminal edge and every later sampled binary count against integer input ticks.</summary>
    private static void Check(uint reload, bool completed)
    {
        string parent = Path.GetFullPath(Path.Combine("artifacts", "tests"));
        string root = Path.Combine(parent, "pit-zero-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var a = PcMachine.CreateDemo();
            a.Pit.Write(0x43, 0x30, 1); a.Pit.Write(0x40, reload & 255, 1); a.Pit.Write(0x40, (reload >> 8) & 255, 1);
            ulong edge = ((ulong)reload * 1_000_000_000 + 1_193_181) / 1_193_182;
            a.Scheduler.AdvanceTo(edge - 1);
            if (a.Pit.InterruptPulses != 0) throw new Exception("Mode-zero edge occurred before its terminal input tick.");
            if (completed) a.Scheduler.AdvanceTo(edge + 10_000);
            string saved = Path.Combine(root, "own.dossav"); SnapshotStore.Capture(a, saved);
            using var b = SnapshotStore.Load(saved, Path.Combine(root, "own.dosebranch"));
            foreach (ulong target in new[] { edge + 20_000, edge + 60_000_000, edge + 120_000_000 })
            {
                a.Scheduler.AdvanceTo(target); b.Scheduler.AdvanceTo(target);
                if (a.Pit.InterruptPulses != 1 || b.Pit.InterruptPulses != 1) throw new Exception("Restored one-shot repeated or lost its edge.");
                ushort expected = unchecked((ushort)(reload - target * 1_193_182 / 1_000_000_000));
                foreach (var pc in new[] { a, b })
                {
                    pc.Pit.Write(0x43, 0, 1); ushort count = (ushort)(pc.Pit.Read(0x40, 1) | (pc.Pit.Read(0x40, 1) << 8));
                    if (count != expected) throw new Exception("Mode-zero counter stopped instead of wrapping after terminal count.");
                    pc.Pit.Write(0x43, 0xe2, 1);
                    if ((pc.Pit.Read(0x40, 1) & 128) == 0) throw new Exception("Mode-zero OUT did not remain high after terminal count.");
                }
                if (JsonSerializer.Serialize(a.CaptureHardwareState(), StateValues.Json) != JsonSerializer.Serialize(b.CaptureHardwareState(), StateValues.Json))
                    throw new Exception("Mode-zero restore changed a component or scheduler field.");
            }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected test path.");
            Directory.Delete(root, true);
        }
    }

    /// <summary>Registers pending and completed snapshots at the smallest, inner and zero-encoded largest binary loads.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (uint value in new uint[] { 1, 17, 65536 })
            foreach (bool completedValue in new[] { false, true })
            {
                uint reload = value; bool completed = completedValue;
                test($"PIT zero binary {reload} {(completed ? "completed" : "pending")} snapshot wraps without a second edge", () => Check(reload, completed));
            }
    }
}
