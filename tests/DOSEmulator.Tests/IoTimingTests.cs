// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

internal static class IoTimingTests
{
    private sealed class Latch(DeviceScheduler clock) : IIoPortDevice
    {
        public uint Value;
        public ulong ObservedAt;
        public Action? OnAccess;
        /// <summary>Reads the fixture's actual result for comparison with independently specified expected data.</summary>
        public uint Read(ushort port, int width) { ObservedAt = clock.Now; OnAccess?.Invoke(); return Value; }
        /// <summary>Writes explicit private-fixture input data without modifying original user media.</summary>
        public void Write(ushort port, uint value, int width) { ObservedAt = clock.Now; Value = value; OnAccess?.Invoke(); }
    }
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool condition, string message = "I/O timing assertion failed.") { if (!condition) throw new Exception(message); }
    /// <summary>Registers the named io timing regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("timed I/O completes intervening events before device read and trace", () =>
        {
            var clock = new DeviceScheduler(); var io = new IoPortBus(); var timing = new Isa8BitIoTiming(clock); var device = new Latch(clock);
            var order = new List<string>(); device.OnAccess = () => order.Add("device"); io.Map(0x388, 2, device, timing);
            clock.ScheduleAfter(300, () => { device.Value = 0xc6; order.Add("event"); });
            io.Accessed += (port, value, width, write) => { Check(clock.Now == 750 && value == 0xc6 && !write); order.Add("trace"); };
            Check(io.Read(0x388, 1) == 0xc6 && device.ObservedAt == 750);
            Check(string.Join(',', order) == "event,device,trace");
        });
        test("timed writes reach the latch only at bus completion", () =>
        {
            var clock = new DeviceScheduler(); var io = new IoPortBus(); var device = new Latch(clock);
            io.Map(0x388, 2, device, new Isa8BitIoTiming(clock));
            clock.ScheduleAfter(749, () => Check(device.Value == 0)); io.Write(0x389, 0x135, 1);
            Check(device.Value == 0x35 && device.ObservedAt == 750 && clock.Now == 750);
        });
        test("normal ISA byte-cycle time accumulates independently of CPU frequency remainder", () =>
        {
            var clock = new DeviceScheduler(); var timing = new Isa8BitIoTiming(clock); var io = new IoPortBus(); io.Map(0x388, 2, new Latch(clock), timing);
            for (int i = 0; i < 33000; i++) { io.Read(0x388, 1); clock.AdvanceCycles(4, 33000000); }
            Check(clock.Now == 28_750_000 && timing.Accesses == 33000 && timing.ByteCycles == 33000 && timing.ElapsedNanoseconds == 24_750_000);
        });
        test("unmapped and untimed ports retain their prior behavior", () =>
        {
            var clock = new DeviceScheduler(); var io = new IoPortBus(); var timing = new Isa8BitIoTiming(clock);
            io.Map(0x388, 2, new Latch(clock), timing); io.Map(0x80, 1, new Latch(clock));
            io.Write(0x80, 1, 1); Check(io.Read(0x228, 1) == 0xff && clock.Now == 0 && timing.Accesses == 0);
        });
        test("invalid widths and mapping conflicts cannot install or consume bus time", () =>
        {
            var clock = new DeviceScheduler(); var io = new IoPortBus(); var device = new Latch(clock); var timing = new Isa8BitIoTiming(clock);
            io.Map(0x388, 2, device, timing);
            try { io.Read(0x388, 3); throw new Exception("Invalid width accepted."); } catch (ArgumentOutOfRangeException) { }
            try { io.Map(0x387, 3, device, timing); throw new Exception("Mapping conflict accepted."); } catch (InvalidOperationException) { }
            io.Read(0x387, 1); Check(clock.Now == 0 && timing.Accesses == 0);
        });
        test("wider timed accesses retain explicit device width semantics", () =>
        {
            using var m = PcMachine.CreateDemo(new MachineProfile { SoundCard = "adlib-opl2-development" });
            Check(m.Ports.Read(0x388, 2) == 0xff && m.Scheduler.Now == 1500);
            m.Ports.Write(0x388, 0xff02, 2); Check(m.Opl!.RegisterWrites == 0 && m.FmIoTiming!.ByteCycles == 4);
        });
        test("actual x86 six and 35 status reads satisfy OPL write recovery", () =>
        {
            using var m = PcMachine.CreateDemo(new MachineProfile { SoundCard = "sb2-opl2-development" });
            var code = new List<byte>();
            foreach (ushort port in new ushort[] { 0x388, 0x228 })
            {
                code.AddRange([0xba, (byte)port, (byte)(port >> 8), 0xb0, 2, 0xee]);
                code.AddRange(Enumerable.Repeat((byte)0xec, 6));
                code.AddRange([0x42, 0xb0, 0xff, 0xee, 0x4a]); code.AddRange(Enumerable.Repeat((byte)0xec, 35));
            }
            code.AddRange([0xfa, 0xf4]); code.ToArray().CopyTo(m.Memory.Ram, 0x1000);
            m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.Eip = 0x1000; m.RunSteps(1000);
            Check(m.Fault is null && m.Cpu.Halted && m.Opl!.RegisterWrites == 2 && m.Opl.RejectedWrites == 0);
            Check(m.Opl!.InspectRegister(2) == 0xff && m.FmIoTiming!.Accesses == 86 && m.FmIoTiming.ElapsedNanoseconds == 64500);
        });
        test("ISA time alone does not conceal missing OPL recovery reads", () =>
        {
            using var m = PcMachine.CreateDemo(new MachineProfile { SoundCard = "adlib-opl2-development" });
            m.Ports.Write(0x388, 2, 1); m.Ports.Write(0x389, 255, 1);
            Check(m.Opl!.RejectedWrites == 1 && m.Opl.RegisterWrites == 0);
        });
    }
}
