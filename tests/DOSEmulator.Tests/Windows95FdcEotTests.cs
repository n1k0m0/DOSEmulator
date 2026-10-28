// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual READ/WRITE DATA and DMA TC-before-EOT regressions; DMA verify is not the FDC VERIFY command.</summary>
internal static class Windows95FdcEotTests
{
    /// <summary>Own DMA buffer below the64-KiB wrap boundary, outside the instruction and descriptor fixtures.</summary>
    private const int Buffer = 0x5000;
    /// <summary>Requires an independent byte, backend, command/result, DMA or future-state effect.</summary>
    private static void Check(bool value, string why = "FDC terminal count before EOT failed.")
    { if (!value) throw new InvalidOperationException(why); }
    /// <summary>Generates only private synthetic sectors with independently addressable bytes.</summary>
    private static byte[] Image()
    {
        var data = new byte[2880 * 512];
        for (int sector = 0; sector < 2880; sector++)
            for (int i = 0; i < 512; i++) data[sector * 512 + i] = (byte)(0x31 + sector * 7 + i * 13);
        return data;
    }
    /// <summary>Checked read-only synthetic backend; contact counters distinguish media work from FDC success counters.</summary>
    private class Disk : IReadOnlyBlockDevice
    {
        /// <summary>Private raw bytes; never an original image.</summary>
        public readonly byte[] Bytes = Image();
        /// <summary>Every actual backend sector read.</summary>
        public readonly List<long> Reads = new();
        /// <summary>Every actual backend sector commit.</summary>
        public readonly List<long> Writes = new();
        /// <summary>Native 1.44-MB sector capacity.</summary>
        public long SectorCount => 2880;
        /// <summary>Names only generated fixture bytes.</summary>
        public string Name => "Own EOT/TC floppy";
        /// <summary>Copies a complete checked sector without allowing host-side sector aliasing.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        {
            if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            Bytes.AsSpan(checked((int)lba * 512), 512).CopyTo(destination); Reads.Add(lba);
        }
        /// <summary>Reopens only an independent copy of this own medium.</summary>
        public virtual IReadOnlyBlockDevice Reopen()
        { var copy = new Disk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>No host handle or endpoint exists.</summary>
        public void Dispose() { }
    }
    /// <summary>Own writable variant for genuine FDC DMA-to-medium commits.</summary>
    private sealed class WritableDisk : Disk, IWritableBlockDevice
    {
        /// <summary>Commits one actual checked sector and records its exact LBA.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        {
            if (lba < 0 || lba >= SectorCount || source.Length != 512) throw new ArgumentOutOfRangeException(nameof(lba));
            source.CopyTo(Bytes.AsSpan(checked((int)lba * 512), 512)); Writes.Add(lba);
        }
        /// <summary>Preserves writable identity in an independent private copy.</summary>
        public override IReadOnlyBlockDevice Reopen()
        { var copy = new WritableDisk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>No host persistence exists for this generated in-memory writable fixture.</summary>
        public void Flush() { }
    }
    /// <summary>Real CPU port caller, FDC, DMA2, clock and PIC; optional full-PC ownership is retained.</summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>Actual guest CPU.</summary>
        public readonly X86Cpu Cpu;
        /// <summary>Actual physical bus and RAM.</summary>
        public readonly MemoryBus Memory;
        /// <summary>Actual I/O decoder.</summary>
        public readonly IoPortBus Ports;
        /// <summary>Actual device scheduler.</summary>
        public readonly DeviceScheduler Clock;
        /// <summary>Actual AT DMA controller.</summary>
        public readonly Dma8237 Dma;
        /// <summary>Actual FDC under test.</summary>
        public readonly Fdc82077 Fdc;
        /// <summary>Actual PIC line receiver.</summary>
        public readonly Pic8259 Pic;
        /// <summary>Optional regular complete machine owner.</summary>
        public readonly PcMachine? Machine;
        /// <summary>Builds a synthetic component graph with actual CPU-mediated I/O.</summary>
        public Rig(Disk disk)
        {
            Ports = new(); (Cpu, Memory) = ProtectedModeTests.Rig([0x90], io: Ports);
            Clock = new(); Dma = new(Memory); Pic = new();
            Fdc = new(Clock, Dma, high => Pic.SetLine(6, high), FloppyGeometry.FromId("1440k"), null, disk, null);
            Ports.Map(0, 0x10, Dma); Ports.Map(0x81, 1, Dma); Ports.Map(0x20, 2, Pic); Ports.Map(0xa0, 2, Pic);
            Ports.Map(0x3f0, 6, Fdc); Ports.Map(0x3f7, 1, Fdc); Initialize();
        }
        /// <summary>Borrows the complete original/imported graph; a restored fixture is not reprogrammed.</summary>
        public Rig(PcMachine machine, bool initialize)
        {
            Machine = machine; Cpu = machine.Cpu; Memory = machine.Memory; Ports = machine.Ports;
            Clock = machine.Scheduler; Dma = machine.Dma; Pic = machine.Pic; Fdc = machine.Floppy;
            if (initialize) { Protected(machine); Initialize(); }
        }
        /// <summary>Programs only the real motor, rate and DMA-mode command ports.</summary>
        private void Initialize()
        { Out((0x3f2, 0x1c), (0x3f7, 0)); Send(3, 0xdf, 2); }
        /// <summary>Executes all own instructions and rejects any CPU fault or handler detour.</summary>
        private void Execute(byte[] code, int count)
        {
            code.CopyTo(Memory.Ram, 0x1000); Cpu.Eip = 0x1000;
            for (int i = 0; i < count; i++)
                if (Machine is null) Cpu.Step(); else Check(Machine.RunSteps(1) == 1 && Machine.Fault is null, Machine.Fault ?? "Own owner stopped.");
            Check(!Cpu.TripleFaulted && !Cpu.Halted && Cpu.GetSegment(Segment.Cs) == 8 && Cpu.Eip == 0x1000 + code.Length,
                "Own actual port caller faulted or stopped: " + Cpu.Position);
        }
        /// <summary>Emits MOV EDX,port; MOV AL,value; OUT DX,AL for every actual byte transaction.</summary>
        public void Out(params (ushort Port, byte Value)[] writes)
        {
            var code = new List<byte>();
            foreach (var (port, value) in writes)
            { code.Add(0xba); code.AddRange(BitConverter.GetBytes((uint)port)); code.Add(0xb0); code.Add(value); code.Add(0xee); }
            Execute(code.ToArray(), writes.Length * 3);
        }
        /// <summary>Reads actual bytes using IN AL,DX and stores each returned byte in own scratch RAM.</summary>
        public byte[] In(ushort port, int count)
        {
            var code = new List<byte>();
            for (int i = 0; i < count; i++)
            { code.Add(0xba); code.AddRange(BitConverter.GetBytes((uint)port)); code.Add(0xec); code.Add(0xa2); code.AddRange(BitConverter.GetBytes((uint)(0x3000 + i))); }
            Execute(code.ToArray(), count * 3); return Memory.Ram.AsSpan(0x3000, count).ToArray();
        }
        /// <summary>Sends a complete actual FIFO command, never injecting completion or sector state.</summary>
        public void Send(params byte[] bytes) => Out(bytes.Select(value => ((ushort)0x3f5, value)).ToArray());
        /// <summary>Programs address, count, page, direction and mask through the actual 8237 ports.</summary>
        public void ProgramDma(int bytes, byte mode, bool masked = false)
        {
            int count = bytes - 1;
            Out((0x0a, 6), (0x0c, 0), (4, (byte)(Buffer & 255)), (4, (byte)(Buffer >> 8)), (0x81, 0),
                (5, (byte)count), (5, (byte)(count >> 8)), (0x0b, mode), (0x0a, masked ? (byte)6 : (byte)2));
        }
        /// <summary>Reads the actual current DMA2 count while respecting the shared byte flip-flop.</summary>
        public ushort DmaCount()
        { Out((0x0c, 0)); byte[] count = In(5, 2); return (ushort)(count[0] | count[1] << 8); }
        /// <summary>Starts READ DATA E6 or its single-track counterpart, or WRITE DATA, with explicit command bytes.</summary>
        public void Data(bool write, bool multi, byte sector, byte eot, byte size = 2, bool mfm = true)
        {
            byte command = (byte)((write ? 5 : 0x26) | (multi ? 0x80 : 0) | (mfm ? 0x40 : 0));
            Send(command, 0, 0, 0, sector, size, eot, 0x1b, 0xff);
        }
        /// <summary>Runs scheduled sector/motor events with no externally fabricated IRQ or DMA grant.</summary>
        public void Wait() => Clock.AdvanceTo(Clock.Now + 1_000_000_000);
        /// <summary>Requires a real result phase and consumes every actual CHRN/status byte.</summary>
        public byte[] Result()
        { Check(In(0x3f4, 1)[0] == 0xd0); byte[] result = In(0x3f5, 7); Check(In(0x3f4, 1)[0] == 0x80 && !Fdc.InterruptAsserted); return result; }
        /// <summary>The complete machine owns its peers; only isolated component graphs are disposed here.</summary>
        public void Dispose() { if (Machine is null) Fdc.Dispose(); }
    }
    /// <summary>Uses real decoder instructions to establish flat PE32 in a full factory PC.</summary>
    private static void Protected(PcMachine machine)
    {
        var c = machine.Cpu; var m = machine.Memory; c.Reset(); c.SetSegment(Segment.Cs, 0); c.Eip = 0x1000; c.SetRegister(Register.Sp, 0x9000);
        ProtectedModeTests.Descriptor(m, 8, 0, 0xfffff, 0x9a, 12); ProtectedModeTests.Descriptor(m, 16, 0, 0xfffff, 0x92, 12);
        for (int i = 0; i < 256; i++) ProtectedModeTests.Gate(m, (byte)i);
        m.WriteByte(0x6000, 0xf4); m.WriteWord(0x300, 0xff); m.WriteDword(0x302, 0x400); m.WriteWord(0x310, 0x7ff); m.WriteDword(0x312, 0x8000);
        new byte[] { 0x0f, 0x01, 0x16, 0, 3, 0x66, 0x0f, 0x01, 0x1e, 0x10, 3,
            0x66, 0xb8, 0x11, 0, 0, 0x60, 0x0f, 0x22, 0xc0, 0x66, 0xea, 0, 0x20, 0, 0, 8, 0 }.CopyTo(m.Ram, 0x1000);
        Check(machine.RunSteps(5) == 5 && c.ProtectedMode && c.Eip == 0x2000 && machine.Fault is null);
        c.SetSegment(Segment.Ss, 16); c.SetSegment(Segment.Ds, 16); c.SetSegment(Segment.Es, 16); c.SetRegister(Register.Sp, 0x18000);
    }
    /// <summary>Seeds guards and a known write payload without obtaining expected bytes from the FDC.</summary>
    private static byte[] Payload(Rig r, int bytes)
    {
        r.Memory.Ram.AsSpan(Buffer - 1, bytes + 2).Fill(0xa6); var data = new byte[bytes];
        for (int i = 0; i < bytes; i++) data[i] = (byte)(0xa7 + i * 11 + i / 512);
        return data;
    }
    /// <summary>Requires both neighboring bytes and exact retained data, including verify's lack of RAM effects.</summary>
    private static void Bytes(Rig r, ReadOnlySpan<byte> expected)
    {
        Check(r.Memory.Ram[Buffer - 1] == 0xa6 && r.Memory.Ram[Buffer + expected.Length] == 0xa6);
        Check(r.Memory.Ram.AsSpan(Buffer, expected.Length).SequenceEqual(expected), "Actual DMA bytes/guard differ.");
    }
    /// <summary>Checks TC-before-EOT on both normal and final valid sectors for read, write and DMA-verify modes.</summary>
    private static void OneSector(byte mode, bool multi, byte sector, byte eot = 36)
    {
        Disk disk = mode == 0x4a ? new WritableDisk() : new Disk(); using var r = new Rig(disk);
        byte[] original = disk.Bytes.ToArray(), data = Payload(r, 512);
        if (mode == 0x4a) data.CopyTo(r.Memory.Ram, Buffer);
        r.ProgramDma(512, mode); r.Data(mode == 0x4a, multi, sector, eot); Check(r.In(0x3f4, 1)[0] == 0x10); r.Wait();
        Check(r.Fdc.InterruptAsserted && r.Dma.IsTerminalCount(2)); byte[] result = r.Result();
        Check(result.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 0, 0, (byte)(sector + 1), 2 }), "TC-before-EOT CHRN/status differ.");
        Check((r.In(8, 1)[0] & 4) != 0 && r.DmaCount() == 0xffff);
        if (mode == 0x4a)
        {
            Check(r.Fdc.ReadSectorCount == 0 && r.Fdc.WrittenSectorCount == 1 && disk.Reads.Count == 0 && disk.Writes.SequenceEqual(new long[] { sector - 1 }));
            data.CopyTo(original, (sector - 1) * 512); Check(disk.Bytes.AsSpan().SequenceEqual(original)); Bytes(r, data);
        }
        else
        {
            Check(r.Fdc.ReadSectorCount == 1 && r.Fdc.WrittenSectorCount == 0 && disk.Reads.SequenceEqual(new long[] { sector - 1 }) && disk.Writes.Count == 0);
            Bytes(r, mode == 0x42 ? Enumerable.Repeat((byte)0xa6, 512).ToArray() : original.AsSpan((sector - 1) * 512, 512).ToArray());
            Check(disk.Bytes.AsSpan().SequenceEqual(original));
        }
    }
    /// <summary>Allows all valid sectors before the first nonexistent ID, without aliasing R19 to head1 or rolling back real prefix effects.</summary>
    private static void Prefix(byte mode)
    {
        Disk disk = mode == 0x4a ? new WritableDisk() : new Disk(); using var r = new Rig(disk);
        byte[] original = disk.Bytes.ToArray(), data = Payload(r, 19 * 512); if (mode == 0x4a) data.CopyTo(r.Memory.Ram, Buffer);
        r.ProgramDma(data.Length, mode); r.Data(mode == 0x4a, true, 1, 36); r.Wait(); byte[] result = r.Result();
        Check(result.AsSpan().SequenceEqual(new byte[] { 0x40, 4, 0, 0, 0, 19, 2 }) && !r.Dma.IsTerminalCount(2) && r.DmaCount() == 511);
        long[] lbas = Enumerable.Range(0, 18).Select(x => (long)x).ToArray();
        if (mode == 0x4a)
        { Check(r.Fdc.WrittenSectorCount == 18 && r.Fdc.ReadSectorCount == 0 && disk.Reads.Count == 0 && disk.Writes.SequenceEqual(lbas)); data.AsSpan(0, 18 * 512).CopyTo(original); Bytes(r, data); }
        else
        {
            Check(r.Fdc.ReadSectorCount == 18 && r.Fdc.WrittenSectorCount == 0 && disk.Reads.SequenceEqual(lbas) && disk.Writes.Count == 0);
            byte[] expected = Enumerable.Repeat((byte)0xa6, data.Length).ToArray(); if (mode != 0x42) original.AsSpan(0, 18 * 512).CopyTo(expected); Bytes(r, expected);
        }
        Check(disk.Bytes.AsSpan().SequenceEqual(original), "A missing sector changed/aliased unrelated private medium bytes.");
    }
    /// <summary>Preserves exact-EOT terminal CHRN behavior and legitimate multitrack advancement.</summary>
    private static void Exact(bool multi)
    {
        var disk = new Disk(); using var r = new Rig(disk); Payload(r, 512); r.ProgramDma(512, 0x46); r.Data(false, multi, 18, 18); r.Wait();
        byte[] result = r.Result(); Check(result.AsSpan().SequenceEqual(new byte[] { multi ? (byte)4 : (byte)0, 0, 0, multi ? (byte)0 : (byte)1, multi ? (byte)1 : (byte)0, 1, 2 }));
        Check(r.Dma.IsTerminalCount(2) && r.Fdc.ReadSectorCount == 1); Bytes(r, disk.Bytes.AsSpan(17 * 512, 512));
    }
    /// <summary>Requires existing error gates with ordinary EOT18; these are controls, not consequences of oversized-EOT admission.</summary>
    private static void Error(string kind)
    {
        var disk = new Disk(); using var r = new Rig(disk); byte[] original = disk.Bytes.ToArray(); Payload(r, 512);
        bool write = kind == "write-protect"; byte rate = kind == "rate" ? (byte)2 : (byte)0;
        r.Out((0x3f7, rate)); if (kind == "dor") r.Out((0x3f2, 0x14)); if (kind == "nondma") r.Send(3, 0xdf, 3);
        r.ProgramDma(512, kind == "direction" ? (byte)0x4a : (byte)0x46, kind == "masked");
        byte sector = kind == "eot-low" ? (byte)2 : kind == "r-zero" ? (byte)0 : kind == "r19" ? (byte)19 : (byte)1;
        r.Data(write, true, sector, kind == "eot-low" ? (byte)1 : (byte)18, kind == "size" ? (byte)1 : (byte)2, kind != "mfm"); r.Wait();
        byte expected = kind == "write-protect" ? (byte)2 : kind is "masked" or "direction" or "dor" or "nondma" ? (byte)0x10 : (byte)4;
        byte[] result = r.Result(); Check((result[0] & 0x40) != 0 && result[1] == expected && result[2] == 0);
        Check(r.Fdc.ReadSectorCount == 0 && r.Fdc.WrittenSectorCount == 0 && disk.Writes.Count == 0 && disk.Bytes.AsSpan().SequenceEqual(original));
        Check(disk.Reads.Count == (kind is "masked" or "direction" ? 1 : 0)); Bytes(r, Enumerable.Repeat((byte)0xa6, 512).ToArray());
    }
    /// <summary>Checks full CPU/RAM/devices/scheduler equality at a normal owner boundary, without excluding diagnostics.</summary>
    private static void Equal(PcMachine first, PcMachine second)
    {
        Check(first.Memory.Ram.AsSpan().SequenceEqual(second.Memory.Ram));
        Check(JsonSerializer.Serialize(first.CaptureHardwareState(), StateValues.Json) == JsonSerializer.Serialize(second.CaptureHardwareState(), StateValues.Json), "Full FDC future differs.");
    }
    /// <summary>Captures a real32MiB factory PC after its first sector with EOT36 and a remaining second DMA sector.</summary>
    private static void FullSnapshot()
    {
        string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts/tests"));
        string root = Path.Combine(parent, "win95-eot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var profile = new MachineProfile { Id = MachineProfile.S3Trio64ProfileId, Name = "Own EOT36 future", Cpu = MachineProfile.P54cCpuId,
                CpuHz = 100_000_000, RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId, FloppyDriveA = "1440k" };
            byte[] image = Image(); using var first = new PcMachine(profile, new MemoryDisk(DevelopmentFirmware.CreateDemoDisk(), "Own EOT HDD"), new MemoryDisk(image, "Own EOT floppy"));
            using var r = new Rig(first, true); Payload(r, 1024); r.ProgramDma(1024, 0x46); r.Data(false, true, 1, 36);
            Check(r.Clock.NextEventTime.HasValue);
            for (int i = 0; i < 10000 && first.Floppy.ReadSectorCount == 0; i++)
            { Check(r.Clock.NextEventTime.HasValue); r.Clock.AdvanceTo(r.Clock.NextEventTime!.Value); }
            Check(first.Floppy.ReadSectorCount == 1 && first.Floppy.Busy && !first.Dma.IsTerminalCount(2));
            Check(first.Memory.Ram.AsSpan(Buffer, 512).SequenceEqual(image.AsSpan(0, 512)) && first.Memory.Ram.AsSpan(Buffer + 512, 512).IndexOfAnyExcept((byte)0xa6) < 0);
            string save = Path.Combine(root, "partial.dossav"); SnapshotStore.Capture(first, save);
            using var second = SnapshotStore.Load(save, Path.Combine(root, "restored.dosebranch")); using var restored = new Rig(second, false); Equal(first, second);
            ulong end = r.Clock.Now + 50_000_000; first.Scheduler.AdvanceTo(end); second.Scheduler.AdvanceTo(end); Equal(first, second);
            Check(first.Floppy.ReadSectorCount == 2 && second.Floppy.ReadSectorCount == 2 && first.Dma.IsTerminalCount(2) && second.Dma.IsTerminalCount(2));
            byte[] a = r.Result(), b = restored.Result(); Check(a.AsSpan().SequenceEqual(b) && a.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 3, 2 }));
            Bytes(r, image.AsSpan(0, 1024)); Bytes(restored, image.AsSpan(0, 1024)); Equal(first, second);
        }
        finally { Check(Path.GetDirectoryName(root) == parent && Path.GetFileName(root).StartsWith("win95-eot-", StringComparison.Ordinal)); Directory.Delete(root, true); }
    }
    /// <summary>Registers meaningful oversized-EOT positives, preserved error controls and a full active-container continuation.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (byte mode in new byte[] { 0x42, 0x46, 0x4a }) foreach (bool multi in new[] { false, true }) foreach (byte sector in new byte[] { 1, 18 })
            test($"M4 Win95 FDC actual CPU TC512 before EOT36 mode={mode:X2} MT={multi} R={sector}", () => OneSector(mode, multi, sector));
        test("M4 Win95 FDC actual CPU TC before maximum EOT255 retains final valid R18", () => OneSector(0x46, true, 18, 255));
        foreach (byte mode in new byte[] { 0x42, 0x46, 0x4a })
            test("M4 Win95 FDC actual CPU missing R19 preserves18-sector prefix mode=" + mode.ToString("X2"), () => Prefix(mode));
        foreach (bool multi in new[] { false, true }) test("M4 Win95 FDC existing exact EOT18 TC result MT=" + multi, () => Exact(multi));
        foreach (string kind in new[] { "eot-low", "size", "r-zero", "r19", "rate", "mfm", "write-protect", "masked", "direction", "dor", "nondma" })
            test("M4 Win95 FDC existing validation before data effects " + kind, () => Error(kind));
        test("M4 Win95 FDC active EOT36 full32MiB SnapshotStore future", FullSnapshot);
    }
}
