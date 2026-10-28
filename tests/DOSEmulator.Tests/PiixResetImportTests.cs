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

/// <summary>Actual rehashed reset containers, early semantic rejection before own media opening, and genuine pending/held resume controls.</summary>
internal static class PiixResetImportTests
{
    /// <summary>Requires an independent state, owner, byte or event invariant.</summary>
    private static void Check(bool value, string why = "PIIX reset import invariant failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Complete fixed reset board, retaining a real DSP reset pin for the held-domain checks.</summary>
    private static MachineProfile Profile(bool sb2 = false) => new()
    {
        Id = MachineProfile.PiixResetProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        SoundCard = sb2 ? "sb2-dsp-development" : SoundBlaster16.ProfileId
    };
    /// <summary>Programs a real nonhalting private RAM loop without starting POST or opening a host endpoint.</summary>
    private static void Loop(PcMachine machine)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.SetSegment(Segment.Ss, 0); machine.Cpu.SetSegment(Segment.Ds, 0);
        machine.Cpu.SetRegister(Register.Sp, 0x9000); machine.Cpu.Eflags = 2; machine.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(machine.Memory.Ram, 0x1000);
        Check(machine.RunSteps(2) == 2 && machine.Fault is null);
    }
    /// <summary>Own source and independently running old owner; only explicitly created bytes and fresh journals are accessed.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/piix-reset-import-" + Guid.NewGuid().ToString("N")));
        public readonly PcMachine Source, Owner;
        public readonly bool Writable;
        /// <summary>Creates either an owned immutable memory medium or a fresh own RAW/COW pair.</summary>
        public Fixture(string phase, bool writable = false, bool srstHigh = false, bool sb2 = false)
        {
            Writable = writable; Directory.CreateDirectory(Root);
            byte[] bytes = Enumerable.Range(0, 128 * 512).Select(i => unchecked((byte)(i * 11 + 0x35))).ToArray();
            if (writable)
            {
                using (var file = new FileStream(PathFor("base.img"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
                Source = MachineFactory.FromDefinition(new() { Hardware = Profile(sb2), HardDisks = [new(0, PathFor("base.img"), PathFor("active.cow"))] });
            }
            else Source = PcMachine.CreateWithDisks(Profile(sb2), [new MemoryDisk(bytes, "Own reset import source"), null, null, null]);
            Owner = PcMachine.CreateWithDisks(new(), [new MemoryDisk(new byte[128 * 512], "Own independent live owner"), null, null, null]);
            Loop(Owner); Owner.Memory.Ram[^1] = 0x6d; Owner.Memory.Ram[0x23456] = 0xa7;
            Loop(Source); Source.Memory.Ram[^1] = 0x96; Source.Memory.Ram[0x34567] = 0x52;
            if (srstHigh)
            {
                Source.Ports.Write(0xcf8, 0x80003940, 4); Source.Ports.Write(0xcfc, 0x80008000, 4);
                Source.Ports.Write(0xcf8, 0x80003904, 4); Source.Ports.Write(0xcfc, 3, 2);
                Source.Ports.Write(0x3f6, 4, 1); Source.Ports.Write(0x376, 4, 1);
            }
            if (phase == "idle") return;
            Source.Cpu.Eip = 0x1000;
            new byte[] { 0xba, 0xf9, 0x0c, 0xb0, 0x06, 0xee, 0xeb, 0xfe }.CopyTo(Source.Memory.Ram, 0x1000);
            Check(Source.RunSteps(3) == 3 && Source.PiixReset!.PendingRequest == PiixResetRequest.Hard && Source.PiixReset.Phase == PiixResetPhase.Idle,
                "Own fixture did not execute a real pending CF9 OUT.");
            if (phase == "pending") return;
            ulong cut = Source.Scheduler.Now + (phase == "all" ? 200_000UL : 1_500_000UL);
            Check(Source.RunSteps(1, cut) == 1 && Source.PiixReset!.CpuResetHeld && Source.Cpu.Instructions == 0);
            if (phase == "ready")
            {
                Source.Scheduler.AdvanceTo(Source.PiixReset!.CpuReleaseAt);
                Check(Source.PiixReset.Phase == PiixResetPhase.CpuOnly && Source.PiixReset.CpuReleaseReady);
            }
            else Check(Source.PiixReset!.Phase == (phase == "all" ? PiixResetPhase.AllAsserted : PiixResetPhase.CpuOnly));
            Source.Audio.AdvanceTo(Source.Scheduler.Now);
        }
        /// <summary>Returns only a child path within this unique private directory.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Captures real state and, for writable media, relocates only the own identical base to a separately lockable sealed path.</summary>
        public string Save(string name)
        {
            string saved = PathFor(name); SnapshotStore.Capture(Source, saved);
            if (Writable)
            {
                string mediaPath = Path.Combine(saved, "media.dosemedia", "media.json");
                var media = JsonNode.Parse(File.ReadAllBytes(mediaPath))!.AsObject();
                File.Copy(PathFor("base.img"), Path.Combine(saved, "media.dosemedia", "owned-base.img"));
                media["machine"]!["hardDisks"]![0]!["imagePath"] = "owned-base.img";
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(media, StateValues.Json); File.WriteAllBytes(mediaPath, data);
                string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
                manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(data));
                File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
            }
            return saved;
        }
        /// <summary>Points exclusively to the captured own base, never to an active or personal journal.</summary>
        public string SealedBase(string saved) => Path.Combine(saved, "media.dosemedia", Writable ? "owned-base.img" : "ide0.img");
        /// <summary>Disposes the own PCs and removes only the verified private fixture root.</summary>
        public void Dispose()
        {
            Source.Dispose(); Owner.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("piix-reset-import-", StringComparison.Ordinal));
            Directory.Delete(Root, true);
        }
    }
    /// <summary>Full owned hardware, callback descriptors and actual virtual scheduler phase.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares complete hardware and all installed RAM without normalization.</summary>
    private static void Same(PcMachine first, PcMachine second) => Check(State(first) == State(second) &&
        first.Memory.Ram.AsSpan().SequenceEqual(second.Memory.Ram), "Complete reset-state continuation differs.");
    /// <summary>Mutates only the own semantic state and repairs its exact manifest hash, size and clock.</summary>
    private static void Edit(string saved, Action<JsonObject> mutate)
    {
        string statePath = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(statePath))!.AsObject(); mutate(state);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(statePath, data);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = data.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(data));
        manifest["virtualNanoseconds"] = state["scheduler"]!["now"]!.GetValue<ulong>();
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Returns exactly one explicit owned field map.</summary>
    private static JsonObject Fields(JsonObject state, string name) => state["components"]![name]!["fields"]!.AsObject();
    /// <summary>Returns only the actual CF9 sequencer's fields.</summary>
    private static JsonObject Reset(JsonObject state) => Fields(state, "piix-reset");
    /// <summary>Looks up the one genuine live release row through its saved token, rather than assuming an array index.</summary>
    private static JsonObject Release(JsonObject state, string field)
    {
        long token = Reset(state)[field]!.GetValue<long>();
        return state["scheduler"]!["events"]!.AsArray().Single(row => row!["id"]!.GetValue<long>() == token)!.AsObject();
    }
    /// <summary>Adds a known-shaped fresh callback, retaining token uniqueness and complete original rows.</summary>
    private static long AddEvent(JsonObject state, string owner, string operation, ulong at, bool cancelled = false)
    {
        var scheduler = state["scheduler"]!.AsObject(); long token = scheduler["sequence"]!.GetValue<long>() + 1; scheduler["sequence"] = token;
        var events = scheduler["events"]!.AsArray().Select(row => row!.DeepClone()).ToList();
        events.Add(JsonSerializer.SerializeToNode(new ScheduledStateEvent(token, at, owner, operation, []), StateValues.Json)!);
        scheduler["events"] = new JsonArray(events.OrderBy(row => row["time"]!.GetValue<ulong>()).ThenBy(row => row["id"]!.GetValue<long>()).ToArray());
        if (cancelled) scheduler["cancelled"]!.AsArray().Add(token);
        return token;
    }
    /// <summary>Changes one allowed raw configuration byte, keeping its exact byte-array representation.</summary>
    private static void Config(JsonObject state, string owner, int offset, byte value)
    {
        var fields = Fields(state, owner); byte[] config = fields["_config"]!.Deserialize<byte[]>(StateValues.Json)!;
        config[offset] = value; fields["_config"] = JsonSerializer.SerializeToNode(config, StateValues.Json);
    }
    /// <summary>Changes one raw architectural GPR while preserving the complete eight-register shape.</summary>
    private static void RegisterValue(JsonObject state, int index)
    {
        var fields = Fields(state, "cpu"); uint[] values = fields["_registers"]!.Deserialize<uint[]>(StateValues.Json)!;
        values[index] ^= 1; fields["_registers"] = JsonSerializer.SerializeToNode(values, StateValues.Json);
    }
    /// <summary>Changes a real hidden CS attribute while preserving descriptor shape and all other caches.</summary>
    private static void CsCache(JsonObject state, string field)
    {
        var fields = Fields(state, "cpu"); var values = fields["_segmentCaches"]!.Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        int cs = (int)Segment.Cs;
        values[cs] = field == "base" ? values[cs] with { Base = 0 } : values[cs] with { Flags = 4 };
        fields["_segmentCaches"] = JsonSerializer.SerializeToNode(values, StateValues.Json);
    }
    /// <summary>Proves semantic validation precedes denied base opening, keeps the old owner and remains usable via direct and IPC paths.</summary>
    private static void Reject(Fixture fixture, string saved)
    {
        var owner = fixture.Owner; string before = State(owner), sourceBefore = State(fixture.Source);
        byte[] ram = owner.Memory.Ram.ToArray(); string direct = fixture.PathFor("direct-rejected.dosebranch"), ipc = fixture.PathFor("ipc-rejected.dosebranch");
        using var locked = new FileStream(fixture.SealedBase(saved), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false;
        try { using var wrong = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(direct) && State(owner) == before && owner.Memory.Ram.AsSpan().SequenceEqual(ram),
            "Import reached denied media or changed the independent old owner.");
        using var session = new MachineSession(owner); long revision = session.Revision, generation = session.InputGeneration;
        var request = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = ipc }));
        session.PumpCommands(); var response = request.GetAwaiter().GetResult();
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, owner) &&
            session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(ipc));
        var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping", new { })); session.PumpCommands();
        Check(ping.GetAwaiter().GetResult().Success && State(owner) == before && State(fixture.Source) == sourceBefore && owner.Memory.Ram.AsSpan().SequenceEqual(ram));
        long instructions = owner.Cpu.Instructions;
        Check(owner.RunSteps(3) == 3 && owner.Cpu.Instructions == instructions + 3 && owner.Fault is null, "The old CPU stopped after rejected reset import.");
    }
    /// <summary>Registers independent corruptions and genuine source-created positive reset phase controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        var cases = new List<(string Name, string Phase, Action<JsonObject> Mutate)>();
        // Registers one named, genuine rehashed container mutation for both direct and owner rejection paths.
        void Add(string name, string phase, Action<JsonObject> mutate) => cases.Add((name, phase, mutate));
        Add("missing sequencer", "all", state => state["components"]!.AsObject().Remove("piix-reset"));
        foreach (byte reserved in new byte[] { 1, 0x80 })
            Add("reserved CF9 bit " + reserved, "pending", state => Reset(state)["_control"] = (byte)(6 | reserved));
        Add("unknown pending request", "pending", state => Reset(state)["_pending"] = 3);
        Add("unknown phase", "all", state => Reset(state)["_phase"] = 3);
        foreach (string token in new[] { "_pciEvent", "_cpuEvent" })
            Add("negative token " + token, "all", state => Reset(state)[token] = -1);
        foreach (string count in new[] { "SoftEdges", "HardEdges" })
            Add("negative edge count " + count, "all", state => Reset(state)[count] = -1);
        Add("held control programming", "all", state => Reset(state)["_control"] = 2);
        Add("held pending INIT", "all", state => Reset(state)["_pending"] = 1);
        Add("held pending hard reset", "all", state => Reset(state)["_pending"] = 2);
        Add("idle unfinished pulse", "all", state => Reset(state)["_phase"] = 0);
        Add("CPU-only before PCI release", "all", state => Reset(state)["_phase"] = 2);
        Add("ready before release", "all", state => Reset(state)["_cpuReleaseReady"] = true);
        Add("unready after real release", "ready", state => Reset(state)["_cpuReleaseReady"] = false);
        Add("ready retains consumed CPU token", "ready", state => Reset(state)["_cpuEvent"] = 1);
        Add("CPU-only retains PCI token", "cpu", state => Reset(state)["_pciEvent"] = Reset(state)["_cpuEvent"]!.GetValue<long>());
        Add("idle ready flag", "idle", state => Reset(state)["_cpuReleaseReady"] = true);
        Add("idle unanchored assertion", "idle", state => Reset(state)["_assertedAt"] = 1);
        Add("missing hard pulse deadlines", "all", state => { Reset(state)["_assertedAt"] = 0; Reset(state)["_pciReleaseAt"] = 0; Reset(state)["_cpuReleaseAt"] = 0; });
        Add("PCI deadline drift", "all", state => Reset(state)["_pciReleaseAt"] = Reset(state)["_pciReleaseAt"]!.GetValue<ulong>() + 1);
        Add("CPU deadline drift", "all", state => Reset(state)["_cpuReleaseAt"] = Reset(state)["_cpuReleaseAt"]!.GetValue<ulong>() + 1);
        Add("unrepresentable pulse end", "all", state => Reset(state)["_assertedAt"] = ulong.MaxValue - 1);
        Add("coherent future assertion", "all", state =>
        {
            ulong at = state["scheduler"]!["now"]!.GetValue<ulong>() + 1;
            Release(state, "_pciEvent")["time"] = at + 1_000_000; Release(state, "_cpuEvent")["time"] = at + 2_000_000;
            Reset(state)["_assertedAt"] = at; Reset(state)["_pciReleaseAt"] = at + 1_000_000; Reset(state)["_cpuReleaseAt"] = at + 2_000_000;
        });
        Add("expired PCI callback at current time", "all", state => state["scheduler"]!["now"] = Reset(state)["_pciReleaseAt"]!.GetValue<ulong>());
        Add("expired CPU callback before ready", "cpu", state => state["scheduler"]!["now"] = Reset(state)["_cpuReleaseAt"]!.GetValue<ulong>());
        Add("colliding release references", "all", state => Reset(state)["_pciEvent"] = Reset(state)["_cpuEvent"]!.GetValue<long>());
        Add("swapped release references", "all", state =>
        {
            long pci = Reset(state)["_pciEvent"]!.GetValue<long>(), cpu = Reset(state)["_cpuEvent"]!.GetValue<long>();
            Reset(state)["_pciEvent"] = cpu; Reset(state)["_cpuEvent"] = pci;
        });
        foreach (string token in new[] { "_pciEvent", "_cpuEvent" })
        foreach (string change in new[] { "missing", "cancelled", "owner", "operation", "deadline", "now", "payload", "id", "duplicate-id", "zero-reference" })
            Add(token + " " + change, "all", state =>
            {
                var row = Release(state, token); long id = row["id"]!.GetValue<long>();
                switch (change)
                {
                    case "missing": state["scheduler"]!["events"]!.AsArray().Remove(row); break;
                    case "cancelled": state["scheduler"]!["cancelled"]!.AsArray().Add(id); break;
                    case "owner": row["owner"] = "pit"; break;
                    case "operation": row["operation"] = token == "_pciEvent" ? "release-cpu-ready" : "release-pci-isa"; break;
                    case "deadline": row["time"] = row["time"]!.GetValue<ulong>() + 1; break;
                    case "now": row["time"] = state["scheduler"]!["now"]!.GetValue<ulong>(); break;
                    case "payload": row["payload"] = new JsonArray(JsonValue.Create(0)); break;
                    case "id": long fresh = state["scheduler"]!["sequence"]!.GetValue<long>() + 1; state["scheduler"]!["sequence"] = fresh; row["id"] = fresh; break;
                    case "duplicate-id": row["id"] = Reset(state)[token == "_pciEvent" ? "_cpuEvent" : "_pciEvent"]!.GetValue<long>(); break;
                    case "zero-reference": Reset(state)[token] = 0; break;
                }
            });
        foreach (string phase in new[] { "idle", "all", "cpu", "ready" })
        foreach (string operation in new[] { "release-pci-isa", "release-cpu-ready" })
            Add("unreferenced live " + phase + "/" + operation, phase, state =>
                AddEvent(state, "piix-reset", operation, state["scheduler"]!["now"]!.GetValue<ulong>() + 10_000));
        foreach (var value in new (string Field, uint Value)[] { ("Eip", 0xfff1), ("Eflags", 0x202), ("Cr0", 0x60000000), ("Cr2", 1), ("Cr3", 0x1000), ("Cr4", 8), ("Dr6", 0), ("Dr7", 0x2400), ("_fpuIp", 0x1234), ("_fpuData", 0x5678) })
            Add("held CPU " + value.Field, "all", state => Fields(state, "cpu")[value.Field] = value.Value);
        foreach (string flag in new[] { "Halted", "TripleFaulted", "_publishedFpuError", "IgnoreNumericError", "_interruptShadow", "_debugShadow" })
            Add("held CPU " + flag, "all", state => Fields(state, "cpu")[flag] =
                flag is "_interruptShadow" or "_debugShadow" ? JsonValue.Create(1) : JsonValue.Create(true));
        Add("held x87 FNINIT control", "all", state => Fields(state, "cpu")["FpuControlWord"] = 0x37f);
        Add("held x87 status", "all", state => Fields(state, "cpu")["FpuStatusWord"] = 1);
        Add("held x87 tags", "all", state => Fields(state, "cpu")["_fpuEmpty"] = 255);
        Add("held x87 raw value", "all", state =>
        {
            var fields = Fields(state, "cpu"); var raw = fields["_fpuRegisters"]!.Deserialize<Float80Value[]>(StateValues.Json)!;
            raw[0] = Float80Value.One; fields["_fpuRegisters"] = JsonSerializer.SerializeToNode(raw, StateValues.Json);
        });
        foreach (int register in Enumerable.Range(0, 8))
            Add("held GPR " + register, "all", state => RegisterValue(state, register));
        Add("held CS selector", "all", state =>
        {
            var fields = Fields(state, "cpu"); var segments = fields["_segments"]!.Deserialize<ushort[]>(StateValues.Json)!;
            segments[(int)Segment.Cs] = 0; fields["_segments"] = JsonSerializer.SerializeToNode(segments, StateValues.Json);
        });
        foreach (string field in new[] { "base", "flags" }) Add("held CS " + field, "all", state => CsCache(state, field));
        foreach (string table in new[] { "Gdtr", "Idtr" })
            Add("held " + table, "all", state => Fields(state, "cpu")[table] = JsonSerializer.SerializeToNode(new DescriptorTableRegister(0x3ff, 0), StateValues.Json));
        foreach (string phase in new[] { "all", "cpu", "ready" })
            Add("held TSC epoch " + phase, phase, state => Fields(state, "cpu")["_tscEpochTicks"] = Fields(state, "cpu")["_tscEpochTicks"]!.GetValue<ulong>() ^ 1UL);
        Add("held coherent32MiB DRB decoder", "all", state => { Config(state, "hostbridge", 0x64, 8); Fields(state, "memory")["DecodedRamBytes"] = 32 * 1048576; });
        Add("held PAM shadow target", "all", state => Config(state, "hostbridge", 0x59, 0x30));
        Add("held PCI MAE", "all", state => Config(state, "hostbridge", 4, 4));
        Add("held coherent HEN hole", "all", state => { Config(state, "hostbridge", 0x57, 0x41); Fields(state, "memory")["_ramHoleStart"] = 0x80000; Fields(state, "memory")["_ramHoleLength"] = 0x20000; });
        Add("held PIRQ route", "all", state => Config(state, "piix-irq", 0x60, 0x0a));
        Add("held MIRQ route", "all", state => Config(state, "piix-irq", 0x70, 0x0b));
        Add("held coherent ELCR/PIC level", "all", state => { Fields(state, "piix-irq")["_elcr"] = 0x400; Fields(state, "pic/_slave")["LevelMask"] = 4; });
        Add("held IDE IOSE", "all", state => Config(state, "piix-ide", 4, 3));
        Add("held IDE BAR", "all", state => Config(state, "piix-ide", 0x21, 0xc0));
        Add("held IDETIM", "all", state => Config(state, "piix-ide", 0x41, 0x80));
        Add("held PCI address latch", "all", state => Fields(state, "pci")["_address"] = 0x80000000u);
        Add("early keyboard release", "all", state => { Fields(state, "keyboard")["_enabled"] = true; Fields(state, "keyboard")["_scanning"] = true; Fields(state, "keyboard")["_commandByte"] = 0x45; });
        Add("early keyboard scanning release", "all", state => Fields(state, "keyboard")["_scanning"] = true);
        Add("early floppy release with real result callback shape", "all", state =>
        {
            var fields = Fields(state, "floppy"); fields["_dor"] = 0x0c; fields["_executing"] = true;
            fields["_event"] = AddEvent(state, "floppy", "event0", state["scheduler"]!["now"]!.GetValue<ulong>() + 1_000_000);
        });
        Add("early primary ATA SRST release", "all", state => { var fields = Fields(state, "ata0/_drives/0"); fields["_resetActive"] = false; fields["_control"] = 0; fields["Status"] = 0x50; });
        Add("early SB16 release with readiness callback shape", "all", state =>
        {
            var fields = Fields(state, "sb16"); fields["_resetHigh"] = false;
            fields["_resetEvent"] = AddEvent(state, "sb16", "event1", state["scheduler"]!["now"]!.GetValue<ulong>() + 100_000);
        });
        foreach (string phase in new[] { "all", "cpu", "ready" })
            Add("held coherent old PIT programming " + phase, phase, state =>
            {
                var fields = Fields(state, "pit"); ulong now = state["scheduler"]!["now"]!.GetValue<ulong>();
                fields["_resetOutputLow"] = false; fields["_running"] = true; fields["_reload"] = 16;
                fields["_mode"] = 2; fields["_low"] = 16; fields["_start"] = now; fields["_remainder"] = 16_000_000_000UL % 1_193_182;
                fields["_event"] = AddEvent(state, "pit", "event0", now + 16_000_000_000UL / 1_193_182);
            });
        foreach (var item in cases)
            test("M4 PIIX reset import rejects before own media: " + item.Name, () =>
            {
                using var fixture = new Fixture(item.Phase); string saved = fixture.Save("bad.dossav");
                Edit(saved, item.Mutate); Reject(fixture, saved);
            });
        test("M4 PIIX reset import rejects early SB2 release before own media", () =>
        {
            using var fixture = new Fixture("all", sb2: true); string saved = fixture.Save("bad.dossav");
            Edit(saved, state =>
            {
                var fields = Fields(state, "sb2"); fields["_resetHigh"] = false;
                fields["_resetEvent"] = AddEvent(state, "sb2", "event1", state["scheduler"]!["now"]!.GetValue<ulong>() + 100_000);
            });
            Reject(fixture, saved);
        });
        foreach (string phase in new[] { "pending", "all", "cpu", "ready" }) foreach (bool writable in new[] { false, true })
            test($"M4 PIIX reset import preserves genuine {phase} on own writable={writable} media via direct and owner paths", () =>
            {
                using var fixture = new Fixture(phase, writable); string saved = fixture.Save("good.dossav");
                string cut = State(fixture.Source); byte[] ram = fixture.Source.Memory.Ram.ToArray();
                using var direct = SnapshotStore.Load(saved, fixture.PathFor("direct.dosebranch"));
                Check(State(direct) == cut && direct.Memory.Ram.AsSpan().SequenceEqual(ram));
                using var session = new MachineSession(fixture.Owner); long revision = session.Revision, generation = session.InputGeneration;
                var request = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = fixture.PathFor("owner.dosebranch") }));
                session.PumpCommands(); Check(request.GetAwaiter().GetResult().Success && session.Revision == revision + 1 && session.InputGeneration == generation + 1);
                Check(State(session.Machine) == cut && session.Machine.Memory.Ram.AsSpan().SequenceEqual(ram));
                ulong future = fixture.Source.Scheduler.Now + 3_000_000;
                foreach (var machine in new[] { fixture.Source, direct, session.Machine })
                { machine.RunSteps(6, future); machine.Audio.AdvanceTo(machine.Scheduler.Now); Check(machine.Fault is null); }
                Same(fixture.Source, direct); Same(fixture.Source, session.Machine);
            });
        foreach (string phase in new[] { "all", "cpu", "ready" })
            test("M4 PIIX reset import permits hard reset already preceded by both-channel SRST high " + phase, () =>
            {
                using var fixture = new Fixture(phase, srstHigh: true); string saved = fixture.Save("previous-srst.dossav");
                using var restored = SnapshotStore.Load(saved, fixture.PathFor("previous-srst.dosebranch")); Same(fixture.Source, restored);
                ulong future = fixture.Source.Scheduler.Now + 3_000_000;
                fixture.Source.RunSteps(6, future); restored.RunSteps(6, future);
                fixture.Source.Audio.AdvanceTo(fixture.Source.Scheduler.Now); restored.Audio.AdvanceTo(restored.Scheduler.Now); Same(fixture.Source, restored);
            });
        foreach (string operation in new[] { "release-pci-isa", "release-cpu-ready" })
            test("M4 PIIX reset import permits inert canceled historical callback " + operation, () =>
            {
                using var fixture = new Fixture("idle"); string saved = fixture.Save("canceled.dossav"); long token = 0;
                Edit(saved, state => token = AddEvent(state, "piix-reset", operation, state["scheduler"]!["now"]!.GetValue<ulong>() + 1000, true));
                using var restored = SnapshotStore.Load(saved, fixture.PathFor("canceled.dosebranch"));
                var state = restored.CaptureHardwareState(); Check(state.Scheduler.Cancelled.Contains(token) && state.Scheduler.Events.Any(row => row.Id == token));
                long resets = restored.HardwareResetCount; restored.Scheduler.AdvanceTo(restored.Scheduler.Now + 2000); restored.Audio.AdvanceTo(restored.Scheduler.Now);
                Check(restored.PiixReset!.Phase == PiixResetPhase.Idle && restored.HardwareResetCount == resets && !restored.PiixReset.CpuResetHeld);
            });
    }
}