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

    /// <summary>Registers owner-boundary host-clock and honest realtime telemetry checks.</summary>
    internal static void AddTo(Action<string, Action> test)
    {
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
