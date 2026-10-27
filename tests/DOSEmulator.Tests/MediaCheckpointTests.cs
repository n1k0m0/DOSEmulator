// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class MediaCheckpointTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly string DirectoryPath;
        public readonly string[] Bases = new string[6];
        public readonly CopyOnWriteDisk[] Media = new CopyOnWriteDisk[6];
        public readonly PcMachine Machine;
        /// <summary>Creates six distinct own media with guarded patterns and no original OS/game bytes.</summary>
        public Fixture()
        {
            DirectoryPath = Path.Combine(Environment.CurrentDirectory, "artifacts/tests/media-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            for (int i = 0; i < 6; i++)
            {
                Bases[i] = Path.Combine(DirectoryPath, "base" + i + ".img");
                byte[] bytes = new byte[(i < 4 ? 32 : 720) * 512];
                DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(bytes);
                bytes.AsSpan(512).Fill((byte)(0x30 + i)); File.WriteAllBytes(Bases[i], bytes);
                Media[i] = new(Bases[i], Path.Combine(DirectoryPath, "active" + i + ".cow"));
                Media[i].WriteSector(2, Pattern((byte)(0x50 + i)));
            }
            Machine = PcMachine.CreateWithDisks(new() { RamMiB = 4, FloppyDriveA = "360k", FloppyDriveB = "360k" },
                Media.Take(4).ToArray(), Media[4], Media[5]);
        }
        /// <summary>Produces one new generation directory in this fixture's already existing private parent.</summary>
        public string Capture(string name = "capture") => Path.Combine(DirectoryPath, name + ".dosemedia");
        /// <summary>Produces one new independent branch destination without creating it.</summary>
        public string Branch(string name = "branch") => Path.Combine(DirectoryPath, name + ".dosebranch");
        /// <summary>Closes active media; fixture artifacts remain available for investigation.</summary>
        public void Dispose() => Machine.Dispose();
    }

    /// <summary>Fails on a semantic mismatch rather than merely checking successful serialization.</summary>
    private static void Check(bool value, string message = "Media checkpoint assertion failed.")
    { if (!value) throw new Exception(message); }
    /// <summary>Requires a documented failure before a caller-owned destination can be adopted.</summary>
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException) { return; }
        throw new Exception("Expected checkpoint rejection.");
    }
    /// <summary>Builds one independently expected sector value.</summary>
    private static byte[] Pattern(byte value) => Enumerable.Repeat(value, 512).ToArray();
    /// <summary>Compares a complete actual sector, including bytes that were inherited from the protected base.</summary>
    private static void Expect(IReadOnlyBlockDevice disk, long lba, byte value)
    { byte[] data = new byte[512]; disk.ReadSector(lba, data); Check(data.SequenceEqual(Pattern(value))); }
    /// <summary>Hashes explicitly named fixture files; this suite never enumerates or opens personal media.</summary>
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    /// <summary>Copies only a fixture's own flat checkpoint for one destructive negative test.</summary>
    private static void Clone(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
    /// <summary>Changes a bounded private manifest while preserving the generation files for independent validation.</summary>
    private static void Mutate(string directory, Action<JsonObject> action)
    {
        string path = Path.Combine(directory, "media.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); action(json);
        File.WriteAllText(path, json.ToJsonString(IpcProtocol.JsonOptions));
    }
    /// <summary>Submits an actual queued IPC operation and pumps it on this test's sole emulation owner.</summary>
    private static IpcResponse Send(MachineSession session, string command, object payload)
    {
        var task = session.Enqueue(new(1, Guid.NewGuid().ToString("N"), command, JsonSerializer.SerializeToElement(payload, IpcProtocol.JsonOptions)));
        session.PumpCommands(); return task.GetAwaiter().GetResult();
    }

    /// <summary>Registers media isolation, failure/rollback, parser, lease and owner-thread integration regressions.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("Media checkpoint: all six slots share an unchanged owner boundary", () =>
        {
            using var f = new Fixture(); f.Machine.Running = true; f.Machine.RunSteps(200);
            var before = f.Machine.GetStatus(); var cp = MediaCheckpointStore.Capture(f.Machine, f.Capture());
            var after = f.Machine.GetStatus();
            Check(cp.Media.Length == 6 && cp.VirtualNanoseconds == before.VirtualNanoseconds &&
                before == after && f.Machine.Running);
            Check(cp.Machine.HardDisks.Select(v => v.Slot).SequenceEqual(new[] { 0, 1, 2, 3 }) &&
                cp.Machine.Floppies.Select(v => v.Slot).SequenceEqual(new[] { 0, 1 }));
        });
        test("Media checkpoint: continuation writes cannot alter the sealed media point", () =>
        {
            using var f = new Fixture(); var cp = MediaCheckpointStore.Capture(f.Machine, f.Capture());
            string[] hashes = cp.Media.Select(v => Hash(Path.Combine(f.Capture(), v.JournalName!))).ToArray();
            for (int i = 0; i < 6; i++) f.Media[i].WriteSector(2, Pattern((byte)(0x80 + i)));
            var definition = MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch());
            for (int i = 0; i < 6; i++)
            {
                var item = i < 4 ? definition.HardDisks.Single(v => v.Slot == i) : definition.Floppies.Single(v => v.Slot == i - 4);
                using var old = new CopyOnWriteDisk(item.ImagePath, item.OverlayPath!);
                Expect(old, 2, (byte)(0x50 + i)); Expect(f.Media[i], 2, (byte)(0x80 + i));
                Check(Hash(Path.Combine(f.Capture(), cp.Media[i].JournalName!)) == hashes[i]);
            }
            using var branch = MachineFactory.FromDefinition(definition);
            var captured = branch.CaptureDefinition();
            Check(captured.Id == cp.Machine.Id && branch.Scheduler.Now == 0 && captured.HardDisks.Length == 4);
        });
        test("Media checkpoint: two branches have independent writable journals and inherit untouched sectors", () =>
        {
            using var f = new Fixture(); MediaCheckpointStore.Capture(f.Machine, f.Capture());
            var a = MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch("a"));
            var b = MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch("b"));
            for (int i = 0; i < 6; i++)
            {
                var x = i < 4 ? a.HardDisks[i] : a.Floppies[i - 4];
                var y = i < 4 ? b.HardDisks[i] : b.Floppies[i - 4];
                using var left = new CopyOnWriteDisk(x.ImagePath, x.OverlayPath!);
                using var right = new CopyOnWriteDisk(y.ImagePath, y.OverlayPath!);
                Expect(left, 2, (byte)(0x50 + i)); Expect(right, 2, (byte)(0x50 + i));
                left.WriteSector(2, Pattern(0x96)); right.WriteSector(3, Pattern(0x69));
                Expect(left, 2, 0x96); Expect(right, 2, (byte)(0x50 + i)); Expect(left, 3, (byte)(0x30 + i)); Expect(right, 3, 0x69);
            }
        });
        test("Media checkpoint: readonly media and IDE gaps preserve topology and never acquire overlays", () =>
        {
            using var f = new Fixture();
            using var m = PcMachine.CreateWithDisks(f.Machine.Profile, new IReadOnlyBlockDevice?[]
                { null, new ReadOnlyRawDisk(f.Bases[1]), null, new ReadOnlyRawDisk(f.Bases[3]) },
                new ReadOnlyRawDisk(f.Bases[4]), new ReadOnlyRawDisk(f.Bases[5]));
            var cp = MediaCheckpointStore.Capture(m, f.Capture());
            var def = MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch());
            Check(cp.Media.Length == 4 && cp.Media.All(v => v.JournalName is null && v.JournalBytes == 0) &&
                def.HardDisks.Select(v => v.Slot).SequenceEqual(new[] { 1, 3 }) &&
                def.HardDisks.Concat(def.Floppies).All(v => v.OverlayPath is null));
            Check(Directory.GetFiles(f.Capture()).Length == 1 && Directory.GetFiles(f.Branch()).Length == 1);
        });
        test("Media checkpoint: export preserves a bounded cache and separate append cursor", () =>
        {
            using var f = new Fixture(); string basis = Path.Combine(f.DirectoryPath, "large.img");
            using (var file = File.Create(basis)) file.SetLength(5000 * 512);
            using var disk = new CopyOnWriteDisk(basis, Path.Combine(f.DirectoryPath, "large.cow"));
            for (int i = 0; i < 4200; i++) disk.WriteSector(i, Pattern((byte)i));
            string generation = Path.Combine(f.DirectoryPath, "sealed.cow"); var info = disk.ExportGeneration(generation);
            Expect(disk, 0, 0); disk.WriteSector(4999, Pattern(0xe7)); Expect(disk, 4199, (byte)(4199 & 255));
            Check(disk.CachedSectorCount <= 4096 && info.Bytes == 48 + 4200 * 552 && Hash(generation) == info.Sha256);
            using var sealedReader = new CopyOnWriteDisk(basis, generation);
            Expect(sealedReader, 4199, (byte)(4199 & 255)); Expect(sealedReader, 4999, 0);
        });
        test("Media checkpoint: pending and disposed leases cannot export", () =>
        {
            using var f = new Fixture(); var pending = (CopyOnWriteDisk)f.Media[0].Reopen();
            string path = Path.Combine(f.DirectoryPath, "forbidden.cow");
            Reject(() => pending.ExportGeneration(path)); Check(!File.Exists(path));
            pending.Dispose(); f.Media[0].Dispose(); Reject(() => f.Media[0].ExportGeneration(path)); Check(!File.Exists(path));
        });
        test("Media checkpoint: output failure leaves the active writer usable", () =>
        {
            using var f = new Fixture(); string target = Path.Combine(f.DirectoryPath, "occupied.cow"); File.WriteAllText(target, "sentinel");
            Reject(() => f.Media[0].ExportGeneration(target)); Check(File.ReadAllText(target) == "sentinel");
            Reject(() => f.Media[0].ExportGeneration(f.Bases[0])); f.Media[0].WriteSector(2, Pattern(0x99)); Expect(f.Media[0], 2, 0x99);
        });
        test("Media checkpoint: memory media are rejected before publication", () =>
        {
            using var f = new Fixture(); using var m = PcMachine.CreateDemo();
            Reject(() => MediaCheckpointStore.Capture(m, f.Capture())); Check(!Directory.Exists(f.Capture()));
        });
        foreach (string kind in new[] { "capture-file", "capture-directory", "branch-file", "branch-directory", "bad-extension", "missing-parent" })
            test("Media checkpoint: destination protection " + kind, () =>
            {
                using var f = new Fixture(); MediaCheckpointStore.Capture(f.Machine, f.Capture("valid"));
                string target = kind.StartsWith("capture") ? f.Capture() : f.Branch();
                if (kind.EndsWith("-file")) File.WriteAllText(target, "sentinel");
                if (kind.EndsWith("-directory")) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "sentinel"), "keep"); }
                if (kind == "bad-extension") target = Path.Combine(f.DirectoryPath, "wrong.json");
                if (kind == "missing-parent") target = Path.Combine(f.DirectoryPath, "missing", "bad.dosebranch");
                Reject(() => { if (kind.StartsWith("capture")) MediaCheckpointStore.Capture(f.Machine, target);
                    else MediaCheckpointStore.CreateBranch(f.Capture("valid"), target); });
                if (kind.EndsWith("-file")) Check(File.ReadAllText(target) == "sentinel");
                if (kind.EndsWith("-directory")) Check(File.ReadAllText(Path.Combine(target, "sentinel")) == "keep");
                Expect(f.Media[0], 2, 0x50);
            });

        var mutations = new (string Name, Action<JsonObject> Change)[]
        {
            ("version", j => j["schemaVersion"] = 2),
            ("kind", j => j["kind"] = "DOSE-FULL-SNAPSHOT"),
            ("id", j => j["id"] = "invalid"),
            ("missing machine", j => j["machine"] = null),
            ("memory fixture", j => j["machine"]!["builtInTestDisk"] = true),
            ("unknown profile", j => j["machine"]!["hardware"]!["videoCard"] = "unknown"),
            ("floppy type mismatch", j => j["machine"]!["hardware"]!["floppyDriveB"] = "1440k"),
            ("missing rows", j => j["media"] = null),
            ("missing slot", j => j["media"]!.AsArray().RemoveAt(0)),
            ("duplicate row", j => j["media"]!.AsArray()[1] = j["media"]![0]!.DeepClone()),
            ("unknown bus", j => j["media"]![0]!["device"] = "scsi"),
            ("wrong slot", j => j["media"]![0]!["slot"] = 9),
            ("zero sectors", j => j["media"]![0]!["sectors"] = 0),
            ("oversized sectors", j => j["media"]![0]!["sectors"] = long.MaxValue),
            ("base digest", j => j["media"]![0]!["baseSha256"] = new string('0', 64)),
            ("lowercase digest", j => j["media"]![0]!["baseSha256"] = new string('a', 64)),
            ("journal digest", j => j["media"]![0]!["journalSha256"] = new string('0', 64)),
            ("short journal digest", j => j["media"]![0]!["journalSha256"] = "AA"),
            ("partial length", j => j["media"]![0]!["journalBytes"] = 49),
            ("traversal", j => j["media"]![0]!["journalName"] = "../ide0.cow"),
            ("other journal binding", j => j["machine"]!["hardDisks"]![0]!["overlayPath"] = "../active0.cow"),
            ("missing base", j => j["machine"]!["hardDisks"]![0]!["imagePath"] = "../missing.img"),
            ("readonly disagreement", j => j["machine"]!["hardDisks"]![0]!["overlayPath"] = null),
            ("unknown root field", j => j["fullSnapshot"] = true),
            ("unknown row field", j => j["media"]![0]!["surprise"] = 1)
        };
        foreach (var (name, change) in mutations)
            test("Media checkpoint: rejects metadata " + name, () =>
            {
                using var f = new Fixture(); MediaCheckpointStore.Capture(f.Machine, f.Capture());
                Mutate(f.Capture(), change); Reject(() => MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch()));
                Check(!Directory.Exists(f.Branch()) && !Directory.GetDirectories(f.DirectoryPath, ".*.tmp").Any());
                f.Media[0].WriteSector(3, Pattern(0x74)); Expect(f.Media[0], 3, 0x74);
            });
        foreach (string kind in new[] { "header", "header-base", "lba", "checksum", "truncated", "appended" })
            test("Media checkpoint: validates copied journal records " + kind, () =>
            {
                using var f = new Fixture(); MediaCheckpointStore.Capture(f.Machine, f.Capture());
                // The last of six generations fails after earlier files were prepared: all staged results must roll back.
                string path = Path.Combine(f.Capture(), "floppy1.cow"); byte[] data = File.ReadAllBytes(path);
                if (kind == "header") data[0] ^= 1;
                if (kind == "header-base") data[16] ^= 1;
                if (kind == "lba") { BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(48), 10000); SHA256.HashData(data.AsSpan(48, 520), data.AsSpan(568, 32)); }
                if (kind == "checksum") data[70] ^= 1;
                if (kind == "truncated") data = data[..^1];
                if (kind == "appended") data = data.Concat(new byte[] { 1 }).ToArray();
                File.WriteAllBytes(path, data);
                Mutate(f.Capture(), j => { j["media"]![5]!["journalSha256"] = Hash(path); j["media"]![5]!["journalBytes"] = data.Length; });
                Reject(() => MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch()));
                Check(!Directory.Exists(f.Branch()) && !Directory.GetDirectories(f.DirectoryPath, ".*.tmp").Any());
                Expect(f.Media[0], 2, 0x50);
            });
        foreach (string kind in new[] { "duplicate root", "duplicate nested", "oversized", "missing journal", "changed base" })
            test("Media checkpoint: rejects dependency/document " + kind, () =>
            {
                using var f = new Fixture(); MediaCheckpointStore.Capture(f.Machine, f.Capture()); string path = Path.Combine(f.Capture(), "media.json");
                if (kind == "duplicate root") File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"SchemaVersion\":1"));
                if (kind == "duplicate nested") File.WriteAllText(path, File.ReadAllText(path).Replace("\"slot\": 0", "\"slot\": 0,\"SLOT\":0"));
                if (kind == "oversized") File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
                if (kind == "missing journal") File.Delete(Path.Combine(f.Capture(), "ide0.cow"));
                if (kind == "changed base") { f.Machine.Dispose(); byte[] data = File.ReadAllBytes(f.Bases[0]); data[600] ^= 1; File.WriteAllBytes(f.Bases[0], data); }
                Reject(() => MediaCheckpointStore.CreateBranch(f.Capture(), f.Branch())); Check(!Directory.Exists(f.Branch()));
            });
        test("Media checkpoint: owner IPC distinguishes media-only and full snapshots and retains the active machine", () =>
        {
            using var f = new Fixture(); using var session = new MachineSession(f.Machine);
            var caps = JsonSerializer.SerializeToElement(Send(session, "GetCapabilities", new { }).Payload, IpcProtocol.JsonOptions);
            Check(caps.GetProperty("mediaCheckpoints").GetBoolean() && caps.GetProperty("runningStateSnapshots").GetBoolean());
            Check(Send(session, "CreateMediaCheckpoint", new { path = f.Capture() }).Success);
            Check(Send(session, "CreateMediaBranch", new { path = f.Capture(), destination = f.Branch() }).Success);
            Check(!Send(session, "CreateMediaBranch", new { path = f.Capture(), destination = f.Branch() }).Success);
            Mutate(f.Capture(), j => j["kind"] = "invalid");
            var invalid = Send(session, "CreateMediaBranch", new { path = f.Capture(), destination = f.Branch("invalid") });
            Check(!invalid.Success && invalid.ErrorCode == "ResourceUnavailable");
            Check(ReferenceEquals(session.Machine, f.Machine) && session.Revision == 1);
            Expect(f.Media[0], 2, 0x50);
        });
    }
}
