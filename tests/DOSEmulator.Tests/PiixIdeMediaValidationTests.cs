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

/// <summary>Actual rehashed containers bind active ATA phases to sealed slot, write permission and capacity, without rejecting genuine future IDNF or terminal state.</summary>
internal static class PiixIdeMediaValidationTests
{
    /// <summary>Selects only the explicit fixed IDE validation board.</summary>
    private static MachineProfile Profile() => new() { Id = MachineProfile.PiixIdeProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32 };
    /// <summary>Requires an exact semantic, media or guest-execution result.</summary>
    private static void Check(bool value, string why = "IDE sealed-media validation invariant failed.") { if (!value) throw new Exception(why); }
    /// <summary>Own files and a real PC, with either an immutable memory medium or one fresh writable RAW/COW slot.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Verified unique own test directory.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/piix-ide-media-" + Guid.NewGuid().ToString("N")));
        /// <summary>Actual factory-created complete PC.</summary>
        public readonly PcMachine Machine;
        /// <summary>True only for the own eight-sector RAW plus COW fixture.</summary>
        public readonly bool Writable;
        /// <summary>Constructs only own media; the in-memory case has exactly128 sectors and an absent slave.</summary>
        public Fixture(bool writable = false)
        {
            Writable = writable; Directory.CreateDirectory(Root);
            if (writable)
            {
                byte[] bytes = new byte[8 * 512]; bytes.AsSpan(7 * 512, 512).Fill(0x6d); File.WriteAllBytes(PathFor("base.img"), bytes);
                Machine = MachineFactory.FromDefinition(new() { Hardware = Profile(), HardDisks = [new(0, PathFor("base.img"), PathFor("active.cow"))] });
            }
            else Machine = PcMachine.CreateWithDisks(Profile(), [new MemoryDisk(new byte[128 * 512], "Own128-sector readonly fixture"), null, null, null]);
            Machine.Cpu.SetSegment(Segment.Cs, 0); Machine.Cpu.SetSegment(Segment.Ss, 0); Machine.Cpu.SetSegment(Segment.Ds, 0);
            Machine.Cpu.SetRegister(Register.Sp, 0x9000); Machine.Cpu.Eflags = 2; Machine.Cpu.Eip = 0x1000;
            new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(Machine.Memory.Ram, 0x1000); Check(Machine.RunSteps(2) == 2 && Machine.Fault is null); Enable(Machine);
        }
        /// <summary>Returns only a child of the fresh fixture directory.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Captures the actual machine and makes only its own identical RAW base independently lockable inside the container.</summary>
        public string Save(string name)
        {
            string saved = PathFor(name); SnapshotStore.Capture(Machine, saved);
            if (Writable)
            {
                string mediaPath = Path.Combine(saved, "media.dosemedia", "media.json"); var media = JsonNode.Parse(File.ReadAllBytes(mediaPath))!.AsObject();
                File.Copy(PathFor("base.img"), Path.Combine(saved, "media.dosemedia", "owned-base.img"));
                media["machine"]!["hardDisks"]![0]!["imagePath"] = "owned-base.img";
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(media, StateValues.Json); File.WriteAllBytes(mediaPath, bytes);
                string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
                manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
            }
            return saved;
        }
        /// <summary>Locates only the copied own base which has no active media handles.</summary>
        public string CopiedBase(string saved) => Path.Combine(saved, "media.dosemedia", Writable ? "owned-base.img" : "ide0.img");
        /// <summary>Disposes peers and deletes only the verified private fixture directory.</summary>
        public void Dispose()
        {
            Machine.Dispose(); string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("piix-ide-media-", StringComparison.Ordinal)); Directory.Delete(Root, true);
        }
    }
    /// <summary>Enables real ATA targets without starting a BM command, preserving real device phase independence.</summary>
    private static void Enable(PcMachine m)
    {
        m.HostBridge!.WriteConfiguration(0x64, 8, 1);
        m.Ports.Write(0xcf8, 0x80003920, 4); m.Ports.Write(0xcfc, 0xc001, 4);
        m.Ports.Write(0xcf8, 0x80003940, 4); m.Ports.Write(0xcfc, 0x80008000, 4);
        m.Ports.Write(0xcf8, 0x80003904, 4); m.Ports.Write(0xcfc, 3, 2);
    }
    /// <summary>Programs one actual primary-master LBA command.</summary>
    private static void Command(PcMachine m, byte command, uint lba, int count = 1)
    {
        m.Ports.Write(0x1f6, 0xe0 | (lba >> 24 & 15), 1); m.Ports.Write(0x1f2, (uint)(count & 255), 1); m.Ports.Write(0x1f3, lba & 255, 1);
        m.Ports.Write(0x1f4, lba >> 8 & 255, 1); m.Ports.Write(0x1f5, lba >> 16 & 255, 1); m.Ports.Write(0x1f7, command, 1);
    }
    /// <summary>Exact complete registered raw hardware and scheduler state.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares the complete raw graph and all32MiB of physical memory.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) && a.Memory.Ram.SequenceEqual(b.Memory.Ram));
    /// <summary>Mutates only own sealed state and repairs the enclosing integrity fields.</summary>
    private static void Edit(string saved, Action<JsonObject> edit)
    {
        string path = Path.Combine(saved, "state.json"); var root = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(root);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(root, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Returns exactly one registered drive/controller field map.</summary>
    private static JsonObject Fields(JsonObject state, string owner) => state["components"]![owner]!["fields"]!.AsObject();
    /// <summary>Creates a properly sorted real scheduled callback with a fresh token.</summary>
    private static long Event(JsonObject state, string owner, string operation)
    {
        var scheduler = state["scheduler"]!.AsObject(); long token = scheduler["sequence"]!.GetValue<long>() + 1; scheduler["sequence"] = token;
        var entries = scheduler["events"]!.AsArray().Select(e => e!.DeepClone()).ToList();
        entries.Add(JsonSerializer.SerializeToNode(new ScheduledStateEvent(token, scheduler["now"]!.GetValue<ulong>() + 5000, owner, operation, []), StateValues.Json)!);
        scheduler["events"] = new JsonArray(entries.OrderBy(e => e["time"]!.GetValue<ulong>()).ThenBy(e => e["id"]!.GetValue<long>()).ToArray()); return token;
    }
    /// <summary>Builds a fully coherent active command shape while leaving only media presence/permission/range impossible.</summary>
    private static void Forge(JsonObject state, int drive, bool dma, bool write, string phase, long lba = 3, int remaining = 1)
    {
        string owner = "ata0/_drives/" + drive; var fields = Fields(state, owner);
        Fields(state, "ata0")["_selected"] = drive; Fields(state, "ata0")["_dmaDriveIndex"] = dma ? drive : -1;
        for (int slot = 0; slot < 2; slot++)
        {
            var taskFields = Fields(state, "ata0/_drives/" + slot); byte[] task = taskFields["_task"]!.Deserialize<byte[]>(StateValues.Json)!;
            task[2] = (byte)remaining; task[3] = (byte)lba; task[4] = (byte)(lba >> 8); task[5] = (byte)(lba >> 16); task[6] = (byte)(0xe0u | (uint)drive << 4 | ((uint)(lba >> 24) & 15u));
            if (slot == drive) task[1] = 0; taskFields["_task"] = JsonSerializer.SerializeToNode(task, StateValues.Json);
        }
        fields["_dmaDirection"] = dma ? write ? 2 : 1 : 0; fields["_writing"] = write; fields["_identify"] = phase == "identify";
        fields["_lba"] = lba; fields["_remaining"] = remaining; fields["_offset"] = phase == "commit" ? 512 : 0;
        fields["_pendingInterrupt"] = false; fields["_resetActive"] = false; fields["Status"] = phase == "ready" ? 0x58 : 0x80;
        fields["_event"] = phase == "ready" ? 0 : Event(state, owner, dma ? phase == "commit" ? "dma-write-commit" : write ? "dma-write-ready" : "dma-read-ready" : phase == "commit" ? "event0" : phase == "identify" ? "event1" : write ? "event2" : "event3");
    }
    /// <summary>Imports with exclusive copied-base denial, checking exact early rejection and old owner/CPU usability.</summary>
    private static void Reject(Fixture f, string saved)
    {
        var m = f.Machine; string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); string branch = f.PathFor("rejected.dosebranch");
        using var locked = new FileStream(f.CopiedBase(saved), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false; try { using var wrong = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(branch) && State(m) == before && ram.SequenceEqual(m.Memory.Ram));
        using var session = new MachineSession(m); long revision = session.Revision, generation = session.InputGeneration;
        var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = branch })); session.PumpCommands(); var response = pending.GetAwaiter().GetResult();
        Check(!response.Success && ReferenceEquals(session.Machine, m) && session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(branch));
        var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping", new { })); session.PumpCommands(); Check(ping.GetAwaiter().GetResult().Success && State(m) == before && ram.SequenceEqual(m.Memory.Ram));
        long instructions = m.Cpu.Instructions; Check(m.RunSteps(3) == 3 && m.Cpu.Instructions == instructions + 3 && m.Fault is null);
    }
    /// <summary>Registers media-aware negatives and real permitted future error/terminal continuations.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool absent in new[] { false, true }) foreach (string phase in new[] { "ready", "busy", "commit" })
            test($"M4 IDE media rejects DMAwrite absent{absent}/{phase} before opening", () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, absent ? 1 : 0, true, true, phase)); Reject(f, saved);
            });
        foreach (string phase in new[] { "ready", "busy" })
            test("M4 IDE media rejects absent DMAread " + phase, () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, 1, true, false, phase)); Reject(f, saved);
            });
        foreach (var range in new (long Lba, int Remaining)[] { (8, 1), (7, 2), (0xfffffff, 256) }) foreach (string phase in new[] { "ready", "commit" })
            test($"M4 IDE media rejects writable DMA range {range.Lba}/{range.Remaining}/{phase}", () =>
            {
                using var f = new Fixture(true); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, 0, true, true, phase, range.Lba, range.Remaining)); Reject(f, saved);
            });
        foreach (long lba in new long[] { 128, 0xfffffff })
            test("M4 IDE media rejects impossible already-ready DMAread LBA " + lba, () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, 0, true, false, "ready", lba)); Reject(f, saved);
            });
        foreach (bool absent in new[] { false, true }) foreach (string phase in new[] { "ready", "busy", "commit" })
            test($"M4 IDE media rejects PIOwrite absent{absent}/{phase} before opening", () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, absent ? 1 : 0, false, true, phase)); Reject(f, saved);
            });
        foreach (string phase in new[] { "busy", "identify" })
            test("M4 IDE media rejects absent PIO read or identify callback " + phase, () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, 1, false, false, phase)); Reject(f, saved);
            });
        foreach (int drive in new[] { 0, 1 })
            test("M4 IDE media rejects orphan old PIO writecommit " + drive, () =>
            {
                using var f = new Fixture(); string saved = f.Save("bad.dossav"); Edit(saved, state => Event(state, "ata0/_drives/" + drive, "event0")); Reject(f, saved);
            });
        foreach (var range in new (long Lba, int Remaining)[] { (8, 1), (7, 2) }) foreach (string phase in new[] { "ready", "commit" })
            test($"M4 IDE media rejects writable PIO range {range.Lba}/{range.Remaining}/{phase}", () =>
            {
                using var f = new Fixture(true); string saved = f.Save("bad.dossav"); Edit(saved, state => Forge(state, 0, false, true, phase, range.Lba, range.Remaining)); Reject(f, saved);
            });
        foreach (uint lba in new uint[] { 128, 0xfffffff })
            test("M4 IDE media permits real busy DMAread then future IDNF " + lba, () =>
            {
                using var f = new Fixture(); var m = f.Machine; Command(m, 0xc8, lba); Check(m.Ata.DmaDirection == AtaDmaDirection.DeviceToMemory && m.Ata.DmaStatus == 0x80);
                string saved = f.Save("read-error.dossav"); using var b = SnapshotStore.Load(saved, f.PathFor("read-error.dosebranch")); Same(m, b);
                ulong deadline = m.Scheduler.Now + 5000; m.Scheduler.AdvanceTo(deadline); b.Scheduler.AdvanceTo(deadline); Same(m, b);
                Check(m.Ata.Status == 0x51 && m.Ports.Read(0x1f1, 1) == 0x10 && m.Ata.DmaDirection == AtaDmaDirection.None && m.Ata.ReadSectorCount == 0 && m.Ata.InterruptAsserted);
                string terminal = f.PathFor("terminal.dossav"); SnapshotStore.Capture(m, terminal); using var c = SnapshotStore.Load(terminal, f.PathFor("terminal.dosebranch")); Same(m, c);
            });
        test("M4 IDE media permits ready last-sector DMAread with later genuine IDNF", () =>
        {
            using var f = new Fixture(); var m = f.Machine; Command(m, 0xc8, 127, 2); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            Check(m.Ata.DmaReady && m.Ata.DmaLba == 127 && m.Ata.DmaRemainingSectors == 2);
            string saved = f.Save("last.dossav"); using var b = SnapshotStore.Load(saved, f.PathFor("last.dosebranch")); Same(m, b);
            foreach (var side in new[] { m, b })
            {
                side.Memory.Ram.AsSpan(0x20000, 1024).Fill(0xa5);
                new byte[] { 0, 0, 2, 0, 0, 4, 0, 0x80 }.CopyTo(side.Memory.Ram, 0x10000);
                side.Ports.Write(0xcf8, 0x80003904, 4); side.Ports.Write(0xcfc, 7, 2);
                side.Ports.Write(0xc000, 8, 1); side.Ports.Write(0xc004, 0x10000, 4); side.Ports.Write(0xc000, 9, 1);
            }
            for (int i = 0; m.Ata.DmaDirection != AtaDmaDirection.None && i < 100; i++)
            {
                Check(m.Scheduler.NextEventTime == b.Scheduler.NextEventTime && m.Scheduler.NextEventTime is not null);
                ulong deadline = m.Scheduler.NextEventTime!.Value; m.Scheduler.AdvanceTo(deadline); b.Scheduler.AdvanceTo(deadline); Same(m, b);
            }
            Check(m.Ata.DmaDirection == AtaDmaDirection.None && m.Ata.ReadSectorCount == 1 && m.Ata.Status == 0x51 && m.Ports.Read(0x1f1, 1) == 0x10);
            Check(m.Memory.Ram.AsSpan(0x20000, 512).ToArray().All(x => x == 0) && m.Memory.Ram.AsSpan(0x20200, 512).ToArray().All(x => x == 0xa5));
            Check(m.PiixIde!.GetChannelState(0).BytesTransferred == 512 && (m.PiixIde!.GetChannelState(0).Status & 7) == 5);
            string terminal = f.PathFor("end-error.dossav"); SnapshotStore.Capture(m, terminal); using var c = SnapshotStore.Load(terminal, f.PathFor("end-error.dosebranch")); Same(m, c);
        });
        foreach (bool commit in new[] { false, true })
            test("M4 IDE media permits actual last-sector DMAwrite phase commit=" + commit, () =>
            {
                using var f = new Fixture(true); var m = f.Machine; Command(m, 0xca, 7); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000); Check(m.Ata.DmaReady);
                if (commit) Check(m.Ata.WriteDma(Enumerable.Repeat((byte)0x7e, 512).ToArray()) == 512 && m.Ata.DmaOffset == 512);
                string saved = f.Save("write.dossav"); using var b = SnapshotStore.Load(saved, f.PathFor("write.dosebranch")); Same(m, b);
                if (!commit) { byte[] data = Enumerable.Repeat((byte)0x7e, 512).ToArray(); Check(m.Ata.WriteDma(data) == 512 && b.Ata.WriteDma(data) == 512); }
                ulong deadline = m.Scheduler.Now + 5000; m.Scheduler.AdvanceTo(deadline); b.Scheduler.AdvanceTo(deadline); Same(m, b);
                Check(m.Ata.WrittenSectorCount == 1 && m.Ata.DmaDirection == AtaDmaDirection.None && m.Ports.Read(0x1f3, 1) == 8 && m.Ports.Read(0x1f2, 1) == 0);
                string terminal = f.PathFor("write-end.dossav"); SnapshotStore.Capture(m, terminal); using var c = SnapshotStore.Load(terminal, f.PathFor("write-end.dosebranch")); Same(m, c);
            });
        test("M4 IDE media permits actual terminal PIOread one-past-medium", () =>
        {
            using var f = new Fixture(); var m = f.Machine; Command(m, 0x20, 127); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            for (int i = 0; i < 256; i++) Check(m.Ports.Read(0x1f0, 2) == 0);
            Check(m.Ata.ReadSectorCount == 1 && m.Ports.Read(0x1f3, 1) == 128 && m.Ata.Status == 0x50);
            string saved = f.Save("pio-read.dossav"); using var b = SnapshotStore.Load(saved, f.PathFor("pio-read.dosebranch")); Same(m, b); Check(b.RunSteps(3) == 3 && b.Fault is null);
        });
        test("M4 IDE media permits actual terminal PIOwrite retained writing flag and one-past-LBA", () =>
        {
            using var f = new Fixture(true); var m = f.Machine; Command(m, 0x30, 7); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            for (int i = 0; i < 256; i++) m.Ports.Write(0x1f0, 0x5a6b, 2); m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
            var drive = m.CaptureHardwareState().Components["ata0/_drives/0"].Fields;
            Check(drive["_writing"].GetBoolean() && drive["_remaining"].GetInt32() == 0 && drive["_lba"].GetInt64() == 8 && m.Ata.WrittenSectorCount == 1);
            string saved = f.Save("pio-write.dossav"); using var b = SnapshotStore.Load(saved, f.PathFor("pio-write.dosebranch")); Same(m, b);
            foreach (var side in new[] { m, b }) side.Ports.Write(0x1f7, 0xe7, 1);
            Same(m, b); Check(m.Ata.Status == 0x50 && m.Ata.WrittenSectorCount == 1);
        });
    }
}
