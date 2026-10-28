// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Whole product PCs, real coordinated DMA futures and semantic imports with exclusively fresh owned media.</summary>
internal static class PiixIdePcTests
{
    /// <summary>Selects the fixed own IDE board without claiming full PIIX or Windows acceptance.</summary>
    private static MachineProfile Profile() => new() { Id = MachineProfile.PiixIdeProfileId, Cpu = MachineProfile.P54cCpuId,
        CpuHz = 100_000_000, RamMiB = 32, Name = "Own PIIX IDE PC" };
    /// <summary>Requires a concrete whole-machine, byte, event or file invariant.</summary>
    private static void Check(bool value, string why = "PIIX IDE full-PC invariant failed.") { if (!value) throw new Exception(why); }
    /// <summary>Accepts only an intentional argument, state or ownership rejection.</summary>
    private static void Invalid(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Invalid PIIX IDE state accepted."); }
    /// <summary>Independent owned sector data.</summary>
    private static byte[] Pattern(byte marker, long lba) => Enumerable.Range(0, 512).Select(i => unchecked((byte)(marker + lba * 23 + i * 13))).ToArray();
    /// <summary>Complete raw state, never a diagnostic-only digest or selected component projection.</summary>
    private static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares every registered field, scheduler value and every installed physical RAM byte.</summary>
    private static void Same(PcMachine a, PcMachine b)
    { Check(State(a) == State(b), "Entire hardware graph differs."); Check(a.Memory.Ram.SequenceEqual(b.Memory.Ram), "Entire32MiB physical RAM differs."); }
    /// <summary>Runs a real finite own instruction loop with live nonzero raw x87 state.</summary>
    private static void OwnCode(PcMachine m)
    {
        Check(!m.Cpu.Halted); m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetSegment(Segment.Es, 0);
        m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eflags = 2; m.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0xdb, 0xe3, 0xd9, 0xeb, 0xff, 0x06, 0x00, 0x50, 0xeb, 0xfa }.CopyTo(m.Memory.Ram, 0x1000);
        Check(m.RunSteps(3) == 3 && m.Fault is null);
    }
    /// <summary>Own RAW bases and four independent active COW generations, never external media.</summary>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Unique private path under this repository's ordinary tests directory.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/piix-ide-pc-" + Guid.NewGuid().ToString("N")));
        /// <summary>Actual complete factory-created PC.</summary>
        public readonly PcMachine Machine;
        /// <summary>Creates all own base sectors before opening their protected COW wrappers.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(Root); var attachments = new List<DiskAttachment>();
            for (int slot = 0; slot < 4; slot++)
            {
                byte[] image = new byte[1048576]; DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(image);
                Pattern((byte)(0x31 + slot * 0x29), 3).CopyTo(image, 3 * 512); Pattern((byte)(0x31 + slot * 0x29), 4).CopyTo(image, 4 * 512);
                string path = PathFor("base" + slot + ".img"); File.WriteAllBytes(path, image); attachments.Add(new(slot, path, PathFor("active" + slot + ".cow")));
            }
            Machine = MachineFactory.FromDefinition(new() { Name = "Own IDE state fixture", Hardware = Profile(), HardDisks = attachments.ToArray() }); OwnCode(Machine);
        }
        /// <summary>Names only a child of this fresh private fixture.</summary>
        public string PathFor(string name) => Path.Combine(Root, name);
        /// <summary>Closes the PC and removes only the verified own directory after all restored PCs have been disposed.</summary>
        public void Dispose()
        {
            Machine.Dispose(); string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
            string resolved = Path.GetFullPath(Root);
            Check(Path.GetDirectoryName(resolved) == parent && Path.GetFileName(resolved).StartsWith("piix-ide-pc-", StringComparison.Ordinal)); Directory.Delete(resolved, true);
        }
    }
    /// <summary>Reads actual function-one configuration through mechanism1.</summary>
    private static uint Config(PcMachine m, int offset, int width = 4)
    { m.Ports.Write(0xcf8, 0x80003900u | (uint)(offset & ~3), 4); return m.Ports.Read((ushort)(0xcfc + (offset & 3)), width); }
    /// <summary>Writes a complete actual config transaction.</summary>
    private static void SetConfig(PcMachine m, int offset, uint value, int width = 4)
    { m.Ports.Write(0xcf8, 0x80003900u | (uint)(offset & ~3), 4); m.Ports.Write((ushort)(0xcfc + (offset & 3)), value, width); }
    /// <summary>Enables actual fixed ATA targets, BM I/O and PCI master access, with an independently running PIT.</summary>
    private static void Enable(PcMachine m)
    {
        m.HostBridge!.WriteConfiguration(0x64, 8, 1); SetConfig(m, 0x20, 0xc001); SetConfig(m, 0x40, 0x80008000); SetConfig(m, 4, 7, 2);
        m.Ports.Write(0xcf8, 0x80003870, 4); m.Ports.Write(0xcfc, 15, 1);
        m.Ports.Write(0x43, 0x34, 1); m.Ports.Write(0x40, 32, 1); m.Ports.Write(0x40, 0, 1);
    }
    /// <summary>Writes one exact own physical PRD fixture; the device still fetches it through the PCI target path.</summary>
    private static void Prd(PcMachine m, uint table, uint address, uint count, bool end)
    { BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan((int)table, 4), address); BinaryPrimitives.WriteUInt32LittleEndian(m.Memory.Ram.AsSpan((int)table + 4, 4), count | (end ? 0x80000000u : 0)); }
    /// <summary>Programs one real channel/drive command.</summary>
    private static void Command(PcMachine m, int channel, int drive, byte command, int sectors = 1, uint lba = 3)
    {
        ushort port = channel == 0 ? (ushort)0x1f0 : (ushort)0x170;
        m.Ports.Write((ushort)(port + 6), 0xe0u | (uint)(drive << 4) | (lba >> 24 & 15), 1); m.Ports.Write((ushort)(port + 2), (uint)(sectors & 255), 1);
        m.Ports.Write((ushort)(port + 3), lba & 255, 1); m.Ports.Write((ushort)(port + 4), lba >> 8 & 255, 1); m.Ports.Write((ushort)(port + 5), lba >> 16 & 255, 1); m.Ports.Write((ushort)(port + 7), command, 1);
    }
    /// <summary>Programs an actual busmaster register, preserving full bus width.</summary>
    private static void SetBm(PcMachine m, int channel, int offset, uint value, int width = 1) => m.Ports.Write((ushort)(m.PiixIde!.BusMasterBase + channel * 8 + offset), value, width);
    /// <summary>Starts an actual fresh BM command edge.</summary>
    private static void Start(PcMachine m, int channel, bool read, uint table = 0x10000)
    { SetBm(m, channel, 0, read ? 8u : 0); SetBm(m, channel, 4, table, 4); SetBm(m, channel, 0, read ? 9u : 1); }
    /// <summary>Runs only actual existing virtual deadlines to an exact legal owner boundary.</summary>
    private static void Until(PcMachine m, Func<bool> done)
    {
        for (int i = 0; !done() && i < 10000; i++) { Check(m.Scheduler.NextEventTime is not null); m.Scheduler.AdvanceTo(m.Scheduler.NextEventTime!.Value); }
        Check(done(), "Own whole-PC cut was never reached.");
    }
    /// <summary>Mutates one own state container and repairs the manifest to test semantics rather than checksum rejection.</summary>
    private static void Edit(string saved, Action<JsonObject> edit)
    {
        string path = Path.Combine(saved, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject(); edit(state);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateValues.Json); File.WriteAllBytes(path, bytes);
        string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject();
        manifest["stateBytes"] = bytes.Length; manifest["stateSha256"] = Convert.ToHexString(SHA256.HashData(bytes)); File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
    }
    /// <summary>Returns only the specifically named registered component fields.</summary>
    private static JsonObject Fields(JsonObject state, string node) => state["components"]![node]!["fields"]!.AsObject();
    /// <summary>Changes actual persisted config bytes with their normal byte-array representation.</summary>
    private static void ConfigBytes(JsonObject state, Action<byte[]> change)
    { var fields = Fields(state, "piix-ide"); byte[] bytes = fields["_config"]!.Deserialize<byte[]>(StateValues.Json)!; change(bytes); fields["_config"] = JsonSerializer.SerializeToNode(bytes, StateValues.Json); }
    /// <summary>Pumps one real owner command and waits for its concrete response.</summary>
    private static IpcResponse Request(MachineSession session, string command, object payload)
    { var pending = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return pending.GetAwaiter().GetResult(); }
    /// <summary>Registers strict construction, actual full-container futures and rejection rollback.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("M4 PIIX IDE factory binds three exact functions and DMA owner peers", () =>
        {
            using var m = PcMachine.CreateDemo(Profile()); Check(m.Profile.IsPiixIde && m.Profile.IsPiixIrq && m.Profile.IsFxHost && m.Profile.IsP54c);
            Check(m.Pci!.FunctionCount == 3 && m.Pci.HasFunction(0, 0, 0, m.HostBridge!) && m.Pci.HasFunction(0, 7, 0, m.PiixIrq!) && m.Pci.HasFunction(0, 7, 1, m.PiixIde!));
            Check(Config(m, 0) == 0x12308086 && Config(m, 8) == 0x01018002 && Config(m, 4, 2) == 2 && Config(m, 0x20) == 1);
            Check(m.Ata.DmaAvailable && m.SecondaryAta.DmaAvailable && m.PiixIrq!.SupportsMirq && m.Ports.HasOnlyDynamicDecoder(m.PiixIde!));
            Check(m.PiixIde!.HasBindings(m.Scheduler, m.HostBridge!, m.Ata, m.SecondaryAta, m.Ports) && m.Cpu.Model == X86CpuModel.IntelP54cB5Subset && m.Cpu.TimestampFrequency == 100_000_000 && m.Memory.Ram.Length == 32 * 1048576);
            Check(!m.PiixIde!.IoSpaceEnabled && !m.PiixIde!.BusMasterEnabled && m.Ports.Read(0x1f7, 1) == 255); m.CaptureHardwareState();
        });
        foreach (int mode in new[] { 0, 1, 2, 3 })
            test("M4 PIIX IDE preserves older factory PIO and dynamic topology " + mode, () =>
            {
                var profile = mode == 0 ? new MachineProfile() : mode == 1 ? new MachineProfile().WithDevelopmentCpu(true) : Profile() with { Id = mode == 2 ? MachineProfile.FxHostProfileId : MachineProfile.PiixIrqProfileId };
                using var m = PcMachine.CreateDemo(profile); Check(m.PiixIde is null && !m.Ata.DmaAvailable && !m.SecondaryAta.DmaAvailable && m.Ports.DynamicDecoderCount == 0);
                Check(m.Pci?.FunctionCount == (mode < 2 ? (int?)null : mode == 2 ? 1 : 2)); Check(m.PiixIrq is null || !m.PiixIrq!.SupportsMirq); m.CaptureHardwareState();
            });
        foreach (string changed in new[] { "cpu", "90MHz", "133MHz", "8MiB", "16MiB", "64MiB", "unknown" })
            test("M4 PIIX IDE invalid fixed tuple rejects before missing media " + changed, () =>
            {
                var p = changed switch { "cpu" => Profile() with { Cpu = "i486dx-development" }, "90MHz" => Profile() with { CpuHz = 90_000_000 }, "133MHz" => Profile() with { CpuHz = 133_000_000 },
                    "8MiB" => Profile() with { RamMiB = 8 }, "16MiB" => Profile() with { RamMiB = 16 }, "64MiB" => Profile() with { RamMiB = 64 }, _ => Profile() with { Id = "unknown-ide-board" } };
                using var f = new SnapshotTests.Fixture(); Invalid(() => MachineFactory.FromDefinition(new() { Hardware = p, HardDisks = [new(0, f.PathFor("absent.img"), f.PathFor("uncreated.cow"))] }));
                Check(!File.Exists(f.PathFor("uncreated.cow")));
            });
        test("M4 PIIX IDE real firmware POST programs exact target resources", () =>
        {
            using var m = new PcMachine(Profile(), null); m.RunSteps(200000); Check(m.Fault is null && m.Cpu.Halted);
            Check(m.Board.DebugText.Contains("FX + PIIX IDE", StringComparison.Ordinal) && m.Video.ReadText().Contains("FX + PIIX IDE", StringComparison.Ordinal));
            Check(m.Memory.DecodedRamBytes == 32 * 1048576 && Config(m, 0x20) == 0xc001 && Config(m, 0x40) == 0x80008000 && Config(m, 4, 2) == 3);
            Check(m.PiixIrq!.ReadConfiguration(0x70, 1) == 15 && m.PiixIde!.LegacyEnabled(0) && m.PiixIde!.LegacyEnabled(1) && !m.PiixIde!.BusMasterEnabled);
        });
        foreach (int channel in new[] { 0, 1 }) foreach (int drive in new[] { 0, 1 }) foreach (bool write in new[] { false, true })
            test($"M4 PIIX IDE full RAW COW snapshot future channel{channel}/drive{drive}/write{write}", () =>
            {
                using var f = new Fixture(); var m = f.Machine; Enable(m); byte[] payload = Pattern(0xe5, 3).Concat(Pattern(0x8b, 4)).ToArray();
                Prd(m, 0x10000, 0x20000, 510, false); Prd(m, 0x10008, 0x30000, 514, true);
                if (write) { payload.AsSpan(0, 510).CopyTo(m.Memory.Ram.AsSpan(0x20000)); payload.AsSpan(510).CopyTo(m.Memory.Ram.AsSpan(0x30000)); }
                Command(m, channel, drive, write ? (byte)0xca : (byte)0xc8, 2); Start(m, channel, !write);
                Until(m, () => write ? m.PiixIde!.GetChannelState(channel).WriteCommitPending : m.PiixIde!.GetChannelState(channel).BytesTransferred == 32);
                var cut = m.PiixIde!.GetChannelState(channel); Check(cut.Active && cut.DeviceIndex == drive && cut.BytesTransferred == (write ? 512 : 32));
                string saved = f.PathFor("cut.dossav"); SnapshotStore.Capture(m, saved); using var b = SnapshotStore.Load(saved, f.PathFor("resume.dosebranch")); Same(m, b);
                for (int i = 0; i < 20; i++) { Check(m.RunSteps(500) == b.RunSteps(500) && m.Fault is null && b.Fault is null); Same(m, b); }
                Check(!m.PiixIde!.GetChannelState(channel).Active && m.PiixIde!.GetChannelState(channel).BytesTransferred == 1024 && (m.PiixIde!.GetChannelState(channel).Status & 7) == 4);
                int slot = channel * 2 + drive;
                if (write)
                {
                    Check((channel == 0 ? m.Ata : m.SecondaryAta).WrittenSectorCount == 2);
                    foreach (var side in new[] { m, b })
                    {
                        side.Ports.Read(channel == 0 ? (ushort)0x1f7 : (ushort)0x177, 1); Prd(side, 0x10000, 0x40000, 1024, true); Command(side, channel, drive, 0xc8, 2); Start(side, channel, true);
                    }
                    for (int i = 0; i < 20; i++) { Check(m.RunSteps(500) == b.RunSteps(500)); Same(m, b); }
                    Check(m.Memory.Ram.AsSpan(0x40000, 1024).SequenceEqual(payload));
                }
                else
                {
                    byte[] expected = Pattern((byte)(0x31 + slot * 0x29), 3).Concat(Pattern((byte)(0x31 + slot * 0x29), 4)).ToArray();
                    Check(m.Memory.Ram.AsSpan(0x20000, 510).SequenceEqual(expected.AsSpan(0, 510)) && m.Memory.Ram.AsSpan(0x30000, 514).SequenceEqual(expected.AsSpan(510)));
                }
                Check(File.ReadAllBytes(f.PathFor("base" + slot + ".img")).AsSpan(3 * 512, 512).SequenceEqual(Pattern((byte)(0x31 + slot * 0x29), 3)), "Protected own RAW source changed.");
                var left = MediaCheckpointStore.Capture(m, f.PathFor("left.dosemedia")); var right = MediaCheckpointStore.Capture(b, f.PathFor("right.dosemedia"));
                Check(JsonSerializer.Serialize(left.Media, StateValues.Json) == JsonSerializer.Serialize(right.Media, StateValues.Json), "Full media generations differ.");
            });
        foreach (string phase in new[] { "shortEOT", "longPRD", "lazyNextPage" })
            test("M4 PIIX IDE full container preserves legitimate paused or exhausted phase " + phase, () =>
            {
                using var f = new Fixture(); var m = f.Machine; Enable(m); uint table = phase == "lazyNextPage" ? 0x10ff8u : 0x10000u;
                Prd(m, table, 0x20000, phase == "shortEOT" ? 256u : phase == "longPRD" ? 1024u : 512u, phase != "lazyNextPage"); Command(m, 0, 0, 0xc8); Start(m, 0, true, table);
                Until(m, () => phase == "shortEOT" ? !m.PiixIde!.GetChannelState(0).Active : m.Ata.DmaDirection == AtaDmaDirection.None);
                string saved = f.PathFor("special.dossav"); SnapshotStore.Capture(m, saved); using var b = SnapshotStore.Load(saved, f.PathFor("special.dosebranch")); Same(m, b);
                for (int i = 0; i < 15; i++) { Check(m.RunSteps(500) == b.RunSteps(500)); Same(m, b); }
                var c = m.PiixIde!.GetChannelState(0); Check((c.Status & 7) == (phase is "longPRD" or "lazyNextPage" ? 5 : phase == "shortEOT" ? 0 : 4));
                if (phase == "shortEOT") Check(m.Ata.DmaOffset == 256 && m.Ata.DmaRemainingSectors == 1);
            });
        test("M4 PIIX IDE CPU hot reset preserves every powered DMA and PIC field before firmware step", () =>
        {
            using var f = new Fixture(); var m = f.Machine; Enable(m); Prd(m, 0x10000, 0x20000, 1024, true); Command(m, 0, 0, 0xc8, 2); Start(m, 0, true);
            Until(m, () => m.PiixIde!.GetChannelState(0).BytesTransferred == 32); var before = m.CaptureHardwareState(); byte[] ram = m.Memory.Ram.ToArray();
            m.HardwareReset(); var after = m.CaptureHardwareState();
            foreach (var (name, value) in before.Components.Where(p => p.Key != "machine" && p.Key != "cpu")) Check(JsonSerializer.Serialize(value, StateValues.Json) == JsonSerializer.Serialize(after.Components[name], StateValues.Json), "Powered node reset: " + name);
            Check(JsonSerializer.Serialize(before.Scheduler, StateValues.Json) == JsonSerializer.Serialize(after.Scheduler, StateValues.Json) && ram.SequenceEqual(m.Memory.Ram));
            Check(m.HardwareResetCount == 1 && m.Cpu.TimestampCounter == 0 && m.Cpu.Cr4 == 0 && m.PiixIde!.GetChannelState(0).BytesTransferred == 32);
            using var cold = m.CreatePowerCycle(m.Profile); Check(cold.Profile.IsPiixIde && cold.PiixIde is not null && !ReferenceEquals(cold.PiixIde, m.PiixIde));
            Check(Config(cold, 4, 2) == 2 && Config(cold, 0x20) == 1 && cold.PiixIde!.GetChannelState(0).Status == 0 && cold.Ata.DmaDirection == AtaDmaDirection.None && cold.Memory.Ram[0x20000] == 0);
        });
        test("M4 PIIX IDE owner cold adoption constructs fixed complete new peers", () =>
        {
            using var f = new SnapshotTests.Fixture(start: false); using var session = new MachineSession(f.Machine); var cpu = session.Machine.Cpu; long generation = session.InputGeneration;
            var prepare = Request(session, "PrepareConfigurationChange", Profile()); Check(prepare.Success && ReferenceEquals(session.Machine.Cpu, cpu));
            Check(Request(session, "CommitConfigurationChange", (ConfigurationPlan)prepare.Payload!).Success); var m = session.Machine;
            Check(!ReferenceEquals(m.Cpu, cpu) && m.Profile.IsPiixIde && m.PiixIde!.HasBindings(m.Scheduler, m.HostBridge!, m.Ata, m.SecondaryAta, m.Ports));
            Check(m.Pci!.FunctionCount == 3 && m.Ports.HasOnlyDynamicDecoder(m.PiixIde!) && session.InputGeneration == generation + 1 && Request(session, "Ping", new { }).Success);
        });
        foreach (string fault in new[] { "missing-ide", "missing-master-gate", "missing-drive-gate", "missing-latch", "missing-mode", "missing-selected", "missing-timers", "bad-id", "bad-fixed-command", "bad-capability", "latch-wrong-drive", "mode-range", "mode-write-flags", "odd-ATA-offset", "empty-ATA-remaining", "identify-DMA", "reset-DMA", "BM-no-start", "BM-status-reserved", "PRDT-unaligned", "region-unaligned", "region-overrun", "region-too-long", "region-position", "descriptor-index", "descriptor-address", "irq-history", "BM-timer-missing", "BM-timer-owner", "BM-timer-canceled" })
            test("M4 PIIX IDE semantic import rejects before media opening " + fault, () =>
            {
                using var f = new SnapshotTests.Fixture(Profile(), false); var m = f.Machine; OwnCode(m); Enable(m);
                Prd(m, 0x10000, 0x20000, 512, true); Command(m, 0, 0, 0xc8); Start(m, 0, true); Until(m, () => m.PiixIde!.GetChannelState(0).BytesTransferred == 32);
                string saved = f.PathFor("bad.dossav"), branch = f.PathFor("not-created.dosebranch"); SnapshotStore.Capture(m, saved);
                Edit(saved, state =>
                {
                    const string drive = "ata0/_drives/0", cursor = "piix-ide/_channels/0";
                    switch (fault)
                    {
                        case "missing-ide": Check(state["components"]!.AsObject().Remove("piix-ide")); break;
                        case "missing-master-gate": Check(Fields(state, "ata0").Remove("_dmaAvailable")); break;
                        case "missing-drive-gate": Check(Fields(state, drive).Remove("_dmaAvailable")); break;
                        case "missing-latch": Check(Fields(state, "ata0").Remove("_dmaDriveIndex")); break;
                        case "missing-mode": Check(Fields(state, drive).Remove("_dmaDirection")); break;
                        case "missing-selected": Check(Fields(state, drive).Remove("_dmaSelected")); break;
                        case "missing-timers": Check(Fields(state, "piix-ide").Remove("_events")); break;
                        case "bad-id": ConfigBytes(state, bytes => bytes[2] ^= 1); break;
                        case "bad-fixed-command": ConfigBytes(state, bytes => bytes[4] &= 0xfd); break;
                        case "bad-capability": Fields(state, drive)["_dmaAvailable"] = false; break;
                        case "latch-wrong-drive": Fields(state, "ata0")["_dmaDriveIndex"] = 1; break;
                        case "mode-range": Fields(state, drive)["_dmaDirection"] = 3; break;
                        case "mode-write-flags": Fields(state, drive)["_dmaDirection"] = 2; break;
                        case "odd-ATA-offset": Fields(state, drive)["_offset"] = 33; break;
                        case "empty-ATA-remaining": Fields(state, drive)["_remaining"] = 0; break;
                        case "identify-DMA": Fields(state, drive)["_identify"] = true; break;
                        case "reset-DMA": Fields(state, drive)["_resetActive"] = true; break;
                        case "BM-no-start": Fields(state, cursor)["Command"] = 8; break;
                        case "BM-status-reserved": Fields(state, cursor)["Status"] = 0x81; break;
                        case "PRDT-unaligned": Fields(state, cursor)["Prdt"] = 0x10001; break;
                        case "region-unaligned": Fields(state, cursor)["RegionAddress"] = 0x20001; break;
                        case "region-overrun": Fields(state, cursor)["RegionAddress"] = 0x2fff0; break;
                        case "region-too-long": Fields(state, cursor)["RegionLength"] = 65538; break;
                        case "region-position": Fields(state, cursor)["RegionOffset"] = 514; break;
                        case "descriptor-index": Fields(state, cursor)["DescriptorIndex"] = 513; break;
                        case "descriptor-address": Fields(state, cursor)["DescriptorAddress"] = 0x10008; break;
                        case "irq-history": Fields(state, cursor)["IrqHigh"] = true; break;
                        case "BM-timer-missing": Fields(state, "piix-ide")["_events"] = JsonSerializer.SerializeToNode(new long[] { 0, 0 }, StateValues.Json); break;
                        case "BM-timer-owner":
                        {
                            long token = Fields(state, "piix-ide")["_events"]![0]!.GetValue<long>();
                            state["scheduler"]!["events"]!.AsArray().Single(e => e!["id"]!.GetValue<long>() == token)!["owner"] = "ata0/_drives/0"; break;
                        }
                        case "BM-timer-canceled": state["scheduler"]!["cancelled"]!.AsArray().Add(Fields(state, "piix-ide")["_events"]![0]!.GetValue<long>()); break;
                    }
                });
                // This copied own MemoryDisk base has no active writer/reader; any attempt to open media now fails as IOException.
                using var locked = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
                string before = State(m); byte[] ram = m.Memory.Ram.ToArray(); bool rejected = false;
                try { using var wrong = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !Directory.Exists(branch) && before == State(m) && ram.SequenceEqual(m.Memory.Ram));
                using var session = new MachineSession(m); long revision = session.Revision, generation = session.InputGeneration;
                var response = Request(session, "LoadSnapshot", new { path = saved, destination = branch });
                Check(!response.Success && ReferenceEquals(session.Machine, m) && revision == session.Revision && generation == session.InputGeneration && !Directory.Exists(branch));
                Check(before == State(m) && ram.SequenceEqual(m.Memory.Ram) && Request(session, "Ping", new { }).Success);
                long instructions = m.Cpu.Instructions; Check(m.RunSteps(3) == 3 && m.Cpu.Instructions == instructions + 3 && m.Fault is null, "Rejected import made the original guest unusable.");
            });
        test("M4 PIIX IDE source-profile tamper rejects before copied media opening", () =>
        {
            using var f = new SnapshotTests.Fixture(Profile(), false); var m = f.Machine; OwnCode(m);
            string saved = f.PathFor("profile.dossav"), branch = f.PathFor("profile.dosebranch"); SnapshotStore.Capture(m, saved);
            string mediaPath = Path.Combine(saved, "media.dosemedia", "media.json"); var media = JsonNode.Parse(File.ReadAllBytes(mediaPath))!.AsObject(); media["machine"]!["hardware"]!["id"] = MachineProfile.PiixIrqProfileId;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(media, StateValues.Json); File.WriteAllBytes(mediaPath, bytes);
            string manifestPath = Path.Combine(saved, "snapshot.json"); var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))!.AsObject(); manifest["mediaSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, StateValues.Json));
            using var locked = new FileStream(Path.Combine(saved, "media.dosemedia", "ide0.img"), FileMode.Open, FileAccess.Read, FileShare.None);
            string before = State(m); bool rejected = false; try { using var wrong = SnapshotStore.Load(saved, branch); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.Exists(branch) && State(m) == before && m.RunSteps(3) == 3 && m.Fault is null);
        });
    }
}
