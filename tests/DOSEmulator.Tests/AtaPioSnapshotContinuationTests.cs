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

/// <summary>Real PIO continuation phases distinguish active sector requests from harmless terminal or IDENTIFY history.</summary>
internal static class AtaPioSnapshotContinuationTests
{
    /// <summary>Requires a concrete guest word, media effect, early rejection or owner-retention result.</summary>
    private static void Check(bool value, string why = "PIO continuation invariant") { if (!value) throw new Exception(why); }
    /// <summary>Own complete raw sectors with independent LBA and byte patterns.</summary>
    private static byte[] Original(long lba) => Enumerable.Range(0, 512).Select(i => unchecked((byte)(lba * 37 + i * 11 + 0x21))).ToArray();
    /// <summary>Independent expected word at a specific offset of one real read sector.</summary>
    private static uint ReadWord(long lba, int word) => (uint)(unchecked((byte)(lba * 37 + word * 22 + 0x21)) | unchecked((byte)(lba * 37 + word * 22 + 11 + 0x21)) << 8);
    /// <summary>Independent data actually written by each bounded OUT word.</summary>
    private static ushort WriteWord(int word) => unchecked((ushort)(0x7300 ^ word * 0xa3));
    /// <summary>Encodes the exact registered graph and named scheduler state.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares every explicit CPU/device field, event and raw RAM byte.</summary>
    private static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) && a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Whole PIO PC state/RAM differs");
    /// <summary>Uses a real finite RAM loop, with interrupts disabled and no OS, image or native input.</summary>
    private static void Loop(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eip = 0x1000; m.Cpu.Eflags = 2;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000); Check(m.RunSteps(2) == 2 && m.Fault is null);
    }
    /// <summary>Own eight-sector RAW/COW and a separate adopted owner, all confined to a fresh checked test root.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Unique own directory, never an existing user media or state directory.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/ata-pio-continuation-" + Guid.NewGuid().ToString("N")));
        /// <summary>Only this test's immutable original eight-sector basis.</summary>
        public string Basis => PathFor("own-eight-sector.img");
        /// <summary>Actual source PC whose controller receives real ATA commands.</summary>
        public PcMachine? Source;
        /// <summary>Independent adopted old owner used only to prove import rollback and later guest execution.</summary>
        public readonly PcMachine Owner;
        /// <summary>Creates only own patterned sectors and connects the ordinary legacy PIO factory path.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root); File.WriteAllBytes(Basis, Enumerable.Range(0, 8).SelectMany(lba => Original(lba)).ToArray());
            var disk = new CopyOnWriteDisk(Basis, PathFor("own-active.cow"));
            try
            {
                Source = PcMachine.CreateWithDisks(new() { Name = "Own PIO sector source" }, [disk, null, null, null]);
                Owner = PcMachine.CreateWithDisks(new() { Name = "Own old PIO owner" }, [new MemoryDisk(new byte[8 * 512], "Own old owner bytes"), null, null, null]);
                Loop(Source); Loop(Owner); Source.Memory.Ram[^1] = 0x6d; Owner.Memory.Ram[^1] = 0xa7;
            }
            catch { Source?.Dispose(); disk.Dispose(); throw; }
        }
        /// <summary>Names only one direct child of the fresh owned root.</summary>
        public string PathFor(string name) { Check(Path.GetFileName(name) == name); return Path.Combine(Root, name); }
        /// <summary>Retires just the capture source before an exclusive denial lock, leaving the adopted old owner active.</summary>
        public void CloseSource() { Source?.Dispose(); Source = null; }
        /// <summary>Disposes owned machines before deleting only the verified own directory.</summary>
        public void Dispose()
        {
            Source?.Dispose(); Owner.Dispose(); string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("ata-pio-continuation-", StringComparison.Ordinal)); Directory.Delete(Root, true);
        }
    }
    /// <summary>Programs a real primary-master LBA28 task file without mutating device state through test reflection.</summary>
    private static void Command(PcMachine m, byte command, uint lba = 7, byte count = 1)
    {
        m.Ports.Write(0x1f6, 0xe0 | (lba >> 24 & 15), 1); m.Ports.Write(0x1f2, count, 1); m.Ports.Write(0x1f3, lba & 255, 1);
        m.Ports.Write(0x1f4, lba >> 8 & 255, 1); m.Ports.Write(0x1f5, lba >> 16 & 255, 1); m.Ports.Write(0x1f7, command, 1);
    }
    /// <summary>Advances exactly the nominal pending PIO deadline and the complete PC audio timeline.</summary>
    private static void Advance(PcMachine m, ulong delta = 5000)
    { m.Scheduler.AdvanceTo(checked(m.Scheduler.Now + delta)); m.Audio.AdvanceTo(m.Scheduler.Now); }
    /// <summary>Exchanges a known suffix of a sector through actual sixteen-bit data-port transactions.</summary>
    private static void Words(PcMachine m, byte command, int start = 0, bool verify = true, bool written = false)
    {
        for (int i = start; i < 256; i++)
        {
            if (command == 0x30) m.Ports.Write(0x1f0, WriteWord(i), 2);
            else { uint actual = m.Ports.Read(0x1f0, 2); if (verify) Check(actual == (written ? WriteWord(i) : ReadWord(7, i)), "Actual PIO read word differs at " + i); }
        }
    }
    /// <summary>Returns exactly the real primary-master state, including harmless latched history.</summary>
    private static StateBlock Drive(PcMachine m) => m.Ata.StateChildren.Single(row => row.Name == "_drives/0").Value.SaveState();
    /// <summary>Returns exactly the sealed primary-master field map for a controlled rehash mutation.</summary>
    private static JsonObject Fields(JsonObject state) => state["components"]!["ata0/_drives/0"]!["fields"]!.AsObject();
    /// <summary>Creates a full correct container before changing only the targeted latent phase relationships and enclosing hash.</summary>
    private static void Forge(string saved, string mutation)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); var fields = Fields(state);
        if (mutation is "zeroRemaining" or "coherentZeroOnePast") fields["_remaining"] = 0;
        if (mutation is "onePast" or "coherentZeroOnePast") fields["_lba"] = 8;
        if (mutation == "identifyWrite") fields["_identify"] = true;
        if (mutation == "coherentZeroOnePast")
        {
            byte[] task = fields["_task"]!.Deserialize<byte[]>(StateValues.Json)!; task[2] = 0; task[3] = 8; task[4] = task[5] = 0;
            fields["_task"] = JsonSerializer.SerializeToNode(task, StateValues.Json);
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes); string manifestPath = Path.Combine(saved, "snapshot.json");
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Creates an authentic PIO ready or prep cut, retaining the real live event1/event2/event3 token and body.</summary>
    private static void Prepare(PcMachine m, byte command, bool ready)
    {
        Command(m, command); Check((m.Ports.Read(0x3f6, 1) & 0x88) == 0x80);
        if (ready) { Advance(m); Check((m.Ports.Read(0x3f6, 1) & 0x88) == 8); }
    }
    /// <summary>Exercises a previously accepted hostile import after real data words, making the old fault visible in the diagnostic failure message.</summary>
    private static void WitnessOrReject(Fixture f, string saved, byte command)
    {
        string branch = f.PathFor("witness.dosebranch"); PcMachine? imported = null;
        try { imported = SnapshotStore.Load(saved, branch); }
        catch (InvalidDataException) { Check(!Directory.Exists(branch)); return; }
        using (imported)
        {
            string outcome;
            try
            {
                Words(imported, command); Advance(imported);
                var fields = Drive(imported).Fields; outcome = "remaining=" + fields["_remaining"].GetInt32() + ",lba=" + fields["_lba"].GetInt64() + ",SC=" + imported.Ports.Read(0x1f2, 1).ToString("X2");
                try { imported.CaptureHardwareState(); outcome += ",still-capturable"; } catch (InvalidDataException error) { outcome += ",capture=" + error.GetType().Name; }
            }
            catch (Exception error) when (error is ArgumentOutOfRangeException or InvalidDataException or IOException)
            { outcome = error.GetType().Name + ":" + error.Message; }
            throw new Exception("Hostile full PIO import was accepted; after256real data-port words: " + outcome);
        }
    }
    /// <summary>Requires direct and owner semantic failure before any attempt to open the denied own basis.</summary>
    private static void RejectedBeforeMedia(Fixture f, string saved)
    {
        f.CloseSource(); var old = f.Owner; string before = State(old); byte[] ram = old.Memory.Ram.ToArray(); long instructions = old.Cpu.Instructions;
        string direct = f.PathFor("direct.dosebranch"), owner = f.PathFor("owner.dosebranch"); using var deny = new FileStream(f.Basis, FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false; try { using var invalid = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(direct), "PIO semantic rejection opened denied media or created a branch");
        using var session = new MachineSession(old); long revision = session.Revision, generation = session.InputGeneration;
        var task = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = owner })); session.PumpCommands(); var response = task.GetAwaiter().GetResult();
        Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, old) && session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(owner));
        var ping = session.Enqueue(IpcProtocol.CreateRequest("Ping")); session.PumpCommands(); Check(ping.GetAwaiter().GetResult().Success);
        Check(State(old) == before && old.Memory.Ram.AsSpan().SequenceEqual(ram)); Check(old.RunSteps(3) == 3 && old.Cpu.Instructions == instructions + 3 && old.Fault is null);
    }
    /// <summary>Captures one genuine last-sector phase and resumes it without normalizing any cached device state.</summary>
    private static PcMachine Restore(Fixture f, string name)
    { string saved = f.PathFor(name + ".dossav"); SnapshotStore.Capture(f.Source!, saved); var restored = SnapshotStore.Load(saved, f.PathFor(name + ".dosebranch")); Same(f.Source!, restored); return restored; }
    /// <summary>Registers narrow phase relationships and positive controls, preserving task-file and future-error semantics.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (byte command in new byte[] { 0x20, 0x30 })
            test($"ATA PIO continuation old-kernel witness rejects zero-count one-past ready {(command == 0x30 ? "WRITE" : "READ")}", () =>
            {
                using var f = new Fixture(); Prepare(f.Source!, command, true); string saved = f.PathFor("old-witness.dossav"); SnapshotStore.Capture(f.Source!, saved); Forge(saved, "coherentZeroOnePast"); WitnessOrReject(f, saved, command);
            });
        foreach (byte command in new byte[] { 0x20, 0x30 }) foreach (bool ready in new[] { false, true })
        {
            string[] mutations = command == 0x30 ? ["zeroRemaining", "onePast", "coherentZeroOnePast", "identifyWrite"] : ready ? ["zeroRemaining", "onePast", "coherentZeroOnePast"] : ["zeroRemaining", "coherentZeroOnePast"];
            foreach (string mutation in mutations)
                test($"ATA PIO continuation rejects before media command={command:X2} ready={ready} {mutation}", () =>
                {
                    using var f = new Fixture(); Prepare(f.Source!, command, ready); string saved = f.PathFor("invalid.dossav"); SnapshotStore.Capture(f.Source!, saved); Forge(saved, mutation); RejectedBeforeMedia(f, saved);
                });
        }
        foreach (byte command in new byte[] { 0x20, 0x30 }) foreach (int consumed in new[] { 0, 255 })
            test($"ATA PIO continuation preserves real last-sector DRQ and terminal one-past command={command:X2} words={consumed}", () =>
            {
                using var f = new Fixture(); var a = f.Source!; Prepare(a, command, true);
                for (int i = 0; i < consumed; i++) { if (command == 0x30) a.Ports.Write(0x1f0, WriteWord(i), 2); else Check(a.Ports.Read(0x1f0, 2) == ReadWord(7, i)); }
                var phase = Drive(a).Fields; Check(phase["_remaining"].GetInt32() == 1 && phase["_lba"].GetInt64() == 7 && phase["_offset"].GetInt32() == consumed * 2 && (phase["Status"].GetByte() & 8) != 0);
                using var b = Restore(f, "last-drq"); Words(a, command, consumed); Words(b, command, consumed); Advance(a); Advance(b); Same(a, b);
                var end = Drive(a).Fields; Check(end["_remaining"].GetInt32() == 0 && end["_lba"].GetInt64() == 8 && (end["Status"].GetByte() & 0x88) == 0 && a.Ports.Read(0x1f2, 1) == 0);
                Check(end["_offset"].GetInt32() == (command == 0x30 ? 0 : 512)); using var c = Restore(f, "terminal"); Same(a, c);
                foreach (var m in new[] { a, b, c }) { Command(m, 0x20); Advance(m); Words(m, 0x20, written: command == 0x30); }
                Same(a, b); Same(a, c); Check(File.ReadAllBytes(f.Basis).SequenceEqual(Enumerable.Range(0, 8).SelectMany(lba => Original(lba))), "Own immutable RAW basis changed");
            });
        foreach (byte command in new byte[] { 0x20, 0x30 })
            test($"ATA PIO continuation preserves actual DRQ task-register writes apart from latched transfer command={command:X2}", () =>
            {
                using var f = new Fixture(); var a = f.Source!; Prepare(a, command, true);
                a.Ports.Write(0x1f2, 0, 1); a.Ports.Write(0x1f3, 0x33, 1); a.Ports.Write(0x1f4, 0xaa, 1); a.Ports.Write(0x1f5, 0xbb, 1);
                var fields = Drive(a).Fields; Check(fields["_remaining"].GetInt32() == 1 && fields["_lba"].GetInt64() == 7 && a.Ports.Read(0x1f2, 1) == 0 && a.Ports.Read(0x1f3, 1) == 0x33);
                using var b = Restore(f, "changed-task"); Words(a, command); Words(b, command); Advance(a); Advance(b); Same(a, b);
                Check(Drive(a).Fields["_lba"].GetInt64() == 8 && Drive(a).Fields["_remaining"].GetInt32() == 0 && a.Ports.Read(0x1f3, 1) == 8);
            });
        test("ATA PIO continuation preserves a real complete last-sector write waiting for its media commit", () =>
        {
            using var f = new Fixture(); var a = f.Source!; Prepare(a, 0x30, true); Words(a, 0x30); var fields = Drive(a).Fields;
            Check(fields["_offset"].GetInt32() == 512 && fields["_remaining"].GetInt32() == 1 && fields["_lba"].GetInt64() == 7 && (fields["Status"].GetByte() & 0x88) == 0x80 && fields["_event"].GetInt64() > 0);
            using var b = Restore(f, "pending-commit"); Advance(a); Advance(b); Same(a, b); Check(a.Ata.WrittenSectorCount == 1 && a.Ports.Read(0x1f2, 1) == 0 && a.Ports.Read(0x1f3, 1) == 8);
            using var terminal = Restore(f, "committed-terminal"); Same(a, terminal);
        });
        foreach (string history in new[] { "terminal", "aborted-read" }) foreach (bool ready in new[] { false, true })
            test($"ATA PIO continuation IDENTIFY retains genuine prior LBA/count history {history} ready={ready}", () =>
            {
                using var f = new Fixture(); var a = f.Source!;
                if (history == "terminal") { Prepare(a, 0x20, true); Words(a, 0x20); }
                else { Command(a, 0x20, 9, 3); Advance(a); Check(a.Ata.Status == 0x51 && a.Ports.Read(0x1f1, 1) == 0x10); }
                long lba = history == "terminal" ? 8 : 9; int remaining = history == "terminal" ? 0 : 3;
                a.Ports.Write(0x1f7, 0xec, 1); if (ready) Advance(a);
                var fields = Drive(a).Fields; Check(fields["_identify"].GetBoolean() && !fields["_writing"].GetBoolean() && fields["_lba"].GetInt64() == lba && fields["_remaining"].GetInt32() == remaining);
                using var b = Restore(f, "identify-history"); if (!ready) { Advance(a); Advance(b); }
                for (int i = 0; i < 256; i++)
                {
                    uint actual = a.Ports.Read(0x1f0, 2); Check(actual == b.Ports.Read(0x1f0, 2));
                    if (i == 0) Check(actual == 0x0040); if (i == 49) Check(actual == 0x0200); if (i == 60) Check(actual == 8); if (i == 255) Check(actual == 0);
                }
                Same(a, b); fields = Drive(a).Fields; Check(fields["_lba"].GetInt64() == lba && fields["_remaining"].GetInt32() == remaining && fields["_offset"].GetInt32() == 512);
                using var terminal = Restore(f, "identify-terminal"); Same(a, terminal);
            });
        foreach (bool crossed in new[] { false, true })
            test("ATA PIO continuation preserves a real busy read whose later callback must produce IDNF crossed=" + crossed, () =>
            {
                using var f = new Fixture(); var a = f.Source!; Command(a, 0x20, crossed ? 7u : 8u, crossed ? (byte)2 : (byte)1);
                if (crossed) { Advance(a); Words(a, 0x20); }
                var fields = Drive(a).Fields; Check(fields["_lba"].GetInt64() == 8 && fields["_remaining"].GetInt32() == 1 && (fields["Status"].GetByte() & 0x88) == 0x80 && fields["_event"].GetInt64() > 0);
                using var b = Restore(f, "future-idnf"); Advance(a); Advance(b); Same(a, b); Check(a.Ata.Status == 0x51 && b.Ata.Status == 0x51 && a.Ports.Read(0x1f1, 1) == 0x10 && b.Ports.Read(0x1f1, 1) == 0x10);
                Check(a.Ata.ReadSectorCount == (crossed ? 1 : 0) && b.Ata.ReadSectorCount == a.Ata.ReadSectorCount); using var terminal = Restore(f, "idnf-terminal"); Same(a, terminal);
            });
    }
}
