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

/// <summary>Separate 100/133 MHz development tuples, cold S3 firmware selection and exact owner/clock snapshot binding.</summary>
internal static class Windows95ProfileTests
{
    /// <summary>Requires a concrete tuple, real instruction, immutable ROM, media or whole-state result.</summary>
    private static void Check(bool value, string why = "Windows95 development profile invariant failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Requires the existing explicit configuration rejection before a machine can be constructed.</summary>
    private static void Invalid(Action action)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected); }
    /// <summary>Creates only one explicitly named candidate; this is not installation or historical cycle acceptance.</summary>
    private static MachineProfile Profile(bool fast) => new()
    {
        Id = fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
        Name = fast ? "Own 133 MHz Windows95 candidate" : "Own 100 MHz Windows95 candidate",
        Cpu = MachineProfile.P54cCpuId, CpuHz = fast ? 133_000_000 : 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2
    };
    /// <summary>Places an ordinary private real-mode loop without POST, native input or a host callback.</summary>
    private static void Loop(PcMachine machine)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.SetSegment(Segment.Ss, 0); machine.Cpu.SetSegment(Segment.Ds, 0);
        machine.Cpu.SetRegister(Register.Sp, 0x9000); machine.Cpu.Eflags = 2; machine.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(machine.Memory.Ram, 0x1000);
        Check(machine.RunSteps(2) == 2 && machine.Fault is null);
    }
    /// <summary>Own fresh directory and immutable memory basis; no external or original image is opened.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Unique checked output root for configurations, real .dossav files and private branches.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "artifacts/tests/windows95-profile-" + Guid.NewGuid().ToString("N")));
        /// <summary>Actual limited candidate whose full CPU/device/RAM future is captured.</summary>
        public readonly PcMachine Source;
        /// <summary>Independent running owner which import failures must preserve.</summary>
        public readonly PcMachine Owner;
        /// <summary>Constructs the exact selected clock and one protected own source disk.</summary>
        public Fixture(bool fast)
        {
            Directory.CreateDirectory(Root);
            byte[] bytes = Enumerable.Range(0, 128 * 512).Select(i => unchecked((byte)(i * 7 + 0x43))).ToArray();
            Source = PcMachine.CreateWithDisks(Profile(fast), [new MemoryDisk(bytes, "Own Windows95 profile basis"), null, null, null]);
            Owner = PcMachine.CreateWithDisks(new(), [new MemoryDisk(new byte[128 * 512], "Own Windows95 old owner"), null, null, null]);
            Loop(Source); Loop(Owner); Source.Memory.Ram[^1] = 0x68; Owner.Memory.Ram[^1] = 0x97;
        }
        /// <summary>Returns a path only inside this uniquely owned directory.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Captures a genuine complete container with a newly sealed own RAW basis.</summary>
        public string Save()
        { string saved = PathFor("source.dossav"); SnapshotStore.Capture(Source, saved); return saved; }
        /// <summary>Removes only the verified private output tree after disposing both own machines.</summary>
        public void Dispose()
        {
            Source.Dispose(); Owner.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("windows95-profile-", StringComparison.Ordinal));
            Directory.Delete(Root, true);
        }
    }
    /// <summary>Captures all explicit hardware/scheduler fields without normalization.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Requires every CPU/device value, every 32 MiB RAM byte and the rebound architectural TSC.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) &&
        a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram) && a.Cpu.TimestampCounter == b.Cpu.TimestampCounter,
        "Full candidate continuation differs.");
    /// <summary>Reads only the actual immutable physical ROM aperture.</summary>
    private static byte[] Rom(PcMachine machine) => Enumerable.Range(0, 65536).Select(i => machine.Memory.ReadByte(0xf0000u + (uint)i)).ToArray();
    /// <summary>Creates a named legacy PCI/ATAPI control with the same installed disk geometry.</summary>
    private static PcMachine PriorPciBoard() => PcMachine.CreateWithDisks(new()
    {
        Id = MachineProfile.PciBiosProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId
    }, [new MemoryDisk(new byte[128 * 512], "Own Windows95 profile basis"), null, null, null]);

    /// <summary>Rehashes only one new own state or sealed-definition mutation.</summary>
    private static void Corrupt(string saved, string kind, bool fast)
    {
        bool media = kind == "definition"; string payloadPath = Path.Combine(saved, media ? "media.dosemedia/media.json" : "state.json");
        var payload = JsonNode.Parse(File.ReadAllBytes(payloadPath))!.AsObject();
        if (media)
        {
            payload["machine"]!["hardware"]!["id"] = fast ? MachineProfile.Windows95ProfileId : MachineProfile.Windows95Profile133Id;
            payload["machine"]!["hardware"]!["cpuHz"] = fast ? 100_000_000 : 133_000_000;
        }
        else
        {
            var cpu = payload["components"]!["cpu"]!["fields"]!.AsObject();
            if (kind == "frequency") cpu["TimestampFrequency"] = fast ? 100_000_000u : 133_000_000u;
            else cpu["Model"] = JsonSerializer.SerializeToNode(X86CpuModel.Intel486Dx, StateValues.Json);
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, StateValues.Json); File.WriteAllBytes(payloadPath, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        if (!media) manifest["stateBytes"] = bytes.Length;
        manifest[media ? "mediaSha256" : "stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Requires profile/model/clock rejection before the locked own base can be opened, directly and through its old owner.</summary>
    private static void RejectImport(Fixture f, string saved)
    {
        string before = State(f.Owner), source = State(f.Source); byte[] ram = f.Owner.Memory.Ram.ToArray();
        string direct = f.PathFor("direct.dosebranch"), ipc = f.PathFor("ipc.dosebranch");
        using var locked = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false; try { using var wrong = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(direct) && State(f.Owner) == before && f.Owner.Memory.Ram.AsSpan().SequenceEqual(ram));
        using var session = new MachineSession(f.Owner); long revision = session.Revision, generation = session.InputGeneration;
        var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = ipc }));
        session.PumpCommands(); var response = pending.GetAwaiter().GetResult();
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, f.Owner) &&
            session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(ipc) &&
            State(f.Owner) == before && State(f.Source) == source && f.Owner.Memory.Ram.AsSpan().SequenceEqual(ram));
        long instructions = f.Owner.Cpu.Instructions; Check(f.Owner.RunSteps(3) == 3 && f.Owner.Cpu.Instructions == instructions + 3);
    }

    /// <summary>Registers strict fixed tuples, real cold CPU/ROM selection and exact complete snapshot adoption/rejection.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var item in new (string Name, MachineProfile Hardware, bool S3, bool Vbe)[]
        {
            ("486", new(), false, false),
            ("ET4000", new() { VideoCard = "et4000ax-isa-1m-development" }, false, false),
            ("earlier S3", new() { Id = MachineProfile.S3Trio64ProfileId, Cpu = MachineProfile.P54cCpuId,
                CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId }, true, false),
            ("Windows95 candidate100", Profile(false), true, true),
            ("Windows95 candidate133", Profile(true), true, true)
        })
            test("M4 Windows95 IPC capabilities distinguish support and active firmware " + item.Name, () =>
            {
                using var session = new MachineSession(PcMachine.CreateWithDisks(item.Hardware, new IReadOnlyBlockDevice?[4]));
                var machine = session.Machine; string before = State(machine); byte[] ram = machine.Memory.Ram.ToArray();
                long revision = session.Revision, generation = session.InputGeneration;
                var pending = session.Enqueue(IpcProtocol.CreateRequest("GetCapabilities")); session.PumpCommands();
                var response = pending.GetAwaiter().GetResult(); Check(response.Success);
                var caps = JsonSerializer.SerializeToElement(response.Payload, IpcProtocol.JsonOptions);
                Check(caps.GetProperty("trio64Acceleration").GetBoolean() && caps.GetProperty("trio64Acceleration2dSubset").GetBoolean() &&
                    caps.GetProperty("trio64AccelerationScope").GetString()!.Contains("subset", StringComparison.Ordinal) &&
                    caps.GetProperty("trio64HighColor").GetBoolean() && caps.GetProperty("trio64HardwareCursor").GetBoolean() &&
                    !caps.GetProperty("trio64FullChip").GetBoolean() && !caps.GetProperty("fullVbeConformance").GetBoolean());
                Check(caps.GetProperty("trio64PixelFormats").EnumerateArray().Select(v => v.GetString()).SequenceEqual(
                    new[] { "indexed8", "RGB555", "RGB565", "BGRx32" }));
                string cursor = caps.GetProperty("trio64HardwareCursorPolicy").GetString()!;
                Check(cursor.Contains("left storage", StringComparison.Ordinal) && cursor.Contains("Windows/X11", StringComparison.Ordinal) &&
                    cursor.Contains("enhanced scanout", StringComparison.Ordinal));
                Check(caps.GetProperty("trio64Active").GetBoolean() == item.S3 &&
                    caps.GetProperty("trio64VbeActive").GetBoolean() == item.Vbe &&
                    caps.GetProperty("pciBiosActive").GetBoolean() == item.Hardware.IsPciBiosBoard);
                Check(caps.GetProperty("trio64Vbe12Subset").GetBoolean() && caps.GetProperty("trio64VbeVersion").GetInt32() == 0x0102 &&
                    caps.GetProperty("trio64VbeOwnFirmware").GetBoolean() && caps.GetProperty("trio64VbeBanked").GetBoolean() &&
                    !caps.GetProperty("trio64VbeLfbInterface").GetBoolean() && caps.GetProperty("trio64VbeFunctions").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { 0, 1, 2, 3, 5 }));
                Check(caps.GetProperty("windows95CandidateProfiles").EnumerateArray().Select(v => v.GetString()).SequenceEqual(
                    new[] { MachineProfile.Windows95ProfileId, MachineProfile.Windows95Profile133Id }));
                Check(State(machine) == before && machine.Memory.Ram.AsSpan().SequenceEqual(ram) &&
                    session.Revision == revision && session.InputGeneration == generation,
                    "Read-only capabilities changed the machine, RAM or ownership epochs.");
            });
        foreach (bool fast in new[] { false, true })
        {
            test("M4 Windows95 candidate complete profile flags rate=" + (fast ? 133 : 100), () =>
            {
                var p = Profile(fast); p.Validate();
                Check(p.IsWindows95Board && p.IsPciBiosBoard && p.IsAtapiBoard && p.IsAtapi && p.IsS3Trio64Board &&
                    p.IsTrio64 && p.IsPiixReset && p.IsPiixIde && p.IsPiixIrq && p.IsFxHost && p.IsP54c && p.RamMiB == 32);
                Check(ReferenceEquals(p.WithDevelopmentCpu(true), p));
                bool rejected = false; try { p.WithDevelopmentCpu(false); } catch (InvalidOperationException) { rejected = true; } Check(rejected);
                (p with { AtapiSlot = 0 }).Validate(); (p with { AtapiSlot = 3 }).Validate();
            });
            foreach (int hz in fast ? new[] { 90_000_000, 100_000_000, 132_000_000, 133_000_001 } : new[] { 90_000_000, 99_000_000, 100_000_001, 133_000_000 })
                test("M4 Windows95 rejects mismatched fixed candidate clock fast=" + fast + " hz=" + hz,
                    () => Invalid(() => (Profile(fast) with { CpuHz = hz }).ValidateCpuSelection()));
            foreach (int mib in new[] { 8, 24, 256 })
                test("M4 Windows95 rejects unsupported installed RAM fast=" + fast + " MiB=" + mib,
                    () => Invalid(() => (Profile(fast) with { RamMiB = mib }).ValidateCpuSelection()));
            test("M4 Windows95 rejects unbound CPU/video/packet tuples fast=" + fast, () =>
            {
                Invalid(() => (Profile(fast) with { Cpu = "i486dx-development" }).ValidateCpuSelection());
                Invalid(() => (Profile(fast) with { VideoCard = "dose-vga-development" }).ValidateCpuSelection());
                Invalid(() => (Profile(fast) with { OpticalDrive = "none" }).ValidateCpuSelection());
            });
            test("M4 Windows95 delivered candidate JSON preserves exact tuple fast=" + fast, () =>
            {
                string path = Path.Combine(Environment.CurrentDirectory, "profiles", Profile(fast).Id + ".json");
                byte[] before = File.ReadAllBytes(path); var p = JsonSerializer.Deserialize<MachineProfile>(before, IpcProtocol.JsonOptions)!; p.Validate();
                Check(p.Id == Profile(fast).Id && p.CpuHz == Profile(fast).CpuHz && p.RamMiB == 32 && p.AtapiSlot == 2 &&
                    p.IsWindows95Board && File.ReadAllBytes(path).AsSpan().SequenceEqual(before));
            });
            test("M4 Windows95 cold CPU and optional firmware rate=" + (fast ? 133 : 100), () =>
            {
                using var f = new Fixture(fast); using var prior = PriorPciBoard();
                Check(f.Source.Cpu.Model == X86CpuModel.IntelP54cB5Subset && f.Source.Cpu.TimestampFrequency == (uint)Profile(fast).CpuHz &&
                    f.Source.Cpu.UsesTimestampClock(f.Source.Scheduler, (uint)Profile(fast).CpuHz) && f.Source.Atapi is not null &&
                    f.Source.Pci!.FunctionCount == 4 && !Rom(f.Source).AsSpan().SequenceEqual(Rom(prior)));
                f.Source.Memory.Ram[0x1000] = 0x0f; f.Source.Memory.Ram[0x1001] = 0xa2;
                f.Source.Cpu.Eip = 0x1000; f.Source.Cpu.SetRegister(Register.Ax, 1);
                Check(f.Source.RunSteps(1) == 1 && f.Source.Cpu.GetRegister(Register.Dx) == 0x135 &&
                    f.Source.Cpu.GetRegister(Register.Cx) == 0, "The candidate advertised different CPU features.");
                Loop(f.Source);
                ulong tsc = f.Source.Cpu.TimestampCounter; f.Source.Scheduler.AdvanceTo(f.Source.Scheduler.Now + 1_000_000);
                f.Source.Audio.AdvanceTo(f.Source.Scheduler.Now);
                Check(f.Source.Cpu.TimestampCounter - tsc == (ulong)Profile(fast).CpuHz / 1000);
            });
            test("M4 Windows95 exact profile definition persistence fast=" + fast, () =>
            {
                using var f = new Fixture(fast); string file = f.PathFor("own.dospc.json");
                var definition = new MachineDefinition { Name = "Own candidate PC", Hardware = Profile(fast) with { Name = "Own candidate hardware" } };
                MachineDefinitionStore.Save(file, definition); byte[] before = File.ReadAllBytes(file); var restored = MachineDefinitionStore.Load(file);
                Check(restored.Name == definition.Name && restored.Hardware == definition.Hardware && before.AsSpan().SequenceEqual(File.ReadAllBytes(file)));
            });
            test("M4 Windows95 full direct and owner snapshot future fast=" + fast, () =>
            {
                using var f = new Fixture(fast); string saved = f.Save();
                using var restored = SnapshotStore.Load(saved, f.PathFor("direct.dosebranch")); Same(f.Source, restored);
                using var session = new MachineSession(f.Owner); long revision = session.Revision, generation = session.InputGeneration;
                var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = f.PathFor("owner.dosebranch") }));
                session.PumpCommands(); Check(pending.GetAwaiter().GetResult().Success && session.Revision == revision + 1 &&
                    session.InputGeneration == generation + 1 && session.Machine.Profile == f.Source.Profile);
                Same(f.Source, session.Machine);
                foreach (var side in new[] { f.Source, restored, session.Machine }) Check(side.RunSteps(127) == 127 && side.Fault is null);
                Same(f.Source, restored); Same(f.Source, session.Machine);
            });
            foreach (string kind in new[] { "frequency", "model", "definition" })
                test("M4 Windows95 early exact model/profile/clock container fast=" + fast + " change=" + kind, () =>
                { using var f = new Fixture(fast); string saved = f.Save(); Corrupt(saved, kind, fast); RejectImport(f, saved); });
        }
        foreach (var old in new (string Id, int Hz, int MiB, bool S3, bool Atapi)[]
        {
            (MachineProfile.P54cProfileId, 90_000_000, 8, false, false),
            (MachineProfile.FxHostProfileId,100_000_000,32,false,false), (MachineProfile.PiixIrqProfileId,100_000_000,32,false,false),
            (MachineProfile.PiixIdeProfileId,100_000_000,32,false,false), (MachineProfile.PiixResetProfileId,100_000_000,32,false,false),
            (MachineProfile.S3Trio64ProfileId,100_000_000,32,true,false), (MachineProfile.AtapiProfileId,100_000_000,32,true,true),
            (MachineProfile.PciBiosProfileId,100_000_000,32,true,true)
        })
            test("M4 Windows95 leaves prior fixed tuple unchanged " + old.Id, () =>
            {
                var p = new MachineProfile { Id = old.Id, Cpu = MachineProfile.P54cCpuId, CpuHz = old.Hz, RamMiB = old.MiB,
                    VideoCard = old.S3 ? MachineProfile.S3Trio64VideoId : "dose-vga-development", OpticalDrive = old.Atapi ? MachineProfile.AtapiCdRomId : "none" };
                p.Validate(); Check(!p.IsWindows95Board); Invalid(() => (p with { CpuHz = 133_000_000 }).ValidateCpuSelection());
            });
    }
}
