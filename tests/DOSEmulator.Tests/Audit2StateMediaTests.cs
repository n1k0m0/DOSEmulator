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

/// <summary>Rehashed hostile state rejection and transactional cross-slot floppy leases on exclusively fresh private media.</summary>
internal static class Audit2StateMediaTests
{
    /// <summary>Requires an actual byte, architectural state, owner response or lease result.</summary>
    private static void Check(bool value, string why = "Audit2 state/media invariant failed.") { if (!value) throw new Exception(why); }
    /// <summary>Encodes the entire registered graph, including all future scheduler operations.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Pumps a real owner request and leaves failures visible instead of accepting an escaped exception.</summary>
    private static IpcResponse Request(MachineSession session, string command, object? payload = null)
    { var task = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return task.GetAwaiter().GetResult(); }
    /// <summary>Changes only a private complete container and repairs its enclosing state integrity fields.</summary>
    private static void Edit(string saved, Action<JsonObject> edit)
    {
        string path = Path.Combine(saved, "state.json"); var document = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(document);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, StateValues.Json); File.WriteAllBytes(path, bytes);
        path = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Proves rejection through the production owner without replacing the old machine, then executes a valid request and guest work.</summary>
    private static void Rejected(MachineSession session, string saved, string branch)
    {
        var old = session.Machine; string before = State(old); byte[] ram = old.Memory.Ram.ToArray(); long revision = session.Revision;
        var response = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
        Check(!response.Success && ReferenceEquals(old, session.Machine) && session.Revision == revision && !Directory.Exists(branch));
        Check(State(old) == before && old.Memory.Ram.AsSpan().SequenceEqual(ram), "Rejected state changed the old CPU/RAM/devices.");
        Check(Request(session, "Ping").Success, "Owner pump did not accept the next valid request.");
        long instructions = old.Cpu.Instructions; old.RunSteps(2);
        Check(old.Fault is null && old.Cpu.Instructions == instructions + 2, "Old guest could not continue after rejection.");
    }
    /// <summary>Creates an actual decoded continuation through one guest REP element, without replacing a constructed CPU.</summary>
    private static SnapshotTests.Fixture Rep(bool code32, int width, bool move, byte[]? extra = null)
    {
        var f = new SnapshotTests.Fixture(new() { RamMiB = 4 }, false); var m = f.Machine;
        try
        {
            byte opcode = (byte)(move ? width == 1 ? 0xa4 : 0xa5 : width == 1 ? 0xaa : 0xab);
            byte[] prefix = extra ?? (width != 1 && (width == 4) != code32 ? new byte[] { 0x66 } : Array.Empty<byte>());
            byte[] code = [..prefix, 0xf3, opcode, 0x90, 0xf4];
            if (code32)
            {
                m.Memory.WriteDword(0x408, 0xffff); m.Memory.WriteDword(0x40c, 0x00cf9a00);
                m.Memory.WriteDword(0x410, 0xffff); m.Memory.WriteDword(0x414, 0x00cf9200);
                m.Memory.WriteWord(0x300, 0x17); m.Memory.WriteDword(0x302, 0x400);
                byte[] boot = [0x0f, 0x01, 0x16, 0, 3, 0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0];
                boot.CopyTo(m.Memory.Ram, 0x1000); code.CopyTo(m.Memory.Ram, 0x2000); m.RunSteps(4);
                Check(m.Cpu.ProtectedMode && m.Cpu.Eip == 0x2000);
                m.Cpu.SetSegment(Segment.Ds, 16); m.Cpu.SetSegment(Segment.Es, 16); m.Cpu.SetSegment(Segment.Ss, 16); m.Cpu.SetSegment(Segment.Fs, 16);
                m.Cpu.SetRegister(Register.Sp, 0x18000);
            }
            else { code.CopyTo(m.Memory.Ram, 0x1000); m.Cpu.Eip = 0x1000; m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetSegment(Segment.Fs, 0); }
            m.Cpu.Eflags = 2; m.Cpu.SetRegister(Register.Cx, 128); m.Cpu.SetRegister(Register.Si, 0x3000); m.Cpu.SetRegister(Register.Di, 0x5000); m.Cpu.SetRegister(Register.Ax, 0x76543210);
            for (int i = 0; i < 512; i++) m.Memory.Ram[0x3000 + i] = (byte)(i * 17 + 3);
            m.RunSteps(1); Check(m.Cpu.HasRepContinuation && m.Cpu.GetRegister(Register.Cx) == 127 && m.Fault is null);
            return f;
        }
        catch { f.Dispose(); throw; }
    }
    /// <summary>Compares all architectural/device state and every raw RAM byte after each real continuation slice.</summary>
    private static void Same(PcMachine a, PcMachine b)
    { Check(State(a) == State(b) && a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Valid full continuation changed."); }
    private sealed class MediaFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Environment.CurrentDirectory, "artifacts/tests/audit2-state-media-" + Guid.NewGuid().ToString("N"));
        public readonly string[] Bases, Overlays, BaseHashes;
        public readonly IReadOnlyBlockDevice?[] Media;
        public readonly PcMachine Machine;
        /// <summary>Creates distinct journal patterns, optionally over one shared protected base, with two real 1.44-MiB drive geometries.</summary>
        public MediaFixture(bool floppy = false, bool sharedBase = false, bool readOnlySecond = false)
        {
            Directory.CreateDirectory(Root); Bases = new string[floppy ? 2 : 1]; Overlays = new string[Bases.Length]; BaseHashes = new string[Bases.Length];
            var media = Media = new IReadOnlyBlockDevice?[Bases.Length];
            try
            {
                for (int i = 0; i < Bases.Length; i++)
                {
                    Bases[i] = Path.Combine(Root, sharedBase ? "shared.img" : "base-" + i + ".img"); Overlays[i] = Path.Combine(Root, "active-" + i + ".cow");
                    if (!File.Exists(Bases[i]))
                    {
                        byte[] image = new byte[floppy ? 1440 * 1024 : 2 * 1048576]; DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(image);
                        image.AsSpan(512, 512).Fill((byte)(0x31 + i)); File.WriteAllBytes(Bases[i], image);
                    }
                    BaseHashes[i] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Bases[i])));
                    if (readOnlySecond && i == 1) media[i] = new ReadOnlyRawDisk(Bases[i]);
                    else { var cow = new CopyOnWriteDisk(Bases[i], Overlays[i]); cow.WriteSector(3, Pattern((byte)(0x61 + i))); media[i] = cow; }
                }
                Machine = floppy ? new PcMachine(new() { RamMiB = 4, FloppyDriveA = "1440k", FloppyDriveB = "1440k" },
                    new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Own audit2 boot"), media[0], media[1]) : PcMachine.CreateWithDisks(new() { RamMiB = 4 }, [media[0], null, null, null]);
                Machine.Cpu.SetSegment(Segment.Cs, 0); Machine.Cpu.SetSegment(Segment.Ss, 0); Machine.Cpu.SetSegment(Segment.Ds, 0);
                Machine.Cpu.SetRegister(Register.Sp, 0x9000); Machine.Cpu.Eip = 0x1000;
                new byte[] { 0xb8, 0x34, 0x12, 0x90, 0x90, 0xf4 }.CopyTo(Machine.Memory.Ram, 0x1000);
            }
            catch { foreach (var disk in media) disk?.Dispose(); throw; }
        }
        /// <summary>Returns only a new path beneath this fixture's private directory.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Closes owned media and removes only the unique test directory created above.</summary>
        public void Dispose() { Machine.Dispose(); Directory.Delete(Path.GetFullPath(Root), true); }
    }
    /// <summary>Creates a complete independent sector payload.</summary>
    private static byte[] Pattern(byte value) => Enumerable.Repeat(value, 512).ToArray();
    /// <summary>Observes a complete physical sector from a borrowed active floppy medium.</summary>
    private static void Sector(PcMachine m, int slot, long lba, byte marker)
    { byte[] read = new byte[512]; m.Floppy.MediaAt(slot)!.ReadSector(lba, read); Check(read.SequenceEqual(Pattern(marker)), "Wrong floppy medium/lease reached slot " + slot); }
    /// <summary>Preserves one complete active COW through the reviewed owner-safe export API, never raw-opening its exclusive writer.</summary>
    private static byte[] Generation(IReadOnlyBlockDevice disk, string path)
    { ((CopyOnWriteDisk)disk).ExportGeneration(path); return File.ReadAllBytes(path); }
    /// <summary>Checks the complete immutable own RAW images after controller or lease operations.</summary>
    private static void BasesUnchanged(MediaFixture fixture)
    { for (int i = 0; i < fixture.Bases.Length; i++) Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Bases[i]))) == fixture.BaseHashes[i], "Protected own RAW base changed."); }
    /// <summary>Programs an actual ATA data phase through CPU-visible task-file ports.</summary>
    private static void Ata(PcMachine m, byte command, int words = 0)
    {
        m.Ports.Write(0x1f6, 0xe0, 1); m.Ports.Write(0x1f2, 1, 1); m.Ports.Write(0x1f3, 1, 1); m.Ports.Write(0x1f4, 0, 1); m.Ports.Write(0x1f5, 0, 1); m.Ports.Write(0x1f7, command, 1);
        m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000); m.Audio.AdvanceTo(m.Scheduler.Now); Check((m.Ports.Read(0x3f6, 1) & 8) != 0);
        for (int i = 0; i < words; i++) { if (command == 0x30) m.Ports.Write(0x1f0, 0x7654, 2); else m.Ports.Read(0x1f0, 2); }
    }
    /// <summary>Locates only the actually attached primary master engine in a full explicit graph.</summary>
    private static JsonObject AtaFields(JsonObject state) => state["components"]!["ata0/_drives/0"]!["fields"]!.AsObject();
    /// <summary>Registers only reproductions or meaningful positive controls for the audited state and lease boundaries.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (bool code32 in new[] { false, true }) foreach (bool move in new[] { false, true }) foreach (int width in new[] { 1, 2, 4 })
            test($"Audit2 valid complete REP continuation mode32={code32} move={move} width={width}", () =>
            {
                using var f = Rep(code32, width, move); var m = f.Machine; using var resumed = f.Restore(); Same(m, resumed);
                for (int i = 0; i < 8 && !m.Cpu.Halted; i++) { m.RunSteps(32); resumed.RunSteps(32); Same(m, resumed); }
                Check(m.Cpu.Halted && m.Cpu.GetRegister(Register.Cx) == 0 && m.Cpu.GetRegister(Register.Di) == 0x5000 + 128 * width);
                for (int i = 0; i < 128 * width; i++) Check(m.Memory.Ram[0x5000 + i] == (move ? (byte)(i * 17 + 3) : (byte)(0x76543210u >> (8 * (i % width)))));
            });
        foreach (byte[] prefixes in new byte[][] { [0x66, 0x66], [0xf2, 0x66], [0x64, 0x67, 0x66], [0x26, 0x64, 0x66] })
            test("Audit2 valid duplicate and segment REP prefixes " + Convert.ToHexString(prefixes), () =>
            {
                using var f = Rep(true, 2, true, prefixes); using var resumed = f.Restore(); Same(f.Machine, resumed);
                f.Machine.RunSteps(200); resumed.RunSteps(200); Same(f.Machine, resumed); Check(f.Machine.Cpu.Halted && f.Machine.Cpu.GetRegister(Register.Cx) == 0);
            });
        test("Audit2 valid inactive reset REP cache remains importable", () =>
        {
            using var f = new SnapshotTests.Fixture(new() { RamMiB = 4 }); using var resumed = f.Restore(); Same(f.Machine, resumed);
            Check(!resumed.Cpu.HasRepContinuation && resumed.Cpu.SaveState().Fields["_repSize"].GetInt32() == 0);
        });
        test("Audit2 active REP cache may be safely stale after an actual interrupt entry", () =>
        {
            using var f = Rep(false, 1, true); var m = f.Machine; m.Memory.WriteWord(0xc0, 0x5000); m.Memory.WriteWord(0xc2, 0x200); m.Memory.Ram[0x7000] = 0xcf;
            m.Cpu.EnterRealModeInterrupt(0x30); Check(m.Cpu.HasRepContinuation && m.Cpu.GetSegment(Segment.Cs) == 0x200);
            using var resumed = f.Restore(); Same(m, resumed); m.RunSteps(200); resumed.RunSteps(200); Same(m, resumed); Check(m.Cpu.Halted);
        });
        foreach (string change in new[] { "size0", "size3", "size2-on-byte", "length0", "length1", "length16", "opcode", "last-opcode", "no-repeat", "lock-prefix", "code-short", "code-null", "address", "source", "end", "default-width" })
            test("Audit2 rehashed REP import rejection preserves owner and next guest work " + change, () =>
            {
                using var f = Rep(true, 1, true); var m = f.Machine; string saved = f.PathFor("bad-rep.dossav"); SnapshotStore.Capture(m, saved);
                Edit(saved, state =>
                {
                    var cpu = state["components"]!["cpu"]!["fields"]!.AsObject(); byte[] code = cpu["_repCode"]!.Deserialize<byte[]>(StateValues.Json)!;
                    switch (change)
                    {
                        case "size0": cpu["_repSize"] = 0; break; case "size3": cpu["_repSize"] = 3; break; case "size2-on-byte": cpu["_repSize"] = 2; break;
                        case "length0": cpu["_repLength"] = 0; break; case "length1": cpu["_repLength"] = 1; break; case "length16": cpu["_repLength"] = 16; break;
                        case "opcode": cpu["_repOpcode"] = 0xa6; break;
                        case "last-opcode": code[1] = 0xaa; cpu["_repCode"] = JsonSerializer.SerializeToNode(code, StateValues.Json); break;
                        case "no-repeat": code[0] = 0x66; cpu["_repCode"] = JsonSerializer.SerializeToNode(code, StateValues.Json); break;
                        case "lock-prefix": code[0] = 0xf0; cpu["_repCode"] = JsonSerializer.SerializeToNode(code, StateValues.Json); break;
                        case "code-short": cpu["_repCode"] = JsonSerializer.SerializeToNode(code[..14], StateValues.Json); break;
                        case "code-null": cpu["_repCode"] = null; break;
                        case "address": cpu["_repAddress32"] = false; break; case "source": cpu["_repSource"] = 9; break;
                        case "end": cpu["_repEnd"] = cpu["_repEnd"]!.GetValue<uint>() + 1; break;
                        case "default-width": cpu["_repCode32"] = false; break;
                    }
                });
                using var session = new MachineSession(m); Rejected(session, saved, f.PathFor("failed.dosebranch"));
            });
        foreach (byte command in new byte[] { 0x20, 0x30, 0xec }) foreach (int words in new[] { 0, 1, 255, 256 })
            test($"Audit2 valid full ATA state command={command:X2} words={words}", () =>
            {
                using var f = new MediaFixture(); var m = f.Machine; Ata(m, command, words);
                string saved = f.PathFor("ata.dossav"); SnapshotStore.Capture(m, saved); using var resumed = SnapshotStore.Load(saved, f.PathFor("ata.dosebranch")); Same(m, resumed);
                var fields = m.CaptureHardwareState().Components["ata0/_drives/0"].Fields; int offset = fields["_offset"].GetInt32(); byte status = fields["Status"].GetByte();
                Check(offset == 2 * words && ((status & 8) != 0) == (words < 256), "Actual expected ATA phase was not created.");
                if (words < 256)
                {
                    for (int i = words; i < 256; i++)
                        if (command == 0x30) { m.Ports.Write(0x1f0, 0x7654, 2); resumed.Ports.Write(0x1f0, 0x7654, 2); }
                        else
                        {
                            uint actual = m.Ports.Read(0x1f0, 2); Check(actual == resumed.Ports.Read(0x1f0, 2));
                            if (command == 0x20) Check(actual == 0x3131);
                            else if (i == 0) Check(actual == 0x0040); else if (i == 255) Check(actual == 0);
                        }
                }
                m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000); resumed.Scheduler.AdvanceTo(resumed.Scheduler.Now + 5000); m.Audio.AdvanceTo(m.Scheduler.Now); resumed.Audio.AdvanceTo(resumed.Scheduler.Now); Same(m, resumed);
                if (command == 0x30)
                {
                    byte[] written = new byte[512]; f.Media[0]!.ReadSector(1, written);
                    for (int i = 0; i < written.Length; i++) Check(written[i] == (i % 2 == 0 ? 0x54 : 0x76));
                }
                BasesUnchanged(f);
            });
        foreach (byte command in new byte[] { 0x20, 0x30, 0xec }) foreach (int bad in new[] { 1, 511, 512 })
            test($"Audit2 rehashed ATA DRQ offset rejected command={command:X2} offset={bad}", () =>
            {
                using var f = new MediaFixture(); var m = f.Machine; Ata(m, command); string saved = f.PathFor("bad-ata.dossav"); SnapshotStore.Capture(m, saved);
                Edit(saved, state => AtaFields(state)["_offset"] = bad); using var session = new MachineSession(m); Rejected(session, saved, f.PathFor("bad-ata.dosebranch"));
                if (command == 0x30) m.Ports.Write(0x1f0, 0xabcd, 2); else m.Ports.Read(0x1f0, 2);
                Check(m.CaptureHardwareState().Components["ata0/_drives/0"].Fields["_offset"].GetInt32() == 2, "Old ATA engine could not continue after malformed import.");
            });
        foreach (bool sharedBase in new[] { false, true }) foreach (bool readOnlySecond in new[] { false, true }) foreach (bool reverseOrder in new[] { false, true })
            test($"Audit2 actual owner floppy swap shares={sharedBase} readOnlyB={readOnlySecond} reverseOrder={reverseOrder}", () =>
            {
                using var f = new MediaFixture(true, sharedBase, readOnlySecond); using var session = new MachineSession(f.Machine);
                var original = f.Machine.CaptureDefinition(); var swapped = original with { Floppies = original.Floppies.Select(item => item with { Slot = 1 - item.Slot }).ToArray() };
                if (reverseOrder) swapped = swapped with { Floppies = swapped.Floppies.Reverse().ToArray() };
                string path = f.PathFor("swapped.dospc.json"); MachineDefinitionStore.Save(path, swapped); long revision = session.Revision;
                var result = Request(session, "LoadMachineDefinition", new { path }); Check(result.Success && session.Revision == revision + 1 && !ReferenceEquals(f.Machine, session.Machine));
                var m = session.Machine; var actual = m.CaptureDefinition(); Check(actual.Id == original.Id && actual.Name == original.Name);
                for (int slot = 0; slot < 2; slot++)
                {
                    var image = m.Floppy.MediaImage(slot)!; Check(Path.GetFullPath(image.BasePath) == Path.GetFullPath(f.Bases[1 - slot]));
                    Check(slot == 0 && readOnlySecond ? image.OverlayPath is null : Path.GetFullPath(image.OverlayPath!) == Path.GetFullPath(f.Overlays[1 - slot]));
                }
                Sector(m, 1, 3, 0x61); Sector(m, 0, 3, readOnlySecond ? (byte)0 : (byte)0x62);
                ((IWritableBlockDevice)m.Floppy.MediaAt(1)!).WriteSector(4, Pattern(0xa7)); Sector(m, 1, 4, 0xa7);
                Check(Request(session, "Ping").Success); m.RunSteps(200000); Check(m.Fault is null && m.Cpu.Halted && m.Memory.ReadWord(0x500) == 0xbeef); BasesUnchanged(f);
            });
        foreach (bool sharedBase in new[] { false, true }) foreach (string failure in new[] { "late-geometry", "second-open" })
            test($"Audit2 failed cross-slot lease preparation rolls back all media shares={sharedBase} {failure}", () =>
            {
                using var f = new MediaFixture(true, sharedBase); var m = f.Machine; using var session = new MachineSession(m);
                byte[] a = Generation(m.Floppy.MediaAt(0)!, f.PathFor("before-a.cow")), b = Generation(m.Floppy.MediaAt(1)!, f.PathFor("before-b.cow"));
                var original = m.CaptureDefinition(); var bad = original with { Floppies = original.Floppies.Select(item => item with { Slot = 1 - item.Slot }).OrderBy(item => item.Slot).ToArray() };
                if (failure == "late-geometry") bad = bad with { Hardware = bad.Hardware with { FloppyDriveB = "720k" } };
                else bad = bad with { Floppies = bad.Floppies.Select(item => item.Slot == 1 ? item with { ImagePath = f.PathFor("absent.img"), OverlayPath = f.PathFor("absent.cow") } : item).ToArray() };
                string path = f.PathFor("bad.dospc.json"); MachineDefinitionStore.Save(path, bad); string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); long revision = session.Revision;
                var rejected = Request(session, "LoadMachineDefinition", new { path }); Check(!rejected.Success && ReferenceEquals(m, session.Machine) && session.Revision == revision);
                Check(State(m) == before && ram.AsSpan().SequenceEqual(m.Memory.Ram) && !File.Exists(f.PathFor("absent.cow")));
                Check(a.SequenceEqual(Generation(m.Floppy.MediaAt(0)!, f.PathFor("after-a.cow"))) && b.SequenceEqual(Generation(m.Floppy.MediaAt(1)!, f.PathFor("after-b.cow"))));
                ((IWritableBlockDevice)m.Floppy.MediaAt(0)!).WriteSector(4, Pattern(0x83)); ((IWritableBlockDevice)m.Floppy.MediaAt(1)!).WriteSector(4, Pattern(0x94)); Sector(m, 0, 4, 0x83); Sector(m, 1, 4, 0x94);
                Check(Request(session, "Ping").Success); m.RunSteps(2); Check(m.Cpu.GetRegister(Register.Ax) == 0x1234 && m.Fault is null);
                var good = original with { Floppies = original.Floppies.Select(item => item with { Slot = 1 - item.Slot }).ToArray() }; path = f.PathFor("retry.dospc.json"); MachineDefinitionStore.Save(path, good);
                Check(Request(session, "LoadMachineDefinition", new { path }).Success, "Failed preparation leaked a pending lease."); Sector(session.Machine, 0, 4, 0x94); Sector(session.Machine, 1, 4, 0x83); BasesUnchanged(f);
            });
    }
}
