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

/// <summary>Independent regressions for review-found IDE callback/cursor import gaps and explicit legacy-port width boundaries.</summary>
internal static class PiixIdeReviewTests
{
    /// <summary>Selects only the explicitly new own IDE board and its fixed CPU/clock/RAM tuple.</summary>
    private static MachineProfile Profile() => new()
    {
        Id = MachineProfile.PiixIdeProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = 100_000_000, RamMiB = 32, Name = "Own PIIX IDE review fixture"
    };
    /// <summary>Requires actual bytes, typed fields or accepted/rejected product behavior.</summary>
    private static void Check(bool value, string why = "PIIX IDE review regression failed.")
    { if (!value) throw new Exception(why); }
    /// <summary>Captures the entire real product graph without changing device time.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares all physical RAM and the complete CPU/device/scheduler state.</summary>
    private static void Same(PcMachine a, PcMachine b)
    {
        Check(State(a) == State(b), "Full IDE continuation state differs.");
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Full 32-MiB RAM differs.");
    }
    /// <summary>Programs exact function-one configuration lanes through the actual mechanism-one bus.</summary>
    private static void Config(PcMachine m, int offset, uint value, int width)
    {
        m.Ports.Write(0xcf8, 0x80003900u | (uint)(offset & ~3), 4);
        m.Ports.Write((ushort)(0xcfc + (offset & 3)), value, width);
    }
    /// <summary>Enables both legacy target channels and a free BM I/O window, with optional PCI master permission.</summary>
    private static void Enable(PcMachine m, bool master = true)
    {
        Config(m, 0x20, 0xc001, 4); Config(m, 0x40, 0x80008000, 4); Config(m, 4, master ? 7u : 3u, 2);
    }
    /// <summary>Programs one own physically addressed PRD without CPU A20 or mapping side effects.</summary>
    private static void Prd(PcMachine m, uint table, uint address, ushort count, bool eot)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan((int)table, 4), address);
        BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan((int)table + 4, 4), (uint)count | (eot ? 0x80000000u : 0));
    }
    /// <summary>Uses the built-in own read-only fixture medium at LBA zero; no original image is opened.</summary>
    private static void ReadCommand(PcMachine m, int sectors = 1)
    {
        m.Ports.Write(0x1f6, 0xe0, 1); m.Ports.Write(0x1f2, (uint)sectors, 1);
        m.Ports.Write(0x1f3, 0, 1); m.Ports.Write(0x1f4, 0, 1); m.Ports.Write(0x1f5, 0, 1);
        m.Ports.Write(0x1f7, 0xc8, 1);
    }
    /// <summary>Starts a real fresh master command edge using the initial programmed descriptor pointer.</summary>
    private static void Start(PcMachine m)
    { m.Ports.Write(0xc000, 8, 1); m.Ports.Write(0xc004, 0x10000, 4); m.Ports.Write(0xc000, 9, 1); }
    /// <summary>Reaches a concrete device boundary through its real future queue, with a bounded deadline count.</summary>
    private static void Until(PcMachine m, Func<bool> done)
    {
        for (int i = 0; !done() && i < 10000; i++)
        {
            Check(m.Scheduler.NextEventTime is not null, "No actual event can reach this DMA boundary.");
            m.Scheduler.AdvanceTo(m.Scheduler.NextEventTime!.Value);
        }
        Check(done(), "DMA boundary did not arrive.");
    }
    /// <summary>Prepares real busy, ready, partially copied, paused, stopped or terminal states without mutating saved fields.</summary>
    private static void Prepare(PcMachine m, string phase)
    {
        Enable(m, phase != "start-paused");
        bool lazy = phase == "lazy-next-busy", nonEot = phase == "terminal-non-eot";
        Prd(m, 0x10000, 0x20000, 512, !lazy && !nonEot);
        if (lazy) Prd(m, 0x10008, 0x30000, 512, true);
        ReadCommand(m, lazy ? 2 : 1); Start(m);
        if (phase is "read-busy" or "start-paused") return;
        if (phase == "read-ready") { Until(m, () => m.Ata.DmaReady); return; }
        if (phase == "lazy-next-busy")
        {
            Until(m, () => m.PiixIde!.GetChannelState(0).BytesTransferred == 512 && !m.Ata.DmaReady);
            return;
        }
        if (phase is "terminal-eot-new-prdt" or "terminal-non-eot")
        {
            Until(m, () => m.Ata.DmaDirection == AtaDmaDirection.None);
            if (phase == "terminal-eot-new-prdt")
            {
                Check(!m.PiixIde!.GetChannelState(0).Active);
                m.Ports.Write(0xc004, 0x12000, 4);
                Check(m.PiixIde.GetChannelState(0).DescriptorTablePointer == 0x12000);
            }
            else Check(m.PiixIde!.GetChannelState(0).Active);
            return;
        }
        Until(m, () => m.PiixIde!.GetChannelState(0).BytesTransferred == 32);
        if (phase == "bme-paused") Config(m, 4, 3, 2);
        else if (phase == "stopped-partial") m.Ports.Write(0xc000, 8, 1);
    }
    /// <summary>Resolves a named explicit saved component, never a private CLR object graph.</summary>
    private static JsonObject Fields(JsonObject state, string node) => state["components"]![node]!["fields"]!.AsObject();
    /// <summary>Rehashes only a new own sealed container to exercise semantic validation rather than checksum rejection.</summary>
    private static void Edit(string saved, Action<JsonObject> change)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); change(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Adds a correctly shaped, ordered new callback with a unique sequence token and an optional cancellation.</summary>
    private static long AddEvent(JsonObject state, string owner, string operation, bool cancelled)
    {
        var scheduler = state["scheduler"]!.AsObject(); var events = scheduler["events"]!.AsArray();
        long id = checked(scheduler["sequence"]!.GetValue<long>() + 1);
        ulong last = events.Count == 0 ? scheduler["now"]!.GetValue<ulong>() : events.Max(row => row!["time"]!.GetValue<ulong>());
        events.Add(JsonSerializer.SerializeToNode(new ScheduledStateEvent(id, checked(last + 10000), owner, operation, []), StateValues.Json));
        scheduler["sequence"] = id;
        if (cancelled) scheduler["cancelled"]!.AsArray().Add(id);
        return id;
    }
    /// <summary>Removes a forged-away master timer without leaving a secondary cancellation or duplicate-token failure.</summary>
    private static void RemoveMasterTimer(JsonObject state)
    {
        var fields = Fields(state, "piix-ide"); long[] tokens = fields["_events"]!.Deserialize<long[]>(StateValues.Json)!;
        long token = tokens[0]; tokens[0] = 0; fields["_events"] = JsonSerializer.SerializeToNode(tokens, StateValues.Json);
        if (token == 0) return;
        var events = state["scheduler"]!["events"]!.AsArray();
        for (int index = events.Count - 1; index >= 0; index--)
            if (events[index]!["id"]!.GetValue<long>() == token) events.RemoveAt(index);
    }
    /// <summary>Requires semantic failure before media open while the existing machine, RAM, owner revision and input generation remain usable.</summary>
    private static void RejectLocked(SnapshotTests.Fixture fixture, string saved, string branch)
    {
        var m = fixture.Machine; string before = State(m); byte[] ram = m.Memory.Ram.ToArray();
        using var held = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
        bool rejected = false;
        try { using var unexpected = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(branch) && before == State(m) && ram.AsSpan().SequenceEqual(m.Memory.Ram));
        using var session = new MachineSession(m); long revision = session.Revision, epoch = session.InputGeneration;
        var pending = session.Enqueue(IpcProtocol.CreateRequest("LoadSnapshot", new { path = saved, destination = branch }));
        session.PumpCommands(); var response = pending.GetAwaiter().GetResult();
        Check(!response.Success && ReferenceEquals(session.Machine, m) && session.Revision == revision &&
            session.InputGeneration == epoch && !Directory.Exists(branch));
        Check(before == State(m) && ram.AsSpan().SequenceEqual(m.Memory.Ram));
    }

    /// <summary>Registers concrete callback/cursor rejection, permitted transient states and complete unsupported-width answers.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (string form in new[] { "events", "cancelled", "row", "owner", "operation", "payload" })
            test("M4 PIIX IDE review rejects null callback envelope before media " + form, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile());
                string saved = f.PathFor("null-envelope.dossav"), branch = f.PathFor("no-null-envelope.dosebranch");
                Prepare(f.Machine, "read-busy");
                SnapshotStore.Capture(f.Machine, saved);
                Edit(saved, state =>
                {
                    var scheduler = state["scheduler"]!.AsObject();
                    if (form is "events" or "cancelled") scheduler[form] = null;
                    else
                    {
                        var rows = scheduler["events"]!.AsArray();
                        Check(rows.Count != 0, "Own fixture has no real event for envelope mutation.");
                        if (form == "row") rows[0] = null;
                        else rows[0]![form] = null;
                    }
                });
                RejectLocked(f, saved, branch);
            });
        foreach (string phase in new[]
        {
            "read-busy", "read-ready", "partial", "bme-paused", "start-paused",
            "lazy-next-busy", "stopped-partial", "terminal-eot-new-prdt", "terminal-non-eot"
        })
            test("M4 PIIX IDE review accepts and fully continues actual boundary " + phase, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile()); var m = f.Machine; Prepare(m, phase);
                using var restored = f.Restore(); Same(m, restored);
                if (phase is "bme-paused" or "start-paused") { Config(m, 4, 7, 2); Config(restored, 4, 7, 2); }
                if (phase == "terminal-non-eot") { m.Ports.Write(0xc000, 8, 1); restored.Ports.Write(0xc000, 8, 1); }
                if (phase is not ("stopped-partial" or "terminal-eot-new-prdt" or "terminal-non-eot"))
                {
                    Until(m, () => !m.PiixIde!.GetChannelState(0).Active && m.Ata.DmaDirection == AtaDmaDirection.None);
                    restored.Scheduler.AdvanceTo(m.Scheduler.Now);
                }
                else
                {
                    ulong next = m.Scheduler.Now + 20000; m.Scheduler.AdvanceTo(next); restored.Scheduler.AdvanceTo(next);
                }
                Same(m, restored);
                if (phase == "stopped-partial") Check(m.Ata.DmaReady && m.Ata.DmaOffset == 32 && !m.PiixIde!.GetChannelState(0).Active);
            });
        foreach (var extra in new (string Owner, string Operation)[]
        {
            ("piix-ide", "primary-burst"), ("piix-ide", "secondary-burst"),
            ("ata0/_drives/0", "dma-read-ready"), ("ata0/_drives/0", "dma-write-ready"), ("ata0/_drives/0", "dma-write-commit"),
            ("ata0/_drives/1", "dma-read-ready"), ("ata0/_drives/1", "dma-write-ready"), ("ata0/_drives/1", "dma-write-commit"),
            ("ata1/_drives/0", "dma-read-ready"), ("ata1/_drives/0", "dma-write-ready"), ("ata1/_drives/0", "dma-write-commit"),
            ("ata1/_drives/1", "dma-read-ready"), ("ata1/_drives/1", "dma-write-ready"), ("ata1/_drives/1", "dma-write-commit")
        })
            test("M4 PIIX IDE review rejects unreferenced live callback before media " + extra.Owner + "/" + extra.Operation, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile());
                string saved = f.PathFor("orphan.dossav"), branch = f.PathFor("no-orphan.dosebranch");
                SnapshotStore.Capture(f.Machine, saved);
                Edit(saved, state => AddEvent(state, extra.Owner, extra.Operation, false));
                RejectLocked(f, saved, branch);
            });
        foreach (var extra in new (string Owner, string Operation)[]
        {
            ("piix-ide", "primary-burst"), ("piix-ide", "secondary-burst"),
            ("ata0/_drives/0", "dma-read-ready"), ("ata0/_drives/0", "dma-write-ready"), ("ata0/_drives/0", "dma-write-commit")
        })
            test("M4 PIIX IDE review permits canceled historical callback " + extra.Operation, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile());
                string saved = f.PathFor("canceled.dossav"), branch = f.PathFor("canceled.dosebranch");
                SnapshotStore.Capture(f.Machine, saved); long token = 0;
                Edit(saved, state => token = AddEvent(state, extra.Owner, extra.Operation, true));
                using var restored = SnapshotStore.Load(saved, branch);
                var restoredState = restored.CaptureHardwareState();
                Check(restoredState.Scheduler.Cancelled.Contains(token) && restoredState.Scheduler.Events.Any(row => row.Id == token && row.Owner == extra.Owner && row.Operation == extra.Operation));
                Check(restored.Memory.Ram.AsSpan().SequenceEqual(f.Machine.Memory.Ram) && restored.PiixIde!.GetChannelState(0).EventToken == 0);
            });
        foreach (string bad in new[] { "unloaded-ready", "exhausted-active", "unfetched-first" })
            test("M4 PIIX IDE review rejects impossible active cursor before media " + bad, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile()); Prepare(f.Machine, bad == "unfetched-first" ? "read-busy" : "partial");
                string saved = f.PathFor("cursor.dossav"), branch = f.PathFor("no-cursor.dosebranch");
                SnapshotStore.Capture(f.Machine, saved);
                Edit(saved, state =>
                {
                    RemoveMasterTimer(state); var fields = Fields(state, "piix-ide/_channels/0");
                    if (bad == "exhausted-active") fields["RegionOffset"] = fields["RegionLength"]!.GetValue<int>();
                    else fields["Loaded"] = false;
                    if (bad == "unfetched-first") { fields["RegionLength"] = 0; fields["RegionOffset"] = 0; }
                });
                RejectLocked(f, saved, branch);
            });
        foreach (int channel in new[] { 0, 1 }) foreach (int register in Enumerable.Range(1, 8)) foreach (int width in new[] { 2, 4 })
            test($"M4 PIIX IDE review unsupported taskfile width channel={channel} register={register} width={width}", () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Enable(m);
                ushort port = register == 8 ? (channel == 0 ? (ushort)0x3f6 : (ushort)0x376) :
                    (ushort)((channel == 0 ? 0x1f0 : 0x170) + register);
                string before = State(m); uint expected = width == 2 ? 65535u : uint.MaxValue;
                Check(m.Ports.Read(port, width) == expected, "Unsupported taskfile read did not float at full width.");
                m.Ports.Write(port, uint.MaxValue, width);
                Check(State(m) == before, "Unsupported taskfile transaction changed a register, status, source or timer.");
            });
        foreach (int channel in new[] { 0, 1 }) foreach (int width in new[] { 1, 4 })
            test($"M4 PIIX IDE review unsupported data width consumes no ATA word channel={channel} width={width}", () =>
            {
                using var m = PcMachine.CreateDemo(Profile()); Enable(m);
                ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
                m.Ports.Write((ushort)(port + 7), 0xec, 1);
                m.Scheduler.AdvanceTo(m.Scheduler.Now + 5000);
                string before = State(m); uint expected = width == 1 ? 255u : uint.MaxValue;
                Check(m.Ports.Read(port, width) == expected);
                m.Ports.Write(port, 0xa55aa55a, width);
                Check(State(m) == before, "Wrong-width data port access consumed a word or an IRQ.");
            });
    }
}

