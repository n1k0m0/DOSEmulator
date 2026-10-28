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

/// <summary>Whole-container original-Trio64 CPU-transfer futures and semantic rejection before opening any own medium.</summary>
internal static class Windows95S3EngineImportTests
{
    /// <summary>Requires a real state, transferred byte, owner, or disk-boundary invariant.</summary>
    private static void Check(bool value, string why = "Original Trio64 engine import invariant failed.")
    { if (!value) throw new Exception(why); }

    /// <summary>Complete explicit engine schema, used to check absence on earlier adapters and held-reset domains.</summary>
    private static readonly string[] EngineFields =
    [
        "_trioEngineWords", "_trioEngineColors", "_trioEngineActiveWords", "_trioEngineActiveColors",
        "_trioEngineActive", "_trioEngineBlocked", "_trioEnginePixelIndex", "_trioEngineInputData",
        "_trioEnginePixelData", "_trioEngineInputBytes", "_trioEnginePixelBytes", "_trioEngineReadAddress",
        "_trioEngineReadLatch", "_trioEngineReadValid"
    ];

    /// <summary>Complete five-field hardware cursor image, including its pending stack position and committed coordinates.</summary>
    private static readonly string[] CursorFields =
    ["_trioCursorForeground", "_trioCursorBackground", "_trioCursorColorIndex", "_trioCursorOriginX", "_trioCursorOriginY"];

    /// <summary>Programs a private nonhalting RAM loop; no firmware, guest OS or external endpoint is started.</summary>
    private static void Loop(PcMachine machine)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.SetSegment(Segment.Ss, 0); machine.Cpu.SetSegment(Segment.Ds, 0);
        machine.Cpu.SetRegister(Register.Sp, 0x9000); machine.Cpu.Eflags = 2; machine.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(machine.Memory.Ram, 0x1000);
        Check(machine.RunSteps(2) == 2 && machine.Fault is null);
    }

    /// <summary>Contains only a newly owned immutable disk, a fresh source PC and an independent running old owner.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Unique verified private directory; no personal or original medium is consulted.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,
            "artifacts/tests/win95-s3-engine-import-" + Guid.NewGuid().ToString("N")));
        /// <summary>The machine whose actual hardware state is captured.</summary>
        public readonly PcMachine Source;
        /// <summary>The independent currently owned machine which every rejection must preserve.</summary>
        public readonly PcMachine Owner;

        /// <summary>Builds the exact limited S3 board or an unchanged legacy adapter for negative controls.</summary>
        public Fixture(string cut)
        {
            Directory.CreateDirectory(Root);
            MachineProfile profile = cut == "legacy" ? new() : new()
            {
                Id = MachineProfile.S3Trio64ProfileId, Cpu = MachineProfile.P54cCpuId,
                CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId
            };
            byte[] bytes = Enumerable.Range(0, 128 * 512).Select(i => unchecked((byte)(i * 13 + 0x27))).ToArray();
            Source = PcMachine.CreateWithDisks(profile, [new MemoryDisk(bytes, "Own S3 import source"), null, null, null]);
            Owner = PcMachine.CreateWithDisks(new(), [new MemoryDisk(new byte[128 * 512], "Own old S3 import owner"), null, null, null]);
            Loop(Source); Loop(Owner); Owner.Memory.Ram[^1] = 0xa6; Source.Memory.Ram[^1] = 0x5b;
            if (cut is "active16" or "partial-byte" or "read-latch") PrepareTransfer(Source, cut);
            if (cut is "cursor-color1" or "cursor-pending") PrepareCursor(Source, cut);
            if (cut == "held")
            {
                Source.Ports.Write(0xcf9, 6, 1);
                Check(Source.RunSteps(1, Source.Scheduler.Now) == 0 && Source.CpuResetHeld &&
                    Source.PiixReset!.Phase == PiixResetPhase.AllAsserted, "The source did not enter its real PCI reset domain.");
            }
        }

        /// <summary>Returns only a path inside this uniquely owned fixture.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Captures a real complete .dossav; the immutable own disk becomes a private sealed RAW basis.</summary>
        public string Save()
        { string saved = PathFor("source.dossav"); SnapshotStore.Capture(Source, saved); return saved; }
        /// <summary>Disposes both own machines and removes only the verified newly created directory.</summary>
        public void Dispose()
        {
            Source.Dispose(); Owner.Dispose();
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("win95-s3-engine-import-", StringComparison.Ordinal));
            Directory.Delete(Root, true);
        }
    }

    /// <summary>Writes one indexed CRTC register through the actual fixed PC port mappings.</summary>
    private static void Cr(PcMachine machine, byte index, byte value)
    { machine.Ports.Write(0x3d4, index, 1); machine.Ports.Write(0x3d5, value, 1); }
    /// <summary>Writes one actual sixteen-bit enhanced-engine register.</summary>
    private static void Word(PcMachine machine, ushort port, ushort value) => machine.Ports.Write(port, value, 2);

    /// <summary>Uses actual PCI/POS wake and original CRTC keys before accessing the card's enhanced resources.</summary>
    private static void Wake(PcMachine machine)
    {
        machine.Ports.Write(0xcf8, 0x80001804, 4); machine.Ports.Write(0xcfc, 3, 2);
        machine.Ports.Write(0x46e8, 0x10, 1); machine.Ports.Write(0x102, 1, 1); machine.Ports.Write(0x46e8, 8, 1);
        machine.Ports.Write(0x3c2, 0x67, 1); Cr(machine, 0x38, 0x48); Cr(machine, 0x39, 0xa5);
    }
    /// <summary>Leaves an actual width17/height2 monochrome CPU transfer open after its first sixteen pixels.</summary>
    private static void PrepareTransfer(PcMachine machine, string cut)
    {
        Wake(machine);
        Cr(machine, 0x40, 1); Cr(machine, 0x31, 8); Cr(machine, 0x3a, 0x10); Cr(machine, 0x50, 0x40);
        Word(machine, 0x4ae8, 1); Word(machine, 0x42e8, 0x4000);
        Word(machine, 0xaae8, 0xffff); Word(machine, 0xaee8, 0xffff); Word(machine, 0xbae8, 0x0027);
        Word(machine, 0xb6e8, 7); Word(machine, 0xa6e8, 0x005a); Word(machine, 0xa2e8, 0x0013);
        foreach (ushort value in new ushort[] { 0x1000, 0x2000, 0x3fff, 0x4fff, 0xa080 }) Word(machine, 0xbee8, value);
        Word(machine, 0x86e8, 3); Word(machine, 0x82e8, 2); Word(machine, 0x96e8, 16); Word(machine, 0xbee8, 1);
        Word(machine, 0x9ae8, 0x53b3); Word(machine, 0xe2e8, 0xa15c);
        Check(machine.Ports.Read(0x9ae8, 2) == 0x0600, "The real open CPU-transfer command was not busy.");
        if (cut is "partial-byte" or "read-latch") Cr(machine, 0x53, 0x10);
        if (cut == "partial-byte") machine.Memory.WriteByte(0xa0000, 0xf0);
        if (cut == "read-latch") Check(machine.Memory.ReadByte(0xa9ae8) == 0, "Low GP_STAT byte differs from the actual busy word.");
        var fields = machine.Video.SaveState().Fields;
        Check(fields["_trioEngineActive"].Deserialize<bool>(StateValues.Json) &&
            fields["_trioEnginePixelIndex"].Deserialize<int>(StateValues.Json) == 16,
            "The own fixture did not retain the actual sixteen-pixel transfer cursor.");
        if (cut == "partial-byte") Check(fields["_trioEngineInputBytes"].Deserialize<byte>(StateValues.Json) == 1);
        if (cut == "read-latch") Check(fields["_trioEngineReadValid"].Deserialize<bool>(StateValues.Json));
    }

    /// <summary>Captures a true partial shared color stack or pending coordinates distinct from the previously committed pair.</summary>
    private static void PrepareCursor(PcMachine machine, string cut)
    {
        Wake(machine); Cr(machine, 0x45, 1);
        machine.Ports.Write(0x3d4, 0x45, 1); Check((machine.Ports.Read(0x3d5, 1) & 1) != 0);
        if (cut == "cursor-color1")
        {
            Cr(machine, 0x4a, 0x17);
            Check(machine.Video.TrioCursorColorIndex == 1 &&
                machine.Video.SaveState().Fields["_trioCursorForeground"].Deserialize<byte[]>(StateValues.Json)!.SequenceEqual(new byte[] { 0x17, 0, 0 }));
            return;
        }
        Cr(machine, 0x46, 0); Cr(machine, 0x47, 9); Cr(machine, 0x48, 0);
        Check(machine.Video.TrioCursorOriginX == 9 && machine.Video.TrioCursorOriginY == 0,
            "An unchanged CR48 high byte did not latch the actual pending pair.");
        Cr(machine, 0x49, 7); Cr(machine, 0x47, 15);
        Check(machine.Video.TrioCursorOriginX == 9 && machine.Video.TrioCursorOriginY == 0,
            "Pending low coordinate writes incorrectly moved the registered cursor.");
    }

    /// <summary>Serializes all explicit CPU/device/scheduler values without masking diagnostics or caches.</summary>
    private static string State(PcMachine machine) => JsonSerializer.Serialize(machine.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares every component field and every installed RAM byte after each actual future access.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) &&
        a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Original and restored complete engine futures differ.");
    /// <summary>Returns only the actual saved video field map.</summary>
    private static JsonObject Fields(JsonObject state) => state["components"]!["video"]!["fields"]!.AsObject();

    /// <summary>Rehashes only a new own semantic state so rejection cannot be attributed to payload corruption.</summary>
    private static void Edit(string saved, Action<JsonObject> mutate)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        mutate(state); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }

    /// <summary>Changes exactly one otherwise well-formed saved engine value.</summary>
    private static void Nonzero(JsonObject state, string name)
    {
        var fields = Fields(state);
        if (name is "_trioCursorForeground" or "_trioCursorBackground")
        {
            byte[] colors = fields[name]!.Deserialize<byte[]>(StateValues.Json)!; Check(colors.Length == 3);
            colors[0] = 1; fields[name] = JsonSerializer.SerializeToNode(colors, StateValues.Json);
        }
        else if (fields[name] is JsonArray array) array[0] = 1;
        else if (name is "_trioEngineActive" or "_trioEngineBlocked" or "_trioEngineReadValid") fields[name] = true;
        else fields[name] = 1;
    }

    /// <summary>Locks only the captured own basis, proves semantic rejection precedes its open, and preserves the entire old owner.</summary>
    private static void Reject(Fixture fixture, string saved)
    {
        PcMachine owner = fixture.Owner; string before = State(owner), sourceBefore = State(fixture.Source);
        byte[] ram = owner.Memory.Ram.ToArray(); string direct = fixture.PathFor("direct.dosebranch"), ipc = fixture.PathFor("ipc.dosebranch");
        using var locked = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false;
        try { using var wrong = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(direct) && State(owner) == before && owner.Memory.Ram.AsSpan().SequenceEqual(ram),
            "Engine corruption reached denied media or changed the old running owner.");
        using var session = new MachineSession(owner); long revision = session.Revision, generation = session.InputGeneration;
        var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = ipc }));
        session.PumpCommands(); var response = pending.GetAwaiter().GetResult();
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, owner) &&
            session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(ipc));
        var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping", new { })); session.PumpCommands();
        Check(ping.GetAwaiter().GetResult().Success && State(owner) == before && State(fixture.Source) == sourceBefore &&
            owner.Memory.Ram.AsSpan().SequenceEqual(ram));
        long instructions = owner.Cpu.Instructions;
        Check(owner.RunSteps(3) == 3 && owner.Cpu.Instructions == instructions + 3 && owner.Fault is null,
            "The old owner stopped after engine import rejection.");
    }

    /// <summary>Registers exact shape/reset-domain corruptions and positive genuine partial-transfer round trips.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (string name in new[] { "_trioEngineWords", "_trioEngineColors", "_trioEngineActiveWords", "_trioEngineActiveColors" })
        foreach (string shape in new[] { "null", "short", "long" })
            test("M4 Win95 S3 engine early container rejection " + name + " " + shape, () =>
            {
                using var f = new Fixture("idle"); string saved = f.Save();
                Edit(saved, state =>
                {
                    var fields = Fields(state);
                    if (shape == "null") fields[name] = null;
                    else if (shape == "short") fields[name]!.AsArray().RemoveAt(0);
                    else fields[name]!.AsArray().Add(0);
                });
                Reject(f, saved);
            });
        foreach (string cut in new[] { "legacy", "held" })
        foreach (string name in EngineFields)
            test("M4 Win95 S3 engine absent or reset-held container " + cut + " " + name, () =>
            {
                using var f = new Fixture(cut); string saved = f.Save(); Edit(saved, state => Nonzero(state, name)); Reject(f, saved);
            });
        foreach (var item in new[] { (Name: "_trioEngineInputBytes", Value: 5), (Name: "_trioEnginePixelBytes", Value: 5), (Name: "_trioEnginePixelIndex", Value: -1) })
            test("M4 Win95 S3 engine impossible live transfer cursor " + item.Name, () =>
            {
                using var f = new Fixture("active16"); string saved = f.Save();
                Edit(saved, state => Fields(state)[item.Name] = item.Value); Reject(f, saved);
            });
        test("M4 Win95 S3 engine rejects unreachable partial monochrome group before media opening", () =>
        {
            using var f = new Fixture("active16"); string saved = f.Save();
            Edit(saved, state => Fields(state)["_trioEnginePixelIndex"] = 15); Reject(f, saved);
        });
        foreach (string name in new[] { "_trioEngineActiveWords", "_trioEngineInputBytes" })
            test("M4 Win95 S3 engine missing required live cursor " + name, () =>
            {
                using var f = new Fixture("active16"); string saved = f.Save();
                Edit(saved, state => Fields(state).Remove(name)); Reject(f, saved);
            });
        foreach (string name in new[] { "_trioCursorForeground", "_trioCursorBackground" })
        foreach (string shape in new[] { "null", "short", "long" })
            test("M4 Win95 S3 cursor early container shape " + name + " " + shape, () =>
            {
                using var f = new Fixture("idle"); string saved = f.Save();
                Edit(saved, state =>
                {
                    var fields = Fields(state);
                    if (shape == "null") fields[name] = null;
                    else
                    {
                        byte[] bytes = fields[name]!.Deserialize<byte[]>(StateValues.Json)!;
                        byte[] changed = shape == "short" ? bytes[1..] : bytes.Append((byte)0).ToArray();
                        fields[name] = JsonSerializer.SerializeToNode(changed, StateValues.Json);
                    }
                });
                Reject(f, saved);
            });
        foreach (string cut in new[] { "legacy", "held" })
        foreach (string name in CursorFields)
            test("M4 Win95 S3 cursor absent or reset-held container " + cut + " " + name, () =>
            {
                using var f = new Fixture(cut); string saved = f.Save(); Edit(saved, state => Nonzero(state, name)); Reject(f, saved);
            });
        foreach (var item in new[] { (Name: "_trioCursorColorIndex", Value: 3), (Name: "_trioCursorOriginX", Value: 2048), (Name: "_trioCursorOriginY", Value: 2048) })
            test("M4 Win95 S3 cursor impossible saved range " + item.Name, () =>
            {
                using var f = new Fixture("cursor-color1"); string saved = f.Save();
                Edit(saved, state => Fields(state)[item.Name] = item.Value); Reject(f, saved);
            });
        test("M4 Win95 S3 cursor rejects mismatched registered high Y before media opening", () =>
        {
            using var f = new Fixture("cursor-pending"); string saved = f.Save();
            Edit(saved, state => Fields(state)["_trioCursorOriginY"] = 256); Reject(f, saved);
        });
        test("M4 Win95 S3 cursor rejects duplicate canonical color store before media opening", () =>
        {
            using var f = new Fixture("cursor-color1"); string saved = f.Save();
            Edit(saved, state =>
            {
                var fields = Fields(state); byte[] crtc = fields["_crtc"]!.Deserialize<byte[]>(StateValues.Json)!;
                crtc[0x4a] = 1; fields["_crtc"] = JsonSerializer.SerializeToNode(crtc, StateValues.Json);
            });
            Reject(f, saved);
        });
        foreach (string cut in new[] { "cursor-color1", "cursor-pending" })
            test("M4 Win95 S3 cursor complete container future " + cut, () =>
            {
                using var f = new Fixture(cut); string saved = f.Save();
                using var restored = SnapshotStore.Load(saved, f.PathFor("cursor.dosebranch")); Same(f.Source, restored);
                if (cut == "cursor-color1")
                {
                    foreach (byte color in new byte[] { 0x22, 0x33 })
                    { Cr(f.Source, 0x4a, color); Cr(restored, 0x4a, color); Same(f.Source, restored); }
                    Check(f.Source.Video.TrioCursorColorIndex == 0 && restored.Video.TrioCursorColorIndex == 0);
                    byte[] foreground = f.Source.Video.SaveState().Fields["_trioCursorForeground"].Deserialize<byte[]>(StateValues.Json)!;
                    Check(foreground.SequenceEqual(new byte[] { 0x17, 0x22, 0x33 }), "The saved partial color stack was not actually completed.");
                    foreach (var side in new[] { f.Source, restored })
                    { side.Ports.Write(0x3d4, 0x45, 1); Check((side.Ports.Read(0x3d5, 1) & 1) != 0); Cr(side, 0x4b, 0x2a); }
                    Same(f.Source, restored);
                    Check(f.Source.Video.TrioCursorColorIndex == 1 &&
                        f.Source.Video.SaveState().Fields["_trioCursorBackground"].Deserialize<byte[]>(StateValues.Json)![0] == 0x2a);
                }
                else
                {
                    foreach (var side in new[] { f.Source, restored }) Cr(side, 0x48, 0);
                    Check(f.Source.Video.TrioCursorOriginX == 15 && f.Source.Video.TrioCursorOriginY == 7 &&
                        restored.Video.TrioCursorOriginX == 15 && restored.Video.TrioCursorOriginY == 7);
                    Same(f.Source, restored);
                    foreach (var side in new[] { f.Source, restored })
                    { Cr(side, 0x46, 1); Cr(side, 0x47, 2); Cr(side, 0x49, 3); }
                    Same(f.Source, restored);
                    Check(f.Source.Video.TrioCursorOriginX == 15 && f.Source.Video.TrioCursorOriginY == 7);
                    foreach (var side in new[] { f.Source, restored }) Cr(side, 0x48, 1);
                    Check(f.Source.Video.TrioCursorOriginX == 258 && f.Source.Video.TrioCursorOriginY == 259 &&
                        restored.Video.TrioCursorOriginX == 258 && restored.Video.TrioCursorOriginY == 259);
                    Same(f.Source, restored);
                }
            });
        foreach (string cut in new[] { "active16", "partial-byte", "read-latch" })
            test("M4 Win95 S3 engine complete container future " + cut, () =>
            {
                using var f = new Fixture(cut); string saved = f.Save();
                using var restored = SnapshotStore.Load(saved, f.PathFor("resume.dosebranch")); Same(f.Source, restored);
                if (cut == "read-latch")
                {
                    byte a = f.Source.Memory.ReadByte(0xa9ae9), b = restored.Memory.ReadByte(0xa9ae9);
                    Check(a == 6 && b == 6, "The actual pending high GP_STAT byte was lost."); Same(f.Source, restored);
                }
                if (cut == "partial-byte")
                {
                    f.Source.Memory.WriteByte(0xa0001, 0x5a); restored.Memory.WriteByte(0xa0001, 0x5a); Same(f.Source, restored);
                }
                int words = cut == "partial-byte" ? 2 : 3;
                for (int i = 0; i < words; i++)
                {
                    Word(f.Source, 0xe2e8, (ushort)(0x9c31 + i * 0x123)); Word(restored, 0xe2e8, (ushort)(0x9c31 + i * 0x123));
                    Same(f.Source, restored);
                }
                Check((f.Source.Ports.Read(0x9ae8, 2) & 0x0200) == 0 && (restored.Ports.Read(0x9ae8, 2) & 0x0200) == 0,
                    "The original CPU transfer did not actually complete.");
                Same(f.Source, restored);
            });
    }
}
