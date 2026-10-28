// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual full-container packet futures and rehashed semantic failures before opening the own protected ISO basis.</summary>
internal static class AtapiSnapshotTests
{
    /// <summary>Small real PIIX task-file driver; every byte is exchanged through the ordinary PC bus.</summary>
    private sealed class Pipe(PcMachine machine)
    {
        /// <summary>Actual whole PC whose physical ports drive the packet protocol.</summary>
        public readonly PcMachine Machine = machine;
        /// <summary>Physical task-file base for the reserved packet slot.</summary>
        public ushort Port => Machine.Profile.AtapiSlot < 2 ? (ushort)0x1f0 : (ushort)0x170;
        /// <summary>Physical alternate-status and shared device-control port.</summary>
        public ushort Control => Machine.Profile.AtapiSlot < 2 ? (ushort)0x3f6 : (ushort)0x376;
        /// <summary>Programs the real function-one I/O gates and BAR, without changing any saved device state directly.</summary>
        public void Enable()
        {
            Machine.Ports.Write(0xcf8, 0x80003920, 4); Machine.Ports.Write(0xcfc, 0xc001, 4);
            Machine.Ports.Write(0xcf8, 0x80003940, 4); Machine.Ports.Write(0xcfc, 0x80008000, 4);
            Machine.Ports.Write(0xcf8, 0x80003904, 4); Machine.Ports.Write(0xcfc, 7, 2); Select();
        }
        /// <summary>Selects this fixed physical packet target, or its ordinary neighbor when explicitly requested.</summary>
        public void Select(bool optical = true) => Machine.Ports.Write((ushort)(Port + 6), (uint)(0xa0 | ((Machine.Profile.AtapiSlot & 1) ^ (optical ? 0 : 1)) << 4), 1);
        /// <summary>Reads one actual byte task register.</summary>
        public uint Read(int at) => Machine.Ports.Read((ushort)(Port + at), 1);
        /// <summary>Writes one actual byte task register.</summary>
        public void Write(int at, uint value) => Machine.Ports.Write((ushort)(Port + at), value, 1);
        /// <summary>Samples alternate status with no hidden IRQ acknowledgement.</summary>
        public uint Alt => Machine.Ports.Read(Control, 1);
        /// <summary>Dispatches the next actual device deadline and integrates the real audio timeline.</summary>
        public void Next()
        { ulong? due = Machine.Scheduler.NextEventTime; AtapiTestSupport.Check(due.HasValue); Machine.Scheduler.AdvanceTo(due!.Value); Machine.Audio.AdvanceTo(Machine.Scheduler.Now); }
        /// <summary>Bounds all polling by actual scheduled callbacks rather than inventing protocol progress.</summary>
        public void Settled()
        { for (int guard = 0; (Alt & 0x80) != 0; guard++) { AtapiTestSupport.Check(guard < 256); Next(); } }
        /// <summary>Issues PACKET and optionally stops during its true CDB staging phase.</summary>
        public void Begin(byte[] command, int words = 6, bool wait = true)
        {
            Select(); Write(1, 0); Write(4, 0); Write(5, 8); Write(7, 0xa0); if (!wait) return;
            Settled(); AtapiTestSupport.Check((Alt & 0x88) == 8 && (Read(2) & 3) == 1); Read(7); SendWords(command, 0, words);
        }
        /// <summary>Sends only the remaining independently specified CDB words.</summary>
        public void SendWords(byte[] command, int start, int count)
        { for (int word = start; word < start + count; word++) Machine.Ports.Write(Port, BinaryPrimitives.ReadUInt16LittleEndian(command.AsSpan(word * 2, 2)), 2); }
        /// <summary>Consumes announced data and the distinct terminal status, returning exact useful bytes.</summary>
        public byte[] Drain(int alreadyConsumed = 0)
        {
            var bytes = new List<byte>();
            for (int guard = 0; guard < 128; guard++)
            {
                Settled(); if ((Alt & 8) == 0) { AtapiTestSupport.Check((Read(7) & 1) == 0); return bytes.ToArray(); }
                AtapiTestSupport.Check((Read(2) & 3) == 2); int length = (int)(Read(4) | Read(5) << 8); AtapiTestSupport.Check(length is > 0 and <= 2048 && alreadyConsumed >= 0 && alreadyConsumed < length); Read(7); length -= alreadyConsumed; alreadyConsumed = 0;
                for (int i = 0; i < length; i += 2) { uint word = Machine.Ports.Read(Port, 2); bytes.Add((byte)word); if (i + 1 < length) bytes.Add((byte)(word >> 8)); }
            }
            throw new Exception("Full PC packet failed to complete");
        }
        /// <summary>Executes one whole actual packet without bypassing status or word I/O.</summary>
        public byte[] Execute(byte[] command) { Begin(command); return Drain(); }
    }
    /// <summary>Own source PC and a separate old owner; closing the source never retires the adopted old owner.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>All fresh private media and snapshot paths owned by this case.</summary>
        public readonly AtapiTestSupport.Files Files = new();
        /// <summary>Own source PC, explicitly retired before denial-lock tests.</summary>
        public PcMachine? Source;
        /// <summary>Independent running old owner retained through failed imports.</summary>
        public readonly PcMachine Owner;
        /// <summary>Creates only own ISO/RAW/COW bytes and establishes a genuine finite instruction stream.</summary>
        public Fixture(string cut = "idle", int slot = 2, bool empty = false)
        {
            Source = Files.Machine(empty ? null : Files.Iso, slot); Owner = PcMachine.CreateWithDisks(new(), [new MemoryDisk(new byte[64 * 512], "Own independent old owner"), null, null, null]);
            AtapiTestSupport.Loop(Source); AtapiTestSupport.Loop(Owner); Source.Memory.Ram[^1] = 0x6d; Owner.Memory.Ram[^1] = 0xa7;
            var p = new Pipe(Source); p.Enable(); Prepare(p, cut);
        }
        /// <summary>Releases the source's legitimate ISO lease solely for the subsequent exclusive pre-opening rejection proof.</summary>
        public void CloseSource() { Source?.Dispose(); Source = null; }
        /// <summary>Closes all own peers before removing only their fresh fixture directory.</summary>
        public void Dispose() { Source?.Dispose(); Owner.Dispose(); Files.Dispose(); }
    }
    /// <summary>Creates one exact 12-byte packet with one optional parameter byte.</summary>
    private static byte[] Packet(byte opcode, int at = -1, byte value = 0)
    { byte[] bytes = new byte[12]; bytes[0] = opcode; if (at >= 0) bytes[at] = value; return bytes; }
    /// <summary>Independent big-endian READ(10) for two cooked blocks.</summary>
    private static byte[] ReadCdb(uint lba = 3, ushort count = 2)
    { byte[] command = Packet(0x28); BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), lba); BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(7, 2), count); return command; }
    /// <summary>Prepares real callback, partial-CDB, partial-data, sense or mechanical states solely through guest-visible I/O.</summary>
    private static void Prepare(Pipe p, string cut)
    {
        if (cut is not ("sense2" or "sense-status")) { byte[] sense = p.Execute(Packet(3, 4, 18)); AtapiTestSupport.Check(sense.Length == 18); }
        if (cut == "idle") return;
        if (cut == "locked") { p.Execute(Packet(0x1e, 4, 1)); return; }
        if (cut == "ejected") { p.Execute(Packet(0x1b, 4, 2)); return; }
        if (cut == "hardheld")
        { p.Begin(ReadCdb(), 5); p.Machine.Ports.Write(0xcf9, 6, 1); AtapiTestSupport.Check(p.Machine.RunSteps(1, p.Machine.Scheduler.Now) == 0 && p.Machine.CpuResetHeld); return; }
        byte[] command = cut is "sense2" or "sense-status" ? Packet(3, 4, 18) : cut == "status-wait" ? ReadCdb(10, 1) : ReadCdb();
        if (cut == "packet-wait") { p.Begin(command, 0, false); return; }
        int words = cut == "cdb0" ? 0 : cut == "cdb2" ? 1 : cut == "cdb10" ? 5 : 6; p.Begin(command, words);
        if (words < 6 || cut == "command-wait") return;
        if (cut == "read-wait") { for (int guard = 0; p.Machine.Atapi!.Phase == AtapiPhase.CommandWait; guard++) { AtapiTestSupport.Check(guard < 256); p.Next(); } AtapiTestSupport.Check(p.Machine.Atapi!.Phase == AtapiPhase.DataInWait && (p.Alt & 0x80) != 0); return; }
        p.Settled(); AtapiTestSupport.Check((p.Alt & 8) != 0); p.Read(7);
        int consumed = cut == "data2046" ? 1023 : cut == "status-wait" ? 1024 : cut == "sense-status" ? 9 : 1;
        for (int i = 0; i < consumed; i++) p.Machine.Ports.Read(p.Port, 2);
        if (cut == "deselected") p.Select(false);
        if (cut == "srst-preserved") { p.Select(false); p.Machine.Ports.Write(p.Control, 4, 1); }
        p.Machine.Audio.AdvanceTo(p.Machine.Scheduler.Now);
    }
    /// <summary>Completes only the remaining true words from the captured cut, including the original terminal callback.</summary>
    private static byte[] Continue(PcMachine machine, string cut)
    {
        var p = new Pipe(machine);
        if (cut == "hardheld")
        {
            ulong release = machine.PiixReset!.CpuReleaseAt; machine.Scheduler.AdvanceTo(release); machine.Audio.AdvanceTo(release); machine.RunSteps(1, release);
            AtapiTestSupport.Check(!machine.CpuResetHeld && machine.Cpu.TimestampCounter == 0); p.Enable(); AtapiTestSupport.Check(p.Read(4) == 0x14 && p.Read(5) == 0xeb); return [];
        }
        if (cut is "idle" or "locked" or "ejected") return p.Execute(Packet(0x12, 4, 36));
        if (cut == "srst-preserved") machine.Ports.Write(p.Control, 0, 1);
        if (cut is "deselected" or "srst-preserved") p.Select();
        if (cut == "packet-wait") { p.Settled(); p.Read(7); p.SendWords(ReadCdb(), 0, 6); }
        else if (cut is "cdb0" or "cdb2" or "cdb10") { int first = cut == "cdb0" ? 0 : cut == "cdb2" ? 1 : 5; p.SendWords(ReadCdb(), first, 6 - first); }
        return p.Drain(cut == "data2046" ? 2046 : cut is "data2" or "sense2" or "deselected" or "srst-preserved" ? 2 : 0);
    }
    /// <summary>Returns the actual fixed packet owner's field map; no fallback type or alias can hide a wrong graph.</summary>
    private static JsonObject Fields(JsonObject state, int slot = 2) => state["components"]!["ata" + slot / 2 + "/_atapi"]!["fields"]!.AsObject();
    /// <summary>Rehashes a deliberate own-state change so the tested rejection must be semantic.</summary>
    private static void EditState(string saved, Action<JsonObject> change)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); change(state); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Rehashes deliberate sealed-media metadata without altering any basis or journal.</summary>
    private static void EditMedia(string saved, Action<JsonObject> change)
    {
        string path = Path.Combine(saved, "media.dosemedia", "media.json"); var metadata = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); change(metadata); byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Adds a well-shaped, correctly ordered fresh callback, without altering the real device's owned token.</summary>
    private static void Orphan(JsonObject state)
    {
        var scheduler = state["scheduler"]!.AsObject(); long id = scheduler["sequence"]!.GetValue<long>() + 1; scheduler["sequence"] = id;
        var rows = scheduler["events"]!.AsArray().Select(row => row!.DeepClone()).ToList(); rows.Add(JsonSerializer.SerializeToNode(new ScheduledStateEvent(id, scheduler["now"]!.GetValue<ulong>() + 5000, "ata1/_atapi", "phase", []), StateValues.Json)!);
        scheduler["events"] = new JsonArray(rows.OrderBy(row => row["time"]!.GetValue<ulong>()).ThenBy(row => row["id"]!.GetValue<long>()).ToArray());
    }
    /// <summary>Proves both direct and owner failures occur before opening the denied own ISO and retain the actual old machine.</summary>
    private static void Reject(Fixture f, string saved)
    {
        f.CloseSource(); string before = AtapiTestSupport.State(f.Owner); byte[] ram = f.Owner.Memory.Ram.ToArray(); string direct = f.Files.PathFor("direct.dosebranch"), owned = f.Files.PathFor("owner.dosebranch");
        using var deny = new FileStream(f.Files.Iso, FileMode.Open, FileAccess.Read, FileShare.None); bool rejected = false;
        try { using var invalid = SnapshotStore.Load(saved, direct); } catch (InvalidDataException) { rejected = true; }
        AtapiTestSupport.Check(rejected && !Directory.Exists(direct), "Malformed ATAPI state reached ISO opening or created a branch");
        using var session = new MachineSession(f.Owner); long revision = session.Revision, generation = session.InputGeneration;
        var response = AtapiTestSupport.Send(session, "LoadSnapshot", new { path = saved, destination = owned });
        AtapiTestSupport.Check(!response.Success && response.ErrorCode == "ResourceUnavailable" && ReferenceEquals(session.Machine, f.Owner) && session.Revision == revision && session.InputGeneration == generation && !Directory.Exists(owned));
        AtapiTestSupport.Check(AtapiTestSupport.Send(session, "Ping", new { }).Success && AtapiTestSupport.State(f.Owner) == before && f.Owner.Memory.Ram.AsSpan().SequenceEqual(ram));
        long instructions = f.Owner.Cpu.Instructions; AtapiTestSupport.Check(f.Owner.RunSteps(3) == 3 && f.Owner.Cpu.Instructions == instructions + 3 && f.Owner.Fault is null);
    }
    /// <summary>Registers full running-state cuts and independent rehashed metadata or phase failures.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int slot in Enumerable.Range(0, 4)) foreach (string cut in new[] { "cdb10", "data2" })
            test($"ATAPI full SnapshotStore exact packet future slot{slot} {cut}", () => Roundtrip(slot, cut));
        foreach (string cut in new[] { "idle", "packet-wait", "cdb0", "cdb2", "command-wait", "read-wait", "data2046", "status-wait", "sense2", "sense-status", "locked", "ejected", "deselected", "srst-preserved", "hardheld" })
            test("ATAPI full SnapshotStore true phase and media future " + cut, () => Roundtrip(2, cut));
        test("ATAPI empty installed target resumes without inventing a sealed medium", () =>
        {
            using var f = new Fixture(empty: true); string saved = f.Files.PathFor("empty.dossav"); var a = f.Source!; SnapshotStore.Capture(a, saved); using var b = SnapshotStore.Load(saved, f.Files.PathFor("empty.dosebranch")); AtapiTestSupport.Same(a, b);
            AtapiTestSupport.Check(a.Atapi is not null && b.Atapi is not null && a.OpticalDisc is null && b.OpticalDisc is null && b.CaptureDefinition().OpticalDiscs.Length == 0);
            byte[] left = new Pipe(a).Execute(Packet(0x12, 4, 36)), right = new Pipe(b).Execute(Packet(0x12, 4, 36)); AtapiTestSupport.Check(left.SequenceEqual(right) && left.Length == 36 && left[0] == 5); AtapiTestSupport.Same(a, b);
        });
        test("ATAPI valid pending READ error remains loadable before its later CHECK response", () =>
        {
            using var f = new Fixture(); var a = f.Source!; new Pipe(a).Begin(ReadCdb(11, 1)); string saved = f.Files.PathFor("future-check.dossav"); SnapshotStore.Capture(a, saved); using var b = SnapshotStore.Load(saved, f.Files.PathFor("future-check.dosebranch")); AtapiTestSupport.Same(a, b);
            var x = new Pipe(a); var y = new Pipe(b); x.Settled(); y.Settled(); AtapiTestSupport.Check((x.Read(7) & 1) != 0 && (y.Read(7) & 1) != 0 && a.Atapi!.ReadBlockCount == 0 && b.Atapi!.ReadBlockCount == 0); AtapiTestSupport.Same(a, b);
            byte[] sa = x.Execute(Packet(3, 4, 18)), sb = y.Execute(Packet(3, 4, 18)); AtapiTestSupport.Check(sa.SequenceEqual(sb) && (sa[2] & 15) == 5 && sa[12] == 0x21); AtapiTestSupport.Same(a, b);
        });
        test("ATAPI CD soft reset may coexist with a genuine neighboring ready HDD DMA request", () =>
        {
            using var f = new Fixture(slot: 1); var a = f.Source!; HddReady(a); var p = new Pipe(a); p.Write(7, 8);
            AtapiTestSupport.Check(a.Atapi!.Phase == AtapiPhase.SoftResetWait && a.Ata.DmaReady); string saved = f.Files.PathFor("coexisting.dossav"); SnapshotStore.Capture(a, saved);
            using var b = SnapshotStore.Load(saved, f.Files.PathFor("coexisting.dosebranch")); AtapiTestSupport.Same(a, b);
            FinishHdd(a); FinishHdd(b); AtapiTestSupport.Check(a.Atapi!.Phase == AtapiPhase.Idle && b.Atapi!.Phase == AtapiPhase.Idle); AtapiTestSupport.Same(a, b);
        });
        test("ATAPI coherent active packet cannot coexist with a neighboring HDD DMA ownership in rehashed import", () =>
        {
            using var donor = new Fixture("cdb0", 1); StateBlock packet = donor.Source!.Atapi!.SaveState();
            using var f = new Fixture(slot: 1); HddReady(f.Source!); string saved = f.Files.PathFor("conflicting.dossav"); SnapshotStore.Capture(f.Source!, saved);
            EditState(saved, s => s["components"]!["ata0/_atapi"] = JsonSerializer.SerializeToNode(packet, StateValues.Json)); Reject(f, saved);
        });
        test("ATAPI zero-block READ remains a valid full snapshot during its real NoData status wait", () =>
        {
            using var f = new Fixture(); var a = f.Source!; var x = new Pipe(a); x.Begin(ReadCdb(3, 0));
            for (int guard = 0; a.Atapi!.Phase == AtapiPhase.CommandWait; guard++) { AtapiTestSupport.Check(guard < 256); x.Next(); }
            AtapiTestSupport.Check(a.Atapi!.Phase == AtapiPhase.StatusWait && a.Atapi.CapturePhase().RemainingBlocks == 0); string saved = f.Files.PathFor("zero-read.dossav"); SnapshotStore.Capture(a, saved);
            using var b = SnapshotStore.Load(saved, f.Files.PathFor("zero-read.dosebranch")); AtapiTestSupport.Same(a, b); AtapiTestSupport.Check(x.Drain().Length == 0 && new Pipe(b).Drain().Length == 0 && a.Atapi.ReadBlockCount == 0 && b.Atapi!.ReadBlockCount == 0); AtapiTestSupport.Same(a, b);
        });
        test("ATAPI rehashed no-medium READ cannot disguise its data phase as a non-read response", () =>
        {
            using var f = new Fixture("data2"); string saved = f.Files.PathFor("disguised-read.dossav"); SnapshotStore.Capture(f.Source!, saved);
            EditState(saved, s => { var fields = Fields(s); fields["_readCommand"] = false; fields["_readLba"] = 0; fields["_remainingBlocks"] = 0; fields["HasDisc"] = false; fields["DiscBlocks"] = 0; });
            EditMedia(saved, s => { s["machine"]!["opticalDiscs"] = new JsonArray(); var rows = s["media"]!.AsArray(); rows.Remove(CdRow(s)); }); Reject(f, saved);
        });
        var cases = new List<(string Name, string Cut, Action<JsonObject> Edit)>
        {
            ("inserted-media-invariant", "data2", s => Fields(s)["HasDisc"] = false),
            ("fixed-packet-position", "idle", s => s["components"]!["ata1"]!["fields"]!["_packetIndex"] = 1),
            ("fixed-absent-slave-shadow", "idle", s => s["components"]!["ata1"]!["fields"]!["_shadowAtapiSlave"] = false),
            ("media-capacity-invariant", "data2", s => Fields(s)["DiscBlocks"] = 12),
            ("device-number-invariant", "data2", s => Fields(s)["DeviceNumber"] = 1),
            ("selected-controller-peer", "data2", s => Fields(s)["_selected"] = false),
            ("missing-selected-field", "idle", s => Fields(s).Remove("_selected")),
            ("missing-event-field", "idle", s => Fields(s).Remove("_event")),
            ("short-taskfile", "data2", s => Fields(s)["_task"] = JsonSerializer.SerializeToNode(new byte[7], StateValues.Json)),
            ("short-CDB-staging", "cdb10", s => Fields(s)["_packet"] = JsonSerializer.SerializeToNode(new byte[11], StateValues.Json)),
            ("short-optical-buffer", "data2", s => Fields(s)["_data"] = JsonSerializer.SerializeToNode(new byte[2047], StateValues.Json)),
            ("unknown-phase", "data2", s => Fields(s)["_phase"] = 255),
            ("zero-active-phase", "data2", s => Fields(s)["_phaseLength"] = 0),
            ("overlong-active-phase", "data2", s => Fields(s)["_phaseLength"] = 2049),
            ("negative-word-offset", "data2", s => Fields(s)["_offset"] = -1),
            ("odd-incomplete-word-offset", "data2", s => Fields(s)["_offset"] = 1),
            ("unretired-last-word", "data2", s => Fields(s)["_offset"] = 2048),
            ("overlong-payload", "data2", s => Fields(s)["_payloadLength"] = 2049),
            ("payload-outside-buffer", "data2", s => Fields(s)["_payloadOffset"] = 2049),
            ("odd-CDB-offset", "cdb10", s => Fields(s)["_packetOffset"] = 9),
            ("overlong-CDB-offset", "cdb10", s => Fields(s)["_packetOffset"] = 14),
            ("negative-ready-LBA", "data2", s => Fields(s)["_readLba"] = -1),
            ("ready-LBA-outside-ISO", "data2", s => Fields(s)["_readLba"] = 11),
            ("read-with-no-remaining-block", "data2", s => Fields(s)["_remainingBlocks"] = 0),
            ("READ-CDB-cannot-disable-command-flag", "data2", s => { var f = Fields(s); f["_readCommand"] = false; f["_readLba"] = 0; f["_remainingBlocks"] = 0; }),
            ("ready-zero-byte-limit", "data2", s => Fields(s)["_byteLimit"] = 0),
            ("pending-phase-without-owned-token", "command-wait", s => Fields(s)["_event"] = 0),
            ("pending-deadline-does-not-match-token", "command-wait", s => Fields(s)["_deadline"] = s["scheduler"]!["now"]!.GetValue<ulong>() + 123),
            ("physical-IRQ-source-mismatch", "data2", s => Fields(s)["_pendingInterrupt"] = true),
            ("data-phase-with-no-DRQ", "data2", s => Fields(s)["Status"] = 0x40),
            ("data-phase-terminal-reason", "data2", s => TaskByte(s, 2, 3)),
            ("data-phase-announced-count", "data2", s => { TaskByte(s, 4, 0); TaskByte(s, 5, 0); }),
            ("HDD-identify-shape-in-CD-read", "data2", s => Fields(s)["_identify"] = true),
            ("sense-clear-flag-without-sense", "data2", s => Fields(s)["_clearUaOnCompletion"] = true),
            ("hardware-held-during-live-data", "data2", s => Fields(s)["_hardwareResetHeld"] = true),
            ("preserved-SRST-without-assert", "data2", s => Fields(s)["_srstPreserved"] = true),
            ("selected-live-phase-under-SRST", "data2", s => Fields(s)["_srstActive"] = true),
            ("zero-transaction-generation", "data2", s => Fields(s)["Generation"] = 0),
            ("negative-command-diagnostic", "idle", s => Fields(s)["CommandsReceived"] = -1),
            ("negative-read-diagnostic", "idle", s => Fields(s)["ReadBlockCount"] = -1),
            ("negative-byte-diagnostic", "idle", s => Fields(s)["DataBytesRead"] = -1),
            ("unreferenced-live-phase-idle", "idle", Orphan),
            ("second-live-phase-token", "command-wait", Orphan)
        };
        foreach (var row in cases)
            test("ATAPI rehashed full import rejects before ISO and preserves old owner " + row.Name, () =>
            {
                using var f = new Fixture(row.Cut); string saved = f.Files.PathFor("invalid.dossav"); SnapshotStore.Capture(f.Source!, saved); EditState(saved, row.Edit); Reject(f, saved);
            });
        var mediaCases = new List<(string Name, Action<JsonObject> Edit)>
        {
            ("optical-sector-is-not-512", s => CdRow(s)["sectorSize"] = 512),
            ("missing-optical-sector-size", s => CdRow(s).Remove("sectorSize")),
            ("optical-capacity-differs-from-fixed-peer", s => CdRow(s)["sectors"] = 12),
            ("zero-optical-capacity", s => CdRow(s)["sectors"] = 0),
            ("overflowing-optical-capacity", s => CdRow(s)["sectors"] = long.MaxValue),
            ("wrong-media-bus", s => CdRow(s)["device"] = "ide"),
            ("wrong-physical-slot", s => CdRow(s)["slot"] = 3),
            ("readonly-CD-cannot-have-journal", s => { var cd = CdRow(s); cd["journalName"] = "cdrom2.cow"; cd["journalBytes"] = 48; cd["journalSha256"] = new string('0', 64); }),
            ("missing-optical-generation", s => { var rows = s["media"]!.AsArray(); rows.Remove(CdRow(s)); })
        };
        foreach (var row in mediaCases)
            test("ATAPI sealed metadata rejects before ISO and preserves old owner " + row.Name, () =>
            {
                using var f = new Fixture("data2"); string saved = f.Files.PathFor("invalid-media.dossav"); SnapshotStore.Capture(f.Source!, saved); EditMedia(saved, row.Edit); Reject(f, saved);
            });
        test("ATAPI coherent missing-medium active READ is rejected before any branch", () =>
        {
            using var f = new Fixture("data2"); string saved = f.Files.PathFor("no-medium.dossav"); SnapshotStore.Capture(f.Source!, saved);
            EditState(saved, s => { Fields(s)["HasDisc"] = false; Fields(s)["DiscBlocks"] = 0; });
            EditMedia(saved, s => { s["machine"]!["opticalDiscs"] = new JsonArray(); var rows = s["media"]!.AsArray(); rows.Remove(CdRow(s)); }); Reject(f, saved);
        });
    }
    /// <summary>Creates a real ready primary-master HDD DMA request while retaining selection of the CD slave.</summary>
    private static void HddReady(PcMachine m)
    {
        AtapiTestSupport.Check(m.Profile.AtapiSlot == 1); var p = new Pipe(m); p.Select(false); p.Write(2, 1); p.Write(3, 3); p.Write(4, 0); p.Write(5, 0); p.Write(6, 0xe0); p.Write(7, 0xc8);
        for (int guard = 0; !m.Ata.DmaReady; guard++) { AtapiTestSupport.Check(guard < 256); p.Next(); }
        AtapiTestSupport.Check(m.Ata.DmaDriveIndex == 0 && m.Ata.DmaLba == 3 && m.Ata.DmaOffset == 0); p.Select();
    }
    /// <summary>Runs one genuine physical PRD/BM transfer from the previously accepted HDD request.</summary>
    private static void FinishHdd(PcMachine m)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan(0x6000, 4), 0x8000); BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan(0x6004, 4), 0x80000200);
        m.Ports.Write(0xc004, 0x6000, 4); m.Ports.Write(0xc000, 9, 1); ulong until = m.Scheduler.Now + 1_000_000; m.Scheduler.AdvanceTo(until); m.Audio.AdvanceTo(until);
        AtapiTestSupport.Check(m.PiixIde!.GetChannelState(0).BytesTransferred == 512 && !m.PiixIde.GetChannelState(0).Active && m.Ata.DmaDirection == AtaDmaDirection.None);
        byte[] expected = Enumerable.Range(0, 512).Select(i => unchecked((byte)(i * 7 + 0x31))).ToArray(); AtapiTestSupport.Check(m.Memory.Ram.AsSpan(0x8000, 512).SequenceEqual(expected));
    }
    /// <summary>Changes one known task-file byte without altering the remaining exact array or its serializer.</summary>
    private static void TaskByte(JsonObject state, int index, byte value)
    { var fields = Fields(state); byte[] bytes = fields["_task"]!.Deserialize<byte[]>(StateValues.Json)!; bytes[index] = value; fields["_task"] = JsonSerializer.SerializeToNode(bytes, StateValues.Json); }
    /// <summary>Selects precisely the sealed optical generation rather than an arbitrary first disk row.</summary>
    private static JsonObject CdRow(JsonObject metadata) => metadata["media"]!.AsArray().Single(row => row!["device"]!.GetValue<string>() == "cdrom")!.AsObject();
    /// <summary>Checks a real full save/load, independently expected remaining payload and subsequent complete CPU/RAM/device future.</summary>
    private static void Roundtrip(int slot, string cut)
    {
        using var f = new Fixture(cut, slot); var a = f.Source!; string saved = f.Files.PathFor("valid.dossav"); SnapshotStore.Capture(a, saved); using var b = SnapshotStore.Load(saved, f.Files.PathFor("valid.dosebranch")); AtapiTestSupport.Same(a, b);
        AtapiTestSupport.Check(a.OpticalDisc!.BlockCount == 11 && b.OpticalDisc!.BlockCount == 11 && b.CaptureDefinition().OpticalDiscs.Single().Slot == slot);
        byte[] left = Continue(a, cut), right = Continue(b, cut); AtapiTestSupport.Check(left.SequenceEqual(right), "Continued packet words differ");
        if (cut is "idle" or "locked" or "ejected") AtapiTestSupport.Check(left.Length == 36 && left[0] == 5);
        else if (cut == "sense2") AtapiTestSupport.Check(left.Length == 16 && (left[0] & 15) == 6 && left[10] == 0x29 && !a.Atapi!.UnitAttention && !b.Atapi!.UnitAttention);
        else if (cut is "sense-status" or "status-wait" or "hardheld") AtapiTestSupport.Check(left.Length == 0);
        else
        {
            byte[] whole = AtapiTestSupport.Block(3).Concat(AtapiTestSupport.Block(4)).ToArray(); int skip = cut == "data2046" ? 2046 : cut is "data2" or "deselected" or "srst-preserved" ? 2 : 0;
            AtapiTestSupport.Check(left.SequenceEqual(whole.AsSpan(skip).ToArray()), "Snapshot payload oracle used the wrong block, byte or command boundary");
        }
        AtapiTestSupport.Same(a, b); long instructionCount = a.Cpu.Instructions;
        AtapiTestSupport.Check(a.RunSteps(12) == b.RunSteps(12) && a.Fault is null && b.Fault is null && a.Cpu.Instructions >= instructionCount); AtapiTestSupport.Same(a, b);
        var sealedA = MediaCheckpointStore.Capture(a, f.Files.PathFor("future-a.dosemedia")); var sealedB = MediaCheckpointStore.Capture(b, f.Files.PathFor("future-b.dosemedia"));
        AtapiTestSupport.Check(JsonSerializer.Serialize(sealedA.Media, StateValues.Json) == JsonSerializer.Serialize(sealedB.Media, StateValues.Json), "Real continued sealed ISO/HDD generations differ");
    }
}
