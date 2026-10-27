// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Round-trip, continuation, sealed-media and transactional rejection checks on exclusively own fixtures.</summary>
internal static class SnapshotTests
{
    internal sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Environment.CurrentDirectory, "artifacts/tests/full-state-" + Guid.NewGuid().ToString("N"));
        public readonly PcMachine Machine;
        /// <summary>Creates an isolated PC, raw extended FPU values and a deterministic halted RAM program.</summary>
        public Fixture(MachineProfile? profile = null, bool start = true)
        {
            Directory.CreateDirectory(Root); Machine = PcMachine.CreateDemo(profile);
            Machine.Cpu.SetSegment(Segment.Cs, 0); Machine.Cpu.SetSegment(Segment.Ss, 0);
            Machine.Cpu.SetRegister(Register.Sp, 0x9000); Machine.Cpu.Eip = 0x1000;
            new byte[] { 0xfa, 0xdb, 0xe3, 0xd9, 0xe8, 0xd9, 0xe9, 0xd9, 0xeb, 0xf4 }.CopyTo(Machine.Memory.Ram, 0x1000);
            if (start) Machine.RunSteps(30);
        }
        /// <summary>Closes only this fixture and removes its known private test directory.</summary>
        public void Dispose() { Machine.Dispose(); Directory.Delete(Path.GetFullPath(Root), true); }
        /// <summary>Returns a new destination below this fixture, never a user's image location.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Seals a complete fixture and returns a resumed PC with independent media leases.</summary>
        public PcMachine Restore()
        { SnapshotStore.Capture(Machine, PathFor("saved.dossav")); return SnapshotStore.Load(PathFor("saved.dossav"), PathFor("branch.dosebranch")); }
    }
    /// <summary>Requires an independently observable state or byte equality.</summary>
    private static void Check(bool condition, string message = "Snapshot assertion failed.") { if (!condition) throw new Exception(message); }
    /// <summary>Encodes a graph including private raw state and event order for exact comparisons.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Rejects a malformed generation through the production loader.</summary>
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException) { return; }
        throw new Exception("Snapshot was unexpectedly accepted.");
    }
    /// <summary>Queues one actual owner-thread request and checks its completion.</summary>
    private static IpcResponse Request(MachineSession session, string command, object payload)
    { var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return pending.GetAwaiter().GetResult(); }
    /// <summary>Rehashes a deliberately changed state so negative tests exercise semantic validation beyond checksums.</summary>
    private static void EditState(string directory, Action<JsonObject> edit)
    {
        string path = Path.Combine(directory, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(state);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, data);
        string manifestPath = Path.Combine(directory, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = data.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(data));
        File.WriteAllText(manifestPath, manifest.ToJsonString(StateValues.Json));
    }
    /// <summary>Registers every full-PC-state case with the existing common regression runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (string sound in new[] { "none", "adlib-opl2-development", "sb2-opl2-development", SoundBlaster16.ProfileId })
        foreach (int ram in new[] { 4, 8, 16 })
        foreach (bool et in new[] { false, true })
            test($"Snapshot exact raw FPU RAM device graph {sound}/{ram}/ET{et}", () =>
            {
                using var f = new Fixture(new() { SoundCard = sound, RamMiB = ram, VideoCard = et ? "et4000ax-isa-1m-development" : DOSEIdentity.VgaModel });
                f.Machine.Memory.Ram[^1] = 0xa7; f.Machine.Running = true;
                string before = State(f.Machine); using var restored = f.Restore();
                Check(before == State(restored), "Raw hardware state differs immediately after restore.");
                Check(f.Machine.Memory.Ram.SequenceEqual(restored.Memory.Ram), "RAM differs.");
                f.Machine.RunSteps(20, 20000000); restored.RunSteps(20, 20000000);
                Check(State(f.Machine) == State(restored), "Hardware future differs.");
            });
        test("Snapshot active COM MIDI printer mouse gameport deadlines resume twice", () =>
        {
            using var f = new Fixture(new() { SoundCard = SoundBlaster16.ProfileId, Mouse = "ps2-two-button", Gameport = AnalogGameport.ProfileId,
                SerialPort = Uart16550.ProfileId, ParallelPort = ParallelPrinterPort.ProfileId, MidiInterface = Mpu401Uart.ProfileId });
            var m = f.Machine; m.Parallel!.ConnectPrinter(); m.ConnectSerialEcho(); m.SetVirtualAdc("ramp", true);
            m.Serial!.Write(0x3fb, 0x80, 1); m.Serial.Write(0x3f8, 1, 1); m.Serial.Write(0x3f9, 0, 1); m.Serial.Write(0x3fb, 3, 1); m.Serial.Write(0x3f8, 0x69, 1); m.Serial.QueueReceive([0x42, 0xf1]);
            m.Midi!.Write(0x331, 0x3f, 1); m.Scheduler.AdvanceTo(1000000); m.Midi.Read(0x330, 1);
            m.Midi.Write(0x330, 0x90, 1); m.Midi.QueueReceive(new MidiInput([0x80, 60, 100]));
            m.Parallel.Write(0x37a, 0x0c, 1); m.Parallel.Write(0x378, 0x55, 1); m.Scheduler.AdvanceTo(1001000); m.Parallel.Write(0x37a, 0x0d, 1);
            m.Gameport!.SetInput(new() { AxOhms = 100000 }); m.Gameport.Write(0x201, 0, 1);
            m.Audio.AdvanceTo(m.Scheduler.Now);
            using var restored = f.Restore(); Check(State(m) == State(restored));
            SnapshotStore.Capture(restored, f.PathFor("second.dossav"));
            using var again = SnapshotStore.Load(f.PathFor("second.dossav"), f.PathFor("second.dosebranch"));
            m.Scheduler.AdvanceTo(30000000); restored.Scheduler.AdvanceTo(30000000); again.Scheduler.AdvanceTo(30000000);
            m.Audio.AdvanceTo(m.Scheduler.Now); restored.Audio.AdvanceTo(restored.Scheduler.Now); again.Audio.AdvanceTo(again.Scheduler.Now);
            Check(State(m) == State(restored) && State(m) == State(again), "Pending peripheral events diverged.");
        });
        foreach (string change in new[] { "RAM", "runtime", "unknown-component", "missing-field", "array", "index", "clock", "event-owner", "event-kind", "null-queue", "duplicate", "extra-field", "cpu-model-changed", "cpu-model-unknown", "tsc-clock", "tsc-cr4", "tsc-epoch" })
            test("Snapshot reject " + change + " without replacing active PC", () =>
            {
                using var f = new Fixture(new() { SerialPort = Uart16550.ProfileId });
                f.Machine.Serial!.Write(0x3fb, 0x80, 1); f.Machine.Serial.Write(0x3f8, 1, 1); f.Machine.Serial.Write(0x3f9, 0, 1);
                f.Machine.Serial.Write(0x3fb, 3, 1); f.Machine.Serial.Write(0x3f8, 0x51, 1);
                using var session = new MachineSession(f.Machine); string saved = f.PathFor("bad.dossav"); SnapshotStore.Capture(f.Machine, saved);
                if (change == "RAM") File.WriteAllBytes(Path.Combine(saved, "ram.bin"), [1]);
                else if (change == "runtime")
                {
                    string p = Path.Combine(saved, "snapshot.json"); var j = JsonNode.Parse(File.ReadAllBytes(p))!.AsObject(); j["runtime"]!["DOSEmulator.Core"] = "bad"; File.WriteAllText(p, j.ToJsonString());
                }
                else if (change == "duplicate")
                {
                    string p = Path.Combine(saved, "snapshot.json"); File.WriteAllText(p, File.ReadAllText(p).Replace("{", "{\"version\":1,", StringComparison.Ordinal));
                }
                else EditState(saved, j =>
                {
                    var blocks = j["components"]!.AsObject(); var cpu = blocks["cpu"]!["fields"]!.AsObject();
                    switch (change)
                    {
                        case "unknown-component": blocks["alien"] = blocks["cpu"]!.DeepClone(); break;
                        case "missing-field": cpu.Remove("Cr0"); break;
                        case "extra-field": cpu["invented"] = 0; break;
                        case "cpu-model-changed": cpu["Model"] = 1; break;
                        case "cpu-model-unknown": cpu["Model"] = 999; break;
                        case "tsc-clock": cpu["TimestampFrequency"] = 90_000_000; break;
                        case "tsc-cr4": cpu["Cr4"] = 4; break;
                        case "tsc-epoch": cpu["_tscEpochTicks"] = 1; break;
                        case "array": cpu["_registers"] = new JsonArray(1); break;
                        case "index": cpu["_cpl"] = 4; break;
                        case "clock": j["scheduler"]!["now"] = ulong.MaxValue; break;
                        case "event-owner": j["scheduler"]!["events"]![0]!["owner"] = "alien"; break;
                        case "event-kind": j["scheduler"]!["events"]![0]!["operation"] = "alien"; break;
                        case "null-queue": blocks["serial"]!["fields"]!["_wire"] = null; break;
                    }
                });
                string before = State(f.Machine); long revision = session.Revision;
                var response = Request(session, "LoadSnapshot", new { path = saved, destination = f.PathFor("failed.dosebranch") });
                Check(!response.Success && ReferenceEquals(session.Machine, f.Machine) && session.Revision == revision && State(f.Machine) == before);
                Check(!Directory.Exists(f.PathFor("failed.dosebranch")), "Failed import left a branch behind.");
                f.Machine.RunSteps(10, 50000000); Check(f.Machine.Fault is null);
            });
        test("Snapshot rejects opaque scheduler callbacks ADC and reentrant capture", () =>
        {
            using var f = new Fixture(new() { SoundCard = "sb2-dsp-development" });
            f.Machine.SoundBlaster!.AdcInput = _ => 123; Reject(() => SnapshotStore.Capture(f.Machine, f.PathFor("adc.dossav")));
            f.Machine.SoundBlaster.AdcInput = null;
            f.Machine.Scheduler.ScheduleAfter(10000, () => { }); Reject(() => SnapshotStore.Capture(f.Machine, f.PathFor("opaque.dossav")));
            bool rejected = false;
            f.Machine.Scheduler.ScheduleAfter(1, () => { Reject(() => SnapshotStore.Capture(f.Machine, f.PathFor("reentrant.dossav"))); rejected = true; });
            f.Machine.Scheduler.AdvanceTo(f.Machine.Scheduler.Now + 1); Check(rejected);
        });
        foreach (int rawCase in Enumerable.Range(0, 8))
            test("Snapshot preserves every raw Float80 payload and empty tag " + rawCase, () =>
            {
                using var f = new Fixture(); var cpu = f.Machine.Cpu.SaveState();
                var raw = Enumerable.Range(0, 8).Select(i => new Float80Value(0xfedcba9876543210UL + (uint)(i * 17 + rawCase), (ushort)(0x7fff ^ (rawCase << 12) ^ i))).ToArray();
                cpu.Fields["_fpuRegisters"] = JsonSerializer.SerializeToElement(raw, StateValues.Json);
                cpu.Fields["_fpuEmpty"] = JsonSerializer.SerializeToElement((byte)(1 << rawCase), StateValues.Json);
                cpu.Fields["FpuControlWord"] = JsonSerializer.SerializeToElement((ushort)(0x037f | rawCase << 10), StateValues.Json);
                f.Machine.Cpu.LoadState(cpu); using var resumed = f.Restore();
                var actual = resumed.Cpu.SaveState().Fields["_fpuRegisters"].Deserialize<Float80Value[]>(StateValues.Json)!;
                Check(raw.SequenceEqual(actual) && State(f.Machine) == State(resumed));
            });
        foreach (string input in new[] { "SendScanCode", "SendMouseInput", "SetGameportInput", "SendSerialInput", "SetParallelInput", "SendMidiInput" })
            test("Snapshot replacement rejects queued old input generation " + input, () =>
            {
                using var f = new Fixture(); using var session = new MachineSession(f.Machine);
                string path = f.PathFor("generation.dossav"); SnapshotStore.Capture(f.Machine, path); string before = State(f.Machine);
                var load = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path, destination = f.PathFor("generation.dosebranch") }));
                var stale = session.Enqueue(IpcProtocol.CreateRequest(input, new { })); session.PumpCommands();
                Check(load.Result.Success && !stale.Result.Success && stale.Result.ErrorCode == "StaleInputGeneration");
                Check(before == State(session.Machine) && session.Machine.IsRestoredSnapshot);
            });
        test("Snapshot rejects external memory IO audio and mouse wiring before publishing", () =>
        {
            using var audio = new Fixture(start: false); audio.Machine.Audio.AddSource((a,b) => 0, 0.5);
            Reject(() => SnapshotStore.Capture(audio.Machine, audio.PathFor("external.dossav")));
            Check(!Directory.Exists(audio.PathFor("external.dossav")));
            using var memory = new Fixture(); memory.Machine.Memory.Map(0x20000000, 1, new RomDevice([1]), "external ROM");
            Reject(() => SnapshotStore.Capture(memory.Machine, memory.PathFor("external.dossav")));
            using var ports = new Fixture(); ports.Machine.Ports.Map(0x500, 1, new Uart16550(new DeviceScheduler(), _ => { }));
            Reject(() => SnapshotStore.Capture(ports.Machine, ports.PathFor("external.dossav")));
            using var mouse = new Fixture(new() { Mouse = "ps2-two-button" }); mouse.Machine.Mouse!.ConnectOutput(_ => true);
            Reject(() => SnapshotStore.Capture(mouse.Machine, mouse.PathFor("external.dossav")));
        });
        test("Snapshot preserves independent hardware and renamed PC identities", () =>
        {
            using var f = new Fixture(); using var session = new MachineSession(f.Machine); var profile = f.Machine.Profile;
            Check(Request(session, "SaveMachineDefinition", new { path = f.PathFor("renamed.dospc.json"), name = "My named PC" }).Success);
            using var restored = f.Restore();
            Check(restored.Profile == profile && restored.CaptureDefinition().Name == "My named PC" && State(restored) == State(f.Machine));
        });
        test("Snapshot IPC preserves RTC and paused state instead of carrying old battery", () =>
        {
            using var f = new Fixture(); using var session = new MachineSession(f.Machine);
            f.Machine.Ports.Write(0x70, 0x38, 1); f.Machine.Ports.Write(0x71, 0xa6, 1);
            f.Machine.Running = false; string before = State(f.Machine); string path = f.PathFor("ipc.dossav");
            Check(Request(session, "CreateSnapshot", new { path }).Success);
            f.Machine.Ports.Write(0x71, 0x17, 1); f.Machine.Running = true;
            Check(Request(session, "LoadSnapshot", new { path, destination = f.PathFor("ipc.dosebranch") }).Success);
            Check(!session.Machine.Running && State(session.Machine) == before && session.Revision == 2);
        });
    }
}
