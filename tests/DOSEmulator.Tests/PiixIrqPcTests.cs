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

/// <summary>Whole owned PCs, real configuration instructions and pre-branch rejection for the separate PIIX IRQ board.</summary>
internal static class PiixIrqPcTests
{
    /// <summary>Names only the own IRQ integration tuple, retaining the earlier host-only tuple unchanged.</summary>
    private static MachineProfile Profile() => new() { Id = MachineProfile.PiixIrqProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = 100_000_000, RamMiB = 32, Name = "Own PIIX IRQ PC" };
    /// <summary>Requires an observable hardware, memory or file result.</summary>
    private static void Check(bool value, string why = "PIIX IRQ PC contract failed.") { if (!value) throw new Exception(why); }
    /// <summary>Accepts only an intentional argument, owner or state rejection.</summary>
    private static void Invalid(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Invalid PIIX PC state was accepted."); }
    /// <summary>Compares every explicit CPU/peripheral/scheduler field and every physical DRAM byte.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        Check(JsonSerializer.Serialize(a.CaptureHardwareState(), StateValues.Json) == JsonSerializer.Serialize(b.CaptureHardwareState(), StateValues.Json), "Whole graph differs.");
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Whole 32-MiB RAM differs.");
    }
    /// <summary>Includes both owned PIC chips when checking a CPU-only reset boundary.</summary>
    private static string PicState(Pic8259 pic) => JsonSerializer.Serialize(new[] { pic.SaveState() }
        .Concat(pic.StateChildren.Select(child => child.Value.SaveState())).ToArray(), StateValues.Json);
    /// <summary>Initializes both fixed-priority PICs through ordinary ports, selecting normal or automatic EOIs.</summary>
    private static void Init(PcMachine m, bool autoMaster = false, bool autoSlave = false)
    {
        m.Ports.Write(0x20, 0x11, 1); m.Ports.Write(0xa0, 0x11, 1);
        m.Ports.Write(0x21, 8, 1); m.Ports.Write(0xa1, 0x70, 1);
        m.Ports.Write(0x21, 4, 1); m.Ports.Write(0xa1, 2, 1);
        m.Ports.Write(0x21, autoMaster ? 3u : 1u, 1); m.Ports.Write(0xa1, autoSlave ? 3u : 1u, 1);
        m.Ports.Write(0x21, 0xfb, 1); m.Ports.Write(0xa1, 0xfb, 1);
    }
    /// <summary>Programs the actual function-zero BDF using the existing configuration bus, not an injected replacement peer.</summary>
    private static void Route(PcMachine m, uint value)
    { m.Ports.Write(0xcf8, 0x80003860, 4); m.Ports.Write(0xcfc, value, 4); }
    /// <summary>Edits a newly created own snapshot and repairs its enclosing hash to exercise semantic checks.</summary>
    private static void Edit(string saved, Action<JsonObject> edit)
    {
        string statePath = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(statePath))!.AsObject(); edit(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(statePath, bytes);
        string path = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Accesses one exact typed node's own fields, never reflection or external data.</summary>
    private static JsonObject Fields(JsonObject state, string node) => state["components"]![node]!["fields"]!.AsObject();
    /// <summary>Changes the real persisted configuration-byte array using the normal serializer representation.</summary>
    private static void Config(JsonObject state, Action<byte[]> change)
    {
        var fields = Fields(state, "piix-irq"); byte[] bytes = fields["_config"]!.Deserialize<byte[]>(StateValues.Json)!;
        change(bytes); fields["_config"] = JsonSerializer.SerializeToNode(bytes, StateValues.Json);
    }
    /// <summary>Pumps one real session command on its owner and observes the actual success/error response.</summary>
    private static IpcResponse Request(MachineSession session, string command, object payload)
    { var task = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return task.GetAwaiter().GetResult(); }

    /// <summary>Registers product construction, physical-pin continuation and atomic rejection cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PIIX IRQ factory has two exact peers and no invented IDE/reset function", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Check(m.Profile.IsPiixIrq && m.Profile.IsFxHost && m.Profile.IsP54c);
            Check(m.Pci!.FunctionCount == 2 && m.Pci.HasFunction(0, 0, 0, m.HostBridge!) && m.Pci.HasFunction(0, 7, 0, m.PiixIrq!));
            Check(m.Pic.SupportsLevelInputs && m.PiixIrq!.HasPicBinding(m.Pic) && m.Pic.LevelMask == 0);
            m.Ports.Write(0xcf8, 0x80003800, 4); Check(m.Ports.Read(0xcfc, 4) == 0x122e8086);
            m.Ports.Write(0xcf8, 0x80003900, 4); Check(m.Ports.Read(0xcfc, 4) == uint.MaxValue);
            Check(m.Cpu.Model == X86CpuModel.IntelP54cB5Subset && m.Cpu.TimestampFrequency == 100_000_000 && m.Memory.Ram.Length == 32 * 1048576);
            long resets = m.HardwareResetCount; m.Ports.Write(0xcf9, 6, 1); Check(m.HardwareResetCount == resets && !m.Board.ConsumeResetRequest());
        });
        foreach (int mode in new[] { 0, 1, 2 }) test("M4 PIIX IRQ leaves previous board factory unchanged " + mode, () =>
        {
            var p = mode == 0 ? new MachineProfile() : mode == 1 ? new MachineProfile().WithDevelopmentCpu(true) : Profile() with { Id = MachineProfile.FxHostProfileId };
            using var m = PcMachine.CreateDemo(p); Check(!p.IsPiixIrq && m.PiixIrq is null && !m.Pic.SupportsLevelInputs && m.Ports.Read(0x4d0, 1) == 255);
            Check(m.Pci?.FunctionCount == (mode == 2 ? 1 : (int?)null)); m.CaptureHardwareState();
        });
        foreach (string changed in new[] { "cpu", "90MHz", "133MHz", "8MiB", "16MiB", "64MiB", "unknown" })
            test("M4 PIIX IRQ rejects mismatched tuple before media open " + changed, () =>
            {
                var p = changed switch { "cpu" => Profile() with { Cpu = "i486dx-development" }, "90MHz" => Profile() with { CpuHz = 90_000_000 },
                    "133MHz" => Profile() with { CpuHz = 133_000_000 }, "8MiB" => Profile() with { RamMiB = 8 }, "16MiB" => Profile() with { RamMiB = 16 },
                    "64MiB" => Profile() with { RamMiB = 64 }, _ => Profile() with { Id = "unimplemented-piix-board" } };
                using var own = new SnapshotTests.Fixture(); Invalid(() => MachineFactory.FromDefinition(new() { Hardware = p,
                    HardDisks = [new(0, own.PathFor("absent.img"), own.PathFor("not-created.cow"))] })); Check(!File.Exists(own.PathFor("not-created.cow")));
            });
        test("M4 PIIX IRQ actual POST labels bounded board and programs all DRBs", () =>
        {
            using var m = new PcMachine(Profile(), null); m.RunSteps(150000); Check(m.Fault is null && m.Cpu.Halted);
            Check(m.Board.DebugText.Contains("FX + PIIX IRQ", StringComparison.Ordinal) && m.Video.ReadText().Contains("FX + PIIX IRQ", StringComparison.Ordinal));
            Check(m.Memory.DecodedRamBytes == 32 * 1048576 && m.PiixIrq!.HasPicBinding(m.Pic));
            for (int i = 0; i < 5; i++) Check(m.HostBridge!.ReadConfiguration(0x60 + i, 1) == 8);
        });
        foreach (bool master in new[] { false, true }) foreach (bool slave in new[] { false, true })
            test($"M4 PIIX IRQ whole PC held pin snapshot future autoEOI={master}/{slave}", () =>
            {
                using var f = new SnapshotTests.Fixture(Profile(), false); var m = f.Machine; Init(m, master, slave);
                m.Ports.Write(0x4d1, 4, 1); Route(m, 0x80800a0a); m.PiixIrq!.SetIsaLine(10, true);
                Check(!m.Pic.HasPending, "Idle selected PIRQ must mask ordinary ISA10.");
                m.PiixIrq.SetPirq(0, true); m.PiixIrq.SetPirq(1, true); Check(m.Pic.HasPending);
                m.Memory.WriteWord(0x72 * 4, 0x2000); m.Memory.WriteWord(0x72 * 4 + 2, 0);
                m.Memory.WriteWord(0x77 * 4, 0x2100); m.Memory.WriteWord(0x77 * 4 + 2, 0);
                new byte[] { 0xb0, 0x20, 0xe6, 0x20, 0xcf }.CopyTo(m.Memory.Ram, 0x2100);
                new byte[] { 0xfb, 0xf4, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000);
                new byte[] { 0xff, 6, 0, 0x30, 0xb0, 0x20, 0xe6, 0xa0, 0xe6, 0x20, 0xcf }.CopyTo(m.Memory.Ram, 0x2000);
                m.Cpu.Eip = 0x1000;
                for (int i = 0; i < 12 && m.Memory.ReadWord(0x3000) == 0; i++) m.RunSteps(1);
                Check(m.Memory.ReadWord(0x3000) == 1 && m.Cpu.Eip == 0x2004, "Actual IRQ10 handler did not commit its first count.");
                m.PiixIrq.SetPirq(0, false); Check(m.PiixIrq.PirqAssertions == 2);
                using var b = f.Restore(); Same(m, b); Check(b.PiixIrq!.PirqAssertions == 2 && b.PiixIrq.IsaLines == m.PiixIrq.IsaLines);
                for (int i = 0; i < 40 && m.Memory.ReadWord(0x3000) < 3; i++)
                { m.RunSteps(1); b.RunSteps(1); Same(m, b); }
                Check(m.Memory.ReadWord(0x3000) == 3, "Held PCI level did not retrigger through the slave cascade.");
                m.PiixIrq.SetPirq(1, false); b.PiixIrq.SetPirq(1, false);
                for (int i = 0; i < 12; i++) { m.RunSteps(1); b.RunSteps(1); Same(m, b); }
                Check(m.Memory.ReadWord(0x3000) == 3 && !m.Pic.HasPending && m.PiixIrq.IsaLines == 0x400);
                Route(m, 0x80808080); Route(b, 0x80808080); Same(m, b); Check(m.Pic.HasPending, "Disabled PIRQ did not restore ordinary high ISA10.");
            });
        test("M4 PIIX IRQ CPU hot reset preserves powered bridge pins ELCR and PCI address", () =>
        {
            using var f = new SnapshotTests.Fixture(Profile(), false); var m = f.Machine; Init(m); m.Ports.Write(0x4d1, 4, 1); Route(m, 0x8080800a); m.PiixIrq!.SetPirq(0, true);
            string before = JsonSerializer.Serialize(m.PiixIrq.SaveState(), StateValues.Json), pic = PicState(m.Pic);
            uint address = m.Pci!.ConfigurationAddress; m.Ports.Write(0x92, 1, 1); m.RunSteps(1);
            Check(m.HardwareResetCount == 1 && before == JsonSerializer.Serialize(m.PiixIrq.SaveState(), StateValues.Json) && pic == PicState(m.Pic));
            Check(m.Pci.ConfigurationAddress == address && m.PiixIrq.HasPicBinding(m.Pic));
            using var cold = PcMachine.CreateDemo(Profile()); Check(cold.PiixIrq!.Elcr == 0 && cold.PiixIrq.PirqAssertions == 0 && cold.PiixIrq.IsaLines == 0 && cold.Pci!.ConfigurationAddress == 0);
        });
        test("M4 PIIX IRQ owner cold adoption and full snapshot adoption retain profile and rebuild peers", () =>
        {
            using var f = new SnapshotTests.Fixture(); using var session = new MachineSession(f.Machine);
            var originalCpu = session.Machine.Cpu; long epoch = session.InputGeneration;
            var prepare = Request(session, "PrepareConfigurationChange", Profile());
            Check(prepare.Success && ReferenceEquals(session.Machine.Cpu, originalCpu));
            Check(Request(session, "CommitConfigurationChange", (ConfigurationPlan)prepare.Payload!).Success);
            var pc = session.Machine; Check(pc.Profile.IsPiixIrq && !ReferenceEquals(pc.Cpu, originalCpu) && pc.PiixIrq!.HasPicBinding(pc.Pic));
            Check(session.InputGeneration == epoch + 1 && pc.Pci!.FunctionCount == 2 && pc.Cpu.UsesTimestampClock(pc.Scheduler, 100_000_000));
            Init(pc); pc.Ports.Write(0x4d1, 4, 1); Route(pc, 0x8080800a); pc.PiixIrq!.SetPirq(0, true); pc.Running = false;
            string saved = f.PathFor("owner.dossav"), branch = f.PathFor("owner.dosebranch");
            Check(Request(session, "CreateSnapshot", new { path = saved }).Success);
            string before = JsonSerializer.Serialize(pc.CaptureHardwareState(), StateValues.Json);
            Check(Request(session, "PowerCycleCurrent", new { }).Success);
            Check(session.Machine.Profile.IsPiixIrq && session.Machine.PiixIrq!.PirqAssertions == 0 && session.Machine.PiixIrq.Elcr == 0);
            Check(Request(session, "LoadSnapshot", new { path = saved, destination = branch }).Success);
            Check(session.InputGeneration == epoch + 3 && session.Machine.PiixIrq!.PirqAssertions == 1 && !session.Machine.Running);
            Check(session.Machine.PiixIrq!.HasPicBinding(session.Machine.Pic) && before == JsonSerializer.Serialize(session.Machine.CaptureHardwareState(), StateValues.Json));
        });
        foreach (string fault in new[] { "missing-bridge", "missing-pic-capability", "wrong-capability", "bad-id", "bad-header", "bad-route-fixed", "bad-elcr", "pic-elcr", "pin-mask", "isa-cascade", "physical-pin", "level-request", "pending-cache", "cascade", "phase" })
            test("M4 PIIX IRQ semantic import rejects before media open " + fault, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile()); var m = f.Machine; Init(m); m.Ports.Write(0x4d1, 4, 1); Route(m, 0x8080800a); m.PiixIrq!.SetPirq(0, true);
                string saved = f.PathFor("own.dossav"), branch = f.PathFor("rejected.dosebranch"); SnapshotStore.Capture(m, saved);
                Edit(saved, state =>
                {
                    switch (fault)
                    {
                        case "missing-bridge": Check(state["components"]!.AsObject().Remove("piix-irq")); break;
                        case "missing-pic-capability": Check(Fields(state, "pic").Remove("_supportsLevelInputs")); break;
                        case "wrong-capability": Fields(state, "pic")["_supportsLevelInputs"] = false; break;
                        case "bad-id": Config(state, c => c[2] ^= 1); break;
                        case "bad-header": Config(state, c => c[0x0e] = 0); break;
                        case "bad-route-fixed": Config(state, c => c[0x60] |= 0x10); break;
                        case "bad-elcr": Fields(state, "piix-irq")["_elcr"] = 0x401; break;
                        case "pic-elcr": Fields(state, "pic/_slave")["LevelMask"] = 0; break;
                        case "pin-mask": Fields(state, "piix-irq")["_pirqAssertions"] = 0x11; break;
                        case "isa-cascade": Fields(state, "piix-irq")["_isaLines"] = 4; break;
                        case "physical-pin": Fields(state, "pic/_slave")["Lines"] = 0; break;
                        case "level-request": Fields(state, "pic/_slave")["Irr"] = 0; break;
                        case "pending-cache": Fields(state, "pic")["_pendingMaster"] = -1; break;
                        case "cascade": Fields(state, "pic/_master")["Lines"] = 0; break;
                        case "phase": Fields(state, "pic/_slave")["InitStep"] = 4; break;
                    }
                });
                using var locked = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
                string before = JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json); byte[] ram = m.Memory.Ram.ToArray();
                bool rejected = false; try { using var wrong = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !Directory.Exists(branch) && before == JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json) && ram.AsSpan().SequenceEqual(m.Memory.Ram));
                using var session = new MachineSession(m); long revision = session.Revision, epoch = session.InputGeneration;
                var response = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
                Check(!response.Success && ReferenceEquals(session.Machine, m) && session.Revision == revision && session.InputGeneration == epoch && !Directory.Exists(branch));
                Check(before == JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json) && ram.AsSpan().SequenceEqual(m.Memory.Ram));
            });
    }
}
