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

/// <summary>Rehashed full Trio64 containers reject before media opening while genuine intermediate control states resume.</summary>
internal static class S3Trio64ImportTests
{
    /// <summary>Selects only the fixed original-Trio64 board tuple.</summary>
    private static MachineProfile Profile() => new() { Id = MachineProfile.S3Trio64ProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId, Name = "Own S3 import fixture" };
    /// <summary>Requires an independent byte, whole-state or owner-retention result.</summary>
    private static void Check(bool condition, string why = "S3 full import invariant") { if (!condition) throw new Exception(why); }
    /// <summary>Runs a real finite private RAM loop, without booting or changing video registers.</summary>
    private static void Loop(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eflags = 2; m.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000); Check(m.RunSteps(2) == 2 && m.Fault is null);
    }
    /// <summary>Programs actual function-zero configuration for the fixed S3 BDF00:03.0.</summary>
    private static void Config(PcMachine m, int offset, uint value, int width)
    { m.Ports.Write(0xcf8, 0x80001800u | (uint)(offset & ~3), 4); m.Ports.Write((ushort)(0xcfc + (offset & 3)), value, width); }
    /// <summary>Writes one real indexed CRTC register through the active color decoder.</summary>
    private static void Crtc(PcMachine m, byte index, byte value) { m.Ports.Write(0x3d4, index, 1); m.Ports.Write(0x3d5, value, 1); }
    /// <summary>Writes one real sequencer register through the locked/unlocked S3 port path.</summary>
    private static void Seq(PcMachine m, byte index, byte value) { m.Ports.Write(0x3c4, index, 1); m.Ports.Write(0x3c5, value, 1); }
    /// <summary>Performs the actual documented setup/POS wake sequence and extension unlocks.</summary>
    private static void Wake(PcMachine m)
    {
        Config(m, 4, 3, 2); m.Ports.Write(0x46e8, 0x10, 1); m.Ports.Write(0x102, 1, 1); m.Ports.Write(0x46e8, 8, 1);
        m.Ports.Write(0x3c2, 0x67, 1); Seq(m, 0, 3); Seq(m, 1, 1); Seq(m, 2, 3); Seq(m, 3, 0); Seq(m, 4, 2);
        Crtc(m, 0x38, 0x48); Crtc(m, 0x39, 0xa5); Seq(m, 8, 6);
    }
    /// <summary>Enables an actual high two-MiB linear window while retaining the independent low CR5A bits.</summary>
    private static void Linear(PcMachine m)
    {
        Wake(m); Config(m, 0x10, 0xe0000000, 4); Crtc(m, 0x5a, 0x21); Crtc(m, 0x40, 1); Crtc(m, 0x31, 8); Crtc(m, 0x58, 0x12);
        Seq(m, 0x0a, 0x80); Seq(m, 0x15, 0x80); Seq(m, 4, 0x0e); m.Ports.Write(0x3ce, 5, 1); m.Ports.Write(0x3cf, 0, 1); m.Ports.Write(0x4ae8, 1, 2);
        Check(m.Video.TrioBarBase == 0xe0000000 && m.Video.TrioLinearBase == 0xe0200000 && m.Video.TrioLinearLength == 0x200000);
    }
    /// <summary>Captures every registered field and future event without taking an external endpoint.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Requires all registered fields and the entire32MiB RAM to agree.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) && a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Entire Trio64 state/RAM differs");
    /// <summary>Own immutable source plus independently running old owner, with no existing image or personal journal.</summary>
    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/s3-import-" + Guid.NewGuid().ToString("N")));
        public readonly PcMachine Source, Owner;
        /// <summary>Creates only fresh byte media and drives the requested real register/hold state.</summary>
        public Fixture(string mode = "idle")
        {
            Directory.CreateDirectory(Root); byte[] bytes = Enumerable.Range(0, 128 * 512).Select(i => unchecked((byte)(i * 17 + 0x31))).ToArray();
            Source = PcMachine.CreateWithDisks(Profile(), [new MemoryDisk(bytes, "Own S3 source bytes"), null, null, null]);
            Owner = PcMachine.CreateWithDisks(new(), [new MemoryDisk(new byte[128 * 512], "Own independent owner bytes"), null, null, null]);
            Loop(Source); Loop(Owner); Source.Memory.Ram[^1] = 0xa7; Owner.Memory.Ram[^1] = 0x6d;
            if (mode == "idle") return;
            if (mode == "setup") { Config(Source, 4, 3, 2); Source.Ports.Write(0x46e8, 0x10, 1); Source.Ports.Write(0x102, 1, 1); return; }
            if (mode == "held")
            {
                Wake(Source); Source.Ports.Write(0xcf9, 6, 1); Check(Source.RunSteps(1, Source.Scheduler.Now) == 0);
                Check(Source.PiixReset!.Phase == PiixResetPhase.AllAsserted && Source.CpuResetHeld); return;
            }
            Wake(Source);
            if (mode is "high" or "disabled" or "partial-dac" or "atc") Linear(Source);
            if (mode == "disabled") Config(Source, 4, 0, 2);
            if (mode == "bank-fallback") { Crtc(Source, 0x31, 9); Crtc(Source, 0x35, 3); Crtc(Source, 0x51, 4); Crtc(Source, 0x6a, 0); Check(Source.Video.TrioBank == 19); }
            if (mode == "legacy64k")
            { Crtc(Source, 0x40, 1); Crtc(Source, 0x31, 9); Crtc(Source, 0x59, 0); Crtc(Source, 0x5a, 0x0a); Crtc(Source, 0x58, 0x10); Crtc(Source, 0x6a, 31); Source.Ports.Write(0x4ae8, 1, 2); }
            if (mode == "partial-dac")
            { Source.Ports.Write(0x3c7, 7, 1); Source.Ports.Read(0x3c9, 1); Source.Ports.Write(0x3c8, 9, 1); Source.Ports.Write(0x3c9, 17, 1); }
            if (mode == "atc") { Source.Ports.Read(0x3da, 1); Source.Ports.Write(0x3c0, 0x33, 1); }
        }
        /// <summary>Names only a child within this fresh private directory.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Disposes both owned PCs before removing only the verified private root.</summary>
        public void Dispose()
        {
            Source.Dispose(); Owner.Dispose(); string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("s3-import-", StringComparison.Ordinal)); Directory.Delete(Root, true);
        }
    }
    /// <summary>Returns exactly the requested registered field map, without searching for a substitute type.</summary>
    private static JsonObject Fields(JsonObject state, string node = "video") => state["components"]![node]!["fields"]!.AsObject();
    /// <summary>Changes a known byte array while retaining the exact serialization and all other bytes.</summary>
    private static void Byte(JsonObject state, string field, int at, byte value, string node = "video")
    { var fields = Fields(state, node); byte[] bytes = fields[field]!.Deserialize<byte[]>(StateValues.Json)!; bytes[at] = value; fields[field] = JsonSerializer.SerializeToNode(bytes, StateValues.Json); }
    /// <summary>Rehashes one deliberately edited complete container so rejection must be semantic.</summary>
    private static void Edit(string saved, Action<JsonObject> mutate)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); mutate(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Injects a fresh well-shaped live timer row, requiring rejection before an impossible S3 callback can be bound.</summary>
    private static void Orphan(JsonObject state, string owner)
    {
        var scheduler = state["scheduler"]!.AsObject(); long id = scheduler["sequence"]!.GetValue<long>() + 1; scheduler["sequence"] = id;
        var rows = scheduler["events"]!.AsArray().Select(row => row!.DeepClone()).ToList(); rows.Add(JsonSerializer.SerializeToNode(new ScheduledStateEvent(id, scheduler["now"]!.GetValue<ulong>() + 1000, owner, "invented-render", []), StateValues.Json)!);
        scheduler["events"] = new JsonArray(rows.OrderBy(row => row["time"]!.GetValue<ulong>()).ThenBy(row => row["id"]!.GetValue<long>()).ToArray());
    }
    /// <summary>Proves direct and owner rejection occurs before the exclusively locked own captured base can be opened.</summary>
    private static void Reject(Fixture f, string saved)
    {
        string before = State(f.Owner), source = State(f.Source); byte[] ram = f.Owner.Memory.Ram.ToArray();
        string direct = f.PathFor("direct.dosebranch"), owner = f.PathFor("owner.dosebranch");
        using var deny = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false; try { using var invalid = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(direct), "Semantic failure reached denied media or created a branch");
        using var session = new MachineSession(f.Owner); long revision = session.Revision, generation = session.InputGeneration;
        var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = owner })); session.PumpCommands(); var response = pending.GetAwaiter().GetResult();
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, f.Owner) && session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(owner));
        var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping", new { })); session.PumpCommands(); Check(ping.GetAwaiter().GetResult().Success);
        Check(State(f.Owner) == before && State(f.Source) == source && f.Owner.Memory.Ram.AsSpan().SequenceEqual(ram)); long instructions = f.Owner.Cpu.Instructions;
        Check(f.Owner.RunSteps(3) == 3 && f.Owner.Cpu.Instructions == instructions + 3 && f.Owner.Fault is null, "The independent old owner stopped after rejected import");
    }
    /// <summary>Registers representative complete semantic rejections and independent valid intermediate controls.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        var cases = new List<(string Name, string Mode, Action<JsonObject> Change)>
        {
            ("wrong-video-model", "idle", s => Fields(s)["IsTrio64"] = false),
            ("conflicting-ET-model", "idle", s => Fields(s)["IsEt4000"] = true),
            ("missing-VRAM-plane", "idle", s => { var f = Fields(s); byte[][] p = f["_planes"]!.Deserialize<byte[][]>(StateValues.Json)!; f["_planes"] = JsonSerializer.SerializeToNode(p.Take(3).ToArray(), StateValues.Json); }),
            ("short-VRAM-plane", "idle", s => { var f = Fields(s); byte[][] p = f["_planes"]!.Deserialize<byte[][]>(StateValues.Json)!; p[2] = p[2][..^1]; f["_planes"] = JsonSerializer.SerializeToNode(p, StateValues.Json); }),
            ("wrong-CRTC-shape", "idle", s => Fields(s)["_crtc"] = JsonSerializer.SerializeToNode(new byte[127], StateValues.Json)),
            ("wrong-sequencer-shape", "idle", s => Fields(s)["_seq"] = JsonSerializer.SerializeToNode(new byte[31], StateValues.Json)),
            ("chip-ID-high", "idle", s => Byte(s, "_crtc", 0x2d, 0x89)),
            ("chip-ID-low", "idle", s => Byte(s, "_crtc", 0x2e, 0x10)),
            ("old-chip-ID", "idle", s => Byte(s, "_crtc", 0x30, 0xe0)),
            ("CR31-unimplemented-bit", "idle", s => Byte(s, "_crtc", 0x31, 0x80)),
            ("CR40-fixed-bit", "idle", s => Byte(s, "_crtc", 0x40, 0)),
            ("CR5E-unimplemented-bit", "idle", s => Byte(s, "_crtc", 0x5e, 0x80)),
            ("display-start-mask", "idle", s => Byte(s, "_crtc", 0x69, 0x80)),
            ("bank-mask", "idle", s => Byte(s, "_crtc", 0x6a, 0x40)),
            ("standard-sequencer-mask", "idle", s => Byte(s, "_seq", 4, 0x80)),
            ("PLL-latch-mask", "idle", s => Byte(s, "_seq", 0x10, 0x80)),
            ("setup-mask", "idle", s => Fields(s)["_trioSetup"] = 1),
            ("POS-mask", "idle", s => Fields(s)["_trioPos"] = 2),
            ("advanced-mask", "idle", s => Fields(s)["_trioAdvanced"] = 2),
            ("DAC-read-component", "idle", s => Fields(s)["_readComponent"] = 3),
            ("DAC-write-component", "idle", s => Fields(s)["_writeComponent"] = 3),
            ("DAC-state", "idle", s => Fields(s)["_dacState"] = 2),
            ("DAC-six-bit-value", "idle", s => Byte(s, "_dac", 1, 64)),
            ("derived-RGB", "idle", s => { var f = Fields(s); uint[] colors = f["_rgb"]!.Deserialize<uint[]>(StateValues.Json)!; colors[0] ^= 1; f["_rgb"] = JsonSerializer.SerializeToNode(colors, StateValues.Json); }),
            ("derived-mode-width", "idle", s => { var key = Fields(s)["_modeKey"]!.AsObject(); Check(key.ContainsKey("item3")); key["item3"] = key["item3"]!.GetValue<int>() + 1; }),
            ("wrapper-config-shape", "idle", s => Fields(s, "s3")["_config"] = JsonSerializer.SerializeToNode(new byte[255], StateValues.Json)),
            ("wrapper-vendor-ID", "idle", s => Byte(s, "_config", 0, 0x32, "s3")),
            ("wrapper-command-mask", "idle", s => Byte(s, "_config", 4, 0x80, "s3")),
            ("duplicate-wrapper-BAR", "idle", s => Byte(s, "_config", 0x10, 0x80, "s3")),
            ("orphan-S3-callback", "idle", s => Orphan(s, "s3")),
            ("orphan-VGA-callback", "idle", s => Orphan(s, "video")),
            ("held-PCI-command", "held", s => Byte(s, "_config", 4, 3, "s3")),
            ("held-setup", "held", s => Fields(s)["_trioSetup"] = 8),
            ("held-advanced", "held", s => Fields(s)["_trioAdvanced"] = 1),
            ("held-canonical-BAR", "held", s => Byte(s, "_crtc", 0x59, 0xe0)),
            ("held-bank-enable", "held", s => Byte(s, "_crtc", 0x31, 1)),
            ("held-sequencer-unlock", "held", s => Byte(s, "_seq", 8, 6)),
            ("held-graphics-controller", "held", s => Byte(s, "_gc", 5, 0x40)),
            ("held-partial-DAC", "held", s => Fields(s)["_writeComponent"] = 1),
            ("held-PIIX-SCE", "held", s => Byte(s, "_config", 4, 15, "piix-irq"))
        };
        foreach (var row in cases)
            test("M4 S3 full import rejects before media and preserves old owner " + row.Name, () =>
            {
                using var f = new Fixture(row.Mode); string saved = f.PathFor("invalid.dossav"); SnapshotStore.Capture(f.Source, saved); Edit(saved, row.Change); Reject(f, saved);
            });
        foreach (string latch in new[] { "_trioSetup", "_trioPos", "_trioAdvanced" })
            test("M4 S3 earlier VGA model rejects foreign chip state before media " + latch, () =>
            {
                using var f = new Fixture(); using var old = new SnapshotTests.Fixture(new(), false); string saved = old.PathFor("foreign.dossav"); SnapshotStore.Capture(old.Machine, saved);
                Edit(saved, s => Fields(s)[latch] = latch == "_trioSetup" ? 8 : 1); Reject(f, saved);
            });
        foreach (string mode in new[] { "idle", "woken", "setup", "bank-fallback", "high", "legacy64k", "disabled", "partial-dac", "atc", "unimplemented-index", "held" })
            test("M4 S3 full import accepts genuine intermediate control " + mode, () =>
            {
                using var f = new Fixture(mode); var a = f.Source;
                if (mode == "high") { a.Memory.WriteByte(0xe0200011, 0xa7); Check(a.Memory.ReadByte(0xe0200011) == 0xa7, "Actual high LFB did not own its byte"); }
                if (mode == "legacy64k") { a.Memory.WriteByte(0xa0011, 0x69); Check(a.Memory.ReadByte(0xa0011) == 0x69, "The legitimate A0000/64K exception must remain usable"); }
                if (mode == "partial-dac")
                { var fields = a.Video.SaveState().Fields; Check(fields["_readComponent"].GetByte() == 1 && fields["_writeComponent"].GetByte() == 1); }
                if (mode == "atc") Check(a.Video.SaveState().Fields["_attributeData"].GetBoolean());
                if (mode == "unimplemented-index") { a.Ports.Write(0x3d4, 0xff, 1); Check(a.Ports.Read(0x3d4, 1) == 0xff && a.Ports.Read(0x3d5, 1) == 0xff); }
                string saved = f.PathFor("valid.dossav"); SnapshotStore.Capture(a, saved); using var b = SnapshotStore.Load(saved, f.PathFor("valid.dosebranch")); Same(a, b);
                Check(a.S3!.BarBase == b.S3!.BarBase && a.S3!.LinearBase == b.S3!.LinearBase && a.S3!.LinearEnabled == b.S3!.LinearEnabled);
                if (mode is "high" or "disabled" or "partial-dac" or "atc")
                { Check(a.Video.TrioBarBase == 0xe0000000 && a.Video.TrioLinearBase == 0xe0200000 && a.Video.SaveState().Fields["_crtc"].Deserialize<byte[]>(StateValues.Json)![0x5a] == 0x21); }
                if (mode == "held")
                {
                    ulong until = a.PiixReset!.CpuReleaseAt; a.Scheduler.AdvanceTo(until); b.Scheduler.AdvanceTo(until); a.Audio.AdvanceTo(until); b.Audio.AdvanceTo(until); Same(a, b);
                    Check(a.RunSteps(1, until) == b.RunSteps(1, until)); Same(a, b); Check(!a.CpuResetHeld && a.Cpu.TimestampCounter == 0);
                }
                else
                { Check(a.RunSteps(7) == b.RunSteps(7)); Same(a, b); Check(a.Fault is null && b.Fault is null); }
            });
    }
}
