// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

internal static class StorageTests
{
    private sealed class UnreadableDataDisk : IReadOnlyBlockDevice
    {
        /// <summary>Capacity in complete 512-byte sectors, fixed while the protected medium is open.</summary>
        public long SectorCount => 4;
        /// <summary>Human-readable name of this configured component or media binding.</summary>
        public string Name => "Bootable with unreadable data sectors";
        /// <summary>Reads one checked 512-byte sector into caller storage without modifying the underlying image.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        {
            if (lba != 0) throw new IOException("Injected medium read failure");
            DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(destination);
        }
        /// <summary>Acquires a separately disposable media instance or prepared journal lease; base images remain protected.</summary>
        public IReadOnlyBlockDevice Reopen() => new UnreadableDataDisk();
        /// <summary>Releases the resources or ownership lease held by this instance.</summary>
        public void Dispose() { }
    }
    private sealed class Fixture
    {
        public readonly string Base, Overlay;
        public readonly byte[] Original;
        /// <summary>Builds the isolated fixture fixture and initializes its caller-owned test state.</summary>
        public Fixture()
        {
            string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "storage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Base = Path.Combine(directory, "base.img"); Overlay = Path.Combine(directory, "private.cow");
            Original = new byte[4 * 512];
            DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(Original);
            Original.AsSpan(512).Fill(0x35); File.WriteAllBytes(Base, Original);
        }
        /// <summary>Acquires the fixture's protected base/private-journal device for a persistence case.</summary>
        public CopyOnWriteDisk Open() => new(Base, Overlay);
        /// <summary>Hashes the original private base fixture and rejects any accidental write-through.</summary>
        public void OriginalUnchanged() => Check(SHA256.HashData(Original).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(Base))), "Base image changed.");
    }
    /// <summary>Fails the regression when its required invariant is false, preserving the supplied diagnostic context.</summary>
    private static void Check(bool value, string message = "Storage assertion failed.") { if (!value) throw new Exception(message); }
    /// <summary>Requires the supplied operation to throw the selected exception type.</summary>
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    /// <summary>Builds a deterministic caller-owned pattern used to detect missing, aliased or reordered transfer data.</summary>
    private static byte[] Pattern(byte value) { byte[] bytes = new byte[512]; bytes.AsSpan().Fill(value); return bytes; }
    /// <summary>Reads an actual device sector and compares all bytes with the independent expected contents.</summary>
    private static void ExpectSector(IReadOnlyBlockDevice disk, int lba, byte[] expected)
    {
        byte[] actual = new byte[512]; disk.ReadSector(lba, actual); Check(actual.AsSpan().SequenceEqual(expected), "Sector contents differ.");
    }
    /// <summary>Submits the requested operation through the component's normal command interface.</summary>
    private static void Command(AtaPioController ata, byte command, int lba, int count = 1)
    {
        ata.Write(0x1f6, (uint)(0xe0 | (lba >> 24 & 15)), 1); ata.Write(0x1f2, (uint)count, 1);
        ata.Write(0x1f3, (uint)lba & 255, 1); ata.Write(0x1f4, (uint)(lba >> 8) & 255, 1);
        ata.Write(0x1f5, (uint)(lba >> 16) & 255, 1); ata.Write(0x1f7, command, 1);
    }
    /// <summary>Sends the controlled test data through the real device-facing transfer interface.</summary>
    private static void Send(AtaPioController ata, byte value, int words = 256)
    {
        for (int i = 0; i < words; i++) ata.Write(0x1f0, (uint)(value | value << 8), 2);
    }
    /// <summary>Boots the supplied private/synthetic medium through the emulated CPU and firmware.</summary>
    private static PcMachine Boot(IReadOnlyBlockDevice disk)
    {
        var machine = new PcMachine(new(), disk); machine.RunSteps(100000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "Test disk did not boot."); return machine;
    }
    /// <summary>Executes the real guest ROM service and checks its return to the test sentinel.</summary>
    private static void Bios(PcMachine machine, ushort ax)
    {
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.Eip = 0x1000; machine.Memory.WriteByte(0x1000, 0xf4);
        machine.Cpu.SetRegister(Register.Ax, ax); machine.Cpu.EnterRealModeInterrupt(0x13); machine.RunSteps(1_000_000);
        Check(machine.Fault is null && machine.Cpu.Halted, machine.Fault ?? "BIOS did not return.");
    }
    /// <summary>Registers the named storage regression cases with the common runner.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("BIOS fixed-disk AH04 consumes actual ATA sectors without touching caller memory", () =>
        {
            var f = new Fixture(); using var m = Boot(new ReadOnlyRawDisk(f.Base));
            m.Memory.Ram.AsSpan(0x4000, 1536).Fill(0xa7); long reads = m.Ata.ReadSectorCount;
            m.Cpu.SetSegment(Segment.Es, 0); m.Cpu.SetRegister(Register.Bx, 0x4000);
            m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Dx, 0x80);
            var commands = new List<byte>(); m.Ports.Accessed += (p, v, _, w) => { if (p == 0x1f7 && w) commands.Add((byte)v); };
            Bios(m, 0x0403);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Cpu.GetRegister(Register.Ax) == 3 && m.Memory.ReadByte(0x474) == 0);
            Check(m.Ata.ReadSectorCount == reads + 3 && m.Ata.WrittenSectorCount == 0 && commands.SequenceEqual(new byte[] { 0x20, 0x20, 0x20 }));
            Check(m.Memory.Ram.AsSpan(0x4000, 1536).ToArray().All(b => b == 0xa7));
            Check(m.Cpu.GetRegister(Register.Bx) == 0x4000 && m.Cpu.GetRegister(Register.Cx) == 2 && m.Cpu.GetRegister(Register.Dx) == 0x80);
            f.OriginalUnchanged();
        });
        test("BIOS fixed-disk AH04 verifies the DOS setup 126-sector request across heads", () =>
        {
            byte[] image = new byte[128 * 512]; DevelopmentFirmware.CreateDemoDisk().AsSpan(0, 512).CopyTo(image);
            using var m = Boot(new MemoryDisk(image, "verify fixture")); long reads = m.Ata.ReadSectorCount;
            m.Memory.Ram.AsSpan(0x2ffff, 126 * 512).Fill(0x67);
            m.Cpu.SetSegment(Segment.Es, 0x2000); m.Cpu.SetRegister(Register.Bx, 0xffff);
            m.Cpu.SetRegister(Register.Cx, 1); m.Cpu.SetRegister(Register.Dx, 0x80); Bios(m, 0x047e);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Ata.ReadSectorCount == reads + 126);
            Check(m.Memory.Ram.AsSpan(0x2ffff, 126 * 512).ToArray().All(b => b == 0x67));
            Check(m.Cpu.GetSegment(Segment.Es) == 0x2000 && m.Cpu.GetRegister(Register.Bx) == 0xffff);
        });
        test("BIOS fixed-disk AH04 rejects zero count and end-of-media without any ATA read", () =>
        {
            var f = new Fixture(); using var m = Boot(f.Open()); long reads = m.Ata.ReadSectorCount;
            m.Cpu.SetRegister(Register.Cx, 4); m.Cpu.SetRegister(Register.Dx, 0x80);
            Bios(m, 0x0402); Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && m.Cpu.GetRegister(Register.Ax) == 0x0402);
            Bios(m, 0x0400); Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && m.Cpu.GetRegister(Register.Ax) == 0x0400);
            Check(m.Ata.ReadSectorCount == reads && m.Ata.WrittenSectorCount == 0 && m.Memory.ReadByte(0x474) == 4);
        });
        test("BIOS fixed-disk AH04 exposes real medium read errors instead of reporting verification", () =>
        {
            using var m = Boot(new UnreadableDataDisk()); long reads = m.Ata.ReadSectorCount;
            m.Memory.Ram.AsSpan(0x4000, 512).Fill(0xc7); m.Cpu.SetSegment(Segment.Es, 0);
            m.Cpu.SetRegister(Register.Bx, 0x4000); m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Dx, 0x80);
            Bios(m, 0x0401);
            Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && m.Cpu.GetRegister(Register.Ax) == 0x0401);
            Check(m.Ata.Status == 0x51 && m.Ata.Read(0x1f1, 1) == 0x40 && m.Ata.ReadSectorCount == reads);
            Check(m.Memory.Ram.AsSpan(0x4000, 512).ToArray().All(b => b == 0xc7));
        });
        test("BIOS fixed-disk AH04 failure and success share last-status service", () =>
        {
            var f = new Fixture(); using var m = Boot(new ReadOnlyRawDisk(f.Base));
            m.Cpu.SetRegister(Register.Cx, 5); m.Cpu.SetRegister(Register.Dx, 0x80); Bios(m, 0x0401); Bios(m, 0x0100);
            Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && m.Cpu.GetRegister(Register.Ax) == 0x0400);
            m.Cpu.SetRegister(Register.Cx, 4); Bios(m, 0x0401); Bios(m, 0x0100);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Cpu.GetRegister(Register.Ax) == 0 && m.Memory.ReadByte(0x474) == 0);
        });
        test("overlay persists last write and leaves base and untouched sectors intact", () =>
        {
            var f = new Fixture();
            using (var disk = f.Open())
            {
                ExpectSector(disk, 1, Pattern(0x35)); disk.WriteSector(1, Pattern(0x12)); disk.WriteSector(1, Pattern(0xa5)); disk.Flush();
                ExpectSector(disk, 1, Pattern(0xa5)); ExpectSector(disk, 2, Pattern(0x35));
            }
            using (var disk = f.Open()) { ExpectSector(disk, 1, Pattern(0xa5)); ExpectSector(disk, 2, Pattern(0x35)); }
            f.OriginalUnchanged();
        });
        test("overlay rejects a changed base identity and releases handles after rejection", () =>
        {
            var f = new Fixture(); using (var disk = f.Open()) disk.WriteSector(1, Pattern(7));
            byte[] changed = (byte[])f.Original.Clone(); changed[600] ^= 1; File.WriteAllBytes(f.Base, changed);
            Reject<InvalidDataException>(() => f.Open()); File.WriteAllBytes(f.Base, f.Original);
            using var restored = f.Open(); ExpectSector(restored, 1, Pattern(7));
        });
        test("overlay enforces exclusive ownership and rejects base as overlay", () =>
        {
            var f = new Fixture(); using var disk = f.Open();
            Reject<IOException>(() => f.Open()); Reject<ArgumentException>(() => new CopyOnWriteDisk(f.Base, f.Base));
            Reject<IOException>(() => new FileStream(f.Base, FileMode.Open, FileAccess.Write));
        });
        test("overlay recovers incomplete tail without discarding committed sectors", () =>
        {
            var f = new Fixture(); using (var disk = f.Open()) disk.WriteSector(1, Pattern(0x5a));
            long committed = new FileInfo(f.Overlay).Length;
            using (var file = new FileStream(f.Overlay, FileMode.Append)) file.Write(new byte[123]);
            using (var disk = f.Open()) { ExpectSector(disk, 1, Pattern(0x5a)); disk.WriteSector(2, Pattern(0x6b)); }
            Check(new FileInfo(f.Overlay).Length == committed + 552);
            using var reopened = f.Open(); ExpectSector(reopened, 2, Pattern(0x6b));
        });
        test("overlay refuses corrupted complete records instead of hiding data loss", () =>
        {
            var f = new Fixture(); using (var disk = f.Open()) disk.WriteSector(1, Pattern(1));
            byte[] bytes = File.ReadAllBytes(f.Overlay); bytes[100] ^= 1; File.WriteAllBytes(f.Overlay, bytes);
            Reject<InvalidDataException>(() => f.Open()); Check(File.ReadAllBytes(f.Overlay).AsSpan().SequenceEqual(bytes));
        });
        test("overlay validates sector range and buffer before journal mutation", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); long before = new FileInfo(f.Overlay).Length;
            Reject<ArgumentOutOfRangeException>(() => disk.WriteSector(4, Pattern(1)));
            Reject<ArgumentOutOfRangeException>(() => disk.WriteSector(0, new byte[511]));
            Reject<ArgumentOutOfRangeException>(() => disk.ReadSector(-1, new byte[512]));
            Check(new FileInfo(f.Overlay).Length == before); f.OriginalUnchanged();
        });
        test("overlay prepared reopen is inactive until old machine releases ownership", () =>
        {
            var f = new Fixture(); var disk = f.Open(); disk.WriteSector(1, Pattern(9));
            using var replacement = (IWritableBlockDevice)disk.Reopen();
            Reject<InvalidOperationException>(() => replacement.WriteSector(1, Pattern(8)));
            Reject<InvalidOperationException>(() => disk.Reopen());
            disk.Dispose(); ExpectSector(replacement, 1, Pattern(9)); replacement.WriteSector(2, Pattern(8));
            Reject<ObjectDisposedException>(() => disk.ReadSector(0, new byte[512]));
        });
        test("overlay abandoned prepared reopen preserves current owner", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); disk.Reopen().Dispose(); disk.WriteSector(1, Pattern(3));
            using var next = disk.Reopen(); ExpectSector(disk, 1, Pattern(3));
        });
        test("ATA write commits complete sectors with busy DRQ IRQ and task progress", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); var clock = new DeviceScheduler(); var ata = new AtaPioController(clock, disk);
            Command(ata, 0x30, 1, 2); Check(ata.Status == 0x80); clock.AdvanceTo(5000);
            Check(ata.Status == 0x58 && !ata.InterruptAsserted); Send(ata, 0x71, 255);
            ExpectSector(disk, 1, Pattern(0x35)); Send(ata, 0x71, 1); Check(ata.Status == 0x80);
            clock.AdvanceTo(10000); Check(ata.Status == 0x58 && ata.InterruptAsserted);
            Check(ata.Read(0x1f2, 1) == 1 && ata.Read(0x1f3, 1) == 2); ata.Read(0x1f7, 1);
            Send(ata, 0x72); clock.AdvanceTo(15000); Check(ata.Status == 0x50 && ata.WrittenSectorCount == 2);
            ExpectSector(disk, 1, Pattern(0x71)); ExpectSector(disk, 2, Pattern(0x72));
            ata.Write(0x1f7, 0xe7, 1); Check(ata.Status == 0x50 && ata.InterruptAsserted); f.OriginalUnchanged();
        });
        test("ATA reset cancels a staged write before its commit event", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); var clock = new DeviceScheduler(); var ata = new AtaPioController(clock, disk);
            Command(ata, 0x30, 1); clock.AdvanceTo(5000); Send(ata, 0x13);
            ata.Write(0x3f6, 4, 1); ata.Write(0x3f6, 0, 1); clock.AdvanceTo(10000);
            Check(ata.WrittenSectorCount == 0 && ata.Status == 0x50); ExpectSector(disk, 1, Pattern(0x35));
        });
        test("ATA range errors never modify an overlay sector", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); var ata = new AtaPioController(new(), disk);
            Command(ata, 0x30, 3, 2); Send(ata, 0xee);
            Check(ata.Status == 0x51 && ata.Read(0x1f1, 1) == 0x10 && ata.WrittenSectorCount == 0);
            ExpectSector(disk, 3, Pattern(0x35));
        });
        test("guest BIOS CHS writes across a segment boundary through ATA", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); using var m = Boot(disk);
            Pattern(0x87).CopyTo(m.Memory.Ram, 0x1fe00); Pattern(0x98).CopyTo(m.Memory.Ram, 0x20000);
            m.Cpu.SetSegment(Segment.Es, 0x1000); m.Cpu.SetRegister(Register.Bx, 0xfe00);
            m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Dx, 0x80); Bios(m, 0x0302);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Ata.WrittenSectorCount == 2);
            ExpectSector(disk, 1, Pattern(0x87)); ExpectSector(disk, 2, Pattern(0x98)); f.OriginalUnchanged();
        });
        test("guest BIOS EDD write and read share the same persisted disk contents", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); using var m = Boot(disk);
            Pattern(0x52).CopyTo(m.Memory.Ram, 0x4000);
            m.Cpu.SetSegment(Segment.Ds, 0); m.Cpu.SetRegister(Register.Si, 0x3000); m.Cpu.SetRegister(Register.Dx, 0x80);
            m.Memory.WriteWord(0x3000, 16); m.Memory.WriteWord(0x3002, 1); m.Memory.WriteWord(0x3004, 0x4000);
            m.Memory.WriteWord(0x3006, 0); m.Memory.WriteDword(0x3008, 2); m.Memory.WriteDword(0x300c, 0);
            Bios(m, 0x4300); Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Ata.WrittenSectorCount == 1);
            Array.Clear(m.Memory.Ram, 0x4000, 512); Bios(m, 0x4200);
            Check((m.Cpu.Eflags & X86Cpu.Carry) == 0 && m.Memory.Ram.AsSpan(0x4000, 512).SequenceEqual(Pattern(0x52)));
        });
        test("guest BIOS readonly media returns write protect and performs no transfer", () =>
        {
            var f = new Fixture(); using var m = Boot(new ReadOnlyRawDisk(f.Base));
            m.Cpu.SetRegister(Register.Bx, 0x4000); m.Cpu.SetRegister(Register.Cx, 2); m.Cpu.SetRegister(Register.Dx, 0x80);
            Bios(m, 0x0301); Check((m.Cpu.Eflags & X86Cpu.Carry) != 0 && (m.Cpu.GetRegister(Register.Ax) >> 8 & 255) == 3);
            Check(m.Ata.WrittenSectorCount == 0); f.OriginalUnchanged();
        });
        test("configuration power cycle transfers overlay ownership and boots same disk", () =>
        {
            var f = new Fixture(); using var disk = f.Open(); disk.WriteSector(2, Pattern(0x55));
            using var session = new MachineSession(new PcMachine(new(), disk));
            var request = new IpcRequest(1, "storage-cycle", "CommitConfigurationChange",
                JsonSerializer.SerializeToElement(new ConfigurationPlan(1, new MachineProfile { RamMiB = 16 }, true), IpcProtocol.JsonOptions));
            var result = session.Enqueue(request); session.PumpCommands(); Check(result.Result.Success);
            session.Machine.RunSteps(100000); Check(session.Machine.Fault is null && session.Machine.Memory.ReadWord(0x500) == 0xbeef);
            using var next = session.Machine.CreatePowerCycle(new()); session.Machine.Dispose(); next.RunSteps(100000);
            Check(next.Fault is null && next.Memory.ReadWord(0x500) == 0xbeef);
        });
    }
}
