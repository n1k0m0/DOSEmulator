// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

namespace DOSEmulator;

/// <summary>Exercises the real GUI owner and compiled menus without constructing a window or opening host endpoints.</summary>
internal static class GuiEngineCheck
{
    /// <summary>Checks persistent native RAM, pause/resume, engine-specific snapshots, reset and cold engine replacement on new own media.</summary>
    public static int Run(string directory)
    {
        string output = Path.GetFullPath(directory);
        if (Directory.Exists(output)) throw new IOException("GUI engine check needs a new directory.");
        Directory.CreateDirectory(output);
        byte[] disk = new byte[1 << 20]; disk[0] = 0xeb; disk[1] = 0xfe; disk[510] = 0x55; disk[511] = 0xaa;
        string image = Path.Combine(output, "own-loop.img"); File.WriteAllBytes(image, disk);
        CheckStartupMedia(output, image);
        using var session = new MachineSession(new PcMachine(new MachineProfile().WithDevelopmentCpu(true),
            new CopyOnWriteDisk(image, Path.Combine(output, "own.cow"))), executionFactory: new WhpxExecutionFactory(),
            executionResolver: name => name == "whpx" ? new WhpxExecutionFactory() : throw new ArgumentException("Unknown engine."));
        // Commands use exactly the owner queue consumed by DosWindow and IPC.
        IpcResponse Command(string name, object? payload = null)
        {
            var pending = session.Enqueue(IpcProtocol.CreateRequest(name, payload)); session.PumpCommands();
            var response = pending.GetAwaiter().GetResult();
            if (!response.Success) throw new InvalidOperationException($"{name}: {response.ErrorCode} {response.Payload}");
            return response;
        }
        Command("Resume");
        var host = Stopwatch.StartNew(); ulong before = session.Machine.Scheduler.Now;
        var clock = new HostClockSynchronizer(); bool callbackRejected = false;
        int observations = 0;
        for (int i = 0; i < 160; i++)
        {
            session.PumpCommands();
            ulong now = session.Machine.Scheduler.Now;
            string position = session.GetStatus().Position;
            bool retained = session.Machine.Memory.HasExternalRam;
            DateTime local = DateTime.Now;
            if (clock.Update(session, true, local, (ulong)(host.Elapsed.TotalSeconds * 1e9)) &&
                (session.Machine.Rtc.Calendar != DateTime.SpecifyKind(local, DateTimeKind.Unspecified) ||
                 session.Machine.Scheduler.Now != now || session.GetStatus().Position != position || session.Machine.Memory.HasExternalRam != retained))
                throw new InvalidOperationException("Calendar input changed CPU time, position or native RAM ownership.");
            session.RunRealtime(TimeSpan.FromMilliseconds(16), () =>
            {
                observations++; _ = session.GetStatus();
                if (!callbackRejected)
                {
                    var rejected = new HostClockSynchronizer(); DateTime previous = session.Machine.Rtc.Calendar;
                    try { rejected.Update(session, true, local.AddDays(1), 0); }
                    catch (InvalidOperationException) { callbackRejected = rejected.Synchronizations == 0 && session.Machine.Rtc.Calendar == previous; }
                    if (!callbackRejected) throw new InvalidOperationException("Clock input was accepted inside an active native observation.");
                }
            });
            if (!session.Machine.Memory.HasExternalRam || session.GetStatus().Fault is not null)
                throw new InvalidOperationException("GUI native slice lost shared RAM or faulted.");
        }
        if (clock.Synchronizations < 3 || !callbackRejected) throw new InvalidOperationException("Periodic native host-clock corrections were not exercised.");
        bool snapshotRejected = false;
        try { session.Machine.CaptureHardwareState(); } catch (InvalidOperationException) { snapshotRejected = true; }
        if (!snapshotRejected) throw new InvalidOperationException("The full snapshot RAM boundary was weakened.");
        double percent = 100d * (session.Machine.Scheduler.Now - before) / (host.Elapsed.TotalSeconds * 1e9);
        if (session.Machine.Cpu.Instructions != 0) throw new InvalidOperationException("Stale software CPU executed.");
        Command("Pause");
        if (session.Machine.Memory.HasExternalRam) throw new InvalidOperationException("Pause did not reconcile full RAM.");
        ulong paused = session.Machine.Scheduler.Now; Thread.Sleep(30);
        if (session.Machine.Scheduler.Now != paused) throw new InvalidOperationException("Paused guest time advanced.");
        long corrections = clock.Synchronizations;
        if (clock.Update(session, true, DateTime.Now.AddDays(1), 99_000_000_000) || clock.Synchronizations != corrections)
            throw new InvalidOperationException("Paused calendar was periodically corrected.");
        session.Machine.Memory.WriteByte(0x2a34, 0x7b);
        string saved = Path.Combine(output, "own.whpxsav"); Command("CreateSnapshot", new { path = saved });
        var oldMachine = session.Machine;
        long generation = session.InputGeneration;
        var invalid = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = Path.Combine(output, "absent.whpxsav"), destination = Path.Combine(output, "invalid.dosebranch") }));
        session.PumpCommands();
        if (invalid.GetAwaiter().GetResult().Success || !ReferenceEquals(oldMachine, session.Machine) || generation != session.InputGeneration || Directory.Exists(Path.Combine(output, "invalid.dosebranch")))
            throw new InvalidOperationException("Failed native load replaced the old owner.");
        Command("LoadSnapshot", new { path = saved, destination = Path.Combine(output, "loaded.dosebranch") });
        if (session.Machine.Memory.ReadByte(0x2a34) != 0x7b || session.Machine.Scheduler.Now != paused)
            throw new InvalidOperationException("GUI native full import differs.");
        DateTime restoredNow = new(2031, 1, 2, 3, 4, 5);
        if (!clock.Update(session, true, restoredNow, 100_000_000_000) || session.Machine.Rtc.Calendar != restoredNow)
            throw new InvalidOperationException("Restored native PC did not adopt the supplied host calendar.");
        Command("HardwareReset");
        if (session.GetStatus().Position != "F000:0000FFF0" || session.Machine.Memory.ReadByte(0x2a34) != 0x7b)
            throw new InvalidOperationException("Hardware reset did not reset the actual native CPU or retained RAM.");
        Command("Resume");
        if (!clock.Update(session, true, restoredNow.AddDays(1), 100_000_000_001) || session.Machine.Rtc.Calendar != restoredNow.AddDays(1))
            throw new InvalidOperationException("Native resume did not adopt the current host calendar.");
        session.RunRealtime(TimeSpan.FromMilliseconds(20), () => { }); Command("Pause");
        Command("SetCpuEngine", new { engine = "software" });
        if (session.UsesExternalCpu || session.Machine.Running) throw new InvalidOperationException("Cold software engine switch failed.");
        Command("SetCpuEngine", new { engine = "whpx" });
        if (!session.UsesExternalCpu || session.Machine.Running) throw new InvalidOperationException("Cold WHPX engine switch failed.");
        var window = (DosWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DosWindow));
        // Only managed state used by the actual menu builder is supplied; no window constructor, GL, audio or input call occurs.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(DosWindow).GetField("_session", flags)!.SetValue(window, session);
        typeof(DosWindow).GetField("_settings", flags)!.SetValue(window, new HostSettings { CpuEngine = "whpx" });
        typeof(DosWindow).GetField("_page", flags)!.SetValue(window, "TIME AND SPEED");
        typeof(DosWindow).GetMethod("RebuildItems", flags)!.Invoke(window, null);
        var menu = (string[])typeof(DosWindow).GetField("_items", flags)!.GetValue(window)!;
        if (!menu.Any(s => s.Contains("CPU ENGINE: WHPX"))) throw new InvalidOperationException("Compiled GUI engine menu missing.");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { passed = true, engine = session.CpuEngine, percent, observations, retainedRam = true, pause = true, fullSnapshot = true,
            nativeHardwareReset = true, failedLoadRetainsOwner = true, bothColdEngineSwitches = true,
            clockCorrections = clock.Synchronizations, periodicClock = true, clockKeepsNativeRam = true,
            activeClockInputRejected = callbackRejected, fullSnapshotBoundaryUnchanged = snapshotRejected,
            restoredClock = true, resumedClock = true, startupMediaLease = true,
            startupDefinitionPreserved = true, startupFailureRetainsOldMedia = true,
            menu, windowsConstructed = 0, hostEndpointsOpened = 0 }, IpcProtocol.JsonOptions));
        return 0;
    }

    /// <summary>Exercises the real entry-point upgrade with writable HDD/floppy media and checks preparation failure before the old lease is released.</summary>
    private static void CheckStartupMedia(string output, string image)
    {
        string floppy = Path.Combine(output, "own-floppy.img"); File.WriteAllBytes(floppy, new byte[1440 * 1024]);
        var original = new PcMachine(new MachineProfile { FloppyDriveA = "1440k" },
            new CopyOnWriteDisk(image, Path.Combine(output, "startup-hdd.cow")),
            new CopyOnWriteDisk(floppy, Path.Combine(output, "startup-floppy.cow")));
        using (original)
        {
            var initial = original.CaptureDefinition();
            using var replacement = Program.UpgradeStartupMachine(original);
            var selected = replacement.CaptureDefinition();
            if (!replacement.Profile.IsP54c || replacement.Profile.RamMiB != initial.Hardware.RamMiB ||
                selected.Id != initial.Id || !selected.HardDisks.SequenceEqual(initial.HardDisks) ||
                !selected.Floppies.SequenceEqual(initial.Floppies) || original.Profile.IsP54c)
                throw new InvalidOperationException("Native startup changed the original definition, RAM or media binding.");
            // Reopening both media proves that the replacement owns active HDD and floppy leases.
            using (replacement.CreatePowerCycle(replacement.Profile)) { }
            replacement.Running = true; replacement.RunSteps(100000);
            if (replacement.Fault is not null || replacement.Board.Post10Count != 1)
                throw new InvalidOperationException("Native startup left its prepared HDD lease inactive.");
        }
        using var retained = new PcMachine(new MachineProfile(),
            new CopyOnWriteDisk(image, Path.Combine(output, "startup-failure.cow")));
        using var pending = retained.CreatePowerCycle(retained.Profile);
        bool rejected = false;
        try { using var unexpected = Program.UpgradeStartupMachine(retained); }
        catch (InvalidOperationException) { rejected = true; }
        pending.Dispose();
        using var recovered = retained.CreatePowerCycle(retained.Profile);
        if (!rejected) throw new InvalidOperationException("An invalid startup preparation was accepted.");
    }
}
