// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Checks actual controller bytes when host input arrives faster than guest mouse reports.</summary>
internal static class MouseEdgeAdmissionTests
{
    /// <summary>Fails with the violated externally observable input contract.</summary>
    private static void Need(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>Reads every actual controller byte, without running a replacement mouse decoder.</summary>
    private static byte[] Drain(Keyboard8042 keyboard)
    {
        var bytes = new List<byte>();
        while (keyboard.OutputByteCount != 0) bytes.Add((byte)keyboard.Read(0x60, 1));
        return bytes.ToArray();
    }
    /// <summary>Configures the real guest mouse command path and removes only its acknowledgements.</summary>
    private static void Enable(PcMachine pc, byte rate)
    {
        pc.Keyboard.Write(0x64, 0x60, 1); pc.Keyboard.Write(0x60, 0x47, 1);
        foreach (byte b in new byte[] { 0xf3, rate, 0xf4 })
        { pc.Keyboard.Write(0x64, 0xd4, 1); pc.Keyboard.Write(0x60, b, 1); }
        Need(Drain(pc.Keyboard).SequenceEqual(new byte[] { 0xfa, 0xfa, 0xfa }), "Real mouse configuration acknowledgements differ.");
        pc.Running = true;
    }
    /// <summary>Creates an own built-in PC with the physical PS/2 device, without opening user media.</summary>
    private static MachineSession Session(byte rate = 40)
    {
        var session = new MachineSession(PcMachine.CreateDemo(new() { Mouse = "ps2-two-button" }));
        Enable(session.Machine, rate); return session;
    }
    /// <summary>Submits one ordinary versioned command and pumps the real session owner once.</summary>
    private static IpcResponse Send(MachineSession session, string command, object? value = null, long? generation = null)
    {
        var request = IpcProtocol.CreateRequest(command, value ?? new { });
        var task = generation is { } epoch ? session.Enqueue(request, epoch) : session.Enqueue(request);
        session.PumpCommands(); return task.GetAwaiter().GetResult();
    }
    /// <summary>Admits only a normal relative sensor input through the public IPC payload.</summary>
    private static bool Mouse(MachineSession session, int buttons, int x = 0, int y = 0) =>
        Send(session, "SendMouseInput", new { x, y, buttons }).Success;
    /// <summary>Registers edge timing, backpressure, lifecycle and retained hardware-policy controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (byte rate in new byte[] { 40, 100, 200 })
            test($"Session mouse edge preserves one-pump click at {rate} Hz", () =>
            {
                using var s = Session(rate); var m = s.Machine; ulong period = 1_000_000_000UL / rate;
                var down = s.Enqueue(IpcProtocol.CreateRequest("SendMouseInput", new { x = 0, y = 0, buttons = 1 }));
                var up = s.Enqueue(IpcProtocol.CreateRequest("SendMouseInput", new { x = 0, y = 0, buttons = 0 }));
                s.PumpCommands(); Need(down.Result.Success && up.Result.Success, "Input was rejected.");
                Need(m.Keyboard.OutputByteCount == 0, "Host admission fabricated an early packet.");
                m.Scheduler.AdvanceTo(period - 1); Need(m.Keyboard.OutputByteCount == 0, "Packet preceded the actual sample.");
                m.Scheduler.AdvanceTo(period); Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 9, 0, 0 }), "Queued release erased the real press.");
                s.PumpCommands(); m.Scheduler.AdvanceTo(period * 2);
                Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 8, 0, 0 }) && m.Mouse!.PacketCount == 2, "Release did not use the next actual sample.");
            });
        test("Session mouse edge waits for controller acceptance under backpressure", () =>
        {
            using var s = Session(); var m = s.Machine;
            for (int i = 0; i < 64; i++) Need(m.Keyboard.TryInjectTranslatedSet1(0x1e), "Own keyboard fill failed.");
            Need(Mouse(s, 1) && Mouse(s, 0), "Input was rejected.");
            m.Scheduler.AdvanceTo(25_000_000); s.PumpCommands();
            Need(m.Mouse!.PacketCount == 0 && Drain(m.Keyboard).Length == 64, "Rejected PS/2 output acknowledged an edge.");
            m.Scheduler.AdvanceTo(50_000_000); Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 9, 0, 0 }), "Press disappeared behind controller backpressure.");
            s.PumpCommands(); m.Scheduler.AdvanceTo(75_000_000);
            Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 8, 0, 0 }), "Release did not follow the accepted press.");
        });
        test("Session mouse edge follows actual remote-mode read acknowledgements", () =>
        {
            using var s = Session(); var m = s.Machine; m.Mouse!.Write(0xf0); Drain(m.Keyboard);
            Need(Mouse(s, 1) && Mouse(s, 0), "Input was rejected.");
            m.Scheduler.AdvanceTo(50_000_000); Need(m.Keyboard.OutputByteCount == 0, "Remote mode invented a stream packet.");
            m.Mouse.Write(0xeb); Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 0xfa, 9, 0, 0 }), "Remote read lost the press.");
            s.PumpCommands(); m.Mouse.Write(0xeb);
            Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 0xfa, 8, 0, 0 }), "Remote release arrived before its predecessor was read.");
        });
        test("Session mouse edge retains movement on both sides of a click", () =>
        {
            using var s = Session(); var m = s.Machine;
            Need(Mouse(s, 1) && Mouse(s, 1, 7, -4) && Mouse(s, 0, -2, 3), "Input was rejected.");
            m.Scheduler.AdvanceTo(25_000_000); Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 0x29, 7, 252 }), "Pressed motion was reordered or stalled.");
            s.PumpCommands(); m.Scheduler.AdvanceTo(50_000_000);
            Need(Drain(m.Keyboard).SequenceEqual(new byte[] { 0x18, 254, 3 }), "Released motion was merged into the preceding press.");
        });
        test("Session mouse edge bounds unreported external transitions", () =>
        {
            using var s = Session(); Need(Mouse(s, 1), "First press rejected.");
            for (int i = 0; i < 128; i++) Need(Mouse(s, i & 1), "Bounded pending input rejected too early.");
            Need(!Mouse(s, 0), "Unbounded external mouse queue admitted excess input.");
        });
        test("Session mouse edge retires unadmitted input when replacing a PC", () =>
        {
            using var s = Session(); long epoch = s.InputGeneration;
            Need(Mouse(s, 1) && Mouse(s, 0) && Mouse(s, 1), "Input was rejected.");
            Need(Send(s, "PowerCycleDemo").Success, "Own cold replacement failed."); Enable(s.Machine, 40);
            s.PumpCommands(); s.Machine.Scheduler.AdvanceTo(75_000_000); s.PumpCommands();
            Need(s.Machine.Keyboard.OutputByteCount == 0, "Retired host input entered the new PC.");
            Need(!Send(s, "SendMouseInput", new { x = 0, y = 0, buttons = 1 }, epoch).Success, "Stale epoch was restamped.");
        });
        test("Session mouse edge retains paused latest-state policy without replaying a click", () =>
        {
            using var s = Session(); Need(Mouse(s, 1) && Mouse(s, 0), "Input was rejected.");
            Need(Send(s, "Pause").Success && Send(s, "Resume").Success, "Pause/resume failed.");
            s.Machine.Scheduler.AdvanceTo(50_000_000); s.PumpCommands();
            Need(s.Machine.Keyboard.OutputByteCount == 0, "Paused input replayed an obsolete click.");
        });
        test("Session mouse edge retains disabled-device latest sensor state", () =>
        {
            using var s = Session(); var m = s.Machine; Need(Mouse(s, 1) && Mouse(s, 0), "Input was rejected.");
            m.Mouse!.Write(0xf5); Drain(m.Keyboard); s.PumpCommands(); m.Mouse.Write(0xf4); Drain(m.Keyboard);
            m.Scheduler.AdvanceTo(50_000_000); s.PumpCommands();
            Need(m.Keyboard.OutputByteCount == 0, "Disabled input produced a ghost click after enable.");
        });
        test("Session mouse edge leaves direct physical sensor sampling unchanged", () =>
        {
            using var s = Session(); var m = s.Machine;
            m.Mouse!.Inject(0, 0, 1); m.Mouse.Inject(0, 0, 0); m.Scheduler.AdvanceTo(25_000_000);
            Need(m.Keyboard.OutputByteCount == 0, "Host admission altered the physical device's direct sampling contract.");
        });
        test("Session mouse edge rejects malformed and out-of-range inputs before admission", () =>
        {
            using var s = Session();
            foreach (object invalid in new object[] { new { x = 32768, y = 0, buttons = 1 }, new { x = 0, y = 0, buttons = 4 }, new { x = 0.5, y = 0, buttons = 1 } })
                Need(!Send(s, "SendMouseInput", invalid).Success, "Invalid input was admitted.");
            s.Machine.Scheduler.AdvanceTo(50_000_000); s.PumpCommands();
            Need(s.Machine.Keyboard.OutputByteCount == 0, "Invalid input had device effects.");
        });
    }
}
