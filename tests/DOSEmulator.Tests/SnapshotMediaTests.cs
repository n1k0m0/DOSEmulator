// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Coordinated full snapshots with all six private media and unfinished ATA/FDC operations.</summary>
internal static class SnapshotMediaTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Environment.CurrentDirectory, "artifacts/tests/snapshot-media-" + Guid.NewGuid().ToString("N"));
        public readonly PcMachine Machine;
        /// <summary>Creates four COW IDE disks and two physical-geometry floppy COWs from exclusively own patterned bytes.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root); var media = new CopyOnWriteDisk[6];
            for (int i = 0; i < 6; i++)
            {
                byte[] raw = new byte[(i < 4 ? 64 : 1440) * 512];
                for (int j = 0; j < raw.Length; j++) raw[j] = (byte)(j * 13 + i * 37);
                string path = Path.Combine(Root, "own" + i + ".img"); File.WriteAllBytes(path, raw);
                media[i] = new(path, Path.Combine(Root, "own" + i + ".cow"));
                media[i].WriteSector(3, Enumerable.Repeat((byte)(i + 0x70), 512).ToArray()); media[i].Flush();
            }
            Machine = PcMachine.CreateWithDisks(new() { FloppyDriveA = "720k", FloppyDriveB = "720k" }, media.Take(4).ToArray(), media[4], media[5]);
        }
        /// <summary>Disposes all six original leases and removes only this fixture's private directory.</summary>
        public void Dispose() { Machine.Dispose(); Directory.Delete(Path.GetFullPath(Root), true); }
        /// <summary>Captures the unretired hardware operation and opens a separately writable resumed branch.</summary>
        public PcMachine Restore()
        {
            SnapshotStore.Capture(Machine, Path.Combine(Root, "saved.dossav"));
            return SnapshotStore.Load(Path.Combine(Root, "saved.dossav"), Path.Combine(Root, "branch.dosebranch"));
        }
    }
    /// <summary>Requires byte-for-byte state or media equality rather than only matching success flags.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    /// <summary>Compares every named private state field, raw RAM byte and queued event.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        Check(JsonSerializer.Serialize(a.CaptureHardwareState(), StateValues.Json) == JsonSerializer.Serialize(b.CaptureHardwareState(), StateValues.Json), "Hardware state differs.");
        Check(a.Memory.Ram.SequenceEqual(b.Memory.Ram), "Raw RAM differs.");
    }
    /// <summary>Clocks both graphs across the same device deadline and audio integration boundary.</summary>
    private static void Advance(PcMachine a, PcMachine b, ulong delta)
    {
        a.Scheduler.AdvanceTo(a.Scheduler.Now + delta); b.Scheduler.AdvanceTo(b.Scheduler.Now + delta);
        a.Audio.AdvanceTo(a.Scheduler.Now); b.Audio.AdvanceTo(b.Scheduler.Now); Same(a, b);
    }
    /// <summary>Registers actual pending PIO and floppy command continuation cases across four fixed IDE positions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int slot in Enumerable.Range(0, 4))
        foreach (int phase in Enumerable.Range(0, 8))
            test($"Snapshot coordinated six media ATA slot{slot} phase{phase}", () =>
            {
                using var f = new Fixture(); var m = f.Machine; ushort port = slot < 2 ? (ushort)0x1f0 : (ushort)0x170;
                ushort control = slot < 2 ? (ushort)0x3f6 : (ushort)0x376;
                m.Ports.Write((ushort)(port + 6), (uint)(0xe0 | (slot & 1) << 4), 1);
                m.Ports.Write((ushort)(port + 2), phase == 7 ? 2u : 1u, 1);
                m.Ports.Write((ushort)(port + 3), 4, 1); m.Ports.Write((ushort)(port + 4), 0, 1); m.Ports.Write((ushort)(port + 5), 0, 1);
                m.Ports.Write((ushort)(port + 7), phase is 0 or 6 ? 0xecu : phase < 3 ? 0x20u : 0x30u, 1);
                if (phase is 2 or 4 or 5 or 7)
                {
                    m.Scheduler.AdvanceTo(5000);
                    for (int i = 0; i < (phase == 5 ? 256 : 128); i++)
                    { if (phase == 2) m.Ports.Read(port, 2); else m.Ports.Write(port, (uint)(0xb000 + i), 2); }
                }
                if (phase == 6) { m.Ports.Write(control, 4, 1); m.Ports.Write(control, 0, 1); }
                m.Audio.AdvanceTo(m.Scheduler.Now);
                using var restored = f.Restore(); Same(m, restored); Advance(m, restored, 100000);
                // Complete whichever bounded PIO phase remains; every read word and IRQ side effect must match.
                for (int sector = 0; sector < 3; sector++)
                {
                    uint status = m.Ports.Read((ushort)(port + 7), 1); Check(status == restored.Ports.Read((ushort)(port + 7), 1), "ATA status differs.");
                    if ((status & 8) == 0) break;
                    int words = (phase is 2 or 4 or 7) && sector == 0 ? 128 : 256;
                    for (int i = 0; i < words; i++)
                    {
                        if (phase < 3) Check(m.Ports.Read(port, 2) == restored.Ports.Read(port, 2), "PIO word differs.");
                        else { m.Ports.Write(port, (uint)(0x6000 + i), 2); restored.Ports.Write(port, (uint)(0x6000 + i), 2); }
                    }
                    Advance(m, restored, 100000);
                }
                Same(m, restored);
                var a = m.CaptureDefinition(); var b = restored.CaptureDefinition();
                Check(a.HardDisks.Length == 4 && b.HardDisks.Length == 4 && b.Floppies.Length == 2, "A media slot was lost.");
                var sealedA = MediaCheckpointStore.Capture(m, Path.Combine(f.Root, "final-a.dosemedia"));
                var sealedB = MediaCheckpointStore.Capture(restored, Path.Combine(f.Root, "final-b.dosemedia"));
                Check(JsonSerializer.Serialize(sealedA.Media, StateValues.Json) == JsonSerializer.Serialize(sealedB.Media, StateValues.Json), "Continued sealed media differ.");
                // Loading never modifies the sealed generation and a second branch is independently reproducible.
                using var again = SnapshotStore.Load(Path.Combine(f.Root, "saved.dossav"), Path.Combine(f.Root, "again.dosebranch"));
                Check(again.Scheduler.Now <= m.Scheduler.Now, "Saved time changed after continuation.");
            });
        foreach (bool secondSector in new[] { false, true })
            test("Snapshot floppy command and next-sector DMA callback " + secondSector, () =>
            {
                using var f = new Fixture(); var m = f.Machine; m.Ports.Write(0x3f2, 0x1c, 1); m.Scheduler.AdvanceTo(1000000);
                m.Ports.Write(0x0a, 6, 1); m.Ports.Write(0x0c, 0, 1);
                m.Ports.Write(4, 0, 1); m.Ports.Write(4, 0x40, 1); m.Ports.Write(5, 0xff, 1); m.Ports.Write(5, 3, 1);
                m.Ports.Write(0x81, 0, 1); m.Ports.Write(0x0b, 0x46, 1); m.Ports.Write(0x0a, 2, 1); m.Ports.Write(0x3f7, 2, 1);
                foreach (byte value in new byte[] { 0x46, 0, 0, 0, 1, 2, 2, 0x1b, 0xff }) m.Ports.Write(0x3f5, value, 1);
                if (secondSector) m.Scheduler.AdvanceTo(m.Scheduler.NextEventTime!.Value);
                m.Audio.AdvanceTo(m.Scheduler.Now);
                Check(m.CaptureHardwareState().Scheduler.Events.Any(e => e.Owner == "floppy" && e.Operation == (secondSector ? "event3" : "event1")), "Actual FDC command/sector callback missing.");
                using var resumed = f.Restore(); Advance(m, resumed, 1000000000);
                Check(m.Floppy.ReadSectorCount == 2 && resumed.Floppy.ReadSectorCount == 2, "Two actual DMA-read sectors did not finish.");
                Check(m.Memory.Ram.AsSpan(0x4000, 1024).ContainsAnyExcept((byte)0), "No floppy DMA bytes reached RAM.");
            });
        foreach (int phase in Enumerable.Range(0, 4))
            test("Snapshot floppy reset command seek deadlines " + phase, () =>
            {
                using var f = new Fixture(); var m = f.Machine;
                m.Ports.Write(0x3f2, 0x1c, 1);
                if (phase > 0)
                {
                    m.Scheduler.AdvanceTo(1000000);
                    if (phase == 1) { m.Ports.Write(0x3f5, 0x0f, 1); m.Ports.Write(0x3f5, 0, 1); m.Ports.Write(0x3f5, 4, 1); }
                    else { m.Ports.Write(0x3f5, 0x07, 1); m.Ports.Write(0x3f5, 0, 1); }
                    if (phase == 3) { m.Ports.Write(0x3f2, 0, 1); m.Ports.Write(0x3f2, 0x1c, 1); }
                }
                m.Audio.AdvanceTo(m.Scheduler.Now); using var resumed = f.Restore(); Same(m, resumed); Advance(m, resumed, 1000000000);
                for (int i = 0; i < 12; i++)
                { Check(m.Ports.Read(0x3f4, 1) == resumed.Ports.Read(0x3f4, 1), "FDC status differs."); Check(m.Ports.Read(0x3f5, 1) == resumed.Ports.Read(0x3f5, 1), "FDC result differs."); }
                Same(m, resumed);
            });
    }
}
