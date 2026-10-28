// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Own cooked images, explicit target slots and cold owner replacement without opening any existing medium.</summary>
internal static class AtapiMediaTests
{
    /// <summary>Registers file-format, protected-lease, profile and actual IPC adoption boundaries.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int bytes in new[] { 0, 1, 512, 2047, 2049, 4095 })
            test("ATAPI cooked ISO rejects malformed byte extent and releases its failed lease " + bytes, () =>
            {
                using var files = new AtapiTestSupport.Files(); string path = files.PathFor("malformed.iso"); File.WriteAllBytes(path, new byte[bytes]); bool rejected = false;
                try { using var invalid = new ReadOnlyIsoImage(path); } catch (InvalidDataException) { rejected = true; }
                AtapiTestSupport.Check(rejected); using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); AtapiTestSupport.Check(writable.Length == bytes);
            });
        test("ATAPI cooked image reads independent complete 2048-byte blocks and excludes writers", () =>
        {
            using var files = new AtapiTestSupport.Files(); using (var iso = new ReadOnlyIsoImage(files.Iso))
            {
                AtapiTestSupport.Check(iso.BlockCount == 11); byte[] bytes = Enumerable.Repeat((byte)0xa7, 2050).ToArray(); iso.ReadBlock(3, bytes);
                AtapiTestSupport.Check(bytes.AsSpan(0, 2048).SequenceEqual(AtapiTestSupport.Block(3)) && bytes[2048] == 0xa7 && bytes[2049] == 0xa7);
                bool denied = false; try { using var invalid = new FileStream(files.Iso, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { denied = true; } AtapiTestSupport.Check(denied, "Live ISO allowed a writer");
                using var second = iso.Reopen(); byte[] read = new byte[2048]; second.ReadBlock(10, read); AtapiTestSupport.Check(read.SequenceEqual(AtapiTestSupport.Block(10)));
            }
            using var released = new FileStream(files.Iso, FileMode.Open, FileAccess.ReadWrite, FileShare.None); AtapiTestSupport.Check(released.Length == 11 * 2048);
        });
        foreach (long lba in new long[] { -1, 11, long.MaxValue })
            test("ATAPI invalid cooked LBA has no destination effect " + lba, () =>
            {
                using var files = new AtapiTestSupport.Files(); using var iso = new ReadOnlyIsoImage(files.Iso); byte[] buffer = Enumerable.Repeat((byte)0x6d, 2048).ToArray(); bool rejected = false;
                try { iso.ReadBlock(lba, buffer); } catch (ArgumentOutOfRangeException) { rejected = true; } AtapiTestSupport.Check(rejected && buffer.All(v => v == 0x6d));
                iso.ReadBlock(2, buffer); AtapiTestSupport.Check(buffer.SequenceEqual(AtapiTestSupport.Block(2)));
            });
        test("ATAPI short cooked destination is rejected before consuming protected image bytes", () =>
        {
            using var files = new AtapiTestSupport.Files(); using var iso = new ReadOnlyIsoImage(files.Iso); byte[] buffer = Enumerable.Repeat((byte)0x6d, 2047).ToArray(); bool rejected = false;
            try { iso.ReadBlock(0, buffer); } catch (ArgumentException) { rejected = true; } AtapiTestSupport.Check(rejected && buffer.All(v => v == 0x6d));
        });
        foreach (int slot in Enumerable.Range(0, 4))
        {
            test("ATAPI fixed IDE reservation rejects HDD collision before either missing file opens " + slot, () =>
            {
                using var files = new AtapiTestSupport.Files(); var definition = new MachineDefinition { Hardware = AtapiTestSupport.Profile(slot),
                    HardDisks = [new(slot, files.PathFor("missing-hdd.img"))], OpticalDiscs = [new(slot, files.PathFor("missing-cd.iso"))] };
                bool rejected = false; try { using var invalid = MachineFactory.FromDefinition(definition); } catch (ArgumentException) { rejected = true; } AtapiTestSupport.Check(rejected);
            });
            test("ATAPI installed empty drive and explicit cooked media retain their exact slot " + slot, () =>
            {
                using var files = new AtapiTestSupport.Files(); using var empty = files.Machine(null, slot); using var loaded = files.Machine(files.Iso, slot);
                AtapiTestSupport.Check(empty.Atapi is not null && empty.OpticalDisc is null && empty.CaptureDefinition().OpticalDiscs.Length == 0);
                AtapiTestSupport.Check(loaded.Atapi is not null && loaded.OpticalDisc!.BlockCount == 11 && loaded.CaptureDefinition().OpticalDiscs.Single().Slot == slot);
                AtapiTestSupport.Check(loaded.GetStatus().OpticalDisc is { } status && status.Slot == slot && status.Bytes == 11 * 2048 && loaded.GetStatus().HardDisks!.All(d => d.Slot != slot));
            });
        }
        test("ATAPI optical binding on an earlier board rejects before missing image opens", () =>
        {
            using var files = new AtapiTestSupport.Files(); var definition = new MachineDefinition { Hardware = new(), OpticalDiscs = [new(2, files.PathFor("missing.iso"))] }; bool rejected = false;
            try { using var invalid = MachineFactory.FromDefinition(definition); } catch (ArgumentException) { rejected = true; } AtapiTestSupport.Check(rejected);
        });
        foreach (string change in new[] { "slot", "cpu", "clock", "ram", "video", "model" })
            test("ATAPI incompatible profile rejects before optical opening " + change, () =>
            {
                using var files = new AtapiTestSupport.Files(); var profile = AtapiTestSupport.Profile(); profile = change switch
                { "slot" => profile with { AtapiSlot = 4 }, "cpu" => profile with { Cpu = "i486" }, "clock" => profile with { CpuHz = 90_000_000 }, "ram" => profile with { RamMiB = 16 }, "video" => profile with { VideoCard = "dose-vga-development" }, _ => profile with { OpticalDrive = "unknown-packet-target" } };
                var definition = new MachineDefinition { Hardware = profile, OpticalDiscs = [new(2, files.PathFor("missing.iso"))] }; bool rejected = false;
                try { using var invalid = MachineFactory.FromDefinition(definition); } catch (ArgumentException) { rejected = true; } AtapiTestSupport.Check(rejected);
            });
        foreach (string invalid in new[] { "missing.iso", "bad.iso" })
            test("ATAPI cold optical IPC failure retains old owner RAM revision generation and ISO lease " + invalid, () =>
            {
                using var files = new AtapiTestSupport.Files(); File.WriteAllBytes(files.PathFor("bad.iso"), new byte[512]); using var session = new MachineSession(files.Machine(files.Iso)); var old = session.Machine;
                AtapiTestSupport.Loop(old); old.Memory.Ram[0x2a31] = 0xb7; string state = AtapiTestSupport.State(old); byte[] ram = old.Memory.Ram.ToArray(); long revision = session.Revision, generation = session.InputGeneration;
                var response = AtapiTestSupport.Send(session, "SetOpticalDisc", new { path = files.PathFor(invalid) }); AtapiTestSupport.Check(!response.Success && ReferenceEquals(session.Machine, old));
                AtapiTestSupport.Check(session.Revision == revision && session.InputGeneration == generation && state == AtapiTestSupport.State(old) && old.Memory.Ram.AsSpan().SequenceEqual(ram));
                AtapiTestSupport.Check(AtapiTestSupport.Send(session, "Ping", new { }).Success && old.RunSteps(3) == 3 && old.Fault is null);
                byte[] block = new byte[2048]; old.OpticalDisc!.ReadBlock(3, block); AtapiTestSupport.Check(block.SequenceEqual(AtapiTestSupport.Block(3)));
            });
        foreach (string replacement in new[] { "same", "other", "remove" })
            test("ATAPI successful optical IPC is a real cold adoption with correct old lease handling " + replacement, () =>
            {
                using var files = new AtapiTestSupport.Files(); using var session = new MachineSession(files.Machine(files.Iso)); var old = session.Machine; AtapiTestSupport.Loop(old); old.Memory.Ram[0x2a31] = 0xb7;
                long revision = session.Revision, generation = session.InputGeneration; string? path = replacement == "same" ? files.Iso : replacement == "other" ? files.OtherIso : null;
                var response = AtapiTestSupport.Send(session, "SetOpticalDisc", new { path }); AtapiTestSupport.Check(response.Success && !ReferenceEquals(session.Machine, old));
                AtapiTestSupport.Check(session.Revision > revision && session.InputGeneration > generation && session.Machine.Memory.Ram[0x2a31] == 0 && session.Machine.Cpu.Eip == 0xfff0 && session.Machine.Cpu.TimestampCounter == 0);
                AtapiTestSupport.Check(session.Machine.CaptureDefinition().OpticalDiscs.Length == (path is null ? 0 : 1) && session.Machine.Atapi is not null);
                if (path is null) AtapiTestSupport.Check(session.Machine.OpticalDisc is null);
                else { byte[] block = new byte[2048]; session.Machine.OpticalDisc!.ReadBlock(3, block); AtapiTestSupport.Check(block.SequenceEqual(AtapiTestSupport.Block(3, replacement == "other" ? 0x92 : 0x51))); }
                if (replacement != "same") { using var released = new FileStream(files.Iso, FileMode.Open, FileAccess.Write, FileShare.None); AtapiTestSupport.Check(released.Length == 11 * 2048); }
            });
        foreach (bool loaded in new[] { false, true })
            test("ATAPI image factory slot zero reserves the real CD target without inventing a demo HDD " + loaded, () =>
            {
                using var files = new AtapiTestSupport.Files(); using var machine = MachineFactory.FromImagesWithOptical(AtapiTestSupport.Profile(0), loaded ? files.Iso : null);
                var definition = machine.CaptureDefinition(); AtapiTestSupport.Check(machine.Atapi is not null && machine.Profile.AtapiSlot == 0 && !definition.BuiltInTestDisk && definition.HardDisks.Length == 0 && machine.GetStatus().HardDisks!.Count == 0);
                AtapiTestSupport.Check(definition.OpticalDiscs.Length == (loaded ? 1 : 0) && (machine.OpticalDisc is not null) == loaded);
                if (loaded) { byte[] block = new byte[2048]; machine.OpticalDisc!.ReadBlock(3, block); AtapiTestSupport.Check(block.SequenceEqual(AtapiTestSupport.Block(3))); }
            });
    }
}

/// <summary>Shared own file and packet helpers for full-media tests, with no dependency on existing OS or user files.</summary>
internal static class AtapiTestSupport
{
    /// <summary>Requires a concrete media, owner or architectural result.</summary>
    public static void Check(bool condition, string why = "ATAPI owned-media invariant") { if (!condition) throw new Exception(why); }
    /// <summary>Builds only the fixed reviewed board with one explicitly identified packet target.</summary>
    public static MachineProfile Profile(int slot = 2) => new() { Id = MachineProfile.AtapiProfileId, Cpu = MachineProfile.P54cCpuId, CpuHz = 100_000_000, RamMiB = 32,
        VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = slot, Name = "Own ATAPI validation PC" };
    /// <summary>Generates a complete independent 2048-byte block with visible quarter and LBA boundaries.</summary>
    public static byte[] Block(long lba, int marker = 0x51) => Enumerable.Range(0, 2048).Select(i => unchecked((byte)(lba * 29 + i * 13 + (i >> 9) * 0x37 + marker))).ToArray();
    /// <summary>Own paths, ISO bytes and HDD bytes, all created within one checked disposable fixture root.</summary>
    public sealed class Files : IDisposable
    {
        /// <summary>Unique checked fresh artifact directory for only this fixture.</summary>
        public readonly string Root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests/atapi-owned-" + Guid.NewGuid().ToString("N")));
        /// <summary>First own cooked basis path.</summary>
        public string Iso => PathFor("own-a.iso");
        /// <summary>Second independently patterned cooked basis path.</summary>
        public string OtherIso => PathFor("own-b.iso");
        /// <summary>Creates two own cooked bases and one own small HDD basis, with no existing file access.</summary>
        public Files()
        {
            Directory.CreateDirectory(Root); File.WriteAllBytes(Iso, Enumerable.Range(0, 11).SelectMany(i => Block(i)).ToArray());
            File.WriteAllBytes(OtherIso, Enumerable.Range(0, 11).SelectMany(i => Block(i, 0x92)).ToArray()); File.WriteAllBytes(PathFor("own-hdd.img"), Enumerable.Range(0, 64 * 512).Select(i => unchecked((byte)(i * 7 + 0x31))).ToArray());
        }
        /// <summary>Names only one direct fixture child.</summary>
        public string PathFor(string name) { Check(Path.GetFileName(name) == name); return Path.Combine(Root, name); }
        /// <summary>Constructs a real PC with an independently writable HDD outside the reserved packet slot.</summary>
        public PcMachine Machine(string? iso, int slot = 2) => MachineFactory.FromDefinition(new MachineDefinition { Hardware = Profile(slot), Name = "Own optical PC", HardDisks = [new(slot == 0 ? 1 : 0, PathFor("own-hdd.img"), PathFor("own-active-" + Guid.NewGuid().ToString("N") + ".cow"))], OpticalDiscs = iso is null ? [] : [new(slot, iso)] });
        /// <summary>Deletes only the verified fresh own directory after all caller-owned PCs and streams have been disposed.</summary>
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests")); Check(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root).StartsWith("atapi-owned-", StringComparison.Ordinal)); Directory.Delete(Root, true);
        }
    }
    /// <summary>Runs genuine finite RAM instructions while leaving the original ROM and external host untouched.</summary>
    public static void Loop(PcMachine m)
    {
        m.Cpu.SetSegment(Segment.Cs, 0); m.Cpu.SetSegment(Segment.Ss, 0); m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetRegister(Register.Sp, 0x9000); m.Cpu.Eflags = 2; m.Cpu.Eip = 0x1000;
        new byte[] { 0xfa, 0x90, 0xeb, 0xfd }.CopyTo(m.Memory.Ram, 0x1000); Check(m.RunSteps(2) == 2 && m.Fault is null);
    }
    /// <summary>Captures every explicit component field and named scheduler operation.</summary>
    public static string State(PcMachine m) => JsonSerializer.Serialize(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Compares full registered state and every raw RAM byte across two real machines.</summary>
    public static void Same(PcMachine a, PcMachine b) => Check(State(a) == State(b) && a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "Complete ATAPI PC future differs");
    /// <summary>Executes a real owner IPC request without activating any native input or endpoint.</summary>
    public static IpcResponse Send(MachineSession session, string command, object payload)
    { var request = session.Enqueue(IpcProtocol.CreateRequest(command, payload)); session.PumpCommands(); return request.GetAwaiter().GetResult(); }
}
