// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Independent packet-word, phase, IRQ and mixed-channel effects using only freshly specified optical bytes.</summary>
internal static class AtapiTests
{
    /// <summary>Read-only blocks with independently detectable quarter-sector, offset and LBA differences.</summary>
    private sealed class Disc(int blocks = 11) : IReadOnlyOpticalDisc
    {
        /// <summary>Actual block requests, including rejected injected reads.</summary>
        public readonly List<long> Reads = [];
        /// <summary>One exact optional backend failure, independent from the packet decoder.</summary>
        public long FailAt = -1;
        /// <summary>Fixed own complete-block capacity.</summary>
        public long BlockCount => blocks;
        /// <summary>No operating-system or user medium is used by this fixture.</summary>
        public string Name => "Own packet test disc";
        /// <summary>Copies precisely one independently generated cooked block and records the actual request.</summary>
        public void ReadBlock(long lba, Span<byte> destination)
        {
            Reads.Add(lba);
            if (lba < 0 || lba >= blocks || lba == FailAt) throw new IOException("Own optical read failure");
            Check(destination.Length >= 2048); Pattern(lba).CopyTo(destination);
        }
        /// <summary>Acquires an independent fixture with the same immutable block specification.</summary>
        public IReadOnlyOpticalDisc Reopen() => new Disc(blocks) { FailAt = FailAt };
        /// <summary>No host medium or handle was acquired.</summary>
        public void Dispose() { }
    }
    /// <summary>Separate ordinary ATA backend proves that CD packet traffic does not consume or rewrite HDD sectors.</summary>
    private sealed class Disk : IWritableBlockDevice
    {
        /// <summary>Actual complete read/write effects.</summary>
        public int Reads, Writes;
        /// <summary>Own finite HDD capacity measured in 512-byte sectors.</summary>
        public long SectorCount => 64;
        /// <summary>Unambiguous own HDD fixture identity.</summary>
        public string Name => "Own HDD beside ATAPI";
        /// <summary>Returns an independent non-optical 512-byte pattern.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        { Check(lba >= 0 && lba < 64); HddPattern(lba).CopyTo(destination); Reads++; }
        /// <summary>Counts complete writable HDD effects only.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        { Check(lba >= 0 && lba < 64 && source.Length == 512); Writes++; }
        /// <summary>No deferred backend work exists.</summary>
        public void Flush() { }
        /// <summary>Acquires another owned deterministic HDD.</summary>
        public IReadOnlyBlockDevice Reopen() => new Disk();
        /// <summary>No external media were opened.</summary>
        public void Dispose() { }
    }
    /// <summary>Real task-file and optional PIIX routing, with passive physical IRQ observations.</summary>
    private sealed class Rig
    {
        /// <summary>Actual deterministic phase and ATA scheduler.</summary>
        public readonly DeviceScheduler Clock = new();
        /// <summary>Optional freshly specified inserted cooked blocks.</summary>
        public readonly Disc? Medium;
        /// <summary>Separate owned ordinary ATA medium.</summary>
        public readonly Disk Hdd = new();
        /// <summary>Installed packet peer under test.</summary>
        public readonly AtapiCdRom Cd;
        /// <summary>Real channel owner and aggregate interrupt source.</summary>
        public readonly AtaPioController Ata;
        /// <summary>Actual task-file and alternate-status windows.</summary>
        public readonly ushort Port, Control;
        /// <summary>Fixed master or slave packet position.</summary>
        public readonly int Device;
        /// <summary>Observed actual external IRQ edges and scheduler times.</summary>
        public readonly List<(ulong Time, bool High)> Edges = [];
        /// <summary>Own physical bytes for PIIX transaction checks.</summary>
        public readonly MemoryBus Memory;
        /// <summary>Real fixed and dynamic I/O decoding.</summary>
        public readonly IoPortBus Ports = new();
        /// <summary>Optional actual PCI DRAM target.</summary>
        public readonly Intel82437Fx? Host;
        /// <summary>Optional actual legacy target and bus-master wrapper.</summary>
        public readonly Intel82371FbIde? Ide;
        /// <summary>Constructs an installed packet target even when its tray has no medium.</summary>
        public Rig(int device = 0, ushort port = 0x170, bool empty = false, bool piix = false)
        {
            Memory = new(piix ? 32 * 1048576 : 2 * 1048576); Device = device; Port = port; Control = port == 0x1f0 ? (ushort)0x3f6 : (ushort)0x376;
            Medium = empty ? null : new Disc(); Cd = new(Clock, Medium, device);
            Ata = new(Clock, device == 0 ? null : Hdd, device == 1 ? null : Hdd, Port, Control, true, Cd, device);
            Ata.InterruptChanged += high => Edges.Add((Clock.Now, high));
            if (piix)
            {
                Host = new(Memory, new byte[65536]); Host.WriteConfiguration(0x64, 8, 1);
                var other = new AtaPioController(Clock, null, null, port == 0x170 ? (ushort)0x1f0 : (ushort)0x170, port == 0x170 ? (ushort)0x3f6 : (ushort)0x376, true);
                Ide = new(Clock, Host, port == 0x1f0 ? Ata : other, port == 0x170 ? Ata : other, Ports);
                Ports.Map(0x1f0, 8, Ide); Ports.Map(0x3f6, 1, Ide); Ports.Map(0x170, 8, Ide); Ports.Map(0x376, 1, Ide); Ports.MapDynamic(Ide);
                Ide.WriteConfiguration(0x20, 0xc001, 4); Ide.WriteConfiguration(4, 7, 2); Ide.WriteConfiguration(0x40, 0x80008000, 4);
            }
            else { Ports.Map(Port, 8, Ata); Ports.Map(Control, 1, Ata); }
            Select(Device);
        }
        /// <summary>Selects the actual target through its shared task-file port.</summary>
        public void Select(int device) => Write(6, (uint)(0xa0 | device << 4));
        /// <summary>Writes a byte task-file register through the real fixed I/O topology.</summary>
        public void Write(int offset, uint value) => Ports.Write((ushort)(Port + offset), value, 1);
        /// <summary>Reads a byte task-file register; register seven performs its actual acknowledgement.</summary>
        public uint Read(int offset) => Ports.Read((ushort)(Port + offset), 1);
        /// <summary>Samples alternate status without acknowledging any device IRQ.</summary>
        public uint Alt => Ports.Read(Control, 1);
        /// <summary>Advances to one actual queued device transition, rejecting a missing callback rather than fabricating readiness.</summary>
        public void Next()
        { ulong? due = Clock.NextEventTime; Check(due.HasValue, "Busy packet phase has no real deadline"); Clock.AdvanceTo(due!.Value); }
        /// <summary>Waits only while the true device BSY bit is set, with a strict finite callback bound.</summary>
        public void Settled()
        { for (int guard = 0; (Alt & 0x80) != 0; guard++) { Check(guard < 64, "Packet did not leave BSY"); Next(); } }
        /// <summary>Issues PACKET and stops before the first CDB word.</summary>
        public void Begin(ushort limit = 2048, byte features = 0)
        { Select(Device); Write(1, features); Write(4, (byte)limit); Write(5, (byte)(limit >> 8)); Write(7, 0xa0); Settled(); }
        /// <summary>Sends exactly the specified real CDB words, without bypassing packet-command admission.</summary>
        public void Cdb(ReadOnlySpan<byte> command, int words = 6)
        { Check(command.Length == 12); for (int i = 0; i < words * 2; i += 2) Ports.Write(Port, (uint)(command[i] | command[i + 1] << 8), 2); }
        /// <summary>Collects announced data words and terminal status, preserving every real phase boundary.</summary>
        public Result Finish()
        {
            var bytes = new List<byte>(); var phases = new List<int>(); var upper = new List<byte>();
            for (int guard = 0; guard < 4096; guard++)
            {
                Settled(); uint status = Alt;
                if ((status & 8) == 0) return new((byte)Read(7), (byte)Read(1), bytes.ToArray(), phases.ToArray(), upper.ToArray());
                Check((Read(2) & 3) == 2, "Data phase has wrong interrupt reason");
                int count = (int)(Read(4) | Read(5) << 8); Check(count is > 0 and <= 65534, "Invalid actual DRQ byte count");
                phases.Add(count); Read(7);
                for (int i = 0; i < count; i += 2)
                {
                    uint word = Ports.Read(Port, 2); bytes.Add((byte)word);
                    if (i + 1 < count) bytes.Add((byte)(word >> 8)); else upper.Add((byte)(word >> 8));
                }
            }
            throw new Exception("Packet exceeded bounded data phases");
        }
        /// <summary>Executes one complete 12-byte packet through actual DRQ and status ports.</summary>
        public Result Execute(byte[] command, ushort limit = 2048)
        { Begin(limit); Check((Alt & 0x88) == 8 && (Read(2) & 3) == 1, "Packet CDB request missing"); Read(7); Cdb(command); return Finish(); }
        /// <summary>Returns actual fixed-format sense data and leaves no synthesized response.</summary>
        public Result Sense(int allocation = 18) => Execute(Packet(3, 4, (byte)allocation));
        /// <summary>Clears only the permitted initial unit-attention report through a real REQUEST SENSE.</summary>
        public void ClearAttention() { Result sense = Sense(); Check((sense.Status & 1) == 0 && sense.Data.Length == 18); }
        /// <summary>Serializes device fields and published deadlines for side-effect-free access controls.</summary>
        public string State() => JsonSerializer.Serialize(new { Ata = Ata.SaveState(), Cd = Cd.SaveState(), Children = Ata.StateChildren.ToDictionary(p => p.Name, p => p.Value.SaveState()), Clock.Now, Clock.NextEventTime }, StateValues.Json);
    }
    /// <summary>Actual returned data, announced phase sizes and unused high bytes of odd transfers.</summary>
    private sealed record Result(byte Status, byte Error, byte[] Data, int[] Phases, byte[] OddUpper);
    /// <summary>Forms an exact 12-byte CDB with one optional independent parameter byte.</summary>
    private static byte[] Packet(byte opcode, int at = -1, byte value = 0)
    { byte[] bytes = new byte[12]; bytes[0] = opcode; if (at >= 0) bytes[at] = value; return bytes; }
    /// <summary>Encodes the historical big-endian READ(10)/(12) fields independently from task-file byte order.</summary>
    private static byte[] ReadPacket(long lba, uint blocks, bool twelve = false)
    {
        byte[] command = Packet(twelve ? (byte)0xa8 : (byte)0x28); BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), checked((uint)lba));
        if (twelve) BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(6, 4), blocks); else BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(7, 2), checked((ushort)blocks)); return command;
    }
    /// <summary>Generates every expected optical byte without consulting packet state, task-file values or the product read method.</summary>
    private static byte[] Pattern(long lba) => Enumerable.Range(0, 2048).Select(i => unchecked((byte)(lba * 29 + i * 13 + (i >> 9) * 0x37 + 0x51))).ToArray();
    /// <summary>Generates an independently different ordinary HDD sector.</summary>
    private static byte[] HddPattern(long lba) => Enumerable.Range(0, 512).Select(i => unchecked((byte)(lba * 11 + i * 7 + 0xa3))).ToArray();
    /// <summary>Requires actual values or effects at a precisely identified protocol boundary.</summary>
    private static void Check(bool value, string reason = "ATAPI packet invariant") { if (!value) throw new Exception(reason); }
    /// <summary>Requires the complete packet result without accepting DRQ, BUSY or CHECK as a successful short response.</summary>
    private static void Good(Result result) => Check((result.Status & 0x89) == 0 && result.Error == 0, "Packet did not complete successfully");
    /// <summary>Verifies independent fixed-format sense fields after the actual preceding CHECK CONDITION.</summary>
    private static void SenseIs(Rig rig, byte key, byte asc, byte ascq = 0)
    { Result sense = rig.Sense(); Good(sense); Check(sense.Data.Length == 18 && (sense.Data[0] & 0x7f) == 0x70 && (sense.Data[2] & 15) == key && sense.Data[12] == asc && sense.Data[13] == ascq, "Wrong fixed-format sense"); }
    /// <summary>Registers real PIO packet, mixed-channel and phase-boundary regression cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (ushort port in new ushort[] { 0x1f0, 0x170 }) foreach (int device in new[] { 0, 1 })
        {
            test($"ATAPI installed empty target exposes signature {port:X}/{device}", () =>
            {
                var r = new Rig(device, port, true); Check(r.Alt == 0 && r.Read(1) == 1 && r.Read(2) == 1 && r.Read(3) == 1 && r.Read(4) == 0x14 && r.Read(5) == 0xeb);
                r.Select(1 - device); Check((r.Alt & 0x50) == 0x50 && r.Read(4) == 0 && r.Read(5) == 0, "Ordinary HDD signature changed");
                r.Select(device); Check(r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
            });
            test($"ATAPI IDENTIFY PACKET real 256 words and PIO-only capabilities {port:X}/{device}", () =>
            {
                var r = new Rig(device, port); r.Write(7, 0xa1); r.Settled(); Check((r.Alt & 0x88) == 8); r.Read(7); byte[] result = new byte[512];
                for (int i = 0; i < result.Length; i += 2) BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i, 2), (ushort)r.Ports.Read(port, 2));
                Check(BinaryPrimitives.ReadUInt16LittleEndian(result) == 0x85a0 && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(98, 2)) == 0x0200);
                Check(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(126, 2)) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(102, 2)) == 0 && r.Ata.DmaDirection == AtaDmaDirection.None && r.Medium!.Reads.Count == 0);
                Check((r.Alt & 0x88) == 0 && r.Ports.Read(port, 2) == 0xffff && r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
            });
            test($"ATAPI partial twelve-byte CDB cannot execute or consume byte accesses {port:X}/{device}", () =>
            {
                var r = new Rig(device, port); r.ClearAttention(); r.Begin(); r.Read(7); byte[] cdb = ReadPacket(3, 1);
                r.Ports.Write(port, (uint)(cdb[0] | cdb[1] << 8), 2); Check((r.Alt & 8) != 0 && (r.Read(2) & 3) == 1 && r.Medium!.Reads.Count == 0);
                string before = r.State(); r.Ports.Write(port, 0xa5, 1); r.Ports.Read(port, 1); r.Ports.Write(port, 0xa5a5a5a5, 4); r.Ports.Read(port, 4); Check(r.State() == before, "Unsupported data width consumed packet bytes");
                for (int i = 2; i < cdb.Length; i += 2) r.Ports.Write(port, (uint)(cdb[i] | cdb[i + 1] << 8), 2);
                Result result = r.Finish(); Good(result); Check(result.Data.SequenceEqual(Pattern(3)) && r.Medium!.Reads.SequenceEqual(new long[] { 3 }));
            });
            test($"ATAPI CDB DataIn Completion IRQ reasons and terminal edge {port:X}/{device}", () =>
            {
                var r = new Rig(device, port); r.ClearAttention(); r.Edges.Clear(); r.Begin(); Check(r.Ata.InterruptAsserted && (r.Read(2) & 3) == 1);
                r.Read(7); Check(!r.Ata.InterruptAsserted); r.Cdb(ReadPacket(4, 1)); r.Settled(); Check(r.Ata.InterruptAsserted && (r.Read(2) & 3) == 2 && (r.Read(4) | r.Read(5) << 8) == 2048);
                r.Read(7); for (int i = 0; i < 1024; i++) r.Ports.Read(port, 2); r.Settled();
                Check((r.Alt & 0x89) == 0 && (r.Read(2) & 3) == 3 && r.Ata.InterruptAsserted, "No distinct terminal completion IRQ");
                r.Read(7); Check(!r.Ata.InterruptAsserted && r.Edges.Select(v => v.High).SequenceEqual(new[] { true, false, true, false, true, false }));
                Check(r.Edges.Where(v => v.High).Select(v => v.Time).Distinct().Count() == 3 && r.Ata.DmaDirection == AtaDmaDirection.None);
            });
        }
        foreach (bool twelve in new[] { false, true }) foreach (ushort limit in new ushort[] { 510, 511, 2048, 4096, 65534, 65535 })
            test($"ATAPI READ{(twelve ? 12 : 10)} big-endian blocks and even phases limit{limit}", () =>
            {
                var r = new Rig(); r.ClearAttention(); Result result = r.Execute(ReadPacket(2, 3, twelve), limit); Good(result);
                Check(result.Data.SequenceEqual(Pattern(2).Concat(Pattern(3)).Concat(Pattern(4))), "CD blocks were split, duplicated or interpreted as 512-byte sectors");
                Check(result.Phases.Sum() == 6144 && result.Phases.All(v => (v & 1) == 0 && v <= Math.Min(65534, (int)limit)) && result.OddUpper.Length == 0);
                Check(r.Medium!.Reads.SequenceEqual(new long[] { 2, 3, 4 }) && r.Ata.ReadSectorCount == 0 && r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
            });
        foreach (bool twelve in new[] { false, true })
            test("ATAPI zero READ block count is a zero-data command " + twelve, () =>
            {
                var r = new Rig(); r.ClearAttention(); Result result = r.Execute(ReadPacket(4, 0, twelve)); Good(result);
                Check(result.Data.Length == 0 && result.Phases.Length == 0 && r.Medium!.Reads.Count == 0 && r.Ata.ReadSectorCount == 0);
            });
        foreach (byte allocation in new byte[] { 1, 3, 5, 17, 35, 36, 255 })
            test("ATAPI INQUIRY allocation and final odd word " + allocation, () =>
            {
                var r = new Rig(); Result result = r.Execute(Packet(0x12, 4, allocation), 512); Good(result); int count = Math.Min((int)allocation, 36);
                Check(result.Data.Length == count && result.Data[0] == 5 && result.Phases.Sum() == count && r.Medium!.Reads.Count == 0);
                if (count > 1) Check((result.Data[1] & 0x80) != 0); if (count > 4) Check(result.Data[4] == 31);
                Check(result.OddUpper.Length == (count & 1));
                string before = r.State(); Check(r.Ports.Read(r.Port, 2) == 0xffff && r.State() == before, "Odd word leaked extra payload or consumed a later command");
                Result sense = r.Sense(); Good(sense); Check((sense.Data[2] & 15) == 6 && sense.Data[12] == 0x29, "INQUIRY consumed the initial unit attention");
            });
        test("ATAPI READ CAPACITY returns cooked block count and 2048 bytes in big-endian order", () =>
        {
            var r = new Rig(); r.ClearAttention(); Result result = r.Execute(Packet(0x25)); Good(result);
            Check(result.Data.Length == 8 && BinaryPrimitives.ReadUInt32BigEndian(result.Data) == 10 && BinaryPrimitives.ReadUInt32BigEndian(result.Data.AsSpan(4)) == 2048 && r.Medium!.Reads.Count == 0);
        });
        test("ATAPI initial attention prevents a media read until actual REQUEST SENSE", () =>
        {
            var r = new Rig(); Result result = r.Execute(ReadPacket(1, 1)); Check((result.Status & 1) != 0 && result.Data.Length == 0 && r.Medium!.Reads.Count == 0); SenseIs(r, 6, 0x29);
            result = r.Execute(ReadPacket(1, 1)); Good(result); Check(result.Data.SequenceEqual(Pattern(1)));
        });
        foreach (byte opcode in new byte[] { 0xff, 0x2a, 0xaa })
            test("ATAPI unsupported or write packet returns CHECK with no optical or HDD write " + opcode.ToString("X2"), () =>
            {
                var r = new Rig(); r.ClearAttention(); Result result = r.Execute(Packet(opcode)); Check((result.Status & 1) != 0 && result.Data.Length == 0 && r.Medium!.Reads.Count == 0 && r.Hdd.Writes == 0); SenseIs(r, 5, 0x20);
            });
        test("ATAPI exact current optical end is rejected before a block backend call", () =>
        {
            var r = new Rig(); r.ClearAttention(); Result result = r.Execute(ReadPacket(11, 1)); Check((result.Status & 1) != 0 && result.Data.Length == 0 && r.Medium!.Reads.Count == 0); SenseIs(r, 5, 0x21);
        });
        test("ATAPI overflowing READ12 extent cannot wrap to an earlier cooked block", () =>
        {
            var r = new Rig(); r.ClearAttention(); Result result = r.Execute(ReadPacket(0xfffffffe, 4, true)); Check((result.Status & 1) != 0 && result.Data.Length == 0 && r.Medium!.Reads.Count == 0); SenseIs(r, 5, 0x21);
        });
        test("ATAPI failed optical backend produces CHECK and no fabricated successful data", () =>
        {
            var r = new Rig(); r.ClearAttention(); r.Medium!.FailAt = 3; Result result = r.Execute(ReadPacket(3, 1)); Check((result.Status & 1) != 0 && result.Data.Length == 0 && r.Medium.Reads.SequenceEqual(new long[] { 3 }));
            Result sense = r.Sense(); Good(sense); Check((sense.Data[2] & 15) == 3 && sense.Data[12] == 0x11 && r.Hdd.Writes == 0);
        });
        foreach (bool empty in new[] { false, true })
            test("ATAPI empty tray remains an installed inquiry and sense target " + empty, () =>
            {
                var r = new Rig(empty: empty); r.ClearAttention(); Result inquiry = r.Execute(Packet(0x12, 4, 36)); Good(inquiry); Check(inquiry.Data.Length == 36 && inquiry.Data[0] == 5);
                Result ready = r.Execute(Packet(0)); if (empty) { Check((ready.Status & 1) != 0); SenseIs(r, 2, 0x3a); } else Good(ready);
            });
        foreach (byte features in new byte[] { 1, 2, 3, 0x80 })
            test("ATAPI unsupported DMA overlap or feature bits abort before CDB or busmaster effects " + features, () =>
            {
                var r = new Rig(piix: true); r.ClearAttention(); byte[] before = r.Memory.Ram.ToArray(); r.Begin(2048, features);
                Check((r.Alt & 0x89) == 1 && (r.Read(1) & 4) != 0 && r.Ata.DmaDirection == AtaDmaDirection.None && r.Ata.DmaDriveIndex == -1 && r.Medium!.Reads.Count == 0);
                Check(r.Memory.Ram.AsSpan().SequenceEqual(before) && r.Hdd.Reads == 0 && r.Hdd.Writes == 0 && r.Ide!.GetChannelState(1).BytesTransferred == 0);
            });
        foreach (ushort limit in new ushort[] { 0, 1 })
            test("ATAPI unusable byte-count limit cannot create an endless zero-byte DRQ " + limit, () =>
            {
                var r = new Rig(); r.ClearAttention(); r.Begin(limit);
                if ((r.Alt & 8) != 0) { r.Read(7); r.Cdb(ReadPacket(1, 1)); }
                Result result = r.Finish(); Check((result.Status & 1) != 0 && result.Data.Length == 0 && result.Phases.Length == 0 && r.Medium!.Reads.Count == 0);
            });
        test("ATAPI one-byte final response is legal with byte-count limit one", () =>
        {
            var r = new Rig(); Result result = r.Execute(Packet(0x12, 4, 1), 1); Good(result); Check(result.Data.SequenceEqual(new byte[] { 5 }) && result.Phases.SequenceEqual(new[] { 1 }) && result.OddUpper.SequenceEqual(new byte[] { 0 }));
        });
        test("ATAPI no-data TUR accepts zero byte-count without starting a data phase", () =>
        {
            var r = new Rig(); r.ClearAttention(); Result result = r.Execute(Packet(0), 0); Good(result); Check(result.Data.Length == 0 && result.Phases.Length == 0 && r.Medium!.Reads.Count == 0);
        });
        test("ATAPI alternate status never acknowledges the physical packet IRQ", () =>
        {
            var r = new Rig(); r.Begin(); Check(r.Ata.InterruptAsserted); uint status = r.Alt; Check(r.Alt == status && r.Ata.InterruptAsserted);
            r.Read(7); Check(!r.Ata.InterruptAsserted && r.Alt == status); r.Cdb(Packet(0x12, 4, 36)); Good(r.Finish());
        });
        foreach (int device in new[] { 0, 1 })
            test("ATAPI deselected target retains pending IRQ until real reselection " + device, () =>
            {
                var r = new Rig(device); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(2, 1)); r.Select(1 - device); for (int guard = 0; r.Cd.Phase != AtapiPhase.DataIn; guard++) { Check(guard < 64); r.Next(); }
                Check(!r.Ata.InterruptAsserted && r.Ata.SelectedDriveIndex == 1 - device && r.Ata.DmaDirection == AtaDmaDirection.None);
                r.Select(device); Check(r.Ata.InterruptAsserted && (r.Read(2) & 3) == 2); Result result = r.Finish(); Good(result); Check(result.Data.SequenceEqual(Pattern(2)));
            });
        foreach (int device in new[] { 0, 1 })
        test("ATAPI nIEN preserves selection and pending data phase " + device, () =>
        {
            var r = new Rig(device); r.ClearAttention(); r.Ports.Write(r.Control, 2, 1); Check(r.Ata.SelectedDriveIndex == device && r.Cd.Selected); r.Edges.Clear(); r.Begin(); Check(!r.Ata.InterruptAsserted); r.Read(7); r.Cdb(ReadPacket(2, 1)); r.Settled();
            Check(!r.Ata.InterruptAsserted && (r.Read(2) & 3) == 2 && r.Edges.Count == 0); r.Ports.Write(r.Control, 0, 1); Check(r.Ata.InterruptAsserted);
            Good(r.Finish()); Check(r.Edges.Any(v => v.High));
        });
        test("ATAPI PIIX BM interrupt W1C observes actual packet pin edges only", () =>
        {
            var r = new Rig(piix: true); r.ClearAttention(); ushort bm = (ushort)(r.Ide!.BusMasterBase + 8); r.Ports.Write((ushort)(bm + 2), 4, 1);
            r.Begin(); Check((r.Ports.Read((ushort)(bm + 2), 1) & 4) != 0 && r.Ata.InterruptAsserted); r.Ports.Write((ushort)(bm + 2), 4, 1);
            Check((r.Ports.Read((ushort)(bm + 2), 1) & 4) == 0 && r.Ata.InterruptAsserted); _ = r.Alt; Check((r.Ports.Read((ushort)(bm + 2), 1) & 4) == 0);
            r.Read(7); r.Cdb(ReadPacket(1, 1)); r.Settled(); Check((r.Ports.Read((ushort)(bm + 2), 1) & 4) != 0);
            Check(r.Ide.GetChannelState(1).BytesTransferred == 0 && !r.Ide.GetChannelState(1).Active); Good(r.Finish());
        });
        foreach (bool iose in new[] { false, true })
            test("ATAPI PIIX decode gate floats ports without consuming an elapsed data phase " + iose, () =>
            {
                var r = new Rig(piix: true); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(3, 1));
                r.Ide!.WriteConfiguration(iose ? 4 : 0x43, iose ? 6u : 0u, iose ? 2 : 1); for (int guard = 0; r.Cd.Phase != AtapiPhase.DataIn; guard++) { Check(guard < 64); r.Next(); } string before = r.State();
                Check(r.Ports.Read(r.Port, 2) == 0xffff && r.Ports.Read((ushort)(r.Port + 7), 1) == 0xff); r.Ports.Write(r.Port, 0x1234, 2); Check(r.State() == before);
                r.Ide.WriteConfiguration(iose ? 4 : 0x43, iose ? 7u : 0x80u, iose ? 2 : 1); Result result = r.Finish(); Good(result); Check(result.Data.SequenceEqual(Pattern(3)));
            });
        test("ATAPI PIO remains usable while PIIX busmaster permission is clear", () =>
        {
            var r = new Rig(piix: true); r.Ide!.WriteConfiguration(4, 3, 2); r.ClearAttention(); Result result = r.Execute(ReadPacket(1, 1)); Good(result);
            Check(result.Data.SequenceEqual(Pattern(1)) && !r.Ide.BusMasterEnabled && r.Ide.GetChannelState(1).BytesTransferred == 0);
        });
        foreach (int device in new[] { 0, 1 })
            test("ATAPI PIIX DWORD data path requires TIME of the actually selected drive " + device, () =>
            {
                var r = new Rig(device, piix: true); r.ClearAttention(); r.Begin(); r.Read(7); byte[] cdb = Packet(0x12, 4, 1); string before = r.State();
                r.Ports.Write(r.Port, 0x12345678, 4); Check(r.State() == before, "Disabled DWORD path consumed a CDB");
                r.Ide!.WriteConfiguration(0x42, device == 0 ? 1u : 0x10u, 1);
                for (int i = 0; i < 12; i += 4) r.Ports.Write(r.Port, BinaryPrimitives.ReadUInt32LittleEndian(cdb.AsSpan(i, 4)), 4);
                r.Settled(); Check((r.Read(4) | r.Read(5) << 8) == 1); r.Read(7); uint words = r.Ports.Read(r.Port, 4);
                Check(words == 0xffff0005, "DWORD path hid a second word transaction across DRQ end"); r.Settled(); Check((r.Alt & 0x89) == 0); r.Read(7);
            });
        foreach (int words in new[] { 0, 1, 5, 6 })
            test("ATAPI SRST selected active packet cancels exact partial CDB phase " + words, () =>
            {
                var r = new Rig(); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(2, 1), words);
                r.Ports.Write(r.Control, 4, 1); Check((r.Alt & 0x80) != 0); r.Ports.Write(r.Control, 0, 1); r.Settled();
                Check((r.Alt & 0x89) == 0 && r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Ata.DmaDirection == AtaDmaDirection.None);
                if (r.Clock.NextEventTime is { } due) r.Clock.AdvanceTo(due + 10000); Check((r.Alt & 0x89) == 0 && r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
            });
        test("ATAPI software DEVICE RESET does not rewrite the neighboring ATA media", () =>
        {
            var r = new Rig(1); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(2, 1), 5); r.Write(7, 8); r.Settled();
            Check((r.Alt & 0x89) == 0 && r.Read(2) == 1 && r.Read(3) == 1 && r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
        });
        test("ATAPI prevent removal denies eject and allows it only after actual ALLOW", () =>
        {
            var r = new Rig(); r.ClearAttention(); Good(r.Execute(Packet(0x1e, 4, 1))); Result denied = r.Execute(Packet(0x1b, 4, 2)); Check((denied.Status & 1) != 0); SenseIs(r, 5, 0x53, 2);
            Good(r.Execute(ReadPacket(1, 1))); Good(r.Execute(Packet(0x1e))); Good(r.Execute(Packet(0x1b, 4, 2)));
            Result ready = r.Execute(Packet(0)); Check((ready.Status & 1) != 0); Result sense = r.Sense(); Good(sense);
            if ((sense.Data[2] & 15) == 6) { ready = r.Execute(Packet(0)); Check((ready.Status & 1) != 0); SenseIs(r, 2, 0x3a); }
            else Check((sense.Data[2] & 15) == 2 && sense.Data[12] == 0x3a);
            Good(r.Execute(Packet(0x12, 4, 36))); Check(r.Hdd.Writes == 0);
        });
        test("ATAPI actual completed sense clears initial attention", () =>
        {
            var r = new Rig(); r.Begin(); r.Read(7); r.Cdb(Packet(3, 4, 18)); r.Settled(); r.Read(7); Check((r.Ports.Read(r.Port, 2) & 255) == 0x70);
            for (int i = 1; i < 9; i++) r.Ports.Read(r.Port, 2); r.Settled(); r.Read(7); Result ready = r.Execute(Packet(0)); Good(ready); Check(r.Medium!.Reads.Count == 0);
        });
        test("ATAPI packet attempt cannot steal a neighboring active HDD DMA handshake", () =>
        {
            var r = new Rig(1); r.Select(0); r.Write(2, 1); r.Write(3, 3); r.Write(4, 0); r.Write(5, 0); r.Write(6, 0xe0); r.Write(7, 0xc8); r.Next();
            Check(r.Ata.DmaReady && r.Ata.DmaDriveIndex == 0 && r.Ata.DmaLba == 3); r.Select(1); r.Write(1, 0); r.Write(4, 0); r.Write(5, 8); r.Write(7, 0xa0);
            Check(r.Ata.DmaReady && r.Ata.DmaDriveIndex == 0 && r.Ata.DmaLba == 3 && r.Ata.DmaOffset == 0 && r.Medium!.Reads.Count == 0 && (r.Alt & 0x88) == 0);
            byte[] sector = new byte[512]; Check(r.Ata.ReadDma(sector) == 512 && sector.SequenceEqual(HddPattern(3)) && r.Ata.DmaDirection == AtaDmaDirection.None && r.Hdd.Reads == 1);
        });
        test("ATAPI ordinary sibling ATA IDENTIFY and PIO sector semantics stay separate", () =>
        {
            var r = new Rig(); r.Select(1); r.Write(7, 0xec); r.Settled(); r.Read(7); byte[] identify = new byte[512];
            for (int i = 0; i < 512; i += 2) BinaryPrimitives.WriteUInt16LittleEndian(identify.AsSpan(i, 2), (ushort)r.Ports.Read(r.Port, 2));
            Check(BinaryPrimitives.ReadUInt16LittleEndian(identify) != 0x85a0 && (BinaryPrimitives.ReadUInt16LittleEndian(identify.AsSpan(98, 2)) & 0x100) != 0);
            r.Write(2, 1); r.Write(3, 3); r.Write(4, 0); r.Write(5, 0); r.Write(6, 0xf0); r.Write(7, 0x20); r.Settled(); r.Read(7); byte[] bytes = new byte[512];
            for (int i = 0; i < 512; i += 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i, 2), (ushort)r.Ports.Read(r.Port, 2));
            Check(bytes.SequenceEqual(HddPattern(3)) && r.Ata.ReadSectorCount == 1 && r.Medium!.Reads.Count == 0); r.Select(0); Check(r.Read(4) == 0 && r.Read(5) == 0, "Idle CD did not retain actual broadcast task-file writes");
            r.Write(7, 0xec); Check((r.Read(7) & 1) != 0 && r.Read(1) == 4 && r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Medium!.Reads.Count == 0, "CD incorrectly identified itself as the neighboring ATA HDD");
        });
        foreach (byte mode in new byte[] { 0, 8, 9, 10, 0x20, 0x40 })
            test("ATAPI SET FEATURES accepts only explicit default PIO0 modes " + mode.ToString("X2"), () =>
            {
                var r = new Rig(); r.Write(1, 3); r.Write(2, mode); r.Write(7, 0xef); r.Settled(); uint status = r.Read(7);
                if (mode is 0 or 8) Check((status & 1) == 0 && r.Read(1) == 0); else Check((status & 1) != 0 && (r.Read(1) & 4) != 0);
                Check(r.Ata.DmaDirection == AtaDmaDirection.None && r.Medium!.Reads.Count == 0 && r.Hdd.Reads == 0);
            });
        foreach (string cut in new[] { "packet", "cdb10", "data2", "sense2", "open" })
            test("ATAPI physical hard reset aborts only packet work and preserves actual tray and image " + cut, () =>
            {
                var r = new Rig();
                if (cut != "sense2") r.ClearAttention();
                if (cut == "open") Good(r.Execute(Packet(0x1b, 4, 2)));
                else
                {
                    r.Begin(); r.Read(7); byte[] command = cut == "sense2" ? Packet(3, 4, 18) : ReadPacket(2, 1);
                    r.Cdb(command, cut == "packet" ? 0 : cut == "cdb10" ? 5 : 6);
                    if (cut is "data2" or "sense2") { r.Settled(); r.Read(7); r.Ports.Read(r.Port, 2); }
                }
                bool tray = r.Cd.TrayOpen; ulong generation = r.Cd.Generation; r.Cd.ApplyPiixHardReset();
                Check((r.Alt & 0x80) != 0 && !r.Ata.InterruptAsserted && r.Cd.Generation > generation && r.Cd.TrayOpen == tray && !r.Cd.PreventRemoval && r.Cd.HasBindings(r.Clock, r.Medium));
                if (r.Clock.NextEventTime is { } due) r.Clock.AdvanceTo(due + 10000);
                Check((r.Alt & 0x80) != 0 && r.Ports.Read(r.Port, 2) == 0xffff && !r.Ata.InterruptAsserted);
                r.Cd.ReleasePiixHardReset(); Check(r.Alt == 0 && r.Read(1) == 1 && r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Cd.TrayOpen == tray && !r.Ata.InterruptAsserted);
                SenseIs(r, 6, 0x29); Good(r.Execute(Packet(0x12, 4, 36))); Check(r.Hdd.Reads == 0 && r.Hdd.Writes == 0);
            });
        foreach (int device in new[] { 0, 1 })
            test("ATAPI unselected active SRST preserves CDB and pending source until release " + device, () =>
            {
                var r = new Rig(device); r.ClearAttention(); r.Begin(); r.Read(7); byte[] cdb = ReadPacket(2, 1); r.Cdb(cdb, 5); r.Select(1 - device);
                var before = r.Cd.CapturePhase(); ulong generation = r.Cd.Generation; r.Ports.Write(r.Control, 4, 1);
                Check(r.Cd.Generation > generation && r.Cd.CapturePacket().AsSpan(0, 10).SequenceEqual(cdb.AsSpan(0, 10)) && r.Cd.CapturePhase().PacketOffset == 10 && !r.Ata.InterruptAsserted);
                r.Ports.Write(r.Control, 0, 1); Check(r.Ata.SelectedDriveIndex == 0 && r.Cd.CapturePhase().Phase == before.Phase && r.Cd.CapturePhase().PacketOffset == before.PacketOffset);
                r.Select(device); r.Ports.Write(r.Port, (uint)(cdb[10] | cdb[11] << 8), 2); Result result = r.Finish(); Good(result); Check(result.Data.SequenceEqual(Pattern(2)));
            });
        test("ATAPI selected slave SRST releases to channel device zero without changing the retained optical identity", () =>
        {
            var r = new Rig(1); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(2, 1), 5); r.Ports.Write(r.Control, 4, 1); r.Ports.Write(r.Control, 0, 1);
            Check(r.Ata.SelectedDriveIndex == 0 && !r.Cd.Selected); r.Select(1); Check(r.Alt == 0 && r.Read(4) == 0x14 && r.Read(5) == 0xeb && r.Cd.HasBindings(r.Clock, r.Medium));
        });
        test("ATAPI active packet excludes a new ordinary sibling HDD command", () =>
        {
            var r = new Rig(); r.ClearAttention(); r.Begin(); r.Read(7); r.Cdb(ReadPacket(2, 1)); r.Settled(); r.Read(7); r.Ports.Read(r.Port, 2); var before = r.Cd.CapturePhase();
            r.Select(1); r.Write(2, 1); r.Write(3, 3); r.Write(4, 0); r.Write(5, 0); r.Write(6, 0xf0); r.Write(7, 0x20);
            Check(r.Hdd.Reads == 0 && r.Hdd.Writes == 0 && r.Ata.DmaDirection == AtaDmaDirection.None && r.Cd.CapturePhase().Offset == before.Offset && r.Cd.CapturePhase().Phase == before.Phase);
            r.Select(0); byte[] bytes = new byte[2048]; bytes[0] = Pattern(2)[0]; bytes[1] = Pattern(2)[1];
            for (int i = 2; i < 2048; i += 2) { uint word = r.Ports.Read(r.Port, 2); bytes[i] = (byte)word; bytes[i + 1] = (byte)(word >> 8); }
            Good(r.Finish()); Check(bytes.SequenceEqual(Pattern(2)) && r.Hdd.Reads == 0);
        });
        foreach (byte command in new byte[] { 0x90, 0xe0, 0xe1, 0xe5, 0xe6 })
            test("ATAPI unimplemented ATA diagnostic or power wrapper is an honest ABRT " + command.ToString("X2"), () =>
            {
                var r = new Rig(); r.Write(7, command); r.Settled(); Check((r.Read(7) & 1) != 0 && r.Read(1) == 4 && r.Read(4) == 0x14 && r.Read(5) == 0xeb);
                Check(r.Medium!.Reads.Count == 0 && r.Hdd.Reads == 0 && r.Hdd.Writes == 0 && r.Ata.DmaDirection == AtaDmaDirection.None);
            });
        foreach (bool msf in new[] { false, true })
            test("ATAPI own single-track TOC contains independent track start and actual lead-out " + msf, () =>
            {
                var r = new Rig(); r.ClearAttention(); byte[] cdb = Packet(0x43); cdb[1] = msf ? (byte)2 : (byte)0; cdb[8] = 20; Result result = r.Execute(cdb); Good(result);
                Check(result.Data.Length == 20 && BinaryPrimitives.ReadUInt16BigEndian(result.Data) == 18 && result.Data[2] == 1 && result.Data[3] == 1 && result.Data[5] == 0x14 && result.Data[6] == 1 && result.Data[13] == 0x14 && result.Data[14] == 0xaa);
                if (msf) Check(result.Data.AsSpan(8, 4).SequenceEqual(new byte[] { 0, 0, 2, 0 }) && result.Data.AsSpan(16, 4).SequenceEqual(new byte[] { 0, 0, 2, 11 }));
                else Check(BinaryPrimitives.ReadUInt32BigEndian(result.Data.AsSpan(8)) == 0 && BinaryPrimitives.ReadUInt32BigEndian(result.Data.AsSpan(16)) == 11);
                Check(r.Medium!.Reads.Count == 0);
            });
        test("ATAPI TOC track AA reports only the true lead-out descriptor", () =>
        {
            var r = new Rig(); r.ClearAttention(); byte[] cdb = Packet(0x43, 6, 0xaa); cdb[8] = 12; Result result = r.Execute(cdb); Good(result);
            Check(result.Data.Length == 12 && BinaryPrimitives.ReadUInt16BigEndian(result.Data) == 10 && result.Data[6] == 0xaa && BinaryPrimitives.ReadUInt32BigEndian(result.Data.AsSpan(8)) == 11 && r.Medium!.Reads.Count == 0);
        });
        test("ATAPI original TOC packet rejects modern byte-two format placement", () =>
        {
            var r = new Rig(); r.ClearAttention(); byte[] cdb = Packet(0x43, 2, 1); cdb[8] = 20; Result result = r.Execute(cdb); Check((result.Status & 1) != 0 && result.Data.Length == 0); SenseIs(r, 5, 0x24);
        });
        foreach (bool locked in new[] { false, true })
            test("ATAPI capability page advertises only real PIO tray and lock functions " + locked, () =>
            {
                var r = new Rig(); r.ClearAttention(); if (locked) Good(r.Execute(Packet(0x1e, 4, 1)));
                byte[] cdb = Packet(0x5a, 2, 0x2a); cdb[8] = 32; Result result = r.Execute(cdb); Good(result); byte[] data = result.Data;
                Check(data.Length == 32 && BinaryPrimitives.ReadUInt16BigEndian(data) == 30 && data[2] == 1 && data.AsSpan(3, 5).ContainsAnyExcept((byte)0) == false && data[8] == 0x2a && data[9] == 22);
                Check(data[12] == 0 && data[13] == 0 && (data[14] & 0xe9) == 0x29 && ((data[14] & 2) != 0) == locked && r.Medium!.Reads.Count == 0);
            });
        test("ATAPI unsupported saved mode pages report saving-parameters-not-supported", () =>
        {
            var r = new Rig(); r.ClearAttention(); byte[] cdb = Packet(0x5a, 2, 0xea); cdb[8] = 32; Result result = r.Execute(cdb); Check((result.Status & 1) != 0 && result.Data.Length == 0); SenseIs(r, 5, 0x39);
        });
    }
}
