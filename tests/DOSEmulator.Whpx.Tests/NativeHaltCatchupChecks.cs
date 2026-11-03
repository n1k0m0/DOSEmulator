// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Whpx;

/// <summary>Actual native IRQ0/EOI/IRET controls distinguish a stopped host HLT wait from deliberately masked guest execution.</summary>
internal static class NativeHaltCatchupChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>Rejects a missed native service, invented interrupt or altered timer deadline.</summary>
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    /// <summary>Runs an own finite native timer program with one explicit host delay and no guest state writes by its observer.</summary>
    private static object Case(int reload, uint hz, string mode)
    {
        var memory = new MemoryBus(4 << 20); var ports = new IoPortBus(); var pic = new Pic8259(); var scheduler = new DeviceScheduler();
        var pit = new Pit8254(scheduler, () => { pic.SetLine(0, true); pic.SetLine(0, false); });
        ports.Map(0x20, 2, pic); ports.Map(0x40, 4, pit);
        using var native = new WhpxMappedMachine(memory, ports, pic, scheduler,
            hz == 233_000_000 ? WhpxCpuContract.IntelP55cMmxSubset : WhpxCpuContract.IntelP54cB5Subset, hz);
        int acknowledgements = 0, eois = 0, stalls = 0; ulong atDelay = 0;
        pic.InterruptAcknowledged += (irq, _, spurious) => { if (irq == 0 && !spurious) acknowledgements++; };
        ports.Accessed += (port, value, _, write) =>
        {
            if (write && port == 0x20 && value == 0x20) eois++;
            if (write && port == 0xe9 && value == 0x53)
            {
                stalls++; atDelay = scheduler.Now;
                Thread.Sleep(15); // A real stalled guest OUT with IF=0 must retain ordinary PIC coalescing.
            }
            if (write && port == 0xe9 && value == 0x54) { stalls++; atDelay = scheduler.Now; }
        };
        var code = new List<byte> { 0xfa, 0xbc, 0, 0x80, 0xb0, mode == "pic-masked" ? (byte)0xff : (byte)0xfe,
            0xe6, 0x21, 0xb0, 0x34, 0xe6, 0x43, 0xb8, (byte)reload, (byte)(reload >> 8), 0xe6, 0x40, 0x88, 0xe0, 0xe6, 0x40 };
        if (mode != "if-masked") code.Add(0xfb);
        if (mode is "guest-cli" or "guest-native-cli")
        {
            // HLT until three original handlers have returned, then mask in the guest and issue the deliberately slow OUT.
            code.AddRange([0xf4, 0x83, 0x3e, 0, 0x30, 3, 0x72, 0xf8, 0xfa, 0xb0,
                mode == "guest-cli" ? (byte)0x53 : (byte)0x54, 0xe6, 0xe9]);
            if (mode == "guest-native-cli") code.AddRange([0x66, 0xb9, 0, 0xe1, 0xf5, 5, 0x66, 0x49, 0x75, 0xfc]);
            code.Add(0xfb);
        }
        code.AddRange([0xf4, 0xeb, 0xfd]); code.ToArray().CopyTo(memory.Ram, 0x1000);
        byte[] handler = [0xff, 6, 0, 0x30, 0x50, 0xb0, 0x20, 0xe6, 0x20, 0x58, 0xcf]; handler.CopyTo(memory.Ram, 0x1100);
        BinaryPrimitives.WriteUInt16LittleEndian(memory.Ram.AsSpan(0x20, 2), 0x1100);
        native.SetEntry(0, 0x1000);
        if (mode == "native-return")
        {
            // Delay only after a real native HLT return, before the owner has marked its HLT flag.
            // The forwarder returns the original, later host timestamp without changing guest state.
            var clock = (WhpxRealtimeClock)typeof(WhpxMappedMachine).GetField("_clock", Hidden)!.GetValue(native)!;
            var field = typeof(WhpxRealtimeClock).GetField("_counter", Hidden)!;
            var original = (Func<long>)field.GetValue(clock)!;
            var exit = (byte[])typeof(WhpxMappedMachine).GetField("_exit", Hidden)!.GetValue(native)!;
            field.SetValue(clock, (Func<long>)(() =>
            {
                if (stalls == 0 && BinaryPrimitives.ReadUInt32LittleEndian(exit) == 8)
                { stalls++; atDelay = scheduler.Now; Thread.Sleep(15); }
                return original();
            }));
        }
        else if (mode is not ("guest-cli" or "guest-native-cli"))
        {
            Action? delay = null;
            delay = () =>
            {
                if (!(bool)typeof(WhpxMappedMachine).GetField("_halted", Hidden)!.GetValue(native)!)
                { scheduler.ScheduleAfter(50_000, delay!); return; }
                stalls++; atDelay = scheduler.Now; Thread.Sleep(15);
            };
            scheduler.ScheduleAfter(2_500_000, delay);
        }
        long began = Stopwatch.GetTimestamp(); native.RunUntilVirtualTime(60_000_000, TimeSpan.FromSeconds(3), 500000);
        double hostSeconds = (double)(Stopwatch.GetTimestamp() - began) / Stopwatch.Frequency;
        int handlers = BitConverter.ToUInt16(memory.Ram, 0x3000);
        Check(stalls == 1 && atDelay != 0 && scheduler.Now == 60_000_000 && pit.InterruptPulses >= 40,
            "Own HLT delay/control did not reach the actual unchanged timer future");
        Check(handlers == eois && Math.Abs(handlers - acknowledgements) <= 1, "Actual IRQ0/EOI/IRET handlers differ from accepted PIC vectors");
        if (mode is "unmasked" or "native-return")
            Check(pit.InterruptPulses - acknowledgements <= 2 && acknowledgements >= 40,
                $"Host HLT wait lost timer service: pulses={pit.InterruptPulses}, ack={acknowledgements}, handler={handlers}");
        else if (mode is "guest-cli" or "guest-native-cli")
            Check(acknowledgements >= 3 && pit.InterruptPulses - acknowledgements >= 8,
                "Deliberately masked guest OUT was incorrectly compensated with extra timer interrupts");
        else
            Check(acknowledgements == 0 && handlers == 0 && (pic.Read(0x20, 1) & 1) != 0,
                "Masked PIC/IF HLT invented delivery or lost its single pending physical request");
        return new { reload, hz, mode, pit.InterruptPulses, acknowledgements, handlers, eois, atDelay, hostSeconds };
    }

    /// <summary>Checks both real Windows PIT periods and native CPU tuples, plus independent IF, PIC and executed-CLI controls.</summary>
    public static void Run()
    {
        var cases = new List<object>();
        foreach (int reload in new[] { 1024, 1193 })
        {
            foreach (uint hz in new uint[] { 100_000_000, 233_000_000 }) cases.Add(Case(reload, hz, "unmasked"));
            foreach (string mode in new[] { "native-return", "if-masked", "pic-masked", "guest-cli", "guest-native-cli" }) cases.Add(Case(reload, 100_000_000, mode));
        }
        Console.WriteLine("PASS: native HLT catch-up " + cases.Count + " real timer/IRQ/EOI/IRET futures; original1024/1193 periods, real stopped-host/native-return delays, masked IF/PIC retain one request, genuine assisted/native CLI still coalesces; " +
            System.Text.Json.JsonSerializer.Serialize(cases));
    }
}
