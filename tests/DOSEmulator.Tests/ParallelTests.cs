// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Original decoded latches/pins, nominal virtual-peer timing, IRQ7 and executed firmware regressions.</summary>
internal static class ParallelTests
{
    /// <summary>Require an independently specified register, timing or ownership invariant.</summary>
    private static void Check(bool ok, string why = "Parallel invariant") { if (!ok) throw new Exception(why); }
    /// <summary>Write one real byte control latch.</summary>
    private static void Control(ParallelPrinterPort p, byte value) => p.Write(0x37a, value, 1);
    /// <summary>Supply all five physical lines without confusing an active-low signal name with its voltage level.</summary>
    private static ParallelInput Pins(int v) => new((v & 16) != 0, (v & 8) != 0, (v & 4) != 0, (v & 2) != 0, (v & 1) != 0);
    /// <summary>Construct an idle selected port and an explicitly attached virtual byte peer.</summary>
    private static (DeviceScheduler S, ParallelPrinterPort P, VirtualParallelPrinter V) Rig()
    {
        var s = new DeviceScheduler(); var p = new ParallelPrinterPort(s, _ => { }); Control(p, 12);
        return (s, p, p.ConnectPrinter());
    }
    /// <summary>Generate an actual valid pulse with independently chosen setup and strobe lengths.</summary>
    private static void Pulse(DeviceScheduler s, ParallelPrinterPort p, byte data, ulong setup = 500, ulong width = 500, byte control = 12)
    {
        p.Write(0x378, data, 1); s.AdvanceTo(s.Now + setup); Control(p, (byte)(control | 1));
        s.AdvanceTo(s.Now + width); Control(p, control);
    }
    /// <summary>Enter the actual POST-installed INT17 and check the full caller return rather than invoking a host service.</summary>
    private static ushort Bios(PcMachine m, ushort ax, ushort dx = 0, int steps = 100000)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0x1357); m.Cpu.SetSegment(Segment.Es, 0x2468);
        m.Cpu.Eip = 0x1000; m.Memory.WriteByte(0x1000, 0xf4); m.Cpu.Eflags = 0x403;
        m.Cpu.SetRegister(Register.Ax, ax); m.Cpu.SetRegister(Register.Dx, dx); m.Cpu.SetRegister(Register.Bx, 0xa596); m.Cpu.SetRegister(Register.Cx, 0x1234);
        m.Cpu.EnterRealModeInterrupt(0x17); m.RunSteps(steps);
        Check(m.Fault is null && m.Cpu.Halted && m.Cpu.Position == "0000:00001001", m.Fault ?? m.Cpu.Position);
        Check(m.Cpu.GetRegister(Register.Bx) == 0xa596 && m.Cpu.GetRegister(Register.Cx) == 0x1234 && m.Cpu.GetRegister(Register.Dx) == dx);
        Check(m.Cpu.GetSegment(Segment.Ds) == 0x1357 && m.Cpu.GetSegment(Segment.Es) == 0x2468 && (m.Cpu.Eflags & 0x401) == 0x401);
        return (ushort)m.Cpu.GetRegister(Register.Ax);
    }
    /// <summary>Register individually named address, truth-table, timing, firmware and integration cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // Keep the exact address, signal pattern or boundary in each failure name.
        void Add(string name, Action action) => test("Parallel: " + name, action);
        Add("reset latches, pullups, reserved and undriven bits", () =>
        {
            var s = new DeviceScheduler(); var p = new ParallelPrinterPort(s, _ => { });
            Check(p.Read(0x378, 1) == 0 && p.Read(0x379, 1) == 0x7f && p.Read(0x37a, 1) == 0xe0 && p.ControlPins == 11);
            p.Write(0x379, 0, 1); p.Write(0x37b, 0, 1); Check(p.Read(0x37b, 1) == 255 && p.WriteCount == 0 && s.NextEventTime is null);
            Check(p.Read(0x377, 1) == 255 && p.Read(0x380, 1) == 255 && p.Printer is null);
        });
        foreach (int index in Enumerable.Range(0, 128))
            Add("A2 and upper-address alias " + index, () =>
            {
                using var m = PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId });
                ushort port = (ushort)(0x378 + index / 2 * 0x400 + index % 2 * 4);
                m.Ports.Write(port, 0x96, 1); m.Ports.Write((ushort)(port + 2), 0x0c, 1);
                Check(m.Ports.Read(0x378, 1) == 0x96 && m.Ports.Read((ushort)(port + 2), 1) == 0xec);
                Check(m.Ports.Read((ushort)(port + 3), 1) == 255 && m.Parallel!.WriteCount == 2 && m.Parallel.ReadCount == 2);
            });
        foreach (int value in Enumerable.Range(0, 32))
            Add("control readback and inverted physical outputs " + value, () =>
            {
                var p = new ParallelPrinterPort(new(), _ => { }); Control(p, (byte)(value | 0xe0));
                Check(p.Control == value && p.Read(0x37a, 1) == (uint)(value | 0xe0));
                Check((p.ControlPins & 1) == ((value & 1) == 0 ? 1 : 0) && (p.ControlPins & 2) == ((value & 2) == 0 ? 2 : 0));
                Check((p.ControlPins & 4) == (value & 4) && (p.ControlPins & 8) == ((value & 8) == 0 ? 8 : 0));
                Check(p.InterruptAsserted == ((value & 16) != 0));
                p.Write(0x378, 0xa5, 1); Check(p.Read(0x378, 1) == 0xa5);
            });
        foreach (int value in Enumerable.Range(0, 32))
        foreach (byte control in new byte[] { 4, 12 })
            Add($"live status pins {value:X2} select control {control:X2}", () =>
            {
                var s = new DeviceScheduler(); var p = new ParallelPrinterPort(s, _ => { }); Control(p, control); p.SetInput(Pins(value));
                uint expected = (uint)(((value << 3) ^ 0x80) | 7 | (control == 4 ? 16 : 0));
                for (int i = 0; i < 5; i++) Check(p.Read(0x379, 1) == expected && s.Now == 0);
                Check(p.InputPins == value << 3 && s.NextEventTime is null && p.Data == 0);
            });
        foreach (int width in new[] { 2, 4 })
            Add("wide accesses do not split into byte strobes " + width, () =>
            {
                var (s, p, v) = Rig(); for (ushort a = 0x378; a <= 0x37f; a++)
                { p.Write(a, uint.MaxValue, width); Check(p.Read(a, width) == (width == 2 ? 65535u : uint.MaxValue)); }
                Check(p.Data == 0 && p.Control == 12 && v.AcceptedBytes == 0 && v.RejectedStrobes == 0 && s.NextEventTime is null);
            });
        Add("direct ACK gate, initial enable edge, PIC IRR and EOI are distinct", () =>
        {
            var pic = new Pic8259(); var p = new ParallelPrinterPort(new(), high => pic.SetLine(7, high)); pic.Write(0x21, 0x7f, 1);
            Control(p, 16); Check(pic.HasPending && p.InterruptEdges == 1 && pic.Acknowledge() == 0x0f);
            _ = p.Read(0x379, 1); Check(p.InterruptAsserted && !pic.HasPending); pic.Write(0x20, 0x20, 1); Check(!pic.HasPending);
            p.SetInput(new(AckHigh: false)); Check(!p.InterruptAsserted && !pic.HasPending);
            p.SetInput(new(AckHigh: true)); Check(p.InterruptAsserted && pic.HasPending && p.InterruptEdges == 2);
            Control(p, 0); Check(!p.InterruptAsserted && pic.HasPending && pic.Acknowledge() == 0x0f);
        });
        foreach (ulong setup in new ulong[] { 0, 499, 500, 501, 5000 })
        foreach (ulong width in new ulong[] { 0, 499, 500, 501, 5000 })
            Add($"virtual peer setup {setup} pulse {width} ns", () =>
            {
                var (s, p, v) = Rig(); var bytes = new List<ParallelByte>(); v.Accepted += bytes.Add;
                Pulse(s, p, 0x96, setup, width); ulong released = s.Now; s.AdvanceTo(released + 499); Check(v.AcceptedBytes == 0);
                s.AdvanceTo(released + 500); bool good = setup >= 500 && width >= 500;
                Check(v.AcceptedBytes == (good ? 1 : 0) && v.RejectedStrobes == (good ? 0 : 1));
                if (good) Check(bytes.Single() == new ParallelByte(0x96, 13, setup, setup + width, setup + width + 500));
                s.AdvanceTo(released + 15500); Check(!v.Busy && v.AckHigh);
            });
        foreach (ulong change in new ulong[] { 0, 499, 500, 501 })
            Add("hold boundary data change after release " + change, () =>
            {
                var (s, p, v) = Rig(); Pulse(s, p, 65); ulong released = s.Now; s.AdvanceTo(released + change);
                p.Write(0x378, 66, 1); s.AdvanceTo(released + 20000);
                Check(v.AcceptedBytes == (change >= 500 ? 1 : 0) && v.RejectedStrobes == (change < 500 ? 1 : 0));
            });
        Add("unchanged data write does not invent hold failure", () =>
        {
            var (s, p, v) = Rig(); Pulse(s, p, 65); p.Write(0x378, 65, 1); s.AdvanceTo(s.Now + 20000);
            Check(v.AcceptedBytes == 1 && v.RejectedStrobes == 0);
        });
        Add("ACK delay, width, BUSY and repeated read semantics", () =>
        {
            var (s, p, v) = Rig(); Control(p, 28); Pulse(s, p, 0xa5, control: 28); ulong released = s.Now;
            Check(v.Busy && v.AckHigh && (p.Status & 0xc0) == 0x40 && p.InterruptEdges == 1);
            s.AdvanceTo(released + 10499); Check(v.AckHigh && v.Busy && v.AcceptedBytes == 1);
            s.AdvanceTo(released + 10500); Check(!v.AckHigh && v.Busy && !p.InterruptAsserted);
            for (int i = 0; i < 100; i++) Check((p.Read(0x379, 1) & 0xc0) == 0);
            s.AdvanceTo(released + 15499); Check(!v.AckHigh && v.Busy);
            s.AdvanceTo(released + 15500); Check(v.AckHigh && !v.Busy && p.InterruptAsserted && p.InterruptEdges == 2);
        });
        foreach (ulong resetAt in new ulong[] { 0, 499, 500, 10499, 10500, 15499 })
            Add("reset cancels pending hold/ACK without retracting accepted bytes " + resetAt, () =>
            {
                var (s, p, v) = Rig(); Pulse(s, p, 65); ulong released = s.Now; s.AdvanceTo(released + resetAt); Control(p, 8);
                s.AdvanceTo(released + 100000); Check(v.AcceptedBytes == (resetAt >= 500 ? 1 : 0) && v.Busy && v.AckHigh);
                Control(p, 12); ulong recovery = s.Now; s.AdvanceTo(recovery + 49999); Check(v.Busy);
                s.AdvanceTo(recovery + 50000); Check(!v.Busy && v.AckHigh && v.Initializations == 1);
            });
        Add("external pin ownership, absent card and IPC failures are atomic", () =>
        {
            using var session = new MachineSession(PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId }));
            var pending = session.Enqueue(IpcProtocol.CreateRequest("SetParallelInput", new ParallelInput(BusyHigh: false)));
            session.PumpCommands(); Check(pending.Result.Success && (session.Machine.Parallel!.InputPins & 128) == 0);
            session.Machine.Parallel!.ConnectPrinter(); byte pins = session.Machine.Parallel.InputPins;
            pending = session.Enqueue(IpcProtocol.CreateRequest("SetParallelInput", new ParallelInput(BusyHigh: true)));
            session.PumpCommands(); Check(!pending.Result.Success && session.Machine.Parallel.InputPins == pins);
        });
        Add("accepted-byte observer reset cannot leave a stale ACK deadline", () =>
        {
            var (s, p, v) = Rig(); v.Accepted += _ => Control(p, 8); Pulse(s, p, 65);
            s.AdvanceTo(s.Now + 100000); Check(v.AcceptedBytes == 1 && v.Busy && v.AckHigh && v.Initializations == 1);
        });
        foreach (string sound in new[] { "sb2-dsp-development", "sb2-opl2-development", "sb16-ct1740-dsp405-development" })
            Add("explicit IRQ7 resource conflict before construction " + sound, () =>
            {
                bool rejected = false; try { new MachineProfile { ParallelPort = ParallelPrinterPort.ProfileId, SoundCard = sound, SoundBlasterIrq = 7 }.Validate(); }
                catch (ArgumentException) { rejected = true; } Check(rejected);
                new MachineProfile { ParallelPort = ParallelPrinterPort.ProfileId, SoundCard = sound, SoundBlasterIrq = 5 }.Validate();
            });
        Add("saved definition is a cold port, while CPU reset preserves powered work", () =>
        {
            using var m = PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId });
            Control(m.Parallel!, 12); var v = m.Parallel!.ConnectPrinter(); Pulse(m.Scheduler, m.Parallel, 65); m.HardwareReset();
            m.Scheduler.AdvanceTo(m.Scheduler.Now + 20000); Check(v.AcceptedBytes == 1 && m.Parallel.Data == 65);
            var d = JsonSerializer.Deserialize<MachineDefinition>(JsonSerializer.Serialize(m.CaptureDefinition(), IpcProtocol.JsonOptions), IpcProtocol.JsonOptions)!;
            using var cold = MachineFactory.FromDefinition(d, m); Check(cold.Parallel is { Data: 0, Control: 0, Printer: null });
            Check(JsonSerializer.Deserialize<MachineProfile>("{}", IpcProtocol.JsonOptions)!.ParallelPort == "none");
        });
        foreach (bool installed in new[] { false, true })
            Add("BDA INT11 and POST coexist with COM1 and gameport " + installed, () =>
            {
                using var m = PcMachine.CreateDemo(new() { ParallelPort = installed ? ParallelPrinterPort.ProfileId : "none", SerialPort = Uart16550.ProfileId, Gameport = AnalogGameport.ProfileId });
                m.RunSteps(100000); Check(m.Fault is null && m.Cpu.Halted);
                Check(m.Memory.ReadWord(0x408) == (installed ? 0x378 : 0) && m.Memory.ReadWord(0x40a) == 0 && m.Memory.ReadWord(0x40c) == 0);
                Check((m.Memory.ReadWord(0x410) & 0xde00) == (installed ? 0x5200 : 0x1200));
                Check(m.Board.DebugText.Contains("LPT1: IBM Printer Adapter") == installed);
            });
        Add("INT17 init and print have real strobes, deadlines and preserved caller", () =>
        {
            using var m = PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId }); var v = m.Parallel!.ConnectPrinter();
            var bytes = new List<ParallelByte>(); v.Accepted += bytes.Add; m.RunSteps(100000);
            Check(Bios(m, 0x015a) == 0x905a && Bios(m, 0x0096) == 0x9096 && Bios(m, 0x025a) == 0x905a);
            var b = bytes.Single(); Check(b.Data == 0x96 && b.Released - b.Asserted >= 500 && b.AcceptedAt == b.Released + 500 && !v.Busy);
        });
        foreach (int value in Enumerable.Range(0, 32))
            Add("INT17 raw status maps only documented BIOS bits " + value, () =>
            {
                using var m = PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId }); m.RunSteps(100000); Control(m.Parallel!, 12); m.Parallel!.SetInput(Pins(value));
                Check(Bios(m, 0x025a) == (ushort)((((value << 3) ^ 0xc8) << 8) | 0x5a));
            });
        Add("INT17 unconnected timeout and invalid port/function never print", () =>
        {
            using var m = PcMachine.CreateDemo(new() { ParallelPort = ParallelPrinterPort.ProfileId }); m.RunSteps(100000); m.Memory.WriteByte(0x478, 0);
            ulong before = m.Scheduler.Now; Check(Bios(m, 0x015a) == 0x305a && m.Scheduler.Now - before < 1000000);
            m.Ports.Write(0x70, 0x8b, 1); Check(Bios(m, 0x0096) == 0x3196 && m.Ports.Read(0x70, 1) == 0x8b && m.Parallel!.Data == 0);
            long writes = m.Parallel!.WriteCount; Check(Bios(m, 0x035a) == 0x015a && Bios(m, 0x005a, 1) == 0x015a && m.Parallel.WriteCount == writes);
        });
    }
}
