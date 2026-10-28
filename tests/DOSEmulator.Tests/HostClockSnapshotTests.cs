// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Full same-build RTC containers, explicit frontend policy boundaries and early transactional time-data rejection on own media.</summary>
internal static class HostClockSnapshotTests
{
    /// <summary>Requires an exact calendar, registered graph, memory or transactional result.</summary>
    private static void Check(bool value, string why = "RTC full-snapshot invariant failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Serializes the actual complete graph; component dictionaries retain every mutable saved value.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);

    /// <summary>Compares every registered field and all physical RAM, without excluding the RTC or diagnostic counters.</summary>
    private static void Same(PcMachine original, PcMachine restored) =>
        Check(State(original) == State(restored) && original.Memory.Ram.AsSpan().SequenceEqual(restored.Memory.Ram),
            "Full graph, scheduler or physical RAM differs after RTC restoration.");

    /// <summary>Requires a policy application to change only the RTC while keeping CPU, deadlines and all RAM exact.</summary>
    private static void ApplyOnlyRtc(PcMachine machine, HostClockSynchronizer policy, DateTime local, ulong host)
    {
        var before = machine.CaptureHardwareState(); byte[] ram = machine.Memory.Ram.ToArray();
        Check(policy.Update(machine, true, local, host), "Explicit owner-boundary policy did not synchronize.");
        var after = machine.CaptureHardwareState(); after.Components["rtc"] = before.Components["rtc"];
        Check(JsonSerializer.Serialize(before, StateValues.Json) == JsonSerializer.Serialize(after, StateValues.Json) &&
            ram.AsSpan().SequenceEqual(machine.Memory.Ram), "Clock policy changed another saved device, CPU, RAM or deadline.");
        Check(machine.Rtc.Calendar == DateTime.SpecifyKind(local, DateTimeKind.Unspecified) &&
            machine.Rtc.Calendar.Kind == DateTimeKind.Unspecified, "Host local civil fields were converted or offset.");
    }

    /// <summary>Uses the real session queue and owner pump, never a frontend field or unobserved background task.</summary>
    private static IpcResponse Request(MachineSession session, string command, object? payload = null)
    {
        var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload));
        session.PumpCommands(); return pending.GetAwaiter().GetResult();
    }

    /// <summary>Runs an own interrupt-disabled RAM loop before anchoring at an intentionally nonzero fractional virtual time.</summary>
    private static void Prepare(PcMachine machine)
    {
        machine.Cpu.Eflags = 2; machine.Cpu.SetSegment(Segment.Ds, 0);
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(machine.Memory.Ram, 0x1000);
        machine.Running = true; Check(machine.RunSteps(4) == 4 && machine.Fault is null);
        machine.Memory.Ram[0x8123] = 0x59; machine.Memory.Ram[^1] = 0xa7;
        machine.Scheduler.AdvanceTo(3_000_000_037);
        machine.Rtc.Write(0x70, 0x0b, 1); machine.Rtc.Write(0x71, 6, 1);
        machine.Rtc.Write(0x70, 0x91, 1); machine.Rtc.Write(0x71, 0x6d, 1);
        machine.Rtc.SetCalendar(new DateTime(2028, 2, 29, 23, 59, 59).AddTicks(9_999_999));
        machine.Scheduler.AdvanceTo(3_000_000_236);
        Check(machine.Rtc.Calendar == new DateTime(2028, 3, 1), "Fractional anchor fixture is not independently known.");
        Check(machine.Rtc.SaveState().Fields["_calendarOriginNanoseconds"].GetUInt64() == 3_000_000_037);
    }

    /// <summary>Repairs only the enclosing integrity fields after one deliberate semantic mutation of own state.</summary>
    private static void Edit(string saved, Action<JsonObject, JsonObject> mutation)
    {
        string statePath = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(statePath))!.AsObject();
        mutation(state, state["components"]!["rtc"]!["fields"]!.AsObject());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(statePath, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.LongLength; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }

    /// <summary>Requires exact early semantic rejection even when the own copied medium cannot be opened, retaining the current owner.</summary>
    private static void RejectBeforeMedium(SnapshotTests.Fixture fixture, string saved)
    {
        var machine = fixture.Machine; string before = State(machine); byte[] ram = machine.Memory.Ram.ToArray();
        string directBranch = fixture.PathFor("bad-direct.dosebranch"), ownerBranch = fixture.PathFor("bad-owner.dosebranch");
        string copiedBase = Path.Combine(saved, "media.dosemedia", "ide0.img");
        Check(File.Exists(copiedBase), "Own immutable MemoryDisk was not copied into the full container.");
        using var locked = new FileStream(copiedBase, FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false;
        try { using var impossible = SnapshotStore.Load(saved, directBranch); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(directBranch), "RTC corruption was accepted or inspected media before semantic validation.");
        Check(State(machine) == before && ram.AsSpan().SequenceEqual(machine.Memory.Ram), "Direct rejection changed the live PC.");
        using var session = new MachineSession(machine);
        long revision = session.Revision, generation = session.InputGeneration;
        var response = Request(session, "LoadSnapshot", new { path = saved, destination = ownerBranch });
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, machine) &&
            revision == session.Revision && generation == session.InputGeneration && !Directory.Exists(ownerBranch),
            "Rejected RTC import replaced the owner, invalidated input, or published a branch.");
        Check(Request(session, "Ping").Success && State(machine) == before && ram.AsSpan().SequenceEqual(machine.Memory.Ram),
            "Failed owner import did not retain the complete live state.");
        long instructions = machine.Cpu.Instructions;
        Check(machine.RunSteps(3) == 3 && machine.Cpu.Instructions == instructions + 3 && machine.Fault is null,
            "Old owner cannot execute after RTC import rejection.");
    }

    /// <summary>Registers full container controls and intentionally corrupted current-build imports with no real host-clock dependency.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (string board in new[] { "486", MachineProfile.Windows95ProfileId, MachineProfile.Windows95Profile133Id })
        {
            string id = board;
            test("host clock snapshot exact nonzero fractional anchor " + id, () =>
            {
                MachineProfile? profile = id == "486" ? null : new() { Id = id, Cpu = MachineProfile.P54cCpuId,
                    CpuHz = id == MachineProfile.Windows95Profile133Id ? 133_000_000 : 100_000_000, RamMiB = 32,
                    VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2 };
                using var f = new SnapshotTests.Fixture(profile, false); var m = f.Machine; Prepare(m);
                string saved = f.PathFor("calendar.dossav"); SnapshotStore.Capture(m, saved);
                using var restored = SnapshotStore.Load(saved, f.PathFor("calendar.dosebranch"));
                Check(restored.IsRestoredSnapshot && restored.Running == m.Running); Same(m, restored);
                m.Scheduler.AdvanceTo(m.Scheduler.Now + 1); restored.Scheduler.AdvanceTo(restored.Scheduler.Now + 1);
                Check(m.Rtc.Calendar == new DateTime(2028, 3, 1).AddTicks(1) &&
                    restored.Rtc.Calendar == new DateTime(2028, 3, 1).AddTicks(1), "Sub-tick phase was discarded by the full container.");
                Same(m, restored);
                Check(m.RunSteps(19) == 19 && restored.RunSteps(19) == 19 && m.Fault is null && restored.Fault is null);
                Same(m, restored);
            });
        }
        test("host clock snapshot retains legacy UTC basis without frontend policy", () =>
        {
            using var f = new SnapshotTests.Fixture(start: false); var m = f.Machine;
            m.Scheduler.AdvanceTo(12_345_678_901); string before = State(m); DateTime date = m.Rtc.Calendar;
            string saved = f.PathFor("legacy.dossav"); SnapshotStore.Capture(m, saved);
            using var restored = SnapshotStore.Load(saved, f.PathFor("legacy.dosebranch"));
            Check(before == State(restored) && date == restored.Rtc.Calendar &&
                restored.Rtc.SaveState().Fields["_epoch"].GetDateTime().Kind == DateTimeKind.Utc);
            var policy = new HostClockSynchronizer();
            Check(!policy.Update(restored, false, new DateTime(2099, 1, 1), 50) && before == State(restored),
                "Disabled frontend policy silently rebased a deterministic snapshot.");
        });
        foreach (bool running in new[] { false, true })
        {
            bool savedRunning = running;
            test("host clock snapshot adopted owner current wall time running " + running, () =>
            {
                using var f = new SnapshotTests.Fixture(start: false); var m = f.Machine; Prepare(m); m.Running = savedRunning;
                string saved = f.PathFor("adopted.dossav"), savedState = State(m); DateTime savedCalendar = m.Rtc.Calendar;
                SnapshotStore.Capture(m, saved);
                var policy = new HostClockSynchronizer(); var retiredDate = new DateTime(2030, 7, 8, 9, 10, 11);
                Check(policy.Update(m, true, retiredDate, 100)); m.Memory.Ram[0x8123] ^= 0xff;
                using var session = new MachineSession(m); long generation = session.InputGeneration, revision = session.Revision;
                Check(Request(session, "LoadSnapshot", new { path = saved, destination = f.PathFor("adopted.dosebranch") }).Success);
                var restored = session.Machine;
                Check(!ReferenceEquals(m, restored) && generation + 1 == session.InputGeneration && revision + 1 == session.Revision &&
                    restored.Running == savedRunning && restored.IsRestoredSnapshot && State(restored) == savedState &&
                    restored.Rtc.Calendar == savedCalendar && restored.Memory.Ram[0x8123] == 0x59,
                    "Session imported the retired clock/battery or mutated saved time before explicit policy.");
                ApplyOnlyRtc(restored, policy, new DateTime(2040, 2, 29, 1, 2, 3, 456, DateTimeKind.Local), 101);
                Check(policy.Synchronizations == 2);
            });
        }
        test("host clock snapshot real session pause and resume resynchronize without advancing saved time", () =>
        {
            using var f = new SnapshotTests.Fixture(start: false); var m = f.Machine; Prepare(m);
            using var session = new MachineSession(m); var policy = new HostClockSynchronizer();
            DateTime first = new(2031, 12, 31, 23, 59, 59); Check(policy.Update(m, true, first, 0));
            Check(Request(session, "Pause").Success && !m.Running);
            string paused = State(m); ulong now = m.Scheduler.Now;
            Check(!policy.Update(m, true, first.AddDays(3), 300_000_000_000) && State(m) == paused,
                "Paused owner was periodically advanced or changed.");
            string saved = f.PathFor("paused.dossav"); SnapshotStore.Capture(m, saved);
            using (var restored = SnapshotStore.Load(saved, f.PathFor("paused.dosebranch")))
                Check(!restored.Running && State(restored) == paused && restored.Rtc.Calendar == first);
            Check(Request(session, "Resume").Success && m.Running);
            ApplyOnlyRtc(m, policy, first.AddDays(3), 300_000_000_001);
            Check(m.Scheduler.Now == now && policy.Synchronizations == 2);
        });
        test("host clock snapshot maximum civil tick permits a representable sub-tick remainder", () =>
        {
            using var f = new SnapshotTests.Fixture(start: false); var m = f.Machine; Prepare(m);
            m.Rtc.SetCalendar(DateTime.MaxValue); m.Scheduler.AdvanceTo(m.Scheduler.Now + 99);
            string saved = f.PathFor("max-tick.dossav"); SnapshotStore.Capture(m, saved);
            using var restored = SnapshotStore.Load(saved, f.PathFor("max-tick.dosebranch"));
            Same(m, restored); Check(restored.Rtc.Calendar.Ticks == DateTime.MaxValue.Ticks);
        });
        foreach (string damage in new[] { "local-epoch", "future-origin", "calendar-overflow", "battery-overflow" })
        {
            string kind = damage;
            test("host clock snapshot rejects rehashed " + kind + " before media and owner adoption", () =>
            {
                using var f = new SnapshotTests.Fixture(start: false); Prepare(f.Machine);
                string saved = f.PathFor("bad.dossav"); SnapshotStore.Capture(f.Machine, saved);
                Edit(saved, (state, rtc) =>
                {
                    ulong now = state["scheduler"]!["now"]!.GetValue<ulong>();
                    switch (kind)
                    {
                        case "local-epoch":
                            rtc["_epoch"] = JsonSerializer.SerializeToNode(new DateTime(2028, 2, 29, 1, 2, 3, DateTimeKind.Local), StateValues.Json); break;
                        case "future-origin": rtc["_calendarOriginNanoseconds"] = now + 1; break;
                        case "calendar-overflow":
                            rtc["_epoch"] = JsonSerializer.SerializeToNode(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Unspecified), StateValues.Json);
                            rtc["_calendarOriginNanoseconds"] = now; rtc["_batteryNanoseconds"] = 100UL; break;
                        case "battery-overflow":
                            rtc["_epoch"] = JsonSerializer.SerializeToNode(DateTime.SpecifyKind(DateTime.MaxValue.AddTicks(-1), DateTimeKind.Unspecified), StateValues.Json);
                            rtc["_calendarOriginNanoseconds"] = now - 1; rtc["_batteryNanoseconds"] = ulong.MaxValue; break;
                        default: throw new InvalidOperationException("Unknown own mutation.");
                    }
                });
                RejectBeforeMedium(f, saved);
            });
        }
    }
}
