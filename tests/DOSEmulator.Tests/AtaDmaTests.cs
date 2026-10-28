// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent ATA DMA sector, IRQ, task-file and raw future tests, without a PCI engine or external media.</summary>
internal static class AtaDmaTests
{
    /// <summary>Deterministic owned sectors with explicit successful and failed backend effects.</summary>
    private sealed class Disk(byte marker, int sectors = 4096) : IWritableBlockDevice
    {
        /// <summary>Fixed complete-sector capacity; no large backing allocation is required.</summary>
        public long SectorCount => sectors;
        /// <summary>Identifies only this deterministic private test medium.</summary>
        public string Name => "Own ATA DMA disk " + marker;
        /// <summary>Only complete successfully committed sectors, retained as independent expected bytes.</summary>
        public readonly Dictionary<long, byte[]> Writes = [];
        /// <summary>Actual backend-call counts and exact independently injected failure LBAs.</summary>
        public long ReadCalls, WriteCalls, FailReadAt = -1, FailWriteAt = -1;
        /// <summary>Number of actual backend flush requests.</summary>
        public int Flushes;
        /// <summary>Returns immutable expected sector content independently from the ATA command engine.</summary>
        public byte[] Sector(long lba) => Writes.TryGetValue(lba, out var saved) ? saved.ToArray() : Pattern(marker, lba);
        /// <summary>Reads one complete sector, failing at the exact controlled backend address.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        {
            if (lba < 0 || lba >= sectors || lba == FailReadAt) throw new IOException("Own injected read failure.");
            Sector(lba).CopyTo(destination); ReadCalls++;
        }
        /// <summary>Commits exactly one complete sector or produces no media effect on failure.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        {
            if (lba < 0 || lba >= sectors || lba == FailWriteAt) throw new IOException("Own injected write failure.");
            Check(source.Length == 512); Writes[lba] = source.ToArray(); WriteCalls++;
        }
        /// <summary>Records a real flush without inventing a disk write.</summary>
        public void Flush() => Flushes++;
        /// <summary>Copies all owned sectors and backend state for a separate actual future.</summary>
        public Disk Clone()
        {
            var copy = new Disk(marker, sectors) { ReadCalls = ReadCalls, WriteCalls = WriteCalls, FailReadAt = FailReadAt, FailWriteAt = FailWriteAt, Flushes = Flushes };
            foreach (var (lba, data) in Writes) copy.Writes.Add(lba, data.ToArray()); return copy;
        }
        /// <summary>Creates a separately owned deterministic media generation.</summary>
        public IReadOnlyBlockDevice Reopen() => Clone();
        /// <summary>Releases this purely in-memory fixture without opening host media.</summary>
        public void Dispose() { }
    }
    /// <summary>Provides the same exact own sectors without the writable-media interface.</summary>
    private sealed class ReadOnly(Disk disk) : IReadOnlyBlockDevice
    {
        /// <summary>Preserves the original complete-sector extent.</summary>
        public long SectorCount => disk.SectorCount;
        /// <summary>Preserves the explicit owned fixture identity.</summary>
        public string Name => disk.Name;
        /// <summary>Reads one complete sector, failing at the exact controlled backend address.</summary>
        public void ReadSector(long lba, Span<byte> destination) => disk.ReadSector(lba, destination);
        /// <summary>Clones the fixture while retaining its missing writable interface.</summary>
        public IReadOnlyBlockDevice Reopen() => new ReadOnly(disk.Clone());
        /// <summary>Releases this purely in-memory fixture without opening host media.</summary>
        public void Dispose() { }
    }
    /// <summary>One complete owned primary or secondary channel, with actual pin and DMA notices.</summary>
    private sealed class Rig
    {
        /// <summary>Actual deterministic owner scheduler for every ready/commit operation.</summary>
        public readonly DeviceScheduler Clock = new();
        /// <summary>Actual channel under test, never a synthetic substitute for its register effects.</summary>
        public readonly AtaPioController Ata;
        /// <summary>Independent owned master/slave backends.</summary>
        public readonly Disk A, B;
        /// <summary>Physical primary/secondary task-file and alternate-status windows.</summary>
        public readonly ushort Port, Control;
        /// <summary>Actual externally visible channel IRQ transitions after nIEN gating.</summary>
        public readonly List<bool> Edges = [];
        /// <summary>Complete read-only observations at actual DMA state publications.</summary>
        public readonly List<(AtaDmaDirection Direction, bool Ready, int Drive, int Offset, int Remaining, long Lba, ulong Time)> Notices = [];
        /// <summary>Builds both actual task engines and attaches observation-only event handlers.</summary>
        public Rig(bool dma = true, ushort port = 0x1f0, Disk? a = null, Disk? b = null, bool readOnly = false)
        {
            Port = port; Control = port == 0x1f0 ? (ushort)0x3f6 : (ushort)0x376; A = a ?? new(0x31); B = b ?? new(0x92);
            Ata = new(Clock, readOnly ? new ReadOnly(A) : A, readOnly ? new ReadOnly(B) : B, Port, Control, dma);
            Ata.InterruptChanged += Edges.Add;
            Ata.DmaStateChanged += () => Notices.Add((Ata.DmaDirection, Ata.DmaReady, Ata.DmaDriveIndex, Ata.DmaOffset, Ata.DmaRemainingSectors, Ata.DmaLba, Clock.Now));
        }
        /// <summary>Advances virtual time through the real scheduler and its named callbacks.</summary>
        public void Advance(ulong nanoseconds = 5000) => Clock.AdvanceTo(Clock.Now + nanoseconds);
        /// <summary>Selects a real task-file view without altering the latched DMA source.</summary>
        public void Select(int drive) => Ata.Write((ushort)(Port + 6), 0xe0u | (uint)(drive << 4), 1);
        /// <summary>Programs every real LBA28 command byte through the selected channel ports.</summary>
        public void Command(byte command, int drive = 0, uint lba = 3, int sectors = 1)
        {
            Ata.Write((ushort)(Port + 6), 0xe0u | (uint)(drive << 4) | (lba >> 24 & 15), 1);
            Ata.Write((ushort)(Port + 2), (uint)(sectors & 255), 1); Ata.Write((ushort)(Port + 3), lba & 255, 1);
            Ata.Write((ushort)(Port + 4), lba >> 8 & 255, 1); Ata.Write((ushort)(Port + 5), lba >> 16 & 255, 1);
            Ata.Write((ushort)(Port + 7), command, 1);
        }
        /// <summary>Reconstructs the currently visible task address only from real register reads.</summary>
        public uint Lba => Ata.Read((ushort)(Port + 3), 1) | Ata.Read((ushort)(Port + 4), 1) << 8 | Ata.Read((ushort)(Port + 5), 1) << 16 | (Ata.Read((ushort)(Port + 6), 1) & 15) << 24;
        /// <summary>Reads the actual visible sector-count register.</summary>
        public uint Count => Ata.Read((ushort)(Port + 2), 1);
        /// <summary>Reads the actual device error register without acknowledging IRQ.</summary>
        public uint Error => Ata.Read((ushort)(Port + 1), 1);
        /// <summary>Performs the real status read and its device-specific interrupt acknowledgement.</summary>
        public uint Status() => Ata.Read((ushort)(Port + 7), 1);
        /// <summary>Executes actual IDENTIFY and collects every real returned PIO data word.</summary>
        public byte[] Identify()
        {
            Ata.Write((ushort)(Port + 7), 0xec, 1); Advance(); byte[] data = new byte[512];
            for (int i = 0; i < 512; i += 2) { uint word = Ata.Read(Port, 2); data[i] = (byte)word; data[i + 1] = (byte)(word >> 8); }
            Status(); return data;
        }
        /// <summary>Issues actual SET FEATURES operands and command through the task file.</summary>
        public void Features(byte feature, byte mode)
        { Ata.Write((ushort)(Port + 1), feature, 1); Ata.Write((ushort)(Port + 2), mode, 1); Ata.Write((ushort)(Port + 7), 0xef, 1); }
    }
    /// <summary>Exact expected bytes depend only on independently fixed media address and test operands.</summary>
    private static byte[] Pattern(byte marker, long lba) => Enumerable.Range(0, 512).Select(i => unchecked((byte)(marker + lba * 29 + i * 7))).ToArray();
    /// <summary>Requires concrete bytes, addresses, phases, pins or deadlines.</summary>
    private static void Check(bool value, string why = "ATA DMA handshake invariant failed.") { if (!value) throw new Exception(why); }
    /// <summary>Enumerates only this real controller and its owned master/slave state nodes.</summary>
    private static Dictionary<string, IStateObject> Graph(Rig r)
    {
        var graph = new Dictionary<string, IStateObject> { ["ata"] = r.Ata };
        foreach (var (name, child) in r.Ata.StateChildren) graph["ata/" + name] = child; return graph;
    }
    /// <summary>Captures exact raw fields and named scheduler operations at an owner boundary.</summary>
    private static string State(Rig r)
    {
        var graph = Graph(r); var owners = graph.ToDictionary(p => (object)p.Value, p => p.Key, ReferenceEqualityComparer.Instance);
        return JsonSerializer.Serialize(new { Components = graph.ToDictionary(p => p.Key, p => p.Value.SaveState()), Scheduler = r.Clock.SaveState(owners) }, StateValues.Json);
    }
    /// <summary>Reconstructs a fresh channel and media generations, loading parent then children without pin publication.</summary>
    private static Rig Restore(Rig source)
    {
        var restored = new Rig(source.Ata.DmaAvailable, source.Port, source.A.Clone(), source.B.Clone());
        var before = Graph(source); var after = Graph(restored);
        var owners = before.ToDictionary(p => (object)p.Value, p => p.Key, ReferenceEqualityComparer.Instance);
        var clock = source.Clock.SaveState(owners);
        foreach (var (name, component) in before) after[name].LoadState(component.SaveState());
        restored.Clock.LoadState(clock, after);
        Check(State(source) == State(restored) && restored.Edges.Count == 0 && restored.Notices.Count == 0, "Raw rehydration published or changed a device state."); return restored;
    }
    /// <summary>Registers the functional handshake independently from PRD/BAR/PCI target semantics.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        test("ATA DMA original constructor ABI and default channels remain PIO-only", () =>
        {
            Check(typeof(AtaPioController).GetConstructor([typeof(DeviceScheduler), typeof(IReadOnlyBlockDevice)]) is not null);
            Check(typeof(AtaPioController).GetConstructor([typeof(DeviceScheduler), typeof(IReadOnlyBlockDevice), typeof(IReadOnlyBlockDevice), typeof(ushort), typeof(ushort)]) is not null);
            var drive = typeof(AtaPioController).Assembly.GetType("DOSEmulator.Hardware.AtaPioDrive")!;
            Check(drive.GetConstructor([typeof(DeviceScheduler), typeof(IReadOnlyBlockDevice)]) is not null);
            using var d = new Disk(7); var a = new AtaPioController(new(), d);
            Check(!a.DmaAvailable && a.DmaDirection == AtaDmaDirection.None && !a.DmaReady && a.DmaDriveIndex == -1);
            Check(a.ReadDma(new byte[512]) == 0 && a.WriteDma(new byte[512]) == 0);
        });
        foreach (ushort port in new ushort[] { 0x1f0, 0x170 }) foreach (int drive in new[] { 0, 1 }) foreach (byte command in new byte[] { 0xc8, 0xc9, 0xca, 0xcb })
        {
            test($"ATA DMA old channel rejects command {command:X2} at {port:X}/{drive}", () =>
            {
                var r = new Rig(false, port); r.Command(command, drive); r.Advance();
                Check(r.Ata.Status == 0x51 && r.Error == 4 && r.Ata.DmaDirection == AtaDmaDirection.None && r.Ata.DmaDriveIndex == -1);
                Check(r.A.ReadCalls == 0 && r.B.ReadCalls == 0 && r.A.WriteCalls == 0 && r.B.WriteCalls == 0 && r.Notices.Count == 0);
            });
            test($"ATA DMA exact words commit task and media command {command:X2} at {port:X}/{drive}", () =>
            {
                var r = new Rig(port: port); var disk = drive == 0 ? r.A : r.B; bool write = command is 0xca or 0xcb;
                byte[] first = Pattern(0xe3, 3), second = Pattern(0xa7, 4), destination = Enumerable.Repeat((byte)0x5a, 1024).ToArray();
                r.Command(command, drive, 3, 2);
                Check(r.Ata.DmaDriveIndex == drive && r.Ata.DmaRemainingSectors == 2 && r.Ata.DmaLba == 3 && !r.Ata.DmaReady && r.Ata.Status == 0x80);
                Check(r.Ata.DmaDirection == (write ? AtaDmaDirection.MemoryToDevice : AtaDmaDirection.DeviceToMemory));
                Check(r.Ata.ReadDma(destination) == 0 && r.Ata.WriteDma(first) == 0); r.Advance(4999); Check(!r.Ata.DmaReady); r.Advance(1);
                Check(r.Ata.DmaReady && r.Edges.Count == 0 && r.Count == 2 && r.Lba == 3);
                if (write)
                {
                    Check(r.Ata.WriteDma(first.AsSpan(0, 2)) == 2 && r.Ata.DmaOffset == 2 && disk.Writes.Count == 0 && r.Lba == 3);
                    Check(r.Ata.WriteDma(first.AsSpan(2)) == 510 && !r.Ata.DmaReady && r.Ata.DmaOffset == 512 && r.Count == 2 && r.Lba == 3);
                    Check(r.Ata.WriteDma(second) == 0); r.Advance(4999); Check(disk.Writes.Count == 0); r.Advance(1);
                    Check(disk.Writes[3].SequenceEqual(first) && r.Ata.WrittenSectorCount == 1 && r.Ata.DmaRemainingSectors == 1 && r.Count == 1 && r.Lba == 4 && r.Ata.DmaReady && r.Edges.Count == 0);
                    Check(r.Ata.WriteDma(second) == 512 && disk.Writes.Count == 1); r.Advance(); Check(disk.Writes[4].SequenceEqual(second));
                    Check(r.Ata.WrittenSectorCount == 2 && r.Ata.ReadSectorCount == 0);
                }
                else
                {
                    Check(r.Ata.ReadDma(destination.AsSpan(0, 2)) == 2 && destination.AsSpan(0, 2).SequenceEqual(disk.Sector(3).AsSpan(0, 2)) && r.Ata.DmaOffset == 2);
                    Check(r.Count == 2 && r.Lba == 3 && r.Ata.ReadSectorCount == 0);
                    Check(r.Ata.ReadDma(destination.AsSpan(2)) == 510 && destination.AsSpan(0, 512).SequenceEqual(disk.Sector(3)) && destination.AsSpan(512).ToArray().All(x => x == 0x5a));
                    Check(!r.Ata.DmaReady && r.Ata.ReadSectorCount == 1 && r.Count == 1 && r.Lba == 4 && r.Edges.Count == 0);
                    r.Advance(); Check(r.Ata.ReadDma(destination.AsSpan(512)) == 512 && destination.AsSpan(512).SequenceEqual(disk.Sector(4)));
                    Check(r.Ata.ReadSectorCount == 2 && r.Ata.WrittenSectorCount == 0);
                }
                Check(r.Ata.DmaDirection == AtaDmaDirection.None && !r.Ata.DmaReady && r.Ata.DmaDriveIndex == -1 && r.Count == 0 && r.Lba == 5);
                Check(r.Ata.Status == 0x50 && r.Edges.SequenceEqual(new[] { true })); r.Ata.Read(r.Control, 1); Check(r.Ata.InterruptAsserted); r.Status(); Check(r.Edges.SequenceEqual(new[] { true, false }));
                Check((drive == 0 ? r.B : r.A).ReadCalls == 0 && (drive == 0 ? r.B : r.A).Writes.Count == 0);
            });
        }
        foreach (byte command in new byte[] { 0xc8, 0xca }) foreach (int consumed in new[] { 0, 2, 510 })
            test($"ATA DMA PIO data ports and wrong direction cannot consume {command:X2}/{consumed}", () =>
            {
                var r = new Rig(); r.Command(command); r.Advance(); byte[] data = Pattern(0x77, 3);
                if (consumed != 0) { if (command == 0xc8) r.Ata.ReadDma(new byte[consumed]); else r.Ata.WriteDma(data.AsSpan(0, consumed)); }
                string before = State(r);
                Check(r.Ata.Read(r.Port, 1) == 255 && r.Ata.Read(r.Port, 2) == 65535 && r.Ata.Read(r.Port, 4) == 65535);
                r.Ata.Write(r.Port, 0x1212, 1); r.Ata.Write(r.Port, 0x3434, 2); r.Ata.Write(r.Port, 0x56565656, 4);
                Check((command == 0xc8 ? r.Ata.WriteDma(data) : r.Ata.ReadDma(new byte[512])) == 0 && State(r) == before);
                Check((command == 0xc8 ? r.Ata.ReadDma(new byte[1]) : r.Ata.WriteDma(data.AsSpan(0, 1))) == 0 && State(r) == before);
                Check((command == 0xc8 ? r.Ata.ReadDma(new byte[3]) : r.Ata.WriteDma(data.AsSpan(0, 3))) == 2);
            });
        foreach (int drive in new[] { 0, 1 }) foreach (byte command in new byte[] { 0xc8, 0xca })
            test($"ATA DMA selected task-file view cannot steal active drive {drive}/{command:X2}", () =>
            {
                var r = new Rig(); r.Command(command, drive); r.Advance(); r.Select(1 - drive);
                r.Ata.Write((ushort)(r.Port + 7), 0xc8, 1);
                Check(r.Ata.DmaDriveIndex == drive && r.Ata.DmaLba == 3 && r.Ata.DmaRemainingSectors == 1 && r.Ata.DmaReady);
                byte[] data = Pattern(0x54, 3); if (command == 0xc8) { Check(r.Ata.ReadDma(data) == 512 && data.SequenceEqual((drive == 0 ? r.A : r.B).Sector(3))); }
                else { Check(r.Ata.WriteDma(data) == 512); r.Advance(); Check((drive == 0 ? r.A : r.B).Writes[3].SequenceEqual(data)); }
                Check((drive == 0 ? r.B : r.A).ReadCalls == 0 && (drive == 0 ? r.B : r.A).Writes.Count == 0 && r.Ata.InterruptAsserted);
                r.Status(); Check(r.Ata.InterruptAsserted, "Wrong selected status acknowledged the active device."); r.Select(drive); r.Status(); Check(!r.Ata.InterruptAsserted);
            });
        foreach (byte command in new byte[] { 0xc8, 0xca })
            test("ATA DMA nIEN retains terminal latch without data-ready IRQ " + command.ToString("X2"), () =>
            {
                var r = new Rig(); r.Ata.Write(r.Control, 2, 1); r.Command(command); r.Advance();
                if (command == 0xc8) r.Ata.ReadDma(new byte[512]); else { r.Ata.WriteDma(new byte[512]); r.Advance(); }
                Check(r.Edges.Count == 0 && r.Ata.Status == 0x50); r.Ata.Read(r.Control, 1); Check(!r.Ata.InterruptAsserted);
                r.Ata.Write(r.Control, 0, 1); Check(r.Edges.SequenceEqual(new[] { true })); r.Status(); Check(r.Edges.SequenceEqual(new[] { true, false }));
            });
        foreach (byte command in new byte[] { 0xc8, 0xca }) foreach (int phase in new[] { 0, 1, 2, 3 })
            test($"ATA DMA SRST cancels only uncommitted operation {command:X2}/{phase}", () =>
            {
                var r = new Rig(); r.Command(command, sectors: 2);
                if (phase > 0) r.Advance();
                if (phase == 2) { if (command == 0xc8) r.Ata.ReadDma(new byte[2]); else r.Ata.WriteDma(new byte[2]); }
                if (phase == 3) { if (command == 0xc8) r.Ata.ReadDma(new byte[512]); else r.Ata.WriteDma(new byte[512]); }
                r.Ata.Write(r.Control, 4, 1); Check(r.Ata.DmaDirection == AtaDmaDirection.None && !r.Ata.DmaReady && r.Ata.Status == 0x80);
                r.Ata.Write(r.Control, 0, 1); r.Advance(20000);
                Check(r.Ata.Status == 0x50 && r.Ata.DmaDriveIndex == -1 && !r.Ata.InterruptAsserted && r.A.WriteCalls == 0 && r.B.WriteCalls == 0);
                Check(r.Ata.ReadSectorCount == (command == 0xc8 && phase == 3 ? 1 : 0));
                r.Command(0xc8, lba: 7); r.Advance(); byte[] data = new byte[512]; Check(r.Ata.ReadDma(data) == 512 && data.SequenceEqual(r.A.Sector(7)));
            });
        foreach (byte command in new byte[] { 0xc8, 0xca })
            test("ATA DMA sector count zero transfers 256 real sectors " + command.ToString("X2"), () =>
            {
                var r = new Rig(); r.Command(command, sectors: 0); Check(r.Ata.DmaRemainingSectors == 256);
                for (int i = 0; i < 256; i++)
                {
                    if (!r.Ata.DmaReady) r.Advance();
                    byte[] data = Pattern(0x69, i + 3);
                    if (command == 0xc8) { Check(r.Ata.ReadDma(data) == 512 && data.SequenceEqual(r.A.Sector(i + 3))); }
                    else { Check(r.Ata.WriteDma(data) == 512); r.Advance(); Check(r.A.Writes[i + 3].SequenceEqual(data)); }
                    Check(r.Edges.Count == (i == 255 ? 1 : 0));
                }
                Check(r.Count == 0 && r.Lba == 259 && r.Ata.DmaDirection == AtaDmaDirection.None && (command == 0xc8 ? r.Ata.ReadSectorCount : r.Ata.WrittenSectorCount) == 256);
            });
        test("ATA DMA only complete successful write sectors survive later backend failure", () =>
        {
            var r = new Rig(a: new Disk(0x31) { FailWriteAt = 4 }); r.Command(0xca, sectors: 2); r.Advance();
            byte[] data = Pattern(0x33, 3); r.Ata.WriteDma(data); r.Advance(); Check(r.A.Writes[3].SequenceEqual(data) && r.Lba == 4 && r.Count == 1);
            r.Ata.WriteDma(Pattern(0x44, 4)); r.Advance(); Check(r.A.Writes.Count == 1 && r.Ata.WrittenSectorCount == 1 && r.Lba == 4 && r.Count == 1 && r.Error == 0x40 && r.Ata.Status == 0x51 && r.Ata.InterruptAsserted);
            Check(r.Ata.DmaDirection == AtaDmaDirection.None && r.Ata.DmaDriveIndex == -1 && r.Ata.WriteDma(data) == 0);
        });
        foreach (bool failRead in new[] { false, true })
            test("ATA DMA read failure preserves completed prefix and remaining task " + failRead, () =>
            {
                var disk = new Disk(0x31, failRead ? 8 : 4) { FailReadAt = failRead ? 4 : -1 }; var r = new Rig(a: disk); r.Command(0xc8, sectors: 2); r.Advance();
                byte[] data = new byte[512]; Check(r.Ata.ReadDma(data) == 512 && data.SequenceEqual(disk.Sector(3))); r.Advance();
                Check(r.Ata.ReadSectorCount == 1 && r.Lba == 4 && r.Count == 1 && r.Ata.Status == 0x51 && r.Error == (failRead ? 0x40u : 0x10u) && r.Ata.InterruptAsserted && r.Ata.DmaDirection == AtaDmaDirection.None);
            });
        test("ATA DMA read-only media accepts C8 but rejects CA before a timer or backend write", () =>
        {
            var r = new Rig(readOnly: true); r.Command(0xca); Check(r.Error == 4 && r.Ata.Status == 0x51 && r.Ata.DmaDirection == AtaDmaDirection.None && r.Clock.NextEventTime is null && r.A.WriteCalls == 0);
            r.Status(); r.Command(0xc8); r.Advance(); byte[] data = new byte[512]; Check(r.Ata.ReadDma(data) == 512 && data.SequenceEqual(r.A.Sector(3)));
        });
        test("ATA DMA IDENTIFY advertises only functional MDMA0 and explicit SET FEATURES selection", () =>
        {
            var old = new Rig(false); var dma = new Rig(); byte[] oldBytes = old.Identify(), bytes = dma.Identify();
            Check(oldBytes[98] == 0 && oldBytes[99] == 2 && oldBytes[126] == 0 && oldBytes[127] == 0);
            Check(bytes[98] == 0 && bytes[99] == 3 && bytes[126] == 1 && bytes[127] == 0);
            bytes[99] = 2; bytes[126] = 0; Check(bytes.SequenceEqual(oldBytes), "Unrelated IDENTIFY bytes changed.");
            dma.Features(3, 0x20); Check(dma.Ata.Status == 0x50 && dma.Ata.InterruptAsserted); dma.Status(); bytes = dma.Identify(); Check(bytes[126] == 1 && bytes[127] == 1);
            foreach (byte mode in new byte[] { 0, 8 }) { dma.Features(3, mode); dma.Status(); bytes = dma.Identify(); Check(bytes[126] == 1 && bytes[127] == 0); }
            dma.Features(3, 0x20); dma.Status(); dma.Ata.Reset(); bytes = dma.Identify(); Check(bytes[126] == 1 && bytes[127] == 0);
        });
        foreach (var selection in new (byte Feature, byte Mode)[] { (0, 0x20), (2, 0x20), (3, 0x21), (3, 0x22), (3, 0x40), (3, 0x0c), (0x82, 0) })
            test($"ATA DMA SET FEATURES rejects unimplemented {selection.Feature:X2}/{selection.Mode:X2}", () =>
            {
                var r = new Rig(); r.Features(selection.Feature, selection.Mode); Check(r.Ata.Status == 0x51 && r.Error == 4 && r.Ata.InterruptAsserted && r.Ata.DmaDirection == AtaDmaDirection.None);
                r.Status(); var data = r.Identify(); Check(data[126] == 1 && data[127] == 0);
            });
        foreach (bool dma in new[] { false, true })
            test("ATA DMA capability leaves existing PIO read/write timing and IRQ phases intact " + dma, () =>
            {
                var r = new Rig(dma); r.Command(0x20, sectors: 2); r.Advance(); Check(r.Ata.InterruptAsserted && r.Ata.DmaDirection == AtaDmaDirection.None && !r.Ata.DmaReady); r.Status();
                for (int sector = 0; sector < 2; sector++)
                {
                    if (sector != 0) { r.Advance(); Check(r.Ata.InterruptAsserted); r.Status(); }
                    byte[] data = new byte[512]; for (int i = 0; i < 512; i += 2) { uint word = r.Ata.Read(r.Port, 2); data[i] = (byte)word; data[i + 1] = (byte)(word >> 8); }
                    Check(data.SequenceEqual(r.A.Sector(sector + 3)));
                }
                r.Command(0x30); r.Advance(); Check(!r.Ata.InterruptAsserted); for (int i = 0; i < 256; i++) r.Ata.Write(r.Port, 0xa569, 2);
                Check(r.A.WriteCalls == 0 && r.Ata.Status == 0x80); r.Advance(); Check(r.Ata.InterruptAsserted && r.Ata.WrittenSectorCount == 1 && r.A.Writes[3].Where((x, i) => x != (i % 2 == 0 ? 0x69 : 0xa5)).Count() == 0);
                Check(r.Notices.Count == 0);
            });
        foreach (int phase in Enumerable.Range(0, 8))
            test("ATA DMA exact raw channel and named scheduler continuation phase " + phase, () =>
            {
                var r = new Rig(); bool write = phase is >= 3 and <= 5; if (phase == 7) r.Ata.Write(r.Control, 2, 1);
                r.Command(write ? (byte)0xca : (byte)0xc8, sectors: phase is 6 or 7 ? 1 : 2);
                if (phase is 1 or 2 or 4 or 5 or 6 or 7) r.Advance();
                if (phase is 1 or 4) { if (write) r.Ata.WriteDma(new byte[2]); else r.Ata.ReadDma(new byte[2]); }
                if (phase is 2 or 5 or 6 or 7) { if (write) r.Ata.WriteDma(new byte[512]); else r.Ata.ReadDma(new byte[512]); }
                var restored = Restore(r);
                for (int i = 0; i < 35; i++)
                {
                    if (r.Ata.DmaReady)
                    {
                        if (r.Ata.DmaDirection == AtaDmaDirection.DeviceToMemory)
                        { byte[] a = new byte[514], b = new byte[514]; Check(r.Ata.ReadDma(a) == restored.Ata.ReadDma(b) && a.SequenceEqual(b)); }
                        else
                        { byte[] data = Pattern(0x87, r.Ata.DmaLba); int offset = r.Ata.DmaOffset; Check(r.Ata.WriteDma(data.AsSpan(offset)) == restored.Ata.WriteDma(data.AsSpan(offset))); }
                    }
                    r.Advance(1000); restored.Advance(1000); Check(State(r) == State(restored));
                    Check(r.A.Writes.Count == restored.A.Writes.Count && r.A.Writes.All(p => restored.A.Writes[p.Key].SequenceEqual(p.Value)));
                }
                Check(r.Ata.DmaDirection == AtaDmaDirection.None && restored.Ata.DmaDirection == AtaDmaDirection.None);
                Check(r.Edges.TakeLast(restored.Edges.Count).SequenceEqual(restored.Edges));
            });
        test("ATA DMA rejected write range schedules no media commit", () =>
        {
            var r = new Rig(a: new Disk(0x31, 4)); r.Command(0xca, lba: 3, sectors: 2);
            Check(r.Ata.Status == 0x51 && r.Error == 0x10 && r.Ata.DmaDriveIndex == -1 && r.Clock.NextEventTime is null);
            r.Advance(20000); Check(r.A.Writes.Count == 0 && r.Ata.WriteDma(new byte[512]) == 0 && r.Ata.WrittenSectorCount == 0);
        });
        test("ATA DMA reset preserves earlier successful write and discards only partial next sector", () =>
        {
            var r = new Rig(); r.Command(0xca, sectors: 2); r.Advance(); byte[] data = Pattern(0xd1, 3);
            r.Ata.WriteDma(data); r.Advance(); r.Ata.WriteDma(new byte[2]); r.Ata.Reset(); r.Advance(20000);
            Check(r.A.Writes.Count == 1 && r.A.Writes[3].SequenceEqual(data) && r.Ata.WrittenSectorCount == 1 && !r.Ata.InterruptAsserted && r.Ata.DmaDriveIndex == -1);
        });
        test("ATA DMA LBA28 task carry follows actual sector handshakes", () =>
        {
            var r = new Rig(a: new Disk(0x31, 0x2000000)); r.Command(0xc8, lba: 0xffffff, sectors: 2); r.Advance();
            Check(r.Ata.ReadDma(new byte[512]) == 512 && r.Lba == 0x1000000 && r.Count == 1 && r.Ata.DmaLba == 0x1000000);
            r.Advance(); Check(r.Ata.ReadDma(new byte[512]) == 512 && r.Lba == 0x1000001 && r.Count == 0 && r.Ata.ReadSectorCount == 2);
        });
        test("ATA DMA absent device never admits a command or touches the other drive", () =>
        {
            var clock = new DeviceScheduler(); using var disk = new Disk(0x31); var a = new AtaPioController(clock, disk, null, 0x1f0, 0x3f6, true);
            a.Write(0x1f6, 0xf0, 1); a.Write(0x1f7, 0xc8, 1); clock.AdvanceTo(20000);
            Check(a.Status == 0 && a.DmaDirection == AtaDmaDirection.None && a.DmaDriveIndex == -1 && a.ReadDma(new byte[512]) == 0 && disk.ReadCalls == 0 && !a.InterruptAsserted);
        });
        test("ATA DMA rejected transfer mode retains previously selected MDMA0", () =>
        {
            var r = new Rig(); r.Features(3, 0x20); r.Status(); r.Features(3, 0x22); Check(r.Error == 4); r.Status();
            byte[] data = r.Identify(); Check(data[126] == 1 && data[127] == 1);
            var old = new Rig(false); old.Features(3, 0x20); Check(old.Ata.Status == 0x51 && old.Error == 4);
        });
        test("ATA DMA real terminal callback observes already copied complete destination", () =>
        {
            var r = new Rig(); r.Command(0xc8); r.Advance(); byte[] destination = new byte[512]; bool saw = false;
            r.Ata.DmaStateChanged += () => { if (r.Ata.DmaDirection == AtaDmaDirection.None) { Check(destination.SequenceEqual(r.A.Sector(3))); saw = true; } };
            Check(r.Ata.ReadDma(destination) == 512 && saw);
        });
    }
}