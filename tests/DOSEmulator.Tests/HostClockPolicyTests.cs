// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Runtime;

/// <summary>Exercises explicit wall-clock inputs without waiting for or changing the real host calendar.</summary>
internal static class HostClockPolicyTests
{
    /// <summary>Rejects an incorrect externally visible time or an unexpected hardware effect.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>Queues a real owner command without pumping it, so several state transitions can share one boundary.</summary>
    private static Task<IpcResponse> Queue(MachineSession session, string command, int version = IpcProtocol.Version) =>
        session.Enqueue(new(version, command, command, JsonSerializer.SerializeToElement(new { })));

    /// <summary>Registers owner-boundary host-clock and honest realtime telemetry checks.</summary>
    internal static void AddTo(Action<string, Action> test)
    {
        test("session host clock routes software RTC input without changing CPU RAM or device time", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            using var session = new MachineSession(machine);
            var policy = new HostClockSynchronizer(); var before = machine.CaptureHardwareState();
            byte[] ram = (byte[])machine.Memory.Ram.Clone(); DateTime local = new(2031, 3, 4, 5, 6, 7);
            Check(policy.Update(session, true, local, 0) && machine.Rtc.Calendar == local, "Session did not route the cold calendar input.");
            Check(policy.Update(session, true, local.AddHours(1), 1_000_000_000) && machine.Rtc.Calendar == local.AddHours(1), "Session missed periodic correction.");
            var after = machine.CaptureHardwareState(); after.Components["rtc"] = before.Components["rtc"];
            Check(JsonSerializer.Serialize(before, StateValues.Json) == JsonSerializer.Serialize(after, StateValues.Json) &&
                ram.AsSpan().SequenceEqual(machine.Memory.Ram) && machine.Cpu.Instructions == 0 && machine.Scheduler.Now == 0,
                "Session clock input changed non-RTC state.");
        });
        test("session host clock preserves disabled pause and coalesced resume policy", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            using var session = new MachineSession(machine); var policy = new HostClockSynchronizer();
            DateTime initial = machine.Rtc.Calendar, local = new(2032, 4, 5, 6, 7, 8);
            Check(!policy.Update(session, false, local, 0) && machine.Rtc.Calendar == initial, "Disabled session synchronization changed the RTC.");
            Check(policy.Update(session, true, local, 1), "Enabled session seed missing.");
            Queue(session, "Pause"); session.PumpCommands();
            Check(!policy.Update(session, true, local.AddDays(1), 1_000_000_001), "Paused session applied a periodic correction.");
            Queue(session, "Resume"); session.PumpCommands();
            Check(policy.Update(session, true, local.AddDays(2), 1_000_000_002) && machine.Rtc.Calendar == local.AddDays(2), "Session resume lost current calendar input.");
        });
        test("session host clock rejects live software callbacks and disposed owners before mutation", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            using var session = new MachineSession(machine); var policy = new HostClockSynchronizer();
            bool rejected = false; DateTime local = new(2033, 5, 6, 7, 8, 9);
            machine.Cpu.InstructionStarting += (_, _) =>
            {
                try { policy.Update(session, true, local, 0); } catch (InvalidOperationException) { rejected = true; }
            };
            machine.RunSteps(1);
            Check(rejected && policy.Synchronizations == 0 && policy.Update(session, true, local, 1), "Live callback consumed session clock history.");
            session.Dispose(); rejected = false;
            try { session.SetCalendar(local.AddDays(1)); } catch (ObjectDisposedException) { rejected = true; }
            Check(rejected, "Disposed owner admitted calendar input.");
        });
        test("host calendar seeds a cold PC without executing or advancing devices", () =>
        {
            using var machine = new PcMachine(new(), null);
            var before = machine.CaptureHardwareState(); byte[] ram = (byte[])machine.Memory.Ram.Clone();
            var local = new DateTime(2028, 2, 29, 23, 59, 58, 321, DateTimeKind.Local);
            var policy = new HostClockSynchronizer();
            Check(policy.Update(machine, true, local, 800), "Cold calendar input missing.");
            Check(machine.Rtc.Calendar == DateTime.SpecifyKind(local, DateTimeKind.Unspecified), "Host wall fields changed.");
            var after = machine.CaptureHardwareState(); after.Components["rtc"] = before.Components["rtc"];
            Check(JsonSerializer.Serialize(before, StateValues.Json) == JsonSerializer.Serialize(after, StateValues.Json), "Calendar changed another device or deadline.");
            Check(ram.AsSpan().SequenceEqual(machine.Memory.Ram), "Calendar input changed guest RAM.");
        });
        test("host calendar is opt in for a deterministic headless PC", () =>
        {
            using var machine = new PcMachine(new(), null);
            var policy = new HostClockSynchronizer(); DateTime before = machine.Rtc.Calendar;
            Check(!policy.Update(machine, false, new DateTime(2099, 1, 1), 0), "Disabled policy changed the PC.");
            Check(machine.Rtc.Calendar == before && policy.Synchronizations == 0, "Deterministic calendar lost.");
        });
        test("host calendar rejects a CPU observer before any RTC or policy mutation", () =>
        {
            using var machine = new PcMachine(new(), null);
            var policy = new HostClockSynchronizer(); DateTime before = machine.Rtc.Calendar;
            bool rejected = false;
            machine.Cpu.InstructionStarting += (_, _) =>
            {
                try { policy.Update(machine, true, new DateTime(2031, 1, 2), 0); }
                catch (InvalidOperationException) { rejected = true; }
            };
            machine.RunSteps(1);
            Check(rejected && policy.Synchronizations == 0 && machine.Rtc.Calendar.Ticks - before.Ticks < 100,
                "Calendar/policy mutated inside a live instruction.");
            Check(policy.Update(machine, true, new DateTime(2031, 1, 2), 1), "Rejected callback poisoned the next owner boundary.");
        });
        test("host calendar follows resume and ignores elapsed pause until resume", () =>
        {
            using var machine = new PcMachine(new(), null);
            var policy = new HostClockSynchronizer(); DateTime first = new(2027, 12, 31, 23, 59, 59);
            policy.Update(machine, true, first, 0);
            Check(!policy.Update(machine, true, first.AddDays(2), 172800000000000), "Paused PC was periodically mutated.");
            machine.Running = true;
            Check(policy.Update(machine, true, first.AddDays(2), 172800000000001), "Resume did not resynchronize.");
            Check(machine.Rtc.Calendar == first.AddDays(2) && machine.Scheduler.Now == 0, "Resume skipped guest work.");
        });
        test("host calendar periodic correction survives a host date jump", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            var policy = new HostClockSynchronizer(); DateTime first = new(2026, 10, 28, 9, 0, 0);
            policy.Update(machine, true, first, 0);
            Check(!policy.Update(machine, true, first.AddHours(1), 999999999), "Policy polled too frequently.");
            Check(policy.Update(machine, true, first.AddHours(1), 1000000000), "Host calendar correction missing.");
            Check(machine.Rtc.Calendar == first.AddHours(1) && machine.Cpu.Instructions == 0, "Wall correction changed CPU time.");
        });
        test("host calendar replacement and reenable use the supplied current time", () =>
        {
            using var first = new PcMachine(new(), null);
            using var replacement = new PcMachine(new(), null);
            var policy = new HostClockSynchronizer(); DateTime now = new(2026, 10, 28);
            policy.Update(first, true, now, 0);
            Check(policy.Update(replacement, true, now.AddYears(1), 1), "Replacement did not receive current time.");
            policy.Update(replacement, false, now.AddYears(2), 2);
            Check(policy.Update(replacement, true, now.AddYears(3), 3), "Reenabled policy did not synchronize.");
            Check(replacement.Rtc.Calendar == now.AddYears(3) && first.Rtc.Calendar == now, "Calendar leaked between owners.");
        });
        test("host calendar old settings without a clock choice retain enabled frontend defaults", () =>
        {
            foreach (string text in new[] { "{}", "{\"schemaVersion\":1,\"filter\":\"Sharp\"}" })
                Check(JsonSerializer.Deserialize<HostSettings>(text, IpcProtocol.JsonOptions)!.SynchronizeClock,
                    "An absent legacy setting disabled the frontend clock default.");
            Check(!JsonSerializer.Deserialize<HostSettings>("{\"synchronizeClock\":false}", IpcProtocol.JsonOptions)!.SynchronizeClock,
                "An explicit disabled setting was overwritten by the default.");
        });
        test("host calendar session resume generation counts only owner-adopted false to true transitions", () =>
        {
            using var machine = new PcMachine(new(), null);
            using var session = new MachineSession(machine);
            Check(session.ResumeGeneration == 0 && !machine.Running, "New session manufactured a resume.");
            var first = Queue(session, "Resume");
            Check(session.ResumeGeneration == 0 && !machine.Running && !first.IsCompleted,
                "Enqueue mutated running state or published a generation before the owner pump.");
            session.PumpCommands();
            Check(first.GetAwaiter().GetResult().Success && machine.Running && session.ResumeGeneration == 1,
                "First actual owner resume was not counted exactly once.");
            var commands = new[] { Queue(session, "Resume"), Queue(session, "Pause"), Queue(session, "Pause"),
                Queue(session, "Resume"), Queue(session, "Resume") };
            session.PumpCommands();
            Check(commands.All(task => task.GetAwaiter().GetResult().Success) && machine.Running && session.ResumeGeneration == 2,
                "Repeated Pause/Resume commands manufactured additional running transitions.");
            Check(session.Revision == 1 && session.InputGeneration == 1 && machine.Scheduler.Now == 0 && machine.Cpu.Instructions == 0,
                "Resume notification changed configuration/input generations or executed hardware.");
        });
        test("host calendar rejected and stopped session commands never publish a resume generation", () =>
        {
            using var machine = new PcMachine(new(), null);
            using var session = new MachineSession(machine);
            var rejected = Queue(session, "Resume", IpcProtocol.Version + 1); session.PumpCommands();
            Check(!rejected.GetAwaiter().GetResult().Success && !machine.Running && session.ResumeGeneration == 0,
                "Protocol rejection adopted a resume transition.");
            session.Dispose(); var stopped = Queue(session, "Resume");
            Check(!stopped.GetAwaiter().GetResult().Success && !machine.Running && session.ResumeGeneration == 0,
                "A stopped owner published a resume transition.");
        });
        foreach (int count in new[] { 1, 3 })
        {
            int transitions = count;
            test($"host calendar coalesced {transitions} Pause Resume pairs resynchronize before the next guest step", () =>
            {
                using var machine = new PcMachine(new(), null) { Running = true };
                using var session = new MachineSession(machine);
                var policy = new HostClockSynchronizer(); var legacy = new HostClockSynchronizer();
                DateTime first = new(2032, 2, 28, 23, 59, 59, 123), current = first.AddDays(3).AddMilliseconds(456);
                Check(policy.Update(machine, true, first, 0, session.ResumeGeneration), "Initial generation-aware seed failed.");
                legacy.Update(machine, true, first, 0);
                string before = JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
                byte[] ram = machine.Memory.Ram.ToArray(); var queued = new List<Task<IpcResponse>>();
                for (int i = 0; i < transitions; i++) { queued.Add(Queue(session, "Pause")); queued.Add(Queue(session, "Resume")); }
                session.PumpCommands();
                Check(queued.All(task => task.GetAwaiter().GetResult().Success) && machine.Running && session.ResumeGeneration == transitions,
                    "Real coalesced owner commands did not produce the independently expected transitions.");
                Check(JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json) == before &&
                    ram.AsSpan().SequenceEqual(machine.Memory.Ram), "Coalesced commands changed saved hardware or RAM.");
                Check(!legacy.Update(machine, true, current, 100), "Legacy state observation unexpectedly saw the coalesced pause.");
                var old = machine.CaptureHardwareState();
                Check(policy.Update(machine, true, current, 100, session.ResumeGeneration) && machine.Rtc.Calendar == current,
                    "A coalesced resume waited for the one-second periodic timer or retained the paused calendar.");
                var after = machine.CaptureHardwareState(); after.Components["rtc"] = old.Components["rtc"];
                Check(JsonSerializer.Serialize(old, StateValues.Json) == JsonSerializer.Serialize(after, StateValues.Json) &&
                    ram.AsSpan().SequenceEqual(machine.Memory.Ram) && machine.Scheduler.Now == 0 && machine.Cpu.Instructions == 0,
                    "Resume resynchronization changed more than the explicit RTC calendar.");
                Check(!policy.Update(machine, true, current.AddHours(1), 101, session.ResumeGeneration) && policy.Synchronizations == 2,
                    "Reading the same resume generation manufactured another synchronization.");
            });
        }
        test("host calendar stale resume generations do not lower a current PC token or poison a replacement", () =>
        {
            using var first = new PcMachine(new(), null) { Running = true };
            using var replacement = new PcMachine(new(), null) { Running = true };
            var policy = new HostClockSynchronizer(); DateTime now = new(2033, 6, 7, 8, 9, 10);
            Check(policy.Update(first, true, now, 0, 7), "First PC token was not established.");
            Check(!policy.Update(first, true, now.AddHours(1), 1, 6) &&
                !policy.Update(first, true, now.AddHours(2), 2, 7) && first.Rtc.Calendar == now,
                "An older token manufactured a resume or lowered the remembered token.");
            Check(policy.Update(first, true, now.AddHours(3), 3, 8), "A genuinely newer token was ignored.");
            Check(policy.Update(replacement, true, now.AddDays(1), 4, 0), "Replacement retained the previous PC token baseline.");
            Check(policy.Update(replacement, true, now.AddDays(2), 5, 1) && replacement.Rtc.Calendar == now.AddDays(2) &&
                first.Rtc.Calendar == now.AddHours(3), "Current replacement resume leaked time into the retired PC.");
        });
        test("host calendar disabled or finally paused coalesced resumes do not mutate RTC", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            using var session = new MachineSession(machine);
            var policy = new HostClockSynchronizer(); DateTime before = machine.Rtc.Calendar, now = new(2034, 7, 8, 9, 10, 11);
            Check(!policy.Update(machine, false, now, 0, session.ResumeGeneration), "Disabled initial token changed RTC.");
            Queue(session, "Pause"); Queue(session, "Resume"); session.PumpCommands();
            Check(!policy.Update(machine, false, now.AddDays(1), 1, session.ResumeGeneration) && machine.Rtc.Calendar == before,
                "A coalesced resume bypassed disabled synchronization.");
            Check(policy.Update(machine, true, now, 2, session.ResumeGeneration), "Reenable did not adopt the current calendar.");
            Queue(session, "Pause"); session.PumpCommands();
            Check(!policy.Update(machine, true, now.AddDays(1), 3, session.ResumeGeneration), "Pause changed the RTC.");
            Queue(session, "Resume"); Queue(session, "Pause"); session.PumpCommands();
            Check(session.ResumeGeneration == 2 && !machine.Running &&
                !policy.Update(machine, true, now.AddDays(2), 4, session.ResumeGeneration) && machine.Rtc.Calendar == now,
                "A resume followed by a final pause executed a calendar correction while paused.");
            Queue(session, "Resume"); session.PumpCommands();
            Check(policy.Update(machine, true, now.AddDays(3), 5, session.ResumeGeneration) &&
                machine.Rtc.Calendar == now.AddDays(3) && machine.Scheduler.Now == 0 && machine.Cpu.Instructions == 0,
                "The following real resume was lost after disabled/paused generation observations.");
        });
        test("host calendar rejected live resume correction leaves its generation available at the next owner boundary", () =>
        {
            using var machine = new PcMachine(new(), null) { Running = true };
            var policy = new HostClockSynchronizer(); DateTime first = new(2035, 8, 9, 10, 11, 12), current = first.AddDays(2);
            policy.Update(machine, true, first, 0, 0); bool rejected = false;
            machine.Cpu.InstructionStarting += (_, _) =>
            {
                try { policy.Update(machine, true, current, 1, 1); }
                catch (InvalidOperationException) { rejected = true; }
            };
            machine.RunSteps(1, null, false);
            Check(rejected && policy.Synchronizations == 1, "Live instruction accepted the RTC or consumed a policy generation.");
            Check(policy.Update(machine, true, current, 2, 1) && policy.Synchronizations == 2 && machine.Rtc.Calendar == current,
                "Rejected correction poisoned the same generation at a valid owner boundary.");
        });
        test("host calendar invalid resume generation is rejected before policy or RTC mutation", () =>
        {
            using var machine = new PcMachine(new(), null);
            var policy = new HostClockSynchronizer(); DateTime before = machine.Rtc.Calendar, now = new(2036, 9, 10, 11, 12, 13);
            bool rejected = false;
            try { policy.Update(machine, true, now, 0, -1); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && policy.Synchronizations == 0 && machine.Rtc.Calendar == before,
                "Invalid generation mutated RTC or policy history.");
            Check(policy.Update(machine, true, now, 1, 0), "Invalid generation poisoned the initial owner seed.");
        });
        foreach (int percent in new[] { 25, 50, 100, 200 })
        {
            int speed = percent;
            test($"realtime meter reports measured {speed} percent without time dilation", () =>
            {
                var meter = new RealtimeSpeedMeter(); meter.Observe(100, 900, true);
                meter.Observe(100 + (ulong)speed * 10000000, 1000000900, true);
                Check(meter.Percent == speed, "Speed report hid actual elapsed virtual time.");
                meter.Observe(100, 1000001000, false);
                Check(meter.Percent is null, "Pause retained an active measurement window.");
                meter.Observe(1, 1000002000, true);
                Check(meter.Percent is null, "Resume included the previous running window.");
            });
        }
        test("realtime meter starts fresh after restore and does not count paused host time", () =>
        {
            var meter = new RealtimeSpeedMeter(); meter.Observe(1000000000, 1000000000, true);
            meter.Observe(2000000000, 2000000000, true); Check(meter.Percent == 100, "Initial speed incorrect.");
            meter.Reset(); meter.Observe(100, 9000000000, true);
            Check(meter.Percent is null, "Restore reused a retired clock origin.");
            meter.Observe(1000000100, 10000000000, true);
            Check(meter.Percent == 100, "Restored speed included host load/pause time.");
        });
    }
}
