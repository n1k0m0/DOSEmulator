// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using SharpPixels;

/// <summary>Controlled numeric IPC rejection and monotonic repeat policy without windows, GL or native input.</summary>
internal static class Audit2InputTests
{
    private static readonly (string Command, string Field, int Low, int High)[] Fields =
    [
        ("SetHardDisk", "slot", 0, 3), ("InsertFloppy", "drive", 0, 1), ("EjectFloppy", "drive", 0, 1),
        ("SendMouseInput", "x", -32767, 32767), ("SendMouseInput", "y", -32767, 32767),
        ("SendMouseInput", "buttons", 0, 3), ("SendScanCode", "scanCode", 0, 255)
    ];
    private static readonly string[] BadNumbers =
    ["2147483648", "-2147483649", "9223372036854775808", "1e1000", "1.5", "1.0", "1e0", "\"1\"", "null", "true", "false", "[]", "{}"];

    /// <summary>Requires an actual response, stable state or precise production timing decision.</summary>
    private static void Check(bool value, string message = "Audit2 input invariant failed") { if (!value) throw new Exception(message); }
    /// <summary>Creates only the built-in owned test machine, never a user image or native endpoint.</summary>
    private static MachineSession Session()
    {
        var session = new MachineSession(PcMachine.CreateDemo(new MachineProfile { Mouse = "ps2-two-button" }));
        Prepare(session.Machine); return session;
    }
    /// <summary>Places only the fixture's own three-instruction RAM program at an unprivileged real-mode boundary.</summary>
    private static void Prepare(PcMachine m)
    {
        m.Cpu.Reset();
        foreach (Segment segment in Enum.GetValues<Segment>()) m.Cpu.SetSegment(segment, 0);
        m.Cpu.SetRegister(Register.Ax, 0x1234); m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 2;
        new byte[] { 0x40, 0x40, 0xf4 }.CopyTo(m.Memory.Ram, 0x1000); m.Running = true;
    }
    /// <summary>Captures the exact reviewed component/scheduler graph without writing a snapshot container.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Creates valid JSON with one malformed numeric field while all other values remain controlled.</summary>
    private static JsonElement Payload(string command, string field, string? raw)
    {
        using var defaults = JsonDocument.Parse(command switch
        {
            "SendMouseInput" => "{\"x\":0,\"y\":0,\"buttons\":0}",
            "SendScanCode" => "{\"scanCode\":30}",
            "SetHardDisk" => "{\"slot\":0}",
            "InsertFloppy" => "{\"drive\":0,\"path\":\"C:/DOSE-Audit2-never-open-this.img\"}",
            _ => "{\"drive\":0}"
        });
        var properties = defaults.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (raw is null) properties.Remove(field);
        else { using var value = JsonDocument.Parse(raw); properties[field] = value.RootElement.Clone(); }
        return JsonSerializer.SerializeToElement(properties, IpcProtocol.JsonOptions);
    }
    /// <summary>Requires the malformed request and the following queued Ping to finish in the same owner pump.</summary>
    private static void RejectedAndAlive(string command, JsonElement payload, bool filled = false)
    {
        using var session = Session(); var original = session.Machine;
        if (filled) for (int i = 0; i < 63; i++) Check(original.Keyboard.TryInjectTranslatedSet1(0x1e));
        string before = State(original); long revision = session.Revision;
        var request = new IpcRequest(1, "audit2-bad", command, payload);
        var rejected = session.Enqueue(request); var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping"));
        session.PumpCommands();
        Check(rejected.IsCompletedSuccessfully && ping.IsCompletedSuccessfully, "Malformed numeric request stranded the owner completion.");
        Check(!rejected.Result.Success && rejected.Result.ErrorCode == "InvalidRequest" && rejected.Result.RequestId == request.RequestId,
            "Malformed numeric field did not return its own controlled InvalidRequest response.");
        Check(ping.Result.Success && ReferenceEquals(session.Machine, original) && session.Revision == revision && State(original) == before,
            "Rejected input mutated the machine or blocked the following request.");
        long instructions = original.Cpu.Instructions; Check(original.RunSteps(3) == 3);
        Check(original.Fault is null && original.Cpu.Halted && original.Cpu.Instructions == instructions + 3 &&
            original.Cpu.GetRegister(Register.Ax) == 0x1236, "Own CPU instructions did not continue after malformed input.");
    }
    /// <summary>Queues one legitimate packet through actual owner admission and returns its completed response.</summary>
    private static IpcResponse Send(MachineSession session, int[] packet)
    {
        var task = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCodes = packet }));
        session.PumpCommands(); Check(task.IsCompletedSuccessfully); return task.Result;
    }
    /// <summary>Reads actual queued scan bytes through the controller's data port.</summary>
    private static byte[] Drain(Keyboard8042 keyboard)
    {
        var result = new List<byte>();
        while (keyboard.OutputByteCount != 0) result.Add((byte)keyboard.Read(0x60, 1));
        return result.ToArray();
    }
    /// <summary>Creates only controlled optional virtual devices, allowing each fresh-token positive control to execute its real handler.</summary>
    private static MachineProfile InputProfile() => new()
    {
        RamMiB = 4, Mouse = "ps2-two-button", Gameport = AnalogGameport.ProfileId, SerialPort = Uart16550.ProfileId,
        ParallelPort = ParallelPrinterPort.ProfileId, SoundCard = SoundBlaster16.ProfileId, MidiInterface = Mpu401Uart.ProfileId
    };
    /// <summary>Executes an actual full-container owner replacement using a distinct new branch target.</summary>
    private static void Restore(MachineSession session, string saved, string branch, long token)
    {
        var previous = session.Machine; long generation = session.InputGeneration;
        var task = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = branch }), token);
        session.PumpCommands();
        Check(task.IsCompletedSuccessfully && task.Result.Success && !ReferenceEquals(previous, session.Machine) &&
            session.Machine.IsRestoredSnapshot && session.InputGeneration == generation + 1, "Actual snapshot replacement/generation increment missing.");
    }
    /// <summary>Checks a retired frontend event submitted after replacement, not merely an event queued before it.</summary>
    private static void RetiredInput(string command, object payload)
    {
        using var fixture = new SnapshotTests.Fixture(InputProfile(), false); Prepare(fixture.Machine);
        Check(fixture.Machine.Keyboard.TryInjectTranslatedSet1(0x2e)); fixture.Machine.Mouse!.Inject(0, 0, 2);
        using var session = new MachineSession(fixture.Machine);
        long uiToken = session.InputGeneration;
        var held = new HashSet<int> { 0x1e }; var mouse = new RelativeMouseInput(); mouse.Capture(); mouse.Button(1, true);
        var repeat = new KeyRepeatClock(); repeat.Restart(0);
        string saved = fixture.PathFor("generation.dossav"); SnapshotStore.Capture(session.Machine, saved);
        Restore(session, saved, fixture.PathFor("generation.dosebranch"), uiToken);
        var restored = session.Machine; string before = State(restored); byte[] ram = restored.Memory.Ram.ToArray(); long revision = session.Revision;
        var request = IpcProtocol.CreateRequest(command, payload);
        var retired = session.Enqueue(request, uiToken); var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping"), uiToken);
        session.PumpCommands();
        Check(retired.IsCompletedSuccessfully && !retired.Result.Success && retired.Result.ErrorCode == "StaleInputGeneration" &&
            retired.Result.RequestId == request.RequestId && ping.IsCompletedSuccessfully && ping.Result.Success,
            "A retired UI input received a new epoch or stranded its following command.");
        Check(State(restored) == before && restored.Memory.Ram.AsSpan().SequenceEqual(ram) && session.Revision == revision,
            "Retired UI state changed the restored KBC/mouse/RAM or scheduler.");
        // Model the production UI order exactly: discard local holds without enqueueing guest releases, then acknowledge the observed token.
        long observed = session.InputGeneration; held.Clear(); mouse.Release(); repeat.Reset(); uiToken = observed;
        Check(held.Count == 0 && !mouse.Captured && mouse.Buttons == 0 && !repeat.TryTake(5000, 50, false) && State(restored) == before);
        var fresh = session.Enqueue(IpcProtocol.CreateRequest(command, payload), uiToken); session.PumpCommands();
        Check(fresh.IsCompletedSuccessfully && fresh.Result.Success, "A newly acknowledged input source did not reach actual current device admission.");
        Check(restored.RunSteps(3) == 3 && restored.Fault is null && restored.Cpu.Halted && restored.Cpu.GetRegister(Register.Ax) == 0x1236,
            "Guest could not continue after retired UI input was rejected.");
    }
    /// <summary>Registers targeted malformed-number, whole-pump survival and real repeat-admission regressions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var input in new (string Name, string Command, object Payload)[]
        {
            ("make", "SendScanCode", new { scanCode = 0x1e }), ("break", "SendScanCode", new { scanCode = 0x9e }),
            ("E0 make", "SendScanCode", new { scanCodes = new[] { 0xe0, 0x48 } }), ("E0 break", "SendScanCode", new { scanCodes = new[] { 0xe0, 0xc8 } }),
            ("CtrlAltDel", "SendCtrlAltDel", new { }), ("mouse movement", "SendMouseInput", new { x = 12, y = -7, buttons = 1 }),
            ("mouse release", "SendMouseInput", new { x = 0, y = 0, buttons = 0 }),
            ("gameport", "SetGameportInput", new GameportInput { AxOhms = 1000, Buttons = 1 }),
            ("serial", "SendSerialInput", new SerialInput([0x41])), ("MIDI", "SendMidiInput", new MidiInput([0x90, 60, 127])),
            ("parallel", "SetParallelInput", new ParallelInput(BusyHigh: true))
        })
            test("Audit2 UI import race rejects retired " + input.Name + " enqueued after actual full replacement", () => RetiredInput(input.Command, input.Payload));
        test("Audit2 UI stale acknowledgement across a second replacement cannot stamp old input as current", () =>
        {
            using var fixture = new SnapshotTests.Fixture(InputProfile(), false); Prepare(fixture.Machine);
            Check(fixture.Machine.Keyboard.TryInjectTranslatedSet1(0x2e));
            using var session = new MachineSession(fixture.Machine);
            long oldUi = session.InputGeneration; string saved = fixture.PathFor("twice.dossav"); SnapshotStore.Capture(session.Machine, saved);
            Restore(session, saved, fixture.PathFor("first.dosebranch"), oldUi);
            long observedByUi = session.InputGeneration;
            Restore(session, saved, fixture.PathFor("second.dosebranch"), observedByUi);
            var machine = session.Machine; string before = State(machine); byte[] ram = machine.Memory.Ram.ToArray();
            foreach (long stale in new[] { oldUi, observedByUi })
            {
                var oldMake = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCode = 0x1e }), stale);
                var control = session.Enqueue(IpcProtocol.CreateRequest("Ping"), stale); session.PumpCommands();
                Check(oldMake.IsCompletedSuccessfully && oldMake.Result.ErrorCode == "StaleInputGeneration" && control.Result.Success);
                Check(State(machine) == before && machine.Memory.Ram.AsSpan().SequenceEqual(ram));
            }
            var held = new HashSet<int> { 0x1e }; var mouse = new RelativeMouseInput(); mouse.Capture();
            var repeat = new KeyRepeatClock(); repeat.Restart(0); long confirmed = session.InputGeneration;
            held.Clear(); mouse.Release(); repeat.Reset(); Check(held.Count == 0 && !mouse.Captured && !repeat.TryTake(5000, 50, false));
            var accepted = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCodes = new[] { 0xe0, 0x48 } }), confirmed);
            session.PumpCommands(); Check(accepted.Result.Success && Drain(machine.Keyboard).SequenceEqual(new byte[] { 0x2e, 0xe0, 0x48 }));
            // Existing non-UI callers retain the original overload with the source's current enqueue-time token.
            var compatible = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCode = 0x1e })); session.PumpCommands();
            Check(compatible.Result.Success && Drain(machine.Keyboard).SequenceEqual(new byte[] { 0x1e }));
            Check(machine.RunSteps(3) == 3 && machine.Fault is null && machine.Cpu.Halted && machine.Cpu.GetRegister(Register.Ax) == 0x1236);
        });
        foreach (var field in Fields)
        {
            foreach (string raw in BadNumbers)
                test($"Audit2 N4 {field.Command}.{field.Field} rejects numeric representation {raw}", () =>
                    RejectedAndAlive(field.Command, Payload(field.Command, field.Field, raw)));
            test($"Audit2 N4 {field.Command}.{field.Field} rejects missing numeric property", () =>
                RejectedAndAlive(field.Command, Payload(field.Command, field.Field, null)));
            foreach (int value in new[] { field.Low - 1, field.High + 1, int.MinValue, int.MaxValue }.Distinct())
                test($"Audit2 N4 {field.Command}.{field.Field} rejects out-of-domain integer {value}", () =>
                    RejectedAndAlive(field.Command, Payload(field.Command, field.Field, value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            foreach (string raw in new[] { "null", "[]", "0", "\"input\"" })
                test($"Audit2 N4 {field.Command}.{field.Field} rejects nonobject payload {raw}", () =>
                { using var document = JsonDocument.Parse(raw); RejectedAndAlive(field.Command, document.RootElement.Clone()); });
        }
        foreach (string raw in BadNumbers)
            test("Audit2 N4 scanCodes validates the entire prefix packet before admitting bad byte " + raw, () =>
            {
                using var document = JsonDocument.Parse("{\"scanCodes\":[224," + raw + "]}");
                RejectedAndAlive("SendScanCode", document.RootElement.Clone(), true);
            });
        test("Audit2 N4 a full pump completes all bad numeric requests and interleaved pings", () =>
        {
            using var session = Session(); var tasks = new List<(bool Bad, Task<IpcResponse> Task)>(); string before = State(session.Machine);
            for (int i = 0; i < 128; i++)
            {
                tasks.Add((true, session.Enqueue(new(1, "bad-" + i, "SendMouseInput", Payload("SendMouseInput", "x", "2147483648")))));
                tasks.Add((false, session.Enqueue(IpcProtocol.CreateRequest("Ping"))));
            }
            var full = session.Enqueue(IpcProtocol.CreateRequest("Ping")); Check(full.IsCompletedSuccessfully && full.Result.ErrorCode == "QueueFull");
            session.PumpCommands();
            Check(tasks.All(item => item.Task.IsCompletedSuccessfully && (item.Bad
                ? !item.Task.Result.Success && item.Task.Result.ErrorCode == "InvalidRequest" : item.Task.Result.Success)));
            Check(State(session.Machine) == before);
            var follow = session.Enqueue(IpcProtocol.CreateRequest("Ping")); session.PumpCommands(); Check(follow.Result.Success);
            Check(session.Machine.RunSteps(3) == 3 && session.Machine.Cpu.Halted && session.Machine.Cpu.GetRegister(Register.Ax) == 0x1236);
        });
        foreach (int x in new[] { -32767, -1, 0, 1, 32767 })
            test("Audit2 N4 canonical bounded integer mouse input remains accepted " + x, () =>
            {
                using var session = Session(); var request = session.Enqueue(IpcProtocol.CreateRequest("SendMouseInput", new { x, y = -x, buttons = 3 }));
                session.PumpCommands(); Check(request.IsCompletedSuccessfully && request.Result.Success && session.Machine.Fault is null);
            });
        test("Audit2 N6 normal repeat cadence uses the monotonic 50ms interval", () =>
        {
            var clock = new KeyRepeatClock(); Check(!clock.TryTake(0, 50, true)); Check(!clock.TryTake(49, 50, true));
            Check(clock.TryTake(50, 50, true) && !clock.TryTake(50, 50, true) && !clock.TryTake(99, 50, true));
            Check(clock.TryTake(100, 50, true) && !clock.TryTake(149, 50, true) && clock.TryTake(150, 50, true));
        });
        foreach (long stall in new long[] { 1000, 60000, 3600000, 86400000 })
            test("Audit2 N6 stalled UI discards missed deadlines instead of catching up each frame " + stall, () =>
            {
                var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(stall, 50, true));
                foreach (long next in new[] { stall, stall + 1, stall + 16, stall + 32, stall + 48 }) Check(!clock.TryTake(next, 50, true));
                Check(clock.TryTake(stall + 50, 50, true) && !clock.TryTake(stall + 51, 50, true));
            });
        test("Audit2 N6 modal callback completion starts a fresh interval", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(50, 50, true));
            clock.Restart(10050); Check(!clock.TryTake(10050, 50, true) && !clock.TryTake(10099, 50, true) && clock.TryTake(10100, 50, true));
        });
        test("Audit2 N6 focus pause or empty-held state discards its old deadline", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(!clock.TryTake(5000, 50, false));
            Check(!clock.TryTake(10000, 50, true) && !clock.TryTake(10049, 50, true) && clock.TryTake(10050, 50, true));
            clock.Reset(); Check(!clock.TryTake(60000, 50, true) && clock.TryTake(60050, 50, true));
        });
        test("Audit2 N6 zero interval disables repeats without a per-frame busy stream", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0);
            foreach (long now in new long[] { 0, 1000, 2000, long.MaxValue }) Check(!clock.TryTake(now, 0, true));
            Check(!clock.TryTake(3000, 50, true) && clock.TryTake(3050, 50, true));
        });
        test("Audit2 N6 large monotonic timestamps do not overflow deadline addition", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(long.MaxValue - 50);
            Check(!clock.TryTake(long.MaxValue - 1, 50, true) && clock.TryTake(long.MaxValue, 50, true));
            Check(!clock.TryTake(long.MaxValue, 50, true));
        });
        test("Audit2 N6 a regressing sample rearms without a burst", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(1000);
            Check(!clock.TryTake(999, 50, true) && !clock.TryTake(1048, 50, true) && clock.TryTake(1049, 50, true));
        });
        test("Audit2 N6 invalid negative timestamps do not alter an armed interval", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0); bool rejected = false;
            try { clock.TryTake(-1, 50, true); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && clock.TryTake(50, 50, true));
        });
        test("Audit2 N6 uint interval retains its complete range", () =>
        {
            var clock = new KeyRepeatClock(); clock.Restart(0);
            Check(!clock.TryTake((long)uint.MaxValue - 1, uint.MaxValue, true) && clock.TryTake(uint.MaxValue, uint.MaxValue, true));
        });
        test("Audit2 N6 a rejected repeated E0 packet retains real KBC backpressure and loses old repeat debt", () =>
        {
            using var session = Session(); var k = session.Machine.Keyboard;
            for (int i = 0; i < 63; i++) Check(k.TryInjectTranslatedSet1(0x1e));
            var clock = new KeyRepeatClock(); clock.Restart(0); Check(clock.TryTake(5000, 50, true));
            var rejected = Send(session, [0xe0, 0x48]); Check(!rejected.Success && rejected.ErrorCode == "InputNotAccepted" && k.OutputByteCount == 63);
            Check(Drain(k).All(b => b == 0x1e));
            Check(!clock.TryTake(5001, 50, true) && !clock.TryTake(5049, 50, true) && clock.TryTake(5050, 50, true));
            Check(Send(session, [0xe0, 0x48]).Success && Send(session, [0xe0, 0xc8]).Success);
            Check(Drain(k).SequenceEqual(new byte[] { 0xe0, 0x48, 0xe0, 0xc8 }));
            Check(GuestKeyRepeatPolicy.ShouldRepeat(0x48, true) && !GuestKeyRepeatPolicy.ShouldRepeat(0x1d, true));
        });
    }
}
