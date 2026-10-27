// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Register, timed-wire, FIFO, IRQ, firmware and machine-definition contracts for the virtual COM1 board.</summary>
internal static class SerialTests
{
    /// <summary>Require a specific independently computed result and identify the failed invariant.</summary>
    private static void Check(bool ok, string reason = "UART invariant") { if (!ok) throw new Exception(reason); }
    /// <summary>Read one real decoded byte register, including its normal acknowledgement effects.</summary>
    private static byte R(Uart16550 u, int offset) => (byte)u.Read((ushort)(0x3f8 + offset), 1);
    /// <summary>Write one real decoded byte register rather than modifying internal device state.</summary>
    private static void W(Uart16550 u, int offset, byte value) => u.Write((ushort)(0x3f8 + offset), value, 1);
    /// <summary>Program a divisor and line format through DLAB, leaving normal THR/RBR aliases selected.</summary>
    private static void Configure(Uart16550 u, ushort divisor = 12, byte lcr = 3)
    { W(u, 3, 128); W(u, 0, (byte)divisor); W(u, 1, (byte)(divisor >> 8)); W(u, 3, lcr); }
    /// <summary>Independently count start/data/parity/stop half-bits; do not call the product's format helper.</summary>
    private static int Bits(byte lcr) => 2 + 2 * (5 + (lcr & 3)) + ((lcr & 8) != 0 ? 2 : 0) +
        ((lcr & 4) == 0 ? 2 : (lcr & 3) == 0 ? 3 : 4);
    /// <summary>Independently convert a known number of crystal cycles to the lower integer-nanosecond boundary.</summary>
    private static ulong Ns(ulong cycles) => cycles * 1_000_000_000 / 1_843_200;
    /// <summary>Advance to one particular pending deadline, retaining the scheduler's actual ordering.</summary>
    private static void Next(DeviceScheduler s) => s.AdvanceTo(s.NextEventTime ?? throw new Exception("Missing UART deadline"));
    /// <summary>Run a normal owner-thread IPC request and retain the actual protocol response.</summary>
    private static IpcResponse Command(MachineSession s, string command, object? payload = null)
    { var p = s.Enqueue(IpcProtocol.CreateRequest(command, payload)); s.PumpCommands(); return p.GetAwaiter().GetResult(); }
    /// <summary>Install a real caller and execute the POST-installed INT14 vector to its actual return address.</summary>
    private static ushort Bios(PcMachine m, ushort ax, ushort port = 0, int steps = 100000)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0x1357); m.Cpu.SetSegment(Segment.Es, 0x2468);
        m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 0x403; m.Memory.WriteByte(0x1000, 0xf4);
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.SetRegister(Register.Dx, port); m.Cpu.SetRegister(Register.Bx, 0x1234);
        m.Cpu.SetRegister(Register.Cx, 0xabcd); m.Cpu.EnterRealModeInterrupt(0x14); m.RunSteps(steps);
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001001", m.Fault ?? m.Cpu.Position);
        Check(m.Cpu.GetRegister(Register.Bx) == 0x1234 && m.Cpu.GetRegister(Register.Cx) == 0xabcd && m.Cpu.GetRegister(Register.Dx) == port);
        Check(m.Cpu.GetSegment(Segment.Ds) == 0x1357 && m.Cpu.GetSegment(Segment.Es) == 0x2468 && (m.Cpu.Eflags & 0x401) == 0x401);
        return (ushort)m.Cpu.GetRegister(Register.Ax);
    }
    /// <summary>Register bounded architecture cases independently of the complete suite.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // Distinct named cases retain the failed format, mask or boundary in the report.
        void Add(string name, Action action) => test("Serial: " + name, action);
        Add("reset register map and DLAB aliases", () =>
        {
            var u = new Uart16550(new(), _ => { });
            Check(R(u, 1) == 0 && R(u, 2) == 1 && R(u, 3) == 0 && R(u, 4) == 0 && R(u, 5) == 0x60 && R(u, 6) == 0);
            W(u, 1, 255); Check(R(u, 1) == 15); W(u, 3, 128); W(u, 0, 0xa5); W(u, 1, 0x5a);
            Check(R(u, 0) == 0xa5 && R(u, 1) == 0x5a && u.Divisor == 0x5aa5 && u.TransmittedCharacters == 0);
            W(u, 3, 3); Check(R(u, 1) == 15); W(u, 7, 0x96); Check(R(u, 7) == 0x96);
            W(u, 4, 255); Check(R(u, 4) == 31); W(u, 5, 0); W(u, 6, 0); Check((R(u, 5) & 0x60) == 0x60);
        });
        foreach (byte lcr in Enumerable.Range(0, 32).Select(x => (byte)x))
        foreach (ushort divisor in new ushort[] { 1, 12, 1047 })
            Add($"complete frame {lcr:X2} divisor {divisor}", () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u, divisor, lcr);
                var frames = new List<UartFrame>(); u.Transmitted += frames.Add; W(u, 0, 0xf5);
                ulong start = Ns(16UL * divisor), duration = Ns((ulong)Bits(lcr) * 8 * divisor);
                s.AdvanceTo(start - 1); Check(!u.Transmitting && frames.Count == 0 && (R(u, 5) & 0x60) == 0);
                s.AdvanceTo(start); Check(u.Transmitting && (R(u, 5) & 0x60) == 0x20);
                s.AdvanceTo(start + duration - 1); Check(frames.Count == 0);
                s.AdvanceTo(start + duration); Check(frames.Count == 1 && frames[0] == new UartFrame(start, start + duration,
                    (byte)(0xf5 & ((1 << (5 + (lcr & 3))) - 1)), 0, lcr, divisor));
                Check(R(u, 5) == 0x60 && s.NextEventTime is null);
            });
        foreach (byte lcr in new byte[] { 0, 1, 2, 3, 4, 7, 8, 0x1f })
            Add("receive complete frame and sampled format " + lcr, () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u, 12, lcr);
                u.QueueReceive(new byte[] { 0xf5 }); ulong end = Ns((ulong)Bits(lcr) * 96);
                Configure(u, 24, (byte)(lcr ^ 3)); s.AdvanceTo(end - 1); Check(R(u, 5) == 0x60);
                s.AdvanceTo(end); Check(R(u, 0) == (0xf5 & ((1 << (5 + (lcr & 3))) - 1)) && u.ReceivedCharacters == 1);
            });
        foreach (byte fcr in new byte[] { 1, 0x41, 0x81, 0xc1 })
            Add("FIFO threshold, timeout and read acknowledgement " + fcr, () =>
            {
                var s = new DeviceScheduler(); bool irq = false; var u = new Uart16550(s, high => irq = high); Configure(u);
                W(u, 4, 8); W(u, 2, fcr); W(u, 1, 1);
                int threshold = new[] { 1, 4, 8, 14 }[fcr >> 6];
                for (byte i = 0; i < threshold; i++)
                { u.QueueReceive(new byte[] { i }); while (u.ReceivedCharacters <= i) Next(s); Check(irq == (i == threshold - 1)); }
                Check(R(u, 2) == 0xc4 && irq); Check(R(u, 0) == 0 && !irq);
                if (threshold > 1)
                {
                    ulong timeout = s.Now + Ns(20UL * 96 * 4); s.AdvanceTo(timeout - 1); Check(!irq);
                    s.AdvanceTo(timeout); Check(irq && R(u, 2) == 0xcc && irq); _ = R(u, 0); Check(!irq);
                }
                W(u, 2, (byte)(fcr | 2)); Check(u.ReceivedCount == 0 && !irq && R(u, 2) == 0xc1);
            });
        foreach (byte errors in new byte[] { 4, 8, 16, 12, 24, 28 })
            Add("per-head errors, FIFO summary and priority " + errors, () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 2, 0x41); W(u, 4, 8); W(u, 1, 15);
                Check(R(u, 2) == 0xc2); u.QueueReceive(new byte[] { 0xa5 }, errors); Next(s);
                Check(R(u, 2) == 0xc6); byte expected = (byte)(errors | ((errors & 16) != 0 ? 8 : 0));
                Check((R(u, 5) & 0x9d) == (0x81 | expected)); Check(R(u, 2) == 0xc1);
                Check((R(u, 5) & 0x9e) == 0); Check(R(u, 0) == ((errors & 16) != 0 ? 0 : 0xa5));
                u.QueueReceive(new byte[] { 1 }); Next(s); u.QueueReceive(new byte[] { 2 }, 4); Next(s);
                Check((R(u, 5) & 0x84) == 0x80 && (R(u, 2) & 15) != 6); Check(R(u, 0) == 1);
                Check(R(u, 2) == 0xc6 && (R(u, 5) & 0x84) == 0x84 && R(u, 0) == 2);
            });
        foreach (bool fifo in new[] { false, true })
            Add("overrun retains documented holding/FIFO data " + fifo, () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 2, fifo ? (byte)1 : (byte)0);
                u.QueueReceive(Enumerable.Range(0, fifo ? 18 : 3).Select(x => (byte)x).ToArray());
                s.AdvanceTo(30_000_000); Check(u.Overruns == 2 && (R(u, 5) & 3) == 3 && (R(u, 5) & 2) == 0);
                for (int i = 0; i < (fifo ? 16 : 1); i++) Check(R(u, 0) == (fifo ? i : 2));
                Check(R(u, 5) == 0x60);
            });
        foreach (int pins in Enumerable.Range(0, 16))
            Add("external modem deltas and no automatic CTS flow " + pins, () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 1, 8); W(u, 4, 8);
                u.SetModemInputs((pins & 1) != 0, (pins & 2) != 0, (pins & 4) != 0, (pins & 8) != 0);
                Check(R(u, 6) == ((pins << 4) | (pins & 11))); Check(R(u, 6) == pins << 4);
                u.SetModemInputs(false, false, false, false); Check(R(u, 6) == pins);
                W(u, 0, 65); s.AdvanceTo(2_000_000); Check(u.TransmittedCharacters == 1 && R(u, 2) == 1);
            });
        foreach (int outputs in Enumerable.Range(0, 16))
            Add("local modem loopback " + outputs, () =>
            {
                var u = new Uart16550(new(), _ => { }); W(u, 4, (byte)(outputs | 16));
                int pins = ((outputs & 1) << 5) | ((outputs & 2) << 3) | ((outputs & 4) << 4) | ((outputs & 8) << 4);
                Check((R(u, 6) & 0xf0) == pins); u.SetModemInputs(true, true, true, true); Check((R(u, 6) & 0xf0) == pins);
                W(u, 4, 0); Check((R(u, 6) & 0xf0) == 0xf0);
            });
        Add("OUT2 board gate and THRE interrupt acknowledgement", () =>
        {
            bool irq = false; var u = new Uart16550(new(), x => irq = x); W(u, 1, 2);
            Check(R(u, 2) == 2 && !irq); W(u, 1, 0); W(u, 1, 2); W(u, 4, 8); Check(irq);
            Check(R(u, 2) == 2 && !irq && R(u, 5) == 0x60 && R(u, 2) == 1);
            W(u, 1, 0); W(u, 1, 2); Check(irq); W(u, 4, 0); Check(!irq && R(u, 2) == 2);
        });
        Add("FIFO short-burst THRE delay and distinct TEMT", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 2, 1); W(u, 0, 65); Next(s);
            ulong start = s.Now, empty = start + Ns(18UL * 96); Check((R(u, 5) & 0x60) == 0);
            s.AdvanceTo(empty - 1); Check((R(u, 5) & 0x20) == 0); s.AdvanceTo(empty); Check((R(u, 5) & 0x60) == 0x20);
            s.AdvanceTo(start + Ns(20UL * 96)); Check(R(u, 5) == 0x60);
        });
        Add("FIFO queue depth, long burst THRE and exact carried clock", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u, 7); W(u, 2, 1);
            var frames = new List<UartFrame>(); u.Transmitted += frames.Add;
            for (byte i = 0; i < 17; i++) W(u, 0, i); Check(u.RejectedTransmitWrites == 1 && u.TransmitCount == 16);
            s.AdvanceTo(20_000_000); Check(frames.Count == 16);
            for (int i = 0; i < 16; i++) Check(frames[i].Data == i && frames[i].End == Ns(112) + Ns((ulong)(i + 1) * 1120));
            Check(R(u, 5) == 0x60);
        });
        Add("FIFO clears preserve both active shift frames", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 2, 1);
            u.QueueReceive(new byte[] { 0x96 }); W(u, 0, 65); W(u, 0, 66); Next(s); W(u, 2, 7);
            Check(u.Transmitting && u.TransmitCount == 0 && u.ReceivedCount == 0);
            s.AdvanceTo(3_000_000); Check(u.TransmittedCharacters == 1 && R(u, 0) == 0x96 && u.FifoControl == 1);
        });
        Add("divisor zero pauses new frames and resumes retained queues", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); u.QueueReceive(new byte[] { 90 }); W(u, 0, 65);
            Check(s.NextEventTime is null); W(u, 3, 0x83); W(u, 0, 12); W(u, 1, 0); W(u, 3, 3); s.AdvanceTo(3_000_000);
            Check(u.TransmittedCharacters == 1 && R(u, 0) == 90);
        });
        Add("local data loopback disconnects external framed input", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 4, 16); int frames = 0; u.Transmitted += _ => frames++;
            u.QueueReceive(new byte[] { 99 }); W(u, 0, 0x5a); s.AdvanceTo(3_000_000);
            Check(frames == 0 && u.ExternalCharactersIgnored == 1 && u.ReceivedCharacters == 1 && R(u, 0) == 0x5a);
        });
        Add("simultaneous interrupt sources preserve priority and separate acknowledgements", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 4, 8);
            u.SetModemInputs(true, true, false, true); u.QueueReceive(new byte[] { 65 }, 4); Next(s); W(u, 1, 15);
            Check(R(u, 2) == 6 && R(u, 2) == 6); Check((R(u, 5) & 5) == 5 && R(u, 2) == 4);
            Check(R(u, 0) == 65 && R(u, 2) == 2 && R(u, 2) == 0 && R(u, 2) == 0);
            Check(R(u, 6) == 0xbb && R(u, 2) == 1 && !u.InterruptAsserted);
        });
        Add("in-flight transmit format and divisor survive reprogramming", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u, 12, 3);
            var frames = new List<UartFrame>(); u.Transmitted += frames.Add; W(u, 0, 0xe5); Next(s); Configure(u, 24, 0);
            s.AdvanceTo(3_000_000); Check(frames.Count == 1 && frames[0].Data == 0xe5 && frames[0].LineControl == 3 && frames[0].Divisor == 12);
        });
        Add("continuous local break reports exactly one BI/FE zero until released", () =>
        {
            var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); W(u, 4, 16); W(u, 3, 0x43);
            s.AdvanceTo(2_000_000); Check((R(u, 5) & 0x19) == 0x19 && R(u, 0) == 0);
            s.AdvanceTo(20_000_000); Check(u.ReceivedCharacters == 1); W(u, 3, 3); W(u, 3, 0x43);
            s.AdvanceTo(22_000_000); Check(u.ReceivedCharacters == 2);
        });
        foreach (int width in new[] { 2, 4 })
            Add("wide I/O is undriven without byte side effects " + width, () =>
            {
                var s = new DeviceScheduler(); var u = new Uart16550(s, _ => { }); Configure(u); u.QueueReceive(new byte[] { 0x96 }); Next(s);
                for (ushort port = 0x3f7; port <= 0x400; port++) { u.Write(port, 0xffffffff, width); Check(u.Read(port, width) == (width == 2 ? 65535u : uint.MaxValue)); }
                Check(R(u, 0) == 0x96 && u.TransmittedCharacters == 0 && u.LineControl == 3 && u.Divisor == 12);
            });
        Add("invalid external input and queue overflow are atomic", () =>
        {
            using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId });
            m.SendSerialInput(new(new int[4096]));
            foreach (var input in new[] { new SerialInput(new[] { 256 }, Cts: true), new SerialInput(new[] { 0 }, 2, Cts: true), new SerialInput(new[] { 0 }, Cts: true) })
            { bool rejected = false; try { m.SendSerialInput(input); } catch (ArgumentException) { rejected = true; } Check(rejected); }
            Check(m.Serial!.PhysicalModem == 0 && m.Serial.ExternalCharactersQueued == 4096);
        });
        Add("hot reset retains powered UART and cold definition reload clears it", () =>
        {
            using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }); Configure(m.Serial!); W(m.Serial!, 7, 0x96);
            m.SendSerialInput(new(new[] { 65 }, Cts: true)); m.HardwareReset(); Check(R(m.Serial!, 7) == 0x96 && m.Serial!.PhysicalModem == 16);
            var definition = JsonSerializer.Deserialize<MachineDefinition>(JsonSerializer.Serialize(m.CaptureDefinition(), IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
            using var cold = MachineFactory.FromDefinition(definition, m); Check(cold.Profile.SerialPort == Uart16550.ProfileId && cold.Serial!.Divisor == 0 && cold.Serial.PhysicalModem == 0);
            Check(JsonSerializer.Deserialize<MachineProfile>("{}", IpcProtocol.JsonOptions)!.SerialPort == "none");
        });
        Add("owner-thread IPC and truthful capabilities", () =>
        {
            using var s = new MachineSession(PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }));
            Check(Command(s, "SendSerialInput", new SerialInput(new[] { 65 }, Dsr: true)).Success && s.Machine.Serial!.ExternalCharactersQueued == 1);
            Check(!Command(s, "SendSerialInput", new SerialInput(new[] { -1 })).Success);
            var capabilities = Command(s, "GetCapabilities"); string json = JsonSerializer.Serialize(capabilities.Payload);
            Check(json.Contains("SendSerialInput") && json.Contains("hostSerialPort\":false"));
        });
        foreach (bool enabled in new[] { false, true })
            Add("POST BDA and equipment count " + enabled, () =>
            {
                using var m = PcMachine.CreateDemo(new() { SerialPort = enabled ? Uart16550.ProfileId : "none", Gameport = AnalogGameport.ProfileId }); m.RunSteps(100000);
                Check(m.Fault is null && m.Cpu.Halted && m.Memory.ReadWord(0x400) == (enabled ? 0x3f8 : 0) && (m.Memory.ReadWord(0x410) & 0x1e00) == (enabled ? 0x1200 : 0x1000));
                Check(m.Memory.ReadWord(0x402) == 0 && m.Memory.ReadWord(0x404) == 0 && m.Memory.ReadWord(0x406) == 0);
                Check(m.Memory.ReadByte(0x47c) == (enabled ? 1 : 0));
                Check(m.Board.DebugText.Contains("COM1: National PC16550D") == enabled);
            });
        ushort[] divisors = [1047, 768, 384, 192, 96, 48, 24, 12];
        foreach (int rate in Enumerable.Range(0, 8))
        foreach (byte format in new byte[] { 2, 3, 0x0a, 0x1f })
            Add($"INT14 initialize rate {rate} format {format:X2}", () =>
            {
                using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }); m.RunSteps(100000);
                ushort result = Bios(m, (ushort)((rate << 5) | format));
                Check(m.Serial!.Divisor == divisors[rate] && m.Serial.LineControl == format && m.Serial.ModemControl == 3 && m.Serial.InterruptEnable == 0 && result == 0x6000);
            });
        Add("INT14 polled full-frame transmit, receive, status and preserved caller", () =>
        {
            using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }); m.RunSteps(100000); m.Serial!.SetModemInputs(true, true, false, true);
            m.Serial.Transmitted += f => m.Serial.QueueReceive(new byte[] { f.Data }, f.Errors);
            _ = Bios(m, 0x00e3); _ = Bios(m, 0x015a); Check(Bios(m, 0x0200) == 0x615a); Check(Bios(m, 0x0300) == 0x60b0);
        });
        Add("INT14 timeout preserves CMOS index/NMI and unsupported calls have no I/O", () =>
        {
            using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }); m.RunSteps(100000); _ = Bios(m, 0x00e3);
            m.Memory.WriteByte(0x47c, 0); m.Ports.Write(0x70, 0x8b, 1); Check(Bios(m, 0x015a) == 0xe05a && m.Ports.Read(0x70, 1) == 0x8b);
            int accesses = 0; m.Ports.Accessed += (port, _, _, _) => { if (port is >= 0x3f8 and <= 0x3ff) accesses++; };
            Check(Bios(m, 0x0496) == 0x8096 && Bios(m, 0x0196, 1) == 0x8096 && accesses == 0);
        });
        Add("INT14 receive crossing the next RTC second cannot expire the first partial second", () =>
        {
            using var m = PcMachine.CreateDemo(new() { SerialPort = Uart16550.ProfileId }); m.RunSteps(100000);
            m.Serial!.SetModemInputs(true, true, false, true); _ = Bios(m, 0x0043);
            m.Scheduler.AdvanceTo((m.Scheduler.Now / 1_000_000_000 + 1) * 1_000_000_000 - 10_000_000);
            m.Serial.QueueReceive(new byte[] { 0x96 }); Check(Bios(m, 0x0200, steps: 1000000) == 0x6196);
        });
    }
}
