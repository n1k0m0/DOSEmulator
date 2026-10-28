// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Own-board UART, MPU and LPT reset boundaries with independent wire timing and complete private-PC continuation.</summary>
internal static class PiixResetExternalTests
{
    /// <summary>Requires an actual port, published byte, physical pin or future-state result.</summary>
    private static void Check(bool value, string why = "PIIX external reset contract failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Writes one real UART byte register.</summary>
    private static void W(Uart16550 u, int offset, byte value) => u.Write((ushort)(Uart16550.BasePort + offset), value, 1);
    /// <summary>Reads one real UART byte register with its normal acknowledgement effects.</summary>
    private static byte R(Uart16550 u, int offset) => (byte)u.Read((ushort)(Uart16550.BasePort + offset), 1);
    /// <summary>Programs both divisor bytes and actual receiver format before ordinary aliases are used.</summary>
    private static void Serial(Uart16550 u, ushort divisor = 12, byte format = 3)
    { W(u, 3, (byte)(format | 0x80)); W(u, 0, (byte)divisor); W(u, 1, (byte)(divisor >> 8)); W(u, 3, format); }
    /// <summary>Computes complete half-bit units independently of the device helper.</summary>
    private static ulong HalfBits(byte format) => (ulong)(2 * (1 + 5 + (format & 3) + ((format & 8) != 0 ? 1 : 0)) +
        ((format & 4) == 0 ? 2 : (format & 3) == 0 ? 3 : 4));
    /// <summary>Computes the independently sampled frame boundary with an optional previous crystal remainder.</summary>
    private static ulong Frame(byte format, ushort divisor, ulong remainder = 0) =>
        (HalfBits(format) * 8 * divisor * 1_000_000_000 + remainder) / 1_843_200;
    /// <summary>Requires a genuine scheduled member token before its reset boundary is exercised.</summary>
    private static long Token(IStateObject component, string field) => component.SaveState().Fields[field].GetInt64();
    /// <summary>Enters the existing nominal UART mode and removes its actual command acknowledgement.</summary>
    private static void MidiMode(DeviceScheduler clock, Mpu401Uart midi)
    { midi.Write((ushort)(midi.BasePort + 1), 0x3f, 1); clock.AdvanceTo(clock.Now + 10_000); Check(midi.Read(midi.BasePort, 1) == 0xfe && midi.UartMode); }
    /// <summary>Releases synthetic INIT before connecting an idle explicitly virtual sink.</summary>
    private static (DeviceScheduler Clock, ParallelPrinterPort Port, VirtualParallelPrinter Printer) PrinterRig()
    {
        var clock = new DeviceScheduler(); var port = new ParallelPrinterPort(clock, _ => { });
        port.Write(0x37a, 12, 1); return (clock, port, port.ConnectPrinter());
    }
    /// <summary>Creates a physical pulse with independently chosen 500-ns setup and strobe periods.</summary>
    private static void Pulse(DeviceScheduler clock, ParallelPrinterPort port, byte value)
    {
        port.Write(0x378, value, 1); clock.AdvanceTo(clock.Now + 500); port.Write(0x37a, 13, 1);
        clock.AdvanceTo(clock.Now + 500); port.Write(0x37a, 12, 1);
    }
    /// <summary>Advances all PC timing without CPU execution and keeps the real partial PCM integrator coherent.</summary>
    private static void Advance(PcMachine machine, ulong amount)
    { machine.Scheduler.AdvanceTo(machine.Scheduler.Now + amount); machine.Audio.AdvanceTo(machine.Scheduler.Now); }
    /// <summary>Compares every saved component/event and every installed physical RAM byte.</summary>
    private static void Same(PcMachine first, params PcMachine[] others)
    {
        string state = JsonSerializer.Serialize(first.CaptureHardwareState(), StateValues.Json);
        foreach (var other in others)
            Check(state == JsonSerializer.Serialize(other.CaptureHardwareState(), StateValues.Json) &&
                first.Memory.Ram.AsSpan().SequenceEqual(other.Memory.Ram), "Complete external reset continuation differs.");
    }

    /// <summary>Registers meaningful active/deadline boundaries; no physical host endpoint or original media is used.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int pins in Enumerable.Range(0, 16)) foreach (bool loop in new[] { false, true })
            test($"M4 PIIX UART reset keeps physical modem pins {pins:X1} loop={loop}", () =>
            {
                var clock = new DeviceScheduler(); var levels = new List<bool>(); var uart = new Uart16550(clock, levels.Add);
                Serial(uart); W(uart, 2, 0xc1); W(uart, 4, loop ? (byte)0x1f : (byte)0x0f); W(uart, 1, 15); W(uart, 7, 0x96);
                uart.SetModemInputs((pins & 1) != 0, (pins & 2) != 0, (pins & 4) != 0, (pins & 8) != 0);
                clock.AdvanceTo(1234); long edges = uart.InterruptEdges; ulong now = clock.Now; uart.ApplyPiixHardReset();
                Check(clock.Now == now && uart.PhysicalModem == pins << 4 && uart.EffectiveModem == pins << 4);
                Check(uart.LineControl == 0 && uart.ModemControl == 0 && uart.InterruptEnable == 0 && uart.FifoControl == 0 && uart.Divisor == 0);
                Check(R(uart, 2) == 1 && R(uart, 5) == 0x60 && R(uart, 6) == pins << 4 && R(uart, 7) == 0);
                Check(!uart.InterruptAsserted && uart.InterruptEdges == edges && uart.ReceivedCount == 0 && uart.TransmitCount == 0);
                Check(clock.NextEventTime is null && levels.LastOrDefault() == false);
            });
        foreach (byte format in new byte[] { 0, 3, 7, 0x1f }) foreach (bool reprogramBeforeEnd in new[] { false, true })
            test($"M4 PIIX UART reset rejects old complete frame {format:X2} reprogram={reprogramBeforeEnd}", () =>
            {
                var clock = new DeviceScheduler(); var uart = new Uart16550(clock, _ => { }); Serial(uart, 7, format);
                uart.QueueReceive([0x75, 0xa6], 4); ulong first = Frame(format, 7);
                ulong remainder = HalfBits(format) * 8 * 7 * 1_000_000_000 % 1_843_200;
                clock.AdvanceTo(first / 2); long token = Token(uart, "_rxEvent"); uart.ApplyPiixHardReset();
                Check(Token(uart, "_rxEvent") == token && uart.ReceivedCount == 0 && clock.NextEventTime == first);
                if (reprogramBeforeEnd) { Serial(uart); W(uart, 2, 1); }
                clock.AdvanceTo(first - 1); Check(uart.ExternalCharactersIgnored == 0 && uart.ReceivedCharacters == 0);
                clock.AdvanceTo(first); Check(uart.ExternalCharactersIgnored == 1 && uart.ReceivedCharacters == 0 && uart.ReceivedCount == 0);
                ulong secondStart = first;
                if (!reprogramBeforeEnd)
                {
                    Check(clock.NextEventTime is null, "Divisor zero started an unclocked queued UART frame.");
                    clock.AdvanceTo(first + 10_000); secondStart = clock.Now; Serial(uart); W(uart, 2, 1);
                }
                ulong end = secondStart + Frame(3, 12, remainder);
                clock.AdvanceTo(end - 1); Check(uart.ReceivedCharacters == 0);
                clock.AdvanceTo(end); Check(uart.ReceivedCharacters == 1 && uart.ExternalCharactersQueued == 2 &&
                    uart.ExternalCharactersIgnored == 1 && (R(uart, 5) & 5) == 5 && R(uart, 0) == 0xa6);
            });
        test("M4 PIIX UART reset repeated generations keep one in-flight deadline and one ignored result", () =>
        {
            var clock = new DeviceScheduler(); var uart = new Uart16550(clock, _ => { }); Serial(uart); uart.QueueReceive([0x96]);
            long token = Token(uart, "_rxEvent"); clock.AdvanceTo(1000); uart.ApplyPiixHardReset();
            clock.AdvanceTo(2000); uart.ApplyPiixHardReset(); Serial(uart);
            Check(Token(uart, "_rxEvent") == token && Token(uart, "_receiveGeneration") == 2);
            clock.AdvanceTo(2_000_000); Check(uart.ExternalCharactersIgnored == 1 && uart.ReceivedCharacters == 0 && uart.ReceivedCount == 0);
        });
        test("M4 PIIX UART reset retains unstarted outer queue while divisor zero pauses its clock", () =>
        {
            var clock = new DeviceScheduler(); var uart = new Uart16550(clock, _ => { }); uart.QueueReceive([0x35, 0x36]);
            uart.ApplyPiixHardReset(); uart.ApplyPiixHardReset(); clock.AdvanceTo(5_000_000);
            Check(uart.ExternalCharactersQueued == 2 && uart.ExternalCharactersIgnored == 0 && clock.NextEventTime is null);
            Serial(uart); W(uart, 2, 1); clock.AdvanceTo(8_000_000);
            Check(uart.ReceivedCharacters == 2 && R(uart, 0) == 0x35 && R(uart, 0) == 0x36);
        });
        foreach (string phase in new[] { "start", "shift", "thre", "timeout", "break", "loopback" })
            test("M4 PIIX UART reset cancels old " + phase + " effect", () =>
            {
                var clock = new DeviceScheduler(); var frames = new List<UartFrame>(); var levels = new List<bool>();
                var uart = new Uart16550(clock, levels.Add); Serial(uart); W(uart, 2, 0xc1); W(uart, 4, 8); W(uart, 1, 15);
                uart.Transmitted += frames.Add; string tokenField;
                if (phase == "timeout")
                {
                    uart.QueueReceive([0x53]); clock.AdvanceTo(Frame(3, 12)); tokenField = "_timeoutEvent";
                    Check(uart.ReceivedCharacters == 1 && uart.ReceivedCount == 1);
                }
                else if (phase == "break")
                { W(uart, 4, 16); W(uart, 3, 0x43); tokenField = "_breakEvent"; }
                else
                {
                    if (phase == "loopback") W(uart, 4, 16);
                    W(uart, 0, 0x51);
                    if (phase != "start") clock.AdvanceTo(16UL * 12 * 1_000_000_000 / 1_843_200);
                    tokenField = phase == "start" ? "_startEvent" : phase == "thre" ? "_threEvent" : "_txEvent";
                }
                Check(Token(uart, tokenField) != 0, "The intended UART callback was not pending.");
                long received = uart.ReceivedCharacters, edges = uart.InterruptEdges; ulong now = clock.Now;
                uart.ApplyPiixHardReset(); Check(clock.Now == now && Token(uart, tokenField) == 0);
                foreach (string field in new[] { "_startEvent", "_txEvent", "_timeoutEvent", "_threEvent", "_breakEvent" })
                    Check(Token(uart, field) == 0);
                clock.AdvanceTo(now + 100_000_000);
                Check(frames.Count == 0 && uart.TransmittedCharacters == 0 && uart.ReceivedCharacters == received &&
                    uart.ReceivedCount == 0 && !uart.InterruptAsserted && uart.InterruptEdges == edges && R(uart, 5) == 0x60);
            });
        test("M4 PIIX UART reset preserves published frames and aborts active plus holding output", () =>
        {
            var clock = new DeviceScheduler(); var uart = new Uart16550(clock, _ => { }); Serial(uart); W(uart, 2, 1);
            var frames = new List<UartFrame>(); uart.Transmitted += frames.Add; W(uart, 0, 0x51); clock.AdvanceTo(2_000_000);
            Check(frames.Count == 1 && frames[0].Data == 0x51); var published = frames[0];
            W(uart, 0, 0x52); W(uart, 0, 0x53); clock.AdvanceTo(clock.Now + 200_000);
            Check(uart.Transmitting && uart.TransmitCount == 1); uart.ApplyPiixHardReset(); clock.AdvanceTo(10_000_000);
            Check(frames.Count == 1 && frames[0] == published && uart.TransmittedCharacters == 1 && !uart.Transmitting && uart.TransmitCount == 0);
        });
        test("M4 PIIX UART reset from completed-frame observer prevents the queued next shift", () =>
        {
            var clock = new DeviceScheduler(); var uart = new Uart16550(clock, _ => { }); Serial(uart); W(uart, 2, 1);
            int frames = 0; uart.Transmitted += _ => { frames++; uart.ApplyPiixHardReset(); };
            W(uart, 0, 0x51); W(uart, 0, 0x52); clock.AdvanceTo(10_000_000);
            Check(frames == 1 && uart.TransmittedCharacters == 1 && !uart.Transmitting && uart.TransmitCount == 0 && clock.NextEventTime is null);
        });
        foreach (byte command in new byte[] { 0xff, 0x3f, 0x11 })
            test("M4 PIIX MPU hardware reset cancels old command without unsolicited ACK " + command.ToString("X2"), () =>
            {
                var clock = new DeviceScheduler(); var midi = new Mpu401Uart(clock); midi.Write(0x331, command, 1);
                midi.QueueReceive(new([0x21, 0x22])); Check(Token(midi, "_commandEvent") != 0); midi.ApplyPiixHardReset();
                Check(Token(midi, "_commandEvent") == 0 && midi.OutputReady && !midi.UartMode && !midi.InterruptAsserted && midi.ReceiveCount == 0);
                clock.AdvanceTo(1_000_000);
                Check(midi.Acknowledgements == 0 && midi.Resets == (command == 0xff ? 1 : 0) && midi.Status == 0xbf &&
                    midi.ReceivedFrames == 2 && midi.IgnoredInputFrames == 2 && midi.ExternalFramesPending == 0);
            });
        test("M4 PIIX MPU reset preserves published output and aborts active plus holding frame", () =>
        {
            var clock = new DeviceScheduler(); var midi = new Mpu401Uart(clock); MidiMode(clock, midi);
            var frames = new List<MidiFrame>(); midi.Transmitted += frames.Add;
            midi.Write(0x330, 0x91, 1); clock.AdvanceTo(clock.Now + 320_000); Check(frames.Count == 1);
            var published = frames[0]; midi.Write(0x330, 0x92, 1); midi.Write(0x330, 0x93, 1);
            Check(midi.Transmitting && !midi.OutputReady); ulong now = clock.Now; midi.ApplyPiixHardReset();
            clock.AdvanceTo(now + 2_000_000);
            Check(frames.Count == 1 && frames[0] == published && midi.TransmittedBytes == 1 && midi.AbortedOutputBytes == 2 &&
                midi.Acknowledgements == 1 && midi.Resets == 0 && !midi.Transmitting && midi.OutputReady);
        });
        foreach (bool reenter in new[] { false, true })
            test("M4 PIIX MPU reset keeps outer line while rejecting stale RX reenter=" + reenter, () =>
            {
                var clock = new DeviceScheduler(); var midi = new Mpu401Uart(clock); MidiMode(clock, midi);
                var receptions = new List<MidiReception>(); midi.Received += receptions.Add;
                ulong began = clock.Now; midi.QueueReceive(new([0x90, 0x45])); long token = Token(midi, "_receiveEvent");
                clock.AdvanceTo(began + 100_000); midi.ApplyPiixHardReset(); Check(Token(midi, "_receiveEvent") == token);
                if (reenter) MidiMode(clock, midi);
                clock.AdvanceTo(began + 319_999); Check(midi.ReceivedFrames == 0);
                clock.AdvanceTo(began + 320_000); Check(midi.IgnoredInputFrames == 1 && midi.ReceivedBytes == 0 && midi.ReceiveCount == 0);
                clock.AdvanceTo(began + 640_000);
                Check(receptions.Count == 2 && receptions[0].Frame == new MidiFrame(began, began + 320_000, 0x90) &&
                    !receptions[0].Accepted && !receptions[0].Overrun && receptions[1].Accepted == reenter);
                Check(midi.ReceivedFrames == 2 && midi.ReceivedBytes == (reenter ? 1 : 0) && midi.IgnoredInputFrames == (reenter ? 1 : 2));
                if (reenter) Check(midi.Read(0x330, 1) == 0x45);
            });
        test("M4 PIIX MPU reset empties input source without erasing cumulative receive diagnostics", () =>
        {
            var clock = new DeviceScheduler(); var midi = new Mpu401Uart(clock); MidiMode(clock, midi);
            midi.QueueReceive(new([0x90, 0x45])); clock.AdvanceTo(clock.Now + 640_000);
            Check(midi.ReceiveCount == 2 && midi.InterruptAsserted); long edges = midi.InterruptEdges; ulong now = clock.Now;
            var levels = new List<bool>(); midi.InterruptChanged += levels.Add; midi.ApplyPiixHardReset();
            Check(clock.Now == now && midi.ReceiveCount == 0 && !midi.InterruptAsserted && levels.SequenceEqual(new[] { false }) &&
                midi.InterruptEdges == edges && midi.ReceivedFrames == 2 && midi.ReceivedBytes == 2 && midi.Acknowledgements == 1);
        });
        test("M4 PIIX MPU reset from output observer cancels next active frame while preserving published byte", () =>
        {
            var clock = new DeviceScheduler(); var midi = new Mpu401Uart(clock); MidiMode(clock, midi);
            int frames = 0; midi.Transmitted += _ => { frames++; midi.ApplyPiixHardReset(); };
            midi.Write(0x330, 0x91, 1); midi.Write(0x330, 0x92, 1); clock.AdvanceTo(2_000_000);
            Check(frames == 1 && midi.TransmittedBytes == 1 && midi.AbortedOutputBytes == 1 && midi.OutputReady && !midi.Transmitting && clock.NextEventTime is null);
        });
        foreach (int pins in Enumerable.Range(0, 32))
            test("M4 PIIX LPT reset keeps all independently supplied external pins " + pins.ToString("X2"), () =>
            {
                var clock = new DeviceScheduler(); var levels = new List<bool>(); var port = new ParallelPrinterPort(clock, levels.Add);
                port.SetInput(new((pins & 16) != 0, (pins & 8) != 0, (pins & 4) != 0, (pins & 2) != 0, (pins & 1) != 0));
                port.Write(0x378, 0x96, 1); port.Write(0x37a, 0x1f, 1); clock.AdvanceTo(1234);
                long writes = port.WriteCount, reads = port.ReadCount, edges = port.InterruptEdges; int pinChanges = 0;
                var outputs = new List<ParallelOutputs>(); port.OutputsChanged += outputs.Add; port.InputsChanged += _ => pinChanges++;
                port.ApplyPiixHardReset();
                Check(port.Data == 0 && port.Control == 0 && port.ControlPins == 11 && port.InputPins == pins << 3 &&
                    port.Status == (((pins << 3) ^ 0x80) | 0x17) && port.DataChangedAt == 1234 && !port.InterruptAsserted);
                Check(port.WriteCount == writes && port.ReadCount == reads && port.InterruptEdges == edges && pinChanges == 0 &&
                    outputs.SequenceEqual(new[] { new ParallelOutputs(1234, 0, 0) }) && clock.NextEventTime is null);
            });
        test("M4 PIIX LPT repeated zero reset fabricates no output edge or data timestamp", () =>
        {
            var clock = new DeviceScheduler(); var port = new ParallelPrinterPort(clock, _ => { }); int changes = 0;
            port.OutputsChanged += _ => changes++; clock.AdvanceTo(1234); port.ApplyPiixHardReset(); port.ApplyPiixHardReset();
            Check(changes == 0 && port.DataChangedAt == 0 && port.ReadCount == 0 && port.WriteCount == 0);
            port.Write(0x37a, 12, 1); clock.AdvanceTo(5678); port.ApplyPiixHardReset();
            Check(changes == 2 && port.DataChangedAt == 0 && port.Control == 0);
        });
        foreach (string phase in new[] { "setup", "strobe", "hold", "ack-pending", "ack-low", "ack-high" })
            test("M4 PIIX LPT reset cancels old virtual handshake at " + phase, () =>
            {
                var (clock, port, printer) = PrinterRig(); var bytes = new List<ParallelByte>(); printer.Accepted += bytes.Add;
                Pulse(clock, port, 0x51); clock.AdvanceTo(clock.Now + 20_000); Check(printer.AcceptedBytes == 1 && !printer.Busy);
                var published = bytes[0]; port.Write(0x378, 0x96, 1); clock.AdvanceTo(clock.Now + 500);
                if (phase != "setup")
                {
                    port.Write(0x37a, 13, 1);
                    if (phase != "strobe")
                    {
                        clock.AdvanceTo(clock.Now + 500); port.Write(0x37a, 12, 1);
                        clock.AdvanceTo(clock.Now + (phase == "hold" ? 499UL : phase == "ack-pending" ? 500UL : phase == "ack-low" ? 10500UL : 15499UL));
                    }
                }
                long accepted = printer.AcceptedBytes; long rejected = printer.RejectedStrobes; ulong now = clock.Now;
                port.ApplyPiixHardReset(); int transitions = 0; port.InputsChanged += _ => transitions++;
                Check(ReferenceEquals(port.Printer, printer) && printer.Busy && printer.AckHigh && printer.AcceptedBytes == accepted && bytes[0] == published);
                clock.AdvanceTo(now + 100_000);
                Check(transitions == 0 && printer.Busy && printer.AckHigh && printer.AcceptedBytes == accepted && printer.RejectedStrobes >= rejected &&
                    bytes.Count == accepted && bytes[0] == published, "A stale printer callback escaped the reset.");
                port.Write(0x37a, 12, 1); ulong recovery = clock.Now;
                clock.AdvanceTo(recovery + 49999); Check(printer.Busy);
                clock.AdvanceTo(recovery + 50000); Check(!printer.Busy && printer.AckHigh);
                Pulse(clock, port, 0x53); clock.AdvanceTo(clock.Now + 500); Check(printer.AcceptedBytes == accepted + 1 && bytes[^1].Data == 0x53);
            });
        test("M4 PIIX LPT reset cancels an actual old initialize recovery callback", () =>
        {
            var clock = new DeviceScheduler(); var port = new ParallelPrinterPort(clock, _ => { }); var printer = port.ConnectPrinter();
            port.Write(0x37a, 12, 1); clock.AdvanceTo(1000); Check(Token(printer, "_initializeEvent") != 0);
            port.ApplyPiixHardReset(); clock.AdvanceTo(100_000);
            Check(printer.Busy && printer.AckHigh && printer.Initializations == 2 && printer.AcceptedBytes == 0);
        });
        test("M4 PIIX LPT reset from accepted-byte observer cancels already scheduled ACK", () =>
        {
            var (clock, port, printer) = PrinterRig(); int bytes = 0;
            printer.Accepted += _ => { bytes++; port.ApplyPiixHardReset(); };
            Pulse(clock, port, 0x51); clock.AdvanceTo(clock.Now + 100_000);
            Check(bytes == 1 && printer.AcceptedBytes == 1 && printer.Initializations == 1 && printer.Busy && printer.AckHigh && clock.NextEventTime is null);
        });
        foreach (string phase in new[] { "uart", "midi", "parallel", "mixed" })
            test("M4 PIIX external reset full private-PC state continues twice from " + phase, () =>
            {
                using var fixture = new SnapshotTests.Fixture(new()
                {
                    Id = "dose-430fx-piix-reset-dev-v1", Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
                    SerialPort = Uart16550.ProfileId, ParallelPort = ParallelPrinterPort.ProfileId,
                    SoundCard = SoundBlaster16.ProfileId, MidiInterface = Mpu401Uart.ProfileId
                });
                var machine = fixture.Machine;
                bool serial = phase is "uart" or "mixed", midi = phase is "midi" or "mixed", parallel = phase is "parallel" or "mixed";
                if (midi) { machine.Midi!.Write(0x331, 0x3f, 1); Advance(machine, 10_000); Check(machine.Midi.Read(0x330, 1) == 0xfe); }
                if (parallel)
                {
                    machine.Parallel!.Write(0x37a, 12, 1); machine.Parallel.ConnectPrinter();
                    Pulse(machine.Scheduler, machine.Parallel, 0x51); Advance(machine, 500); Check(machine.Parallel.Printer!.AcceptedBytes == 1);
                }
                if (serial)
                {
                    Serial(machine.Serial!); W(machine.Serial!, 2, 1); machine.Serial!.SetModemInputs(true, false, true, true);
                    machine.Serial.QueueReceive([0x42, 0x43]); W(machine.Serial, 0, 0x51);
                }
                if (midi) { machine.Midi!.QueueReceive(new([0x90, 0x45])); machine.Midi.Write(0x330, 0x91, 1); }
                Advance(machine, parallel ? 100UL : 100_000UL);
                if (serial) machine.Serial!.ApplyPiixHardReset();
                if (midi) machine.Midi!.ApplyPiixHardReset();
                if (parallel) machine.Parallel!.ApplyPiixHardReset();
                machine.Audio.AdvanceTo(machine.Scheduler.Now);
                using var resumed = fixture.Restore(); Same(machine, resumed);
                SnapshotStore.Capture(resumed, fixture.PathFor("second.dossav"));
                using var again = SnapshotStore.Load(fixture.PathFor("second.dossav"), fixture.PathFor("second.dosebranch")); Same(machine, resumed, again);
                foreach (var item in new[] { machine, resumed, again })
                {
                    if (serial) { Serial(item.Serial!); W(item.Serial!, 2, 1); }
                    if (midi) item.Midi!.Write(0x331, 0x3f, 1);
                    if (parallel) item.Parallel!.Write(0x37a, 12, 1);
                    Advance(item, 10_000);
                    if (midi) Check(item.Midi!.Read(0x330, 1) == 0xfe);
                    Advance(item, 3_000_000);
                    if (serial) Check(item.Serial!.ExternalCharactersIgnored == 1 && item.Serial.ReceivedCharacters == 1 &&
                        item.Serial.ReceivedCount == 1 && item.Serial.TransmittedCharacters == 0 && R(item.Serial, 0) == 0x43);
                    if (midi) Check(item.Midi!.ReceivedFrames == 2 && item.Midi.IgnoredInputFrames == 1 &&
                        item.Midi.ReceivedBytes == 1 && item.Midi.TransmittedBytes == 0 && item.Midi.Read(0x330, 1) == 0x45);
                    if (parallel)
                    {
                        Check(item.Parallel!.Printer!.AcceptedBytes == 1 && !item.Parallel.Printer.Busy);
                        Pulse(item.Scheduler, item.Parallel, 0x53); Advance(item, 500);
                        Check(item.Parallel.Printer.AcceptedBytes == 2);
                    }
                }
                Same(machine, resumed, again);
            });
    }
}