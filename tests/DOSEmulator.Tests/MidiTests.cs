// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Independent protocol, frame-boundary, queue, IRQ-sharing and machine-lifecycle tests for the CT1740 MPU UART.</summary>
internal static class MidiTests
{
    /// <summary>Fail one independently expected protocol or timing invariant.</summary>
    private static void Check(bool value, string why = "MIDI invariant") { if (!value) throw new Exception(why); }
    /// <summary>Read one actual MPU byte register through its public device interface.</summary>
    private static byte R(Mpu401Uart m, int offset = 0) => (byte)m.Read((ushort)(m.BasePort + offset), 1);
    /// <summary>Write one actual MPU data or command byte.</summary>
    private static void W(Mpu401Uart m, byte value, int offset = 0) => m.Write((ushort)(m.BasePort + offset), value, 1);
    /// <summary>Enter UART through the timed command and consume its actual FEh acknowledgement.</summary>
    private static void Enter(DeviceScheduler c, Mpu401Uart m)
    { W(m, 0x3f, 1); c.AdvanceTo(c.Now + 10000); Check(m.UartMode && R(m) == 0xfe && !m.InterruptAsserted); }
    /// <summary>Build a concrete card profile without opening any host MIDI device.</summary>
    private static MachineProfile Profile(int port = 0x330, int irq = 5, int card = 0x220) => new()
    { SoundCard = SoundBlaster16.ProfileId, MidiInterface = Mpu401Uart.ProfileId, MidiBasePort = port, SoundBlasterIrq = irq, SoundBlasterBasePort = card };
    /// <summary>Write an actual mixer index/value pair, preserving independent DSP/MPU acknowledgement paths.</summary>
    private static void Mix(SoundBlaster16 s, byte index, byte value)
    { s.Write((ushort)(s.IoBasePort + 4), index, 1); s.Write((ushort)(s.IoBasePort + 5), value, 1); }
    /// <summary>Read a real mixer register without changing pending source bits.</summary>
    private static byte MixRead(SoundBlaster16 s, byte index = 0x82)
    { s.Write((ushort)(s.IoBasePort + 4), index, 1); return (byte)s.Read((ushort)(s.IoBasePort + 5), 1); }
    /// <summary>Register data transparency, timed transport and integration cases with the existing suite.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // Prefix identifies the bounded transport package separately from previous audio tests.
        void Add(string name, Action action) => test("MIDI: " + name, action);
        Add("cold disconnected command state", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c);
            Check(R(m, 1) == 0xbf && R(m) == 255 && !m.UartMode && c.NextEventTime is null);
            W(m, 0x90); Check(m.RejectedWrites == 1 && m.TransmittedBytes == 0);
        });
        Add("reset is not a UART-input IRQ and has an actual service deadline", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); W(m, 255, 1);
            Check(R(m, 1) == 255); c.AdvanceTo(499999); Check(m.ReceiveCount == 0);
            c.AdvanceTo(500000); Check(R(m, 1) == 0x3f && !m.InterruptAsserted && R(m) == 0xfe && R(m, 1) == 0xbf);
        });
        Add("mode ACK interrupts and status reads do not consume it", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); W(m, 0x3f, 1); c.AdvanceTo(9999);
            Check(!m.UartMode && m.ReceiveCount == 0); c.AdvanceTo(10000);
            for (int i = 0; i < 4; i++) Check(R(m, 1) == 0x3f && m.InterruptAsserted);
            Check(R(m) == 0xfe && !m.InterruptAsserted && m.InterruptEdges == 1);
        });
        foreach (ushort port in new ushort[] { 0x300, 0x330 })
        for (int group = 0; group < 16; group++)
        {
            int first = group * 16;
            Add($"{port:X} exact full frames and raw byte range {first:X2}", () =>
            {
                var c = new DeviceScheduler(); var m = new Mpu401Uart(c, port); Enter(c, m);
                var frames = new List<MidiFrame>(); m.Transmitted += frames.Add;
                for (int i = 0; i < 16; i++)
                {
                    ulong start = c.Now; W(m, (byte)(first + i)); c.AdvanceTo(start + 319999);
                    Check(frames.Count == i); c.AdvanceTo(start + 320000);
                    Check(frames[i] == new MidiFrame(start, start + 320000, (byte)(first + i)));
                }
                Check(m.TransmittedBytes == 16 && m.UartMode && m.Acknowledgements == 1);
            });
        }
        Add("holding readiness is distinct from wire completion and overflow cannot replace data", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m); var frames = new List<MidiFrame>(); m.Transmitted += frames.Add;
            ulong start = c.Now; W(m, 0x90); Check(m.OutputReady && m.Transmitting); W(m, 0xff); W(m, 0x42);
            Check(!m.OutputReady && m.RejectedWrites == 1); c.AdvanceTo(start + 320000);
            Check(m.OutputReady && m.Transmitting && frames[0].Data == 0x90); c.AdvanceTo(start + 640000);
            Check(frames[1] == new MidiFrame(start + 320000, start + 640000, 255) && !m.Transmitting);
        });
        Add("unsupported commands never invent intelligent-mode acknowledgements", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c);
            for (int i = 0; i < 256; i++) if (i is not (0x3f or 255))
            { W(m, (byte)i, 1); c.AdvanceTo(c.Now + 2000); Check(m.ReceiveCount == 0 && !m.UartMode); }
            Enter(c, m); W(m, 0x3f, 1); c.AdvanceTo(c.Now + 2000);
            Check(m.ReceiveCount == 0 && m.IgnoredCommands == 255);
        });
        foreach (int width in new[] { 2, 4 })
        Add($"wide {width} accesses have no decomposed acknowledgement/data effects", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); W(m, 0x3f, 1); c.AdvanceTo(10000);
            m.Write(0x330, 0xff90, width); m.Write(0x331, 255, width);
            Check(m.Read(0x330, width) == (width == 2 ? 65535u : uint.MaxValue) && m.ReceiveCount == 1 && m.UartMode && m.InterruptAsserted);
        });
        Add("RX has full frame boundaries and a 16-byte overrun contract", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m); ulong start = c.Now;
            m.QueueReceive(new(Enumerable.Range(0, 32).ToArray())); c.AdvanceTo(start + 319999); Check(m.ReceiveCount == 0);
            c.AdvanceTo(start + 32 * 320000); Check(m.ReceiveCount == 16 && m.Overruns == 16 && m.ReceivedBytes == 16 && m.ReceivedFrames == 32);
            for (int i = 0; i < 16; i++) { Check(R(m) == i); Check(m.InterruptAsserted == (i < 15)); }
        });
        Add("independent RX/TX clocks overlap without MIDI-message interpretation", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m);
            int[] values = [0xf0, 0xf8, 0x00, 0xf7, 0xfe, 0xff]; ulong start = c.Now; m.QueueReceive(new(values));
            foreach (int value in values) { W(m, (byte)value); c.AdvanceTo(c.Now + 320000); Check(R(m) == value); }
            Check(m.TransmittedBytes == 6 && m.ReceivedBytes == 6 && c.Now == start + 1920000);
        });
        Add("reset aborts unfinished output and invalidates in-flight input without rewinding its peer", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m);
            W(m, 0x90); m.QueueReceive(new([0x41, 0x42, 0x43])); c.AdvanceTo(c.Now + 100); W(m, 255, 1);
            c.AdvanceTo(c.Now + 500000); Check(R(m) == 0xfe && m.TransmittedBytes == 0 && m.AbortedOutputBytes == 1 && m.IgnoredInputFrames == 1);
            Enter(c, m); c.AdvanceTo(c.Now + 1000000);
            Check(m.ReceivedFrames == 3 && m.IgnoredInputFrames == 2 && m.ReceivedBytes == 1 && R(m) == 0x43);
        });
        Add("reset from a completion observer cancels an already scheduled next output frame", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m); m.Transmitted += _ => W(m, 255, 1);
            W(m, 1); W(m, 2); c.AdvanceTo(c.Now + 1000000);
            Check(m.TransmittedBytes == 1 && m.AbortedOutputBytes == 1 && !m.UartMode);
        });
        Add("whole packet capacity and invalid input are checked before any queue mutation", () =>
        {
            var c = new DeviceScheduler(); var m = new Mpu401Uart(c); Enter(c, m);
            int[] bytes = [0x90]; m.QueueReceive(new(bytes)); bytes[0] = 0xff;
            foreach (MidiInput input in new MidiInput[] { new([-1]), new([256]), new([]), new(new int[4096]) })
            { bool fail = false; try { m.QueueReceive(input); } catch (ArgumentException) { fail = true; } Check(fail && m.ExternalFramesPending == 1); }
            c.AdvanceTo(c.Now + 320000); Check(R(m) == 0x90);
        });
        foreach (int port in new[] { 0x300, 0x330 })
        foreach (int irq in new[] { 5, 7, 9, 10 })
        foreach (int card in new[] { 0x220, 0x240, 0x260, 0x280 })
        Add($"profile resources {port:X}/{irq}/{card:X} and unchanged adjacent decode", () =>
        {
            var profile = Profile(port, irq, card); profile.Validate(); using var m = PcMachine.CreateDemo(profile);
            Check(m.Midi!.BasePort == port && m.Ports.Read((ushort)(port + 1), 1) == 0xbf && m.Ports.Read((ushort)(port + 2), 1) == 255);
            Check(profile.GetBlasterEnvironment().Contains($" P{port:X3} T6"));
        });
        foreach (int irq in new[] { 5, 7, 9, 10 })
        Add($"all three shared sources and separate acknowledgements on IRQ{irq}", () =>
        {
            using var m = PcMachine.CreateDemo(Profile(irq: irq)); var s = m.SoundBlaster16!;
            s.Write(0x226, 1, 1); m.Scheduler.AdvanceTo(3000); s.Write(0x226, 0, 1); m.Scheduler.AdvanceTo(103000);
            W(m.Midi!, 0x3f, 1); m.Scheduler.AdvanceTo(113000);
            s.Write(0x22c, 0xf2, 1); m.Scheduler.AdvanceTo(114000); s.Write(0x22c, 0xf3, 1); m.Scheduler.AdvanceTo(115000);
            Check(MixRead(s) == 7 && MixRead(s) == 7); Mix(s, 0x82, 255); Check(MixRead(s) == 7);
            _ = R(m.Midi!, 1); _ = s.Read(0x22f, 2); Check(MixRead(s) == 7);
            _ = s.Read(0x22e, 1); Check(MixRead(s) == 6); Check(R(m.Midi!) == 0xfe && MixRead(s) == 2);
            _ = s.Read(0x22e, 1); Check(MixRead(s) == 2); _ = s.Read(0x22f, 1); Check(MixRead(s) == 0);
        });
        Add("DSP/mixer resets preserve MPU source, data and the serial frame", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); W(m.Midi!, 0x3f, 1); m.Scheduler.AdvanceTo(10000);
            m.SoundBlaster16!.Write(0x226, 1, 1); Check(m.Midi!.UartMode && MixRead(m.SoundBlaster16) == 4);
            Mix(m.SoundBlaster16, 0, 0); Check(MixRead(m.SoundBlaster16) == 4 && R(m.Midi) == 0xfe);
            W(m.Midi, 0x90); m.HardwareReset(); m.Scheduler.AdvanceTo(m.Scheduler.Now + 320000); Check(m.Midi.TransmittedBytes == 1);
            using var cold = MachineFactory.FromDefinition(m.CaptureDefinition(), m); Check(cold.Midi is { UartMode: false, TransmittedBytes: 0, ExternalFramesPending: 0 });
        });
        Add("guest mixer reroutes a pending MIDI source without falsely acknowledging it", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); W(m.Midi!, 0x3f, 1); m.Scheduler.AdvanceTo(10000);
            Mix(m.SoundBlaster16!, 0x80, 8); Check(m.SoundBlaster16!.Irq == 10 && MixRead(m.SoundBlaster16) == 4);
            Mix(m.SoundBlaster16, 0x80, 0); Check(MixRead(m.SoundBlaster16) == 4 && m.Midi!.InterruptAsserted);
            Mix(m.SoundBlaster16, 0x80, 2); Check(R(m.Midi!) == 0xfe && MixRead(m.SoundBlaster16) == 0);
        });
        Add("owned IPC/scenario input, old profiles and invalid card combinations", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo(Profile()));
            var pending = session.Enqueue(IpcProtocol.CreateRequest("SendMidiInput", new MidiInput([0x90, 60, 127]))); session.PumpCommands();
            Check(pending.Result.Success && session.Machine.Midi!.ExternalFramesPending == 3);
            Check(JsonSerializer.Deserialize<MachineProfile>("{}", IpcProtocol.JsonOptions)!.MidiInterface == "none");
            bool rejected = false; try { (Profile() with { SoundCard = "none" }).Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected);
            int[] data = [0x41]; var scenario = new GuestTestScenario([new(WaitForDebug: "READY", Midi: new(data), SettleNanoseconds: 0)]); data[0] = -1;
            Check(scenario.MidiInputsSent == 0);
        });
        Add("configured MIDI POST leaves BIOS equipment port counters unchanged", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); m.RunSteps(100000);
            Check(m.Fault is null && m.Board.DebugText.Contains("MIDI: CT1740 MPU-UART / 330h IRQ5"));
            Check(m.Memory.ReadWord(0x400) == 0 && m.Memory.ReadWord(0x408) == 0 && (m.Memory.ReadWord(0x410) & 0xde00) == 0);
        });
    }
}
