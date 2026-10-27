// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Independent media/topology/firmware/configuration regressions with fresh private files.</summary>
internal static class MachineStorageTests
{
    /// <summary>Provides a distinct deterministic sector pattern and a boot sector without allocating the advertised capacity.</summary>
    private sealed class TestDisk(byte marker, long sectors = 4096) : IWritableBlockDevice
    {
        /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
        public long SectorCount => sectors;
        /// <summary>Human-readable name of this configured component or media binding.</summary>
        public string Name => "Independent test disk " + marker;
        public readonly Dictionary<long, byte[]> Writes = [];
        /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        {
            if (lba < 0 || lba >= sectors) throw new IOException("Outside test medium");
            if (Writes.TryGetValue(lba, out var bytes)) bytes.CopyTo(destination);
            else if (lba == 0) DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(destination);
            else for (int i = 0; i < 512; i++) destination[i] = (byte)(marker + lba + i);
        }
        /// <summary>Commits one checked 512-byte sector through the private writable-media implementation.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source) => Writes[lba] = source[..512].ToArray();
        /// <summary>Flushes pending component output at its documented commit boundary.</summary>
        public void Flush() { }
        /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
        public IReadOnlyBlockDevice Reopen() => new TestDisk(marker, sectors);
        /// <summary>Releases the resources or ownership lease held by this instance.</summary>
        public void Dispose() { }
    }

    /// <summary>Creates a unique artifact directory and a valid protected base with an independently known sector.</summary>
    private static (string Root, string Image) Fixture()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "machine-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string path = Path.Combine(root, "base.img");
        byte[] bytes = new byte[1048576]; DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(bytes);
        bytes.AsSpan(512, 512).Fill(0x5a); File.WriteAllBytes(path, bytes); return (root, path);
    }
    /// <summary>Fails a case with a concrete storage invariant rather than merely checking the absence of an exception.</summary>
    private static void Check(bool condition, string message = "Machine storage invariant failed") { if (!condition) throw new Exception(message); }
    /// <summary>Requires rejection before continuing with tests of the original device's usability.</summary>
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    /// <summary>Programs a real ATA LBA28 request through either channel's ports.</summary>
    private static void AtaCommand(AtaPioController channel, ushort port, int drive, byte command, uint lba = 1)
    {
        channel.Write((ushort)(port + 6), 0xe0u | (uint)(drive << 4) | (lba >> 24 & 15), 1);
        channel.Write((ushort)(port + 2), 1, 1); channel.Write((ushort)(port + 3), lba & 255, 1);
        channel.Write((ushort)(port + 4), lba >> 8 & 255, 1); channel.Write((ushort)(port + 5), lba >> 16 & 255, 1);
        channel.Write((ushort)(port + 7), command, 1);
    }
    /// <summary>Enters the actual ROM service and waits for its real IRET to a private HLT sentinel.</summary>
    private static void Bios(PcMachine machine, ushort ax, byte drive)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.Eip = 0x1000; machine.Memory.WriteByte(0x1000, 0xf4);
        machine.Cpu.SetRegister(Register.Ax, ax); machine.Cpu.SetRegister(Register.Dx, drive);
        machine.Cpu.EnterRealModeInterrupt(0x13); machine.RunSteps(1000000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "ROM service did not return");
    }
    /// <summary>Registers size, topology, rollback and persistent-PC cases for the common runner.</summary>
    public static void AddTo(Action<string, Action> add)
    {
        // Registers one named synchronous regression in the surrounding runner.
        void Test(string name, Action run) => add("storage manager: " + name, run);
        foreach (var size in new[] { (1m, "MiB", 1048576L), (512m, "MiB", 536870912L), (1.5m, "GiB", 1610612736L), (128m, "GiB", 137438953472L) })
            Test($"explicit binary size {size.Item1} {size.Item2}", () => Check(RawDiskImage.SizeInBytes(size.Item1, size.Item2) == size.Item3));
        foreach (var size in new[] { 0m, -1m, .00001m, 128.1m, decimal.MaxValue })
            Test("rejects invalid GiB size " + size, () =>
            { try { RawDiskImage.SizeInBytes(size, "GiB"); } catch (Exception e) when (e is ArgumentException or OverflowException) { return; } throw new Exception("Accepted invalid capacity"); });
        Test("creates a completely zero image and never overwrites it", () =>
        {
            var f = Fixture(); string path = Path.Combine(f.Root, "blank.img"); RawDiskImage.Create(path, 1048576);
            Check(File.ReadAllBytes(path).All(b => b == 0)); File.WriteAllBytes(path, [0x91]);
            Reject<IOException>(() => RawDiskImage.Create(path, 1048576)); Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 0x91 }));
        });
        Test("cancellation publishes no incomplete image or temporary file", () =>
        {
            var f = Fixture(); string path = Path.Combine(f.Root, "cancel.img");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            Reject<OperationCanceledException>(() => RawDiskImage.Create(path, 1048576, stop.Token));
            Check(!File.Exists(path) && !Directory.GetFiles(f.Root, "*.tmp").Any());
        });
        foreach (ushort port in new ushort[] { 0x1f0, 0x170 })
        foreach (int slot in new[] { 0, 1 })
        {
            Test($"ATA independent select/read/write {port:X}/{slot}", () =>
            {
                var clock = new DeviceScheduler(); using var a = new TestDisk(0x10); using var b = new TestDisk(0x60);
                var channel = new AtaPioController(clock, a, b, port, (ushort)(port == 0x1f0 ? 0x3f6 : 0x376));
                AtaCommand(channel, port, slot, 0x20); clock.AdvanceTo(clock.Now + 5000);
                Check(channel.Read(port, 2) == (slot == 0 ? 0x1211u : 0x6261u));
                for (int i = 1; i < 256; i++) channel.Read(port, 2);
                channel.Read((ushort)(port + 7), 1); AtaCommand(channel, port, slot, 0x30);
                clock.AdvanceTo(clock.Now + 5000); for (int i = 0; i < 256; i++) channel.Write(port, 0xbeef, 2);
                clock.AdvanceTo(clock.Now + 5000); Check((slot == 0 ? a : b).Writes[1].AsSpan(0, 2).SequenceEqual(new byte[] { 0xef, 0xbe }));
                Check((slot == 0 ? b : a).Writes.Count == 0 && channel.WrittenSectorCount == 1);
            });
            Test($"ATA IRQ nIEN/alternate/status selection {port:X}/{slot}", () =>
            {
                var clock = new DeviceScheduler(); using var a = new TestDisk(1); using var b = new TestDisk(2);
                ushort control = (ushort)(port == 0x1f0 ? 0x3f6 : 0x376); var channel = new AtaPioController(clock, a, b, port, control);
                var edges = new List<bool>(); channel.InterruptChanged += edges.Add;
                AtaCommand(channel, port, slot, 0x20); clock.AdvanceTo(5000); Check(channel.InterruptAsserted);
                channel.Read(control, 1); Check(channel.InterruptAsserted);
                channel.Write(control, 2, 1); Check(!channel.InterruptAsserted); channel.Write(control, 0, 1); Check(channel.InterruptAsserted);
                channel.Read((ushort)(port + 7), 1); Check(!channel.InterruptAsserted && edges.SequenceEqual(new[] { true, false, true, false }));
            });
        }
        foreach (int mask in new[] { 1, 2, 4, 8, 5, 10, 15 })
            Test("BIOS dense drive map and all physical routes mask=" + mask, () =>
            {
                var disks = Enumerable.Range(0, 4).Select(i => (mask & 1 << i) != 0 ? new TestDisk((byte)(0x10 + i * 0x20)) : null).ToArray();
                using var machine = PcMachine.CreateWithDisks(new(), disks);
                machine.RunSteps(200000); Check(machine.Fault is null && machine.Cpu.Halted);
                int count = disks.Count(d => d is not null); Check(machine.Memory.ReadByte(0x475) == count);
                int bios = 0;
                for (int physical = 0; physical < 4; physical++)
                {
                    if (disks[physical] is null) continue;
                    Bios(machine, 0x0800, (byte)(0x80 + bios));
                    Check((machine.Cpu.Eflags & X86Cpu.Carry) == 0 && (machine.Cpu.GetRegister(Register.Dx) & 255) == count);
                    machine.Cpu.SetSegment(Segment.Es, 0); machine.Cpu.SetRegister(Register.Bx, 0x4000); machine.Cpu.SetRegister(Register.Cx, 2);
                    Bios(machine, 0x0201, (byte)(0x80 + bios));
                    Check((machine.Cpu.Eflags & X86Cpu.Carry) == 0 && machine.Memory.ReadByte(0x4000) == 0x11 + physical * 0x20);
                    machine.Memory.Ram.AsSpan(0x4000, 512).Fill((byte)(0xa0 + physical));
                    machine.Cpu.SetRegister(Register.Cx, 3); Bios(machine, 0x0301, (byte)(0x80 + bios));
                    Check((machine.Cpu.Eflags & X86Cpu.Carry) == 0 && disks[physical]!.Writes[2].All(b => b == 0xa0 + physical));
                    bios++;
                }
                Bios(machine, 0x0800, (byte)(0x80 + count)); Check((machine.Cpu.Eflags & X86Cpu.Carry) != 0);
            });
        foreach (long sectors in new[] { 65536L, 1048576L, 4194304L, 16777216L, 268435456L })
            Test("BIOS translated CHS/EDD capacity=" + sectors, () =>
            {
                using var disk = new TestDisk(0x31, sectors); using var machine = new PcMachine(new(), disk);
                machine.RunSteps(200000); Bios(machine, 0x0800, 0x80);
                uint heads = (machine.Cpu.GetRegister(Register.Dx) >> 8 & 255) + 1;
                Check(heads is 16 or 32 or 64 or 128 or 255);
                machine.Cpu.SetSegment(Segment.Ds, 0); machine.Cpu.SetRegister(Register.Si, 0x3000); machine.Memory.WriteWord(0x3000, 26);
                Bios(machine, 0x4800, 0x80); Check(machine.Memory.ReadDword(0x3010) == sectors && machine.Memory.ReadDword(0x3008) == heads);
                machine.Memory.WriteWord(0x3000, 16); machine.Memory.WriteWord(0x3002, 1); machine.Memory.WriteWord(0x3004, 0x4000);
                machine.Memory.WriteWord(0x3006, 0); machine.Memory.WriteDword(0x3008, (uint)(sectors - 1)); machine.Memory.WriteDword(0x300c, 0);
                Bios(machine, 0x4200, 0x80); Check((machine.Cpu.Eflags & X86Cpu.Carry) == 0 && machine.Memory.ReadByte(0x4000) == (byte)(0x31 + sectors - 1));
            });
        Test("relative machine paths round-trip hardware, four disks and private journals", () =>
        {
            var f = Fixture(); var definition = new MachineDefinition { Name = "DOS games", Hardware = new() { RamMiB = 16, VideoCard = "et4000ax-isa-1m-development" },
                HardDisks = Enumerable.Range(0, 4).Select(i => new DiskAttachment(i, f.Image, Path.Combine(f.Root, i + ".cow"))).ToArray() };
            string path = Path.Combine(f.Root, "pc.dospc.json"); MachineDefinitionStore.Save(path, definition);
            Check(!File.ReadAllText(path).Contains(f.Root.Replace("\\", "\\\\")));
            var loaded = MachineDefinitionStore.Load(path); Check(loaded.Id == definition.Id && loaded.Name == definition.Name && loaded.Hardware == definition.Hardware && loaded.HardDisks.SequenceEqual(definition.HardDisks));
            using var machine = MachineFactory.FromDefinition(loaded); Check(machine.GetStatus().HardDisks!.Count == 4 && machine.Profile.RamMiB == 16);
        });
        Test("legacy VGA PCs load without rewriting inputs and save the canonical DOSE identifier", () =>
        {
            var f = Fixture(); var definition = new MachineDefinition { Name = "My DOS games", Hardware = new() { Name = "My custom hardware" },
                HardDisks = [new(0, f.Image)] };
            string legacy = JsonSerializer.Serialize(definition, IpcProtocol.JsonOptions).Replace(DOSEIdentity.VgaModel, "reference-vga-development", StringComparison.Ordinal);
            string oldPath = Path.Combine(f.Root, "old.dospc.json"); File.WriteAllText(oldPath, legacy);
            var loaded = MachineDefinitionStore.Load(oldPath);
            Check(loaded.Id == definition.Id && loaded.Name == definition.Name && loaded.Hardware.Name == definition.Hardware.Name);
            Check(loaded.Hardware.VideoCard == DOSEIdentity.VgaModel && File.ReadAllText(oldPath) == legacy);
            using var machine = MachineFactory.FromDefinition(loaded); machine.RunSteps(200000);
            Check(machine.Fault is null && machine.Cpu.Halted && machine.Profile.VideoCard == DOSEIdentity.VgaModel);
            string newPath = Path.Combine(f.Root, "new.dospc.json"); MachineDefinitionStore.Save(newPath, machine.CaptureDefinition());
            string saved = File.ReadAllText(newPath);
            Check(saved.Contains(DOSEIdentity.VgaModel, StringComparison.Ordinal) && !saved.Contains("reference-vga-development", StringComparison.Ordinal));
            Check(MachineDefinitionStore.Load(newPath).Id == definition.Id && File.ReadAllText(oldPath) == legacy);
        });
        Test("failed machine load releases pending leases and leaves old machine writable", () =>
        {
            var f = Fixture(); var definition = new MachineDefinition { HardDisks = [new(0, f.Image, Path.Combine(f.Root, "0.cow"))] };
            using var original = MachineFactory.FromDefinition(definition);
            var bad = definition with { HardDisks = [definition.HardDisks[0], new(1, Path.Combine(f.Root, "missing.img"))] };
            Reject<FileNotFoundException>(() => MachineFactory.FromDefinition(bad, original));
            using var session = new MachineSession(original);
            var change = session.Enqueue(IpcProtocol.CreateRequest("SetHardDisk", new { slot = 1, path = f.Image, overlayPath = Path.Combine(f.Root, "1.cow") }));
            session.PumpCommands(); Check(change.Result.Success && session.Machine.GetStatus().HardDisks!.Count == 2);
            session.Machine.RunSteps(200000); Check(session.Machine.Fault is null && session.Machine.Cpu.Halted);
        });
        Test("duplicate slots, overlays and image/journal aliases are rejected", () =>
        {
            var f = Fixture(); string cow = Path.Combine(f.Root, "shared.cow");
            Reject<ArgumentException>(() => new MachineDefinition { HardDisks = [new(1, f.Image), new(1, f.Image)] }.Validate());
            Reject<ArgumentException>(() => MachineDefinitionStore.Resolve(new() { HardDisks = [new(0, f.Image, cow), new(1, f.Image, cow.ToUpperInvariant())] }, f.Root));
            Reject<ArgumentException>(() => MachineDefinitionStore.Resolve(new() { HardDisks = [new(0, f.Image, cow), new(1, cow)] }, f.Root));
            Reject<ArgumentException>(() => MachineDefinitionStore.Save(f.Image, new() { HardDisks = [new(0, f.Image)] }));
            Check(new FileInfo(f.Image).Length == 1048576);
        });
        Test("unknown JSON fields and oversized configuration are rejected", () =>
        {
            var f = Fixture(); string path = Path.Combine(f.Root, "bad.json");
            File.WriteAllText(path, "{\"unknownHardware\":true}"); Reject<JsonException>(() => MachineDefinitionStore.Load(path));
            File.WriteAllText(path, new string(' ', 1048577)); Reject<InvalidDataException>(() => MachineDefinitionStore.Load(path));
        });
        Test("saved machine and CLI overrides cannot be mixed", () => Reject<ArgumentException>(() => MachineLaunch.Open(o => o is "--machine" or "--disk" ? "unused" : null)));
        Test("case-aliased duplicate JSON keys cannot hide another configuration", () =>
        {
            var f = Fixture(); string path = Path.Combine(f.Root, "duplicate.json");
            File.WriteAllText(path, "{\"name\":\"first\",\"Name\":\"second\"}"); Reject<JsonException>(() => MachineDefinitionStore.Load(path));
        });
        Test("an active journal can move between physical IDE slots during a cold load", () =>
        {
            var f = Fixture(); var definition = new MachineDefinition { HardDisks = [new(0, f.Image, Path.Combine(f.Root, "move.cow"))] };
            using var old = MachineFactory.FromDefinition(definition);
            using var replacement = MachineFactory.FromDefinition(definition with { HardDisks = [definition.HardDisks[0] with { Slot = 3 }] }, old);
            old.Dispose(); replacement.RunSteps(200000);
            Check(replacement.Fault is null && replacement.Cpu.Halted && replacement.GetStatus().HardDisks![0].Slot == 3);
        });
        Test("null PCM samples are rejected as invalid data before entering the audio queue", () => Reject<ArgumentNullException>(() => new AudioBlock(0, 48000, 2, null!).Validate()));
        Test("unchanged debug snapshots are immutable and invalidate on actual E9 output", () =>
        {
            var board = new DevelopmentBoardPorts(new MemoryBus(1048576)); board.Write(0xe9, 65, 1);
            string first = board.DebugText; Check(ReferenceEquals(first, board.DebugText));
            board.Write(0xe9, 66, 1); Check(first == "A" && board.DebugText == "AB");
        });
        Test("overlay payload cache stays bounded and misses cannot move the append cursor", () =>
        {
            var f = Fixture(); string image = Path.Combine(f.Root, "larger.img"), cow = Path.Combine(f.Root, "bounded.cow");
            RawDiskImage.Create(image, 4 * 1048576); byte[] data = new byte[512];
            using (var disk = new CopyOnWriteDisk(image, cow))
            {
                for (int lba = 0; lba < 5000; lba++)
                { BinaryPrimitives.WriteInt32LittleEndian(data, lba); disk.WriteSector(lba, data); }
                Check(disk.OverlaySectorCount == 5000 && disk.CachedSectorCount == 4096);
                disk.ReadSector(0, data); Check(BinaryPrimitives.ReadInt32LittleEndian(data) == 0);
                disk.ReadSector(1, data); Check(BinaryPrimitives.ReadInt32LittleEndian(data) == 1);
                data.AsSpan().Fill(0xa5); disk.WriteSector(5000, data);
            }
            Check(new FileInfo(cow).Length == 48 + 5001L * 552);
            using var restored = new CopyOnWriteDisk(image, cow);
            restored.ReadSector(0, data); Check(BinaryPrimitives.ReadInt32LittleEndian(data) == 0);
            restored.ReadSector(4999, data); Check(BinaryPrimitives.ReadInt32LittleEndian(data) == 4999);
            restored.ReadSector(5000, data); Check(data.All(b => b == 0xa5));
            Check(restored.OverlaySectorCount == 5001 && restored.CachedSectorCount == 4096);
        });
        Test("saved PC identity survives factory launch, session save and hardware power cycle", () =>
        {
            var f = Fixture(); var definition = new MachineDefinition { Name = "My persistent PC", HardDisks = [new(0, f.Image, Path.Combine(f.Root, "identity.cow"))] };
            using var session = new MachineSession(MachineFactory.FromDefinition(definition));
            Check(session.Machine.CaptureDefinition().Id == definition.Id);
            string path = Path.Combine(f.Root, "saved.dospc.json");
            var save = session.Enqueue(IpcProtocol.CreateRequest("SaveMachineDefinition", new { path, name = "Renamed PC" })); session.PumpCommands();
            Check(save.Result.Success); var saved = MachineDefinitionStore.Load(path);
            Check(saved.Id == definition.Id && saved.Name == "Renamed PC" && session.Machine.CaptureDefinition().Name == "Renamed PC");
            var cycle = session.Enqueue(IpcProtocol.CreateRequest("PowerCycleCurrent")); session.PumpCommands(); Check(cycle.Result.Success);
            Check(session.Machine.CaptureDefinition().Id == saved.Id && session.Machine.CaptureDefinition().Name == saved.Name);
            var load = session.Enqueue(IpcProtocol.CreateRequest("LoadMachineDefinition", new { path })); session.PumpCommands(); Check(load.Result.Success);
            Check(session.Machine.CaptureDefinition().Id == saved.Id && session.Machine.CaptureDefinition().Name == saved.Name);
        });
        Test("a new PC keeps one stable identity before its first save", () =>
        {
            using var machine = PcMachine.CreateDemo(); var first = machine.CaptureDefinition(); var second = machine.CaptureDefinition();
            Check(first.Id == second.Id && first.Name == second.Name);
        });
    }
}
