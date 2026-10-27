// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Independent nominal timing, adapter decoding, external input and saved-PC regressions.</summary>
internal static class GameportTests
{
    /// <summary>Require a specific input/time/state invariant, rather than accepting an absence of exceptions.</summary>
    private static void Check(bool value) { if (!value) throw new Exception("Analog gameport invariant failed"); }
    /// <summary>Construct four distinct immutable axes without permitting the fixture to mutate device storage.</summary>
    private static GameportInput Input(int? a = 0, int? b = 25000, int? c = 50000, int? d = 100000, int buttons = 0)
        => new() { AxOhms = a, AyOhms = b, BxOhms = c, ByOhms = d, Buttons = buttons };
    /// <summary>Run one normal owner-thread command and retain its actual protocol success/error response.</summary>
    private static IpcResponse Command(MachineSession session, string command, object? payload = null)
    { var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return pending.GetAwaiter().GetResult(); }
    /// <summary>Require invalid external input to leave the original adapter usable and unchanged.</summary>
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid gameport input was accepted"); }

    /// <summary>Register actual boundary, byte-I/O, input isolation, machine and IPC contracts.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // Prefix every case so the bounded package can be selected independently from the entire suite.
        void Add(string name, Action action) => test("Gameport: " + name, action);
        foreach (var point in new[] { (0, 24200UL), (1, 24211UL), (100, 25300UL), (49999, 574189UL), (50000, 574200UL), (99999, 1124189UL), (100000, 1124200UL) })
        for (int index = 0; index < 4; index++)
        {
            int axis = index;
            Add($"axis {axis} nominal {point.Item1} ohms falls at {point.Item2} ns", () =>
            {
                var scheduler = new DeviceScheduler(); var port = new AnalogGameport(scheduler);
                var v = new int?[4]; v[axis] = point.Item1;
                port.SetInput(Input(v[0], v[1], v[2], v[3])); scheduler.AdvanceTo(123456789); port.Write(0x201, 0x96, 1);
                Check(port.Read(0x201, 1) == 255);
                scheduler.AdvanceTo(123456789 + point.Item2 - 1); Check(port.Read(0x201, 1) == 255);
                scheduler.AdvanceTo(123456789 + point.Item2); Check(port.Read(0x201, 1) == (255u & ~(1u << axis)));
                scheduler.AdvanceTo(123456789 + point.Item2 + 1000000000); Check(port.Read(0x201, 1) == (255u & ~(1u << axis)));
                Check(port.TriggerCount == 1 && port.ReadCount == 4 && scheduler.NextEventTime is null);
            });
        }
        foreach (int buttons in Enumerable.Range(0, 16))
            Add("four live active-low switches " + buttons, () =>
            {
                var scheduler = new DeviceScheduler(); var port = new AnalogGameport(scheduler); port.SetInput(Input(buttons: buttons));
                Check(port.Read(0x201, 1) == (uint)((15 ^ buttons) << 4));
                port.Write(0x201, 0, 1); Check(port.Read(0x201, 1) == (uint)(((15 ^ buttons) << 4) | 15));
                port.SetInput(Input(buttons: 15 ^ buttons)); Check(port.Read(0x201, 1) == (uint)((buttons << 4) | 15));
            });
        foreach (int index in Enumerable.Range(0, 64))
            Add("original A9..A0 address decoder alias " + index, () =>
            {
                using var m = PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId }); var port = m.Gameport!;
                ushort address = (ushort)(0x201 + index * 0x400); port.SetInput(Input(buttons: 5));
                m.Ports.Write(address, (uint)index, 1); Check(port.TriggerCount == 1 && m.Ports.Read(address, 1) == 0xaf);
                Check(m.Ports.Read((ushort)(address - 1), 1) == 255 && m.Ports.Read((ushort)(address + 1), 1) == 255);
                Check(port.ReadCount == 1);
            });
        foreach (uint value in new uint[] { 0, 1, 15, 16, 127, 128, 254, 255 })
            Add("write data do not select axes: " + value, () =>
            {
                var s = new DeviceScheduler(); var p = new AnalogGameport(s); p.SetInput(Input()); p.Write(0x201, value, 1);
                s.AdvanceTo(24200); Check(p.Read(0x201, 1) == 0xfe);
                s.AdvanceTo(299200); Check(p.Read(0x201, 1) == 0xfc);
                s.AdvanceTo(574200); Check(p.Read(0x201, 1) == 0xf8);
                s.AdvanceTo(1124200); Check(p.Read(0x201, 1) == 0xf0);
                p.Write(0x201, value, 1); Check(p.Read(0x201, 1) == 255 && p.TriggerTime == 1124200);
            });
        Add("read cannot advance time, retrigger or cancel independent device events", () =>
        {
            var s = new DeviceScheduler(); var p = new AnalogGameport(s); p.SetInput(Input()); p.Write(0x201, 0, 1);
            bool fired = false; s.ScheduleAfter(900000, () => fired = true);
            for (int i = 0; i < 10000; i++) Check(p.Read(0x201, 1) == 255);
            Check(s.Now == 0 && s.NextEventTime == 900000 && !fired && p.TriggerCount == 1);
            s.AdvanceTo(900000); Check(fired && p.Read(0x201, 1) == 0xf8);
        });
        Add("resistances latch at trigger while buttons remain live", () =>
        {
            var s = new DeviceScheduler(); var p = new AnalogGameport(s); p.SetInput(Input()); p.Write(0x201, 0, 1);
            p.SetInput(Input(null, 0, 100000, null, 10)); s.AdvanceTo(574200);
            Check(p.Read(0x201, 1) == 0x58); p.Write(0x201, 0, 1); s.AdvanceTo(574200 + 1124200);
            Check(p.Read(0x201, 1) == 0x59);
        });
        foreach (int bad in new[] { -1, 100001, int.MinValue, int.MaxValue })
        for (int index = 0; index < 4; index++)
        {
            int axis = index;
            Add($"invalid resistance {bad} at axis {axis} is atomic", () =>
            {
                var p = new AnalogGameport(new()); var old = Input(); p.SetInput(old); p.Write(0x201, 0, 1);
                int?[] values = [0, 0, 0, 0]; values[axis] = bad;
                Reject(() => p.SetInput(Input(values[0], values[1], values[2], values[3], 15)));
                Check(ReferenceEquals(old, p.Input) && p.InputUpdates == 1 && p.TriggerCount == 1 && p.Read(0x201, 1) == 255);
            });
        }
        foreach (int bad in new[] { -1, 16, int.MinValue, int.MaxValue })
            Add("invalid switch bits " + bad, () => { var p = new AnalogGameport(new()); Reject(() => p.SetInput(Input(buttons: bad))); Check(p.InputUpdates == 0); });
        foreach (int width in new[] { 2, 4 })
            Add("unsupported wide access is undriven without a hidden byte trigger: " + width, () =>
            {
                var p = new AnalogGameport(new()); p.Write(0x201, 0, width);
                Check(p.Read(0x201, width) == (width == 2 ? 65535 : uint.MaxValue) && p.ReadCount == 0 && p.TriggerCount == 0);
            });
        Add("open circuits remain high after arbitrary time and CPU reset retains the powered conversion", () =>
        {
            using var m = PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId }); var p = m.Gameport!;
            p.SetInput(new()); m.Ports.Write(0x201, 0, 1); m.Scheduler.AdvanceTo(10000000000);
            m.HardwareReset(); Check(m.Ports.Read(0x201, 1) == 255 && p.TriggerCount == 1 && p.InputUpdates == 1);
        });
        foreach (bool configured in new[] { false, true })
            Add("POST equipment/INT11 and AH84 switches, configured=" + configured, () =>
            {
                using var m = PcMachine.CreateDemo(new() { Gameport = configured ? AnalogGameport.ProfileId : "none" }); m.RunSteps(200000);
                Check(m.Fault is null && m.Cpu.Halted && ((m.Memory.ReadWord(0x410) & 0x1000) != 0) == configured);
                Check(m.Board.DebugText.Contains("Game control: IBM analog / 201h") == configured);
                m.Memory.WriteByte(0x1000, 0xf4); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.Cpu.EnterRealModeInterrupt(0x11); m.RunSteps(10000);
                Check(m.Cpu.Halted && ((m.Cpu.GetRegister(Register.Ax) & 0x1000) != 0) == configured);
                m.Cpu.Eip = 0x1000; m.Cpu.SetRegister(Register.Ax, 0x8400); m.Cpu.SetRegister(Register.Dx, 0); m.Cpu.EnterRealModeInterrupt(0x15);
                for (int step = 0; step < 10000 && !(m.Cpu.Halted && m.Cpu.Eip == 0x1001 && m.Cpu.GetSegment(Segment.Cs) == 0); step++) m.RunSteps(1);
                if (!(m.Fault is null && m.Cpu.Halted && (m.Cpu.Eflags & 1) == (configured ? 0u : 1u)))
                    throw new Exception($"AH84 return configured={configured} HLT={m.Cpu.Halted} IP={m.Cpu.Eip:X8} AX={m.Cpu.GetRegister(Register.Ax):X8} FLAGS={m.Cpu.Eflags:X8} fault={m.Fault}");
                Check(configured ? (m.Cpu.GetRegister(Register.Ax) & 0xff) == 0xf0 : (m.Cpu.GetRegister(Register.Ax) & 0xff00) == 0x8600);
            });
        Add("old saved-PC defaults and explicit adapter selection survive serialization", () =>
        {
            Check(JsonSerializer.Deserialize<MachineProfile>("{}", IpcProtocol.JsonOptions)!.Gameport == "none");
            Reject(() => (new MachineProfile { Gameport = "unknown" }).Validate());
            using var m = PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId });
            string dir = Path.Combine("artifacts/tests", "gameport-pc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "pc.dospc.json"); MachineDefinitionStore.Save(file, m.CaptureDefinition());
            var definition = MachineDefinitionStore.Load(file); Check(definition.Hardware.Gameport == AnalogGameport.ProfileId);
            using var cold = MachineFactory.FromDefinition(definition); Check(cold.Gameport is not null && cold.Gameport.Input == new GameportInput());
        });
        Add("IPC capability discovery, external input, invalid rollback and power cycle", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId }));
            var response = Command(session, "GetCapabilities"); Check(response.Success);
            var capabilities = JsonSerializer.SerializeToElement(response.Payload, IpcProtocol.JsonOptions);
            Check(capabilities.GetProperty("analogGameport").GetBoolean() && !capabilities.GetProperty("hostGamepad").GetBoolean());
            Check(capabilities.GetProperty("vbe").GetBoolean() && !capabilities.GetProperty("fullVbeConformance").GetBoolean());
            Check(Command(session, "SetGameportInput", Input(buttons: 7)).Success);
            Check(!Command(session, "SetGameportInput", Input(a: -1)).Success && session.Machine.Gameport!.Input.Buttons == 7);
            Check(Command(session, "PowerCycleCurrent").Success && session.Machine.Gameport!.Input == new GameportInput());
            using var absent = new MachineSession(PcMachine.CreateDemo()); Check(!Command(absent, "SetGameportInput", Input()).Success);
        });
        Add("scenario validates and delivers external input exactly once through the same owner API", () =>
        {
            Reject(() => new GuestTestScenario([new(WaitForDebug: "READY", Gameport: Input(a: -1))]));
            using var m = PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId });
            var scenario = new GuestTestScenario([new(WaitForDebug: "READY", Gameport: Input(buttons: 6), SettleNanoseconds: 0)]);
            m.Ports.Write(0xe9, 'R', 1); m.Ports.Write(0xe9, 'E', 1); m.Ports.Write(0xe9, 'A', 1); m.Ports.Write(0xe9, 'D', 1); m.Ports.Write(0xe9, 'Y', 1);
            scenario.Pump(m); scenario.Pump(m); Check(scenario.Complete && scenario.GameportInputsSent == 1 && m.Gameport!.InputUpdates == 1 && m.Gameport.Input.Buttons == 6);
        });
    }
}
