// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Runtime;
using DOSEmulator.Cpu.X86;

/// <summary>Bounded cancellation storage, stable event continuation and real PIT-OUT full-container regressions.</summary>
internal static class SchedulerAuditTests
{
    private sealed class Owner(DeviceScheduler clock) : IScheduledStateObject
    {
        public readonly List<string> Publications = [];
        public int PayloadEncodes;
        /// <summary>Schedules only a typed local output event; payload encoding remains deferred to explicit capture.</summary>
        public long Queue(ulong delay, int value) => clock.ScheduleAfter(delay, () => Emit(value), this, "emit", () =>
        { PayloadEncodes++; return StateValues.Pack(value); });
        /// <summary>Publishes the exact virtual deadline and optionally queues an equal-time nested event.</summary>
        private void Emit(int value) { Publications.Add($"{clock.Now}:{value}"); if (value == 42) Queue(0, 43); }
        /// <summary>Rebinds only the fixture's one explicitly supported typed callback.</summary>
        public Action BindStateEvent(string operation, JsonElement[] payload)
        {
            if (operation != "emit" || payload.Length != 1) throw new InvalidDataException("Unknown fixture event.");
            int value = payload[0].GetInt32(); return () => Emit(value);
        }
        /// <summary>Captures no device data because fixture publication history is an external observer.</summary>
        public StateBlock SaveState() => new("SchedulerAuditOwner", 1, []);
        /// <summary>Rejects unexpected fixture state instead of activating arbitrary CLR objects.</summary>
        public void LoadState(StateBlock state)
        { if (state.Type != "SchedulerAuditOwner" || state.Version != 1 || state.Fields.Count != 0) throw new InvalidDataException(); }
        /// <summary>Exposes no nested fixture state or host endpoints.</summary>
        public IEnumerable<(string Name, IStateObject Value)> StateChildren => [];
    }

    /// <summary>Requires a concrete queue, clock, event-publication or complete PC-state invariant.</summary>
    private static void Check(bool value, string message = "Scheduler audit invariant failed")
    { if (!value) throw new Exception(message); }
    /// <summary>Compares serialized explicit state rather than a live delegate/object graph.</summary>
    private static string Encode<T>(T state) => JsonSerializer.Serialize(state, StateValues.Json);
    /// <summary>Maps the fixture's one explicitly named event owner for capture.</summary>
    private static Dictionary<object, string> Names(Owner owner) => new(ReferenceEqualityComparer.Instance) { [owner] = "owner" };
    /// <summary>Maps the fixture's one explicitly declared typed component for prepared import.</summary>
    private static Dictionary<string, IStateObject> Objects(Owner owner) => new() { ["owner"] = owner };
    /// <summary>Requires a bounded failure while allowing the tested old component to remain usable.</summary>
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("Invalid scheduler state unexpectedly accepted.");
    }

    /// <summary>Installs the audit's actual PE32 OUT/JMP storm in a private product PC.</summary>
    private static void TimerGuest(PcMachine machine)
    {
        var c = machine.Cpu; var m = machine.Memory;
        ProtectedModeTests.Descriptor(m, 8, 0, 0xfffff, 0x9a, 12);
        ProtectedModeTests.Descriptor(m, 16, 0, 0xfffff, 0x92, 12);
        m.WriteWord(0x300, 0x17); m.WriteDword(0x302, 0x400);
        byte[] boot = [0x0f,0x01,0x16,0,3,0x66,0xb8,0x11,0,0,0x60,0x0f,0x22,0xc0,0x66,0xea,0,0x20,0,0,8,0];
        boot.CopyTo(m.Ram, 0x1000); new byte[] { 0xe6, 0x40, 0xeb, 0xfc, 0xf4 }.CopyTo(m.Ram, 0x2000);
        c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000;
        for (int i = 0; i < 4; i++) c.Step();
        Check(c.ProtectedMode && c.Eip == 0x2000); c.SetSegment(Segment.Ds, 16); c.SetSegment(Segment.Ss, 16);
        c.SetRegister(Register.Sp, 0x30000); c.SetRegister(Register.Ax, 0xff); c.Eflags = 2;
        machine.Ports.Write(0x43, 0x26, 1);
    }

    /// <summary>Registers cancellation bounds, exact scheduler continuations and private full-PC storm cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int count in new[] { 1024, 8192, 120000 })
            test("Audit scheduler cancelled heap is bounded without clock or token reuse " + count, () =>
            {
                var c = new DeviceScheduler(); var o = new Owner(c); c.AdvanceCycles(137, 33_000_000);
                var before = c.SaveState(Names(o)); ulong ticks = c.ClockTicks(33_000_000);
                long live = o.Queue(1_000_000, 77);
                for (int i = 0; i < count; i++) c.Cancel(o.Queue(1, i));
                var after = c.SaveState(Names(o));
                Check(after.Events.Length <= 1024 && after.Cancelled.Length < 1024 && after.Sequence == count + 1);
                Check(after.Now == before.Now && after.Remainder == before.Remainder && after.CachedFrequency == before.CachedFrequency &&
                    after.CachedCycles == before.CachedCycles && after.Whole == before.Whole && after.Fraction == before.Fraction && c.ClockTicks(33_000_000) == ticks);
                Check(after.Events.Any(e => e.Id == live && e.Time == before.Now + 1_000_000));
                c.AdvanceTo(before.Now + 1_000_000); Check(o.Publications.SequenceEqual(new[] { $"{before.Now + 1_000_000}:77" }));
                Check(c.NextEventTime is null && o.Queue(1, 8) == count + 2);
            });
        test("Audit scheduler compaction refreshes an all-cancelled cached deadline", () =>
        {
            var c = new DeviceScheduler(); var o = new Owner(c);
            for (int i = 0; i < 1024; i++) c.Cancel(o.Queue(1, i));
            Check(c.NextEventTime is null && c.StepBudget(4, 33_000_000, 8192) == 8192);
            Check(c.SaveState(Names(o)).Events.Length == 0 && o.PayloadEncodes == 0);
            c.AdvanceTo(100); Check(o.Publications.Count == 0);
        });
        foreach (uint hz in new uint[] { 33_000_000, 90_000_000 })
            test("Audit scheduler live equal-time nested future survives compacted cut " + hz, () =>
            {
                var a = new DeviceScheduler(); var oa = new Owner(a); a.AdvanceCycles(137, hz);
                ulong now = a.Now; long first = oa.Queue(10_000, 42), second = oa.Queue(10_000, 44), later = oa.Queue(20_000, 45);
                for (int i = 0; i < 120000; i++) a.Cancel(oa.Queue(5_000, i));
                var cut = a.SaveState(Names(oa)); string encoded = Encode(cut); Check(Encode(a.SaveState(Names(oa))) == encoded && a.Now == now);
                Check(cut.Events.Where(e => e.Id == first || e.Id == second || e.Id == later).Select(e => e.Id).SequenceEqual(new[] { first, second, later }));
                var b = new DeviceScheduler(); var ob = new Owner(b); b.LoadState(cut, Objects(ob)); Check(Encode(b.SaveState(Names(ob))) == encoded);
                Check(a.NextEventTime == b.NextEventTime && a.StepBudget(4, hz, 10000) == b.StepBudget(4, hz, 10000));
                a.AdvanceTo(now + 30_000); b.AdvanceTo(now + 30_000);
                Check(oa.Publications.SequenceEqual(ob.Publications) && oa.Publications.SequenceEqual(new[] { $"{now + 10000}:42", $"{now + 10000}:44", $"{now + 10000}:43", $"{now + 20000}:45" }));
                Check(Encode(a.SaveState(Names(oa))) == Encode(b.SaveState(Names(ob))) && a.ClockTicks(hz) == b.ClockTicks(hz));
            });
        test("Audit scheduler compaction within callback preserves live same-time order", () =>
        {
            var c = new DeviceScheduler(); var o = new Owner(c); var log = new List<string>();
            c.ScheduleAfter(10, () => { log.Add("first"); for (int i = 0; i < 1024; i++) c.Cancel(o.Queue(0, i)); });
            c.ScheduleAfter(10, () => log.Add("second")); c.AdvanceTo(10);
            Check(log.SequenceEqual(new[] { "first", "second" }) && o.Publications.Count == 0 && c.NextEventTime is null && !c.IsDispatching);
        });
        test("Audit scheduler future cancellation IDs retain prior cancellation semantics", () =>
        {
            var c = new DeviceScheduler(); var o = new Owner(c); c.Cancel(0); c.Cancel(2049);
            for (int i = 1; i <= 2049; i++) { long id = o.Queue(100, i); Check(id == i); if (i != 2049) c.Cancel(id); }
            c.AdvanceTo(100); Check(o.Publications.Count == 0 && c.NextEventTime is null);
        });
        foreach (long bad in new long[] { -1, 2 })
            test("Audit scheduler capture rejects malformed cancellation token before publication " + bad, () =>
            {
                var c = new DeviceScheduler(); var o = new Owner(c); long id = o.Queue(100, 1); c.Cancel(bad);
                Reject(() => c.SaveState(Names(o))); Check(c.Now == 0 && c.NextEventTime == 100);
                c.AdvanceTo(100); Check(o.Publications.SequenceEqual(new[] { "100:1" }) && id == 1);
            });
        test("Audit scheduler capture rejects inside active callback and resumes normally", () =>
        {
            var c = new DeviceScheduler(); var o = new Owner(c); bool checkedBoundary = false;
            c.ScheduleAfter(1, () => { Reject(() => c.SaveState(Names(o))); checkedBoundary = true; });
            c.AdvanceTo(2); Check(checkedBoundary && !c.IsDispatching && c.Now == 2); Check(c.SaveState(Names(o)).Events.Length == 0);
        });
        test("Audit scheduler exact event resource boundary is resumable", () =>
        {
            var a = new DeviceScheduler(); var oa = new Owner(a);
            for (int i = 0; i < 100000; i++) oa.Queue(1000, i);
            var state = a.SaveState(Names(oa)); Check(state.Events.Length == 100000);
            var b = new DeviceScheduler(); var ob = new Owner(b); b.LoadState(state, Objects(ob));
            Check(Encode(b.SaveState(Names(ob))) == Encode(state));
        });
        test("Audit scheduler oversized live queue fails capture before encoding payloads", () =>
        {
            var c = new DeviceScheduler(); var o = new Owner(c); for (int i = 0; i < 100001; i++) o.Queue(100, i);
            Reject(() => c.SaveState(Names(o))); Check(o.PayloadEncodes == 0 && c.Now == 0 && c.NextEventTime == 100);
            c.AdvanceTo(100); Check(o.Publications.Count == 100002 && o.Publications[0] == "100:0" && o.Publications[^1] == "100:43");
        });
        test("Audit scheduler full PC oversized live queue rejects without snapshot target or lost timer", () =>
        {
            using var f = new SnapshotTests.Fixture();
            for (int i = 0; i < 100001; i++) f.Machine.Scheduler.ScheduleAfter(1000, () => { });
            string path = f.PathFor("oversized.dossav"); ulong now = f.Machine.Scheduler.Now;
            Reject(() => SnapshotStore.Capture(f.Machine, path)); Check(!Directory.Exists(path) && f.Machine.Scheduler.Now == now);
            f.Machine.RunSteps(1, now + 2000); Check(f.Machine.Fault is null && f.Machine.Scheduler.Now == now + 2000);
        });
        foreach (int repeat in new[] { 1, 2 })
            test("Audit scheduler actual120000 PIT OUT full snapshot resumes complete PC " + repeat, () =>
            {
                using var f = new SnapshotTests.Fixture(start: false); var a = f.Machine; TimerGuest(a);
                long outs = 0; a.Ports.Accessed += (p, v, w, write) => { if (p == 0x40 && w == 1 && write) outs++; };
                ulong before = a.Scheduler.Now; Check(a.RunSteps(240000) == 240000 && a.Fault is null && outs == 120000);
                Check(a.Scheduler.Now - before == 29_090_909 && a.Pit.InterruptPulses == 0);
                var storm = a.CaptureHardwareState(); Check(storm.Scheduler.Events.Length <= 1024 && storm.Scheduler.Cancelled.Length < 1024 && storm.Scheduler.Sequence >= 120000);
                a.Cpu.Eip = 0x2004; a.RunSteps(1); Check(a.Cpu.Halted && a.Cpu.GetRegister(Register.Ax) == 0xff);
                string exact = Encode(a.CaptureHardwareState()); using var b = f.Restore();
                Check(Encode(a.CaptureHardwareState()) == exact && Encode(b.CaptureHardwareState()) == exact && a.Memory.Ram.SequenceEqual(b.Memory.Ram));
                for (int i = 0; i < 8; i++)
                {
                    ulong deadline = a.Scheduler.Now + 20_000_000;
                    a.RunSteps(100, deadline); b.RunSteps(100, deadline);
                    Check(Encode(a.CaptureHardwareState()) == Encode(b.CaptureHardwareState()) && a.Memory.Ram.SequenceEqual(b.Memory.Ram));
                }
                Check(a.Fault is null && b.Fault is null && a.Pit.InterruptPulses >= 2 && b.Pit.InterruptPulses == a.Pit.InterruptPulses);
            });
    }
}
