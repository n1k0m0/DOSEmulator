// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Captures each concrete peripheral operation while pending, then executes the rebound callback graph.</summary>
internal static class SnapshotEventTests
{
    /// <summary>Requires a real pending event or exact continued component state.</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    /// <summary>Advances the source clock without host time and keeps partial mixer integration coherent.</summary>
    private static void Advance(PcMachine m, ulong delta)
    { m.Scheduler.AdvanceTo(m.Scheduler.Now + delta); m.Audio.AdvanceTo(m.Scheduler.Now); }
    /// <summary>Programs a concrete UART divisor, frame and FIFO through its ordinary ports.</summary>
    private static void Serial(PcMachine m)
    {
        m.Serial!.Write(0x3fb, 0x80, 1); m.Serial.Write(0x3f8, 12, 1); m.Serial.Write(0x3f9, 0, 1);
        m.Serial.Write(0x3fb, 3, 1); m.Serial.Write(0x3fa, 0xc1, 1);
    }
    /// <summary>Completes only the documented DSP reset prelude, leaving later command/sample work for capture.</summary>
    private static void DspReady(PcMachine m)
    { m.Ports.Write(0x226, 1, 1); Advance(m, 4000); m.Ports.Write(0x226, 0, 1); Advance(m, 100000); m.Ports.Read(0x22a, 1); }
    /// <summary>Writes command parameters at actual device-ready intervals.</summary>
    private static void DspBytes(PcMachine m, params byte[] values)
    { foreach (byte value in values) { m.Ports.Write(0x22c, value, 1); Advance(m, 1000); } }
    /// <summary>Registers concrete deadline cuts; ATA/FDC and full DOS continuation have separate additional cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        var cases = new List<(string Owner, string Operation, MachineProfile Profile, Action<PcMachine> Setup)>();
        var serial = new MachineProfile { SerialPort = Uart16550.ProfileId };
        cases.Add(("serial", "event2", serial, m => { Serial(m); m.Serial!.Write(0x3f8, 0x51, 1); }));
        foreach (string op in new[] { "event3", "event4" })
            cases.Add(("serial", op, serial, m => { Serial(m); m.Serial!.Write(0x3f8, 0x51, 1); Advance(m, 104167); }));
        cases.Add(("serial", "event1", serial, m => { Serial(m); m.Serial!.QueueReceive([0x53, 0xa7], 4); }));
        cases.Add(("serial", "event0", serial, m => { Serial(m); m.Serial!.QueueReceive([0x53]); Advance(m, 1041667); }));
        cases.Add(("serial", "event5", serial, m => { Serial(m); m.Serial!.Write(0x3fc, 0x10, 1); m.Serial.Write(0x3fb, 0x43, 1); }));
        foreach (string sound in new[] { "sb2-dsp-development", SoundBlaster16.ProfileId })
        {
            string owner = sound == SoundBlaster16.ProfileId ? "sb16" : "sb2"; var profile = new MachineProfile { SoundCard = sound };
            cases.Add((owner, "event1", profile, m => { m.Ports.Write(0x226, 1, 1); Advance(m, 4000); m.Ports.Write(0x226, 0, 1); }));
            cases.Add((owner, "event0", profile, m => { DspReady(m); m.Ports.Write(0x22c, 0xe1, 1); }));
            cases.Add((owner, "event2", profile, m => { DspReady(m); DspBytes(m, sound == SoundBlaster16.ProfileId ? (byte)0x14 : (byte)0x80, 0xff, 0); }));
        }
        foreach (string sound in new[] { "adlib-opl2-development", SoundBlaster16.ProfileId })
            cases.Add((sound == SoundBlaster16.ProfileId ? "opl3" : "opl2", "event0", new() { SoundCard = sound }, m =>
            { m.Ports.Write(0x388, 2, 1); Advance(m, 50000); m.Ports.Write(0x389, 200, 1); Advance(m, 50000); m.Ports.Write(0x388, 4, 1); Advance(m, 50000); m.Ports.Write(0x389, 1, 1); }));
        cases.Add(("pit", "event0", new(), m => { m.Ports.Write(0x43, 0x34, 1); m.Ports.Write(0x40, 0x34, 1); m.Ports.Write(0x40, 0x12, 1); }));
        cases.Add(("pit/SpeakerCounter", "event0", new(), m => { m.Ports.Write(0x43, 0xb6, 1); m.Ports.Write(0x42, 0x80, 1); m.Ports.Write(0x42, 1, 1); m.Ports.Write(0x61, 3, 1); }));
        cases.Add(("mouse", "event0", new() { Mouse = "ps2-two-button" }, m => { m.Mouse!.SetClockEnabled(true); m.Mouse.Write(0xf4); m.Mouse.Inject(37, -12, 1); }));
        var midi = new MachineProfile { SoundCard = SoundBlaster16.ProfileId, MidiInterface = Mpu401Uart.ProfileId };
        cases.Add(("midi", "event0", midi, m => m.Midi!.Write(0x331, 0x3f, 1)));
        cases.Add(("midi", "event1", midi, m => { m.Midi!.Write(0x331, 0x3f, 1); Advance(m, 10000); m.Midi.Read(0x330, 1); m.Midi.Write(0x330, 0x90, 1); }));
        cases.Add(("midi", "event2", midi, m => { m.Midi!.Write(0x331, 0x3f, 1); Advance(m, 10000); m.Midi.Read(0x330, 1); m.Midi.QueueReceive(new MidiInput([0x90, 0x45, 0x37])); }));
        for (int phase = 0; phase < 4; phase++)
        {
            int step = phase;
            cases.Add(("parallel/Printer", "event" + phase, new() { ParallelPort = ParallelPrinterPort.ProfileId }, m =>
            {
                m.Parallel!.ConnectPrinter(); m.Parallel.Write(0x37a, 0, 1); m.Parallel.Write(0x37a, 0x0c, 1);
                if (step == 0) return;
                Advance(m, 50000); m.Parallel.Write(0x378, 0x69, 1); Advance(m, 1000); m.Parallel.Write(0x37a, 0x0d, 1);
                Advance(m, 1000); m.Parallel.Write(0x37a, 0x0c, 1);
                if (step > 1) Advance(m, 500);
                if (step > 2) Advance(m, 10000);
            }));
        }
        foreach (var item in cases)
            test("Snapshot pending callback " + item.Owner + "/" + item.Operation, () =>
            {
                using var f = new SnapshotTests.Fixture(item.Profile); var m = f.Machine; item.Setup(m); m.Audio.AdvanceTo(m.Scheduler.Now);
                Check(m.CaptureHardwareState().Scheduler.Events.Any(e => e.Owner == item.Owner && e.Operation == item.Operation), "The intended pending callback was not exercised.");
                using var resumed = f.Restore(); Advance(m, 80000000); Advance(resumed, 80000000);
                Check(JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json) == JsonSerializer.Serialize(resumed.CaptureHardwareState(), StateValues.Json), "Rebound peripheral continuation differs.");
                Check(m.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Peripheral DMA/RAM differs.");
            });
    }
}
