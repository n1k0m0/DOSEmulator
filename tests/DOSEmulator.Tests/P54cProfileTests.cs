// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>CPU/platform selection, cold configuration and owner adoption without native input or original media.</summary>
internal static class P54cProfileTests
{
    /// <summary>Requires a concrete profile, file, CPU or owner-generation invariant.</summary>
    private static void Check(bool condition, string message = "P54C profile assertion failed")
    { if (!condition) throw new Exception(message); }
    /// <summary>Requires the exact rejection family before observing the retained machine or untouched target.</summary>
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    /// <summary>Creates a fresh private directory for configuration fixtures; no original or personal media are read.</summary>
    private static string Fixture()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "p54c-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    /// <summary>Hashes the exact user configuration bytes without changing or normalizing the source file.</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    /// <summary>Registers narrow tuple, persistence, launch and atomic owner-adoption cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 P54C profile legacy defaults and reversible selection preserve custom values", () =>
        {
            var defaults = new MachineProfile(); defaults.Validate();
            Check(defaults.Id == "virtual486-dev-v1" && defaults.Cpu == "i486dx-development" && defaults.CpuHz == 33_000_000 && !defaults.IsP54c);
            var original = defaults with { Name = "My unchanged hardware name", RamMiB = 16, VideoCard = "et4000ax-isa-1m-development",
                SoundCard = "sb16-ct1740-dsp405-development", SoundBlasterBasePort = 0x280, SoundBlasterIrq = 10,
                SoundBlasterDmaChannel = 0, SoundBlasterDma16Channel = 7, MidiInterface = "ct1740-mpu401-uart-development", MidiBasePort = 0x300,
                Mouse = "ps2-two-button", Gameport = "ibm-game-control-development", SerialPort = "pc16550d-com1-development", ParallelPort = "ibm-printer-adapter-development" };
            var selected = original.WithDevelopmentCpu(true); selected.Validate();
            Check(selected.IsP54c && selected.CpuHz == MachineProfile.P54cCpuHz && selected.Name == original.Name);
            Check(selected.WithDevelopmentCpu(false) == original && !original.IsP54c && selected == selected.WithDevelopmentCpu(true));
        });
        var invalid = new (string Id, string Cpu, int Hz)[]
        {
            ("virtual486-dev-v1", MachineProfile.P54cCpuId, 33_000_000),
            ("virtual486-dev-v1", MachineProfile.P54cCpuId, 90_000_000),
            (MachineProfile.P54cProfileId, "i486dx-development", 33_000_000),
            (MachineProfile.P54cProfileId, "i486dx-development", 90_000_000),
            ("virtual486-dev-v1", "i486dx-development", 90_000_000),
            (MachineProfile.P54cProfileId, MachineProfile.P54cCpuId, 33_000_000),
            (MachineProfile.P54cProfileId, MachineProfile.P54cCpuId, 0),
            (MachineProfile.P54cProfileId, MachineProfile.P54cCpuId, -90_000_000),
            (MachineProfile.P54cProfileId, MachineProfile.P54cCpuId, 100_000_000),
            ("430fx", MachineProfile.P54cCpuId, 90_000_000),
            ("dose-p54c-at-dev-v2", MachineProfile.P54cCpuId, 90_000_000),
            (MachineProfile.P54cProfileId, "pentium", 90_000_000),
            (MachineProfile.P54cProfileId, "p55c", 90_000_000),
            ("virtual486-dev-v1", "pentium", 33_000_000)
        };
        foreach (var tuple in invalid)
            test($"M4 P54C profile rejects unmatched tuple {tuple.Id}/{tuple.Cpu}/{tuple.Hz}", () =>
            {
                var profile = new MachineProfile { Id = tuple.Id, Cpu = tuple.Cpu, CpuHz = tuple.Hz };
                Reject<ArgumentException>(profile.ValidateCpuSelection); Reject<ArgumentException>(profile.Validate);
            });
        foreach (int version in new[] { 0, 2 })
            test("M4 P54C profile rejects unknown schema before inference " + version, () =>
                Reject<ArgumentException>((new MachineProfile().WithDevelopmentCpu(true) with { SchemaVersion = version }).ValidateCpuSelection));
        foreach (int ram in new[] { 4, 8, 16 })
            test("M4 P54C profile preserves supported RAM and device resource validation " + ram, () =>
            {
                var profile = new MachineProfile().WithDevelopmentCpu(true) with { RamMiB = ram, SoundCard = "sb16-ct1740-dsp405-development",
                    MidiInterface = "ct1740-mpu401-uart-development", ParallelPort = "ibm-printer-adapter-development" };
                profile.Validate();
                Reject<ArgumentException>((profile with { RamMiB = 32 }).Validate);
                Reject<ArgumentException>((profile with { SoundBlasterIrq = 7 }).Validate);
                Reject<ArgumentException>((profile with { SoundBlasterDmaChannel = 2 }).Validate);
                Reject<ArgumentException>((profile with { SoundCard = "none" }).Validate);
            });
        test("M4 P54C profile early preflight permits later legacy floppy inference", () =>
        {
            var profile = new MachineProfile().WithDevelopmentCpu(true) with { BootDevice = "floppy-a", FloppyDriveA = "none" };
            profile.ValidateCpuSelection(); Reject<ArgumentException>(profile.Validate);
        });
        foreach (bool inferred in new[] { false, true })
            test("M4 P54C profile rejects bad tuple before opening missing media inferred=" + inferred, () =>
            {
                string directory = Fixture();
                var hardware = new MachineProfile().WithDevelopmentCpu(true) with { Cpu = "i486dx-development", BootDevice = "floppy-a" };
                var definition = new MachineDefinition { Hardware = hardware, Floppies = [new(0, Path.Combine(directory, "missing-floppy.img"), Path.Combine(directory, "new.cow"))] };
                Reject<ArgumentException>(() => MachineFactory.FromDefinition(definition, inferFloppyTypes: inferred));
                Check(!Directory.EnumerateFiles(directory).Any(), "Invalid CPU selection opened or published media");
            });
        foreach (bool p54c in new[] { false, true })
        {
            test("M4 P54C profile saved PC cold-start and VGA alias preserve identity model=" + p54c, () =>
            {
                string directory = Fixture(), path = Path.Combine(directory, "custom.dospc.json");
                var hardware = new MachineProfile().WithDevelopmentCpu(p54c) with { Name = "Separate hardware name", VideoCard = "dose-vga-development" };
                var definition = new MachineDefinition { Id = Guid.NewGuid().ToString("N"), Name = "My own PC name", Hardware = hardware, BuiltInTestDisk = true };
                MachineDefinitionStore.Save(path, definition);
                string json = File.ReadAllText(path).Replace("dose-vga-development", "reference-vga-development", StringComparison.Ordinal);
                File.WriteAllText(path, json); string originalHash = Hash(path);
                var loaded = MachineDefinitionStore.Load(path); Check(Hash(path) == originalHash);
                Check(loaded.Id == definition.Id && loaded.Name == definition.Name && loaded.Hardware.Name == hardware.Name && loaded.Hardware.IsP54c == p54c);
                Check(loaded.Hardware.VideoCard == "dose-vga-development");
                using var machine = MachineFactory.FromDefinition(loaded);
                Check(machine.Cpu.Model == (p54c ? X86CpuModel.IntelP54cB5Subset : X86CpuModel.Intel486Dx));
                Check(machine.Cpu.HasTimeStampCounter == p54c && machine.Cpu.Instructions == 0 && machine.Scheduler.Now == 0);
                Check(machine.CaptureDefinition().Id == definition.Id && machine.CaptureDefinition().Name == definition.Name && machine.Profile.Name == definition.Name);
                machine.RunSteps(200000); Check(machine.Fault is null && machine.Cpu.Halted && machine.Memory.ReadWord(0x500) == 0xbeef);
                using var second = MachineFactory.FromDefinition(loaded); Check(second.Cpu.Instructions == 0 && second.Memory.ReadWord(0x500) == 0);
                Check(Hash(path) == originalHash, "Cold starting rewrote the source PC configuration");
            });
            test("M4 P54C profile shared CLI profile path chooses actual cold model=" + p54c, () =>
            {
                string directory = Fixture(), path = Path.Combine(directory, "hardware.json");
                var profile = new MachineProfile().WithDevelopmentCpu(p54c) with { Name = "CLI custom hardware" };
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(profile, IpcProtocol.JsonOptions)); string hash = Hash(path);
                using var machine = MachineLaunch.Open(option => option == "--profile" ? path : null);
                Check(machine.Profile == profile && machine.Cpu.HasTimeStampCounter == p54c);
                machine.RunSteps(200000); Check(machine.Fault is null && machine.Cpu.Halted && Hash(path) == hash);
            });
            test("M4 P54C profile queued owner commit replaces CPU clock and rejects retired input from=" + p54c, () =>
            {
                var profile = new MachineProfile().WithDevelopmentCpu(p54c) with { Name = "User hardware label" };
                using var session = new MachineSession(PcMachine.CreateDemo(profile)); var original = session.Machine;
                original.RunSteps(200000); Check(original.Fault is null && original.Cpu.Halted);
                original.Memory.WriteByte(0x7000, 0x5a); var identity = original.CaptureDefinition();
                var next = profile.WithDevelopmentCpu(!p54c);
                var stale = session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange", new ConfigurationPlan(0, next, true)));
                session.PumpCommands(); Check(stale.Result.ErrorCode == "StaleConfiguration" && ReferenceEquals(original, session.Machine));
                var change = session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange", new ConfigurationPlan(session.Revision, next, true)));
                var oldInput = session.Enqueue(IpcProtocol.CreateRequest("SendScanCode", new { scanCode = 0x1e }));
                Check(!change.IsCompleted && ReferenceEquals(original, session.Machine)); session.PumpCommands();
                Check(change.Result.Success && !ReferenceEquals(original, session.Machine) && session.Machine.Profile == next);
                Check(session.Machine.Cpu.HasTimeStampCounter == !p54c && session.Machine.Cpu.Instructions == 0 && session.Machine.Scheduler.Now == 0);
                Check(session.Machine.Memory.ReadByte(0x7000) == 0 && session.Machine.Keyboard.OutputByteCount == 0);
                Check(oldInput.Result.ErrorCode == "StaleInputGeneration" && session.Machine.CaptureDefinition().Id == identity.Id && session.Machine.CaptureDefinition().Name == identity.Name);
                var invalid = session.Enqueue(IpcProtocol.CreateRequest("CommitConfigurationChange", new ConfigurationPlan(session.Revision, next with { CpuHz = 1 }, true)));
                var retained = session.Machine; session.PumpCommands();
                Check(invalid.Result.ErrorCode == "InvalidRequest" && ReferenceEquals(retained, session.Machine));
                session.Machine.RunSteps(200000); Check(session.Machine.Fault is null && session.Machine.Cpu.Halted);
            });
        }
        test("M4 P54C profile supplied DOSE AT file launches limited CPU without host attachments", () =>
        {
            string path = Path.GetFullPath(Path.Combine("profiles", "dose-p54c-at-dev-v1.json"));
            string hash = Hash(path);
            var profile = JsonSerializer.Deserialize<MachineProfile>(File.ReadAllBytes(path), IpcProtocol.JsonOptions)
                ?? throw new Exception("Missing supplied DOSE P54C profile");
            profile.Validate();
            Check(profile.Id == MachineProfile.P54cProfileId && profile.Cpu == MachineProfile.P54cCpuId && profile.CpuHz == 90_000_000);
            Check(profile.RamMiB == 8 && profile.SoundCard == "sb16-ct1740-dsp405-development" && profile.VideoCard == "dose-vga-development");
            using var session = new MachineSession(MachineLaunch.Open(option => option == "--profile" ? path : null));
            var machine = session.Machine;
            Check(machine.Profile == profile && machine.Cpu.Model == X86CpuModel.IntelP54cB5Subset && machine.Cpu.TimestampFrequency == 90_000_000);
            Check(machine.Cpu.HasTimeStampCounter && machine.Cpu.TimestampCounter == 0 && machine.Cpu.Cr4 == 0);
            Check(machine.SoundBlaster16 is not null && machine.Opl3 is not null && machine.Midi is null && machine.Serial is null && machine.Parallel is null && machine.Gameport is null);
            var request = session.Enqueue(IpcProtocol.CreateRequest("GetCapabilities")); session.PumpCommands();
            Check(request.Result.Success);
            var capabilities = JsonSerializer.SerializeToElement(request.Result.Payload, IpcProtocol.JsonOptions);
            foreach (string name in new[] { "hostAudioInput", "hostMidi", "hostSerialPort", "hostPrinter", "hostGamepad", "guestNetwork" })
                Check(!capabilities.GetProperty(name).GetBoolean(), "Unexpected host or network capability: " + name);
            machine.RunSteps(200000);
            Check(machine.Fault is null && machine.Cpu.Halted && machine.Memory.ReadWord(0x500) == 0xbeef && Hash(path) == hash);
        });
        test("M4 P54C profile shared CLI rejects conflicting machine/profile before missing file access", () =>
        {
            string directory = Fixture();
            Reject<ArgumentException>(() => MachineLaunch.Open(option => option is "--machine" or "--profile" ? Path.Combine(directory, option + ".json") : null));
            Check(!Directory.EnumerateFiles(directory).Any());
        });
    }
}
