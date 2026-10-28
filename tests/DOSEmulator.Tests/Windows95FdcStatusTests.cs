// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;

/// <summary>Real command/result and DMA effects for the 82077AA status-bit contract, independent of the Windows Setup diagnosis.</summary>
internal static class Windows95FdcStatusTests
{
    /// <summary>Requires a specific externally observed controller, CPU, memory or medium effect.</summary>
    private static void Check(bool value, string why = "82077AA status contract failed.")
    { if (!value) throw new InvalidOperationException(why); }

    /// <summary>Creates only new synthetic sectors with a fixed byte pattern, without opening any host medium.</summary>
    private static byte[] Image()
    {
        byte[] data = new byte[2880 * 512];
        for (int sector = 0; sector < 2880; sector++)
            for (int i = 0; i < 512; i++) data[sector * 512 + i] = (byte)(0x31 + sector * 7 + i * 13);
        return data;
    }

    /// <summary>Own checked writable memory medium, including independent backend contact counters.</summary>
    private sealed class Disk : IWritableBlockDevice
    {
        /// <summary>Private full-sector bytes used only by this isolated regression.</summary>
        public readonly byte[] Bytes = Image();
        /// <summary>Actual backend sector reads, distinct from FDC success counters.</summary>
        public int Reads { get; private set; }
        /// <summary>Actual backend commits, distinct from the commanded operation.</summary>
        public int Writes { get; private set; }
        /// <summary>Fixed capacity of the own 1.44-MiB medium.</summary>
        public long SectorCount => 2880;
        /// <summary>Names exclusively newly generated synthetic data.</summary>
        public string Name => "Own AA-status writable floppy";
        /// <summary>Reads only a checked complete sector and records actual backend contact.</summary>
        public void ReadSector(long lba, Span<byte> destination)
        {
            if (lba < 0 || lba >= SectorCount || destination.Length < 512) throw new ArgumentOutOfRangeException(nameof(lba));
            Bytes.AsSpan(checked((int)lba * 512), 512).CopyTo(destination); Reads++;
        }
        /// <summary>Commits only one checked sector and records its actual private-media effect.</summary>
        public void WriteSector(long lba, ReadOnlySpan<byte> source)
        {
            if (lba < 0 || lba >= SectorCount || source.Length != 512) throw new ArgumentOutOfRangeException(nameof(lba));
            source.CopyTo(Bytes.AsSpan(checked((int)lba * 512), 512)); Writes++;
        }
        /// <summary>Returns a new independently writable copy if the media interface requires reopening.</summary>
        public IReadOnlyBlockDevice Reopen()
        { var copy = new Disk(); Bytes.CopyTo(copy.Bytes, 0); return copy; }
        /// <summary>Has no pending host I/O or external endpoint.</summary>
        public void Flush() { }
        /// <summary>Owns no host files or operating-system resources.</summary>
        public void Dispose() { }
    }

    /// <summary>Actual CPU port caller, FDC, DMA, PIC and virtual clock around only fresh own memory media.</summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>Guest RAM holds executable callers and independently guarded DMA buffers.</summary>
        public readonly MemoryBus Memory = new(2 * 1048576);
        /// <summary>Existing nominal DOSE device deadlines; not a physical empty-disk timing oracle.</summary>
        public readonly DeviceScheduler Clock = new();
        /// <summary>Actual 8237 programmed through guest byte OUT instructions.</summary>
        public readonly Dma8237 Dma;
        /// <summary>Actual edge-triggered PIC observes the FDC's independent IRQ6 line.</summary>
        public readonly Pic8259 Pic = new();
        /// <summary>Two installed physical drives whose insertion state is independent of their geometry.</summary>
        public readonly Fdc82077 Fdc;
        /// <summary>Real CPU I/O decoder without replacement service handlers or injected completions.</summary>
        private readonly IoPortBus _ports = new();

        /// <summary>Connects ordinary installed drives to the explicit empty, read-only or writable own media.</summary>
        public Rig(IReadOnlyBlockDevice? a = null, IReadOnlyBlockDevice? b = null)
        {
            Dma = new(Memory);
            Fdc = new(Clock, Dma, high => Pic.SetLine(6, high),
                FloppyGeometry.FromId("1440k"), FloppyGeometry.FromId("1440k"), a, b);
            _ports.Map(0, 0x10, Dma); _ports.Map(0x80, 0x10, Dma); _ports.Map(0x20, 2, Pic);
            _ports.Map(0x3f0, 6, Fdc); _ports.Map(0x3f7, 1, Fdc);
            Send(3, 0xdf, 2);
        }

        /// <summary>Executes a small own real-mode byte-port caller until its legitimate CLI/HLT sentinel.</summary>
        private void Run(List<byte> code)
        {
            code.Add(0xf4); code.ToArray().CopyTo(Memory.Ram, 0x1000);
            var cpu = new X86Cpu(Memory, _ports);
            cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ss, 0); cpu.SetSegment(Segment.Ds, 0);
            cpu.SetRegister(Register.Sp, 0x8000); cpu.Eip = 0x1000; cpu.Eflags = 2;
            for (int i = 0; i < 2048 && !cpu.Halted; i++) cpu.Step();
            Check(cpu.Halted && !cpu.TripleFaulted, "Own CPU FDC port caller failed before HLT: " + cpu.Position);
        }

        /// <summary>Performs an actual guest byte OUT without altering any FDC private field.</summary>
        public void Out(ushort port, byte value)
        { Run(new List<byte> { 0xba, (byte)port, (byte)(port >> 8), 0xb0, value, 0xee }); }

        /// <summary>Feeds the real FDC command FIFO using actual CPU OUT instructions in order.</summary>
        public void Send(params byte[] values)
        {
            var code = new List<byte> { 0xba, 0xf5, 3 };
            foreach (byte value in values) code.AddRange(new byte[] { 0xb0, value, 0xee });
            Run(code);
        }

        /// <summary>Consumes exactly the requested FIFO result bytes through actual CPU IN and RAM stores.</summary>
        public byte[] Result(int count)
        {
            var code = new List<byte> { 0xba, 0xf5, 3 };
            Memory.Ram.AsSpan(0x3000, count).Fill(0x6b);
            for (int i = 0; i < count; i++) code.AddRange(new byte[] { 0xec, 0xa2, (byte)i, 0x30 });
            Run(code); return Memory.Ram.AsSpan(0x3000, count).ToArray();
        }

        /// <summary>Issues SENSE DRIVE STATUS and requires its ordinary one-byte, no-new-IRQ result protocol.</summary>
        public byte Sense(byte headUnit)
        {
            Check(!Fdc.InterruptAsserted, "SENSE fixture carried an unrelated old IRQ.");
            Send(4, headUnit); Check(Fdc.Read(0x3f4, 1) == 0xd0 && !Fdc.InterruptAsserted);
            byte value = Result(1)[0]; Check(Fdc.Read(0x3f4, 1) == 0x80 && !Fdc.Busy && !Fdc.InterruptAsserted);
            return value;
        }

        /// <summary>Selects one ordinary drive and its motor through the actual DOR port.</summary>
        public void Select(int unit, bool motor)
        { Out(0x3f2, (byte)(0x0c | unit | (motor ? 0x10 << unit : 0))); }

        /// <summary>Programs complete DMA2 address/count/direction registers through real CPU byte OUTs.</summary>
        public void ProgramDma(bool write)
        {
            Out(0x0a, 6); Out(0x0c, 0); Out(4, 0); Out(4, 0x40); Out(0x81, 0);
            Out(5, 0xff); Out(5, 1); Out(0x0b, write ? (byte)0x4a : (byte)0x46); Out(0x0a, 2);
        }

        /// <summary>Places distinctive bytes before, throughout and after the intended 512-byte DMA target.</summary>
        public void Guard()
        { Memory.Ram.AsSpan(0x3ff0, 0x220).Fill(0xd3); Memory.Ram.AsSpan(0x4000, 512).Fill(0xa7); }

        /// <summary>Requires no out-of-buffer DMA write independently of the controller result bytes.</summary>
        public void CheckGuards()
        {
            Check(Memory.Ram.AsSpan(0x3ff0, 16).ToArray().All(x => x == 0xd3));
            Check(Memory.Ram.AsSpan(0x4200, 16).ToArray().All(x => x == 0xd3));
        }

        /// <summary>Advances only the documented existing motor/sector model, without asserting universal physical timeout behavior.</summary>
        public void NominalCompletion() => Clock.AdvanceTo(Clock.Now + 400_000_000);

        /// <summary>Releases only the fresh in-memory media owned by this fixture.</summary>
        public void Dispose() => Fdc.Dispose();
    }

    /// <summary>Builds the explicit null, read-only or writable synthetic media of a SENSE scenario.</summary>
    private static IReadOnlyBlockDevice? Medium(int kind) => kind switch
    {
        0 => null,
        1 => new MemoryDisk(Image(), "Own AA-status read-only floppy"),
        2 => new Disk(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Registers narrow primary status-bit cases and independent transfer/reset controls, not a Windows Setup completion claim.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        // Intel 82077AA 290166-007 section 6.4: ST3.5 and ST3.3 are unused and always one.
        foreach (var scenario in new (string Name, int Unit, bool Motor, int Kind, bool Head)[]
        {
            ("empty A motor off", 0, false, 0, false), ("empty A motor on", 0, true, 0, true),
            ("readonly A motor off", 0, false, 1, false), ("readonly A motor on", 0, true, 1, true),
            ("writable B motor off", 1, false, 2, true), ("writable B motor on", 1, true, 2, false),
            ("unconnected unit2", 2, false, 0, false), ("unconnected unit3 head1", 3, false, 0, true)
        })
        test("M4 Win95 82077AA ST3 fixed bits through actual SENSE " + scenario.Name, () =>
        {
            IReadOnlyBlockDevice? medium = Medium(scenario.Kind);
            using var r = new Rig(scenario.Unit == 0 ? medium : null, scenario.Unit == 1 ? medium : null);
            r.Select(scenario.Unit, scenario.Motor);
            byte headUnit = (byte)(scenario.Unit | (scenario.Head ? 4 : 0)); byte status = r.Sense(headUnit);
            Check((status & 0x28) == 0x28, $"ST3 unused bits must be one: {status:X2}");
            Check((status & 7) == headUnit && (status & 0x80) == 0, "SENSE lost physical unit/head or set reserved bit7.");
            if (scenario.Unit < 2) Check((status & 0x10) != 0, "Initial installed drive did not report its actual track0 position.");
            if (scenario.Kind == 1) Check((status & 0x40) != 0, "Read-only medium lost write protection.");
            if (scenario.Kind == 2) Check((status & 0x40) == 0, "Writable own medium invented write protection.");
            Check(r.Fdc.ReadSectorCount == 0 && r.Fdc.WrittenSectorCount == 0 && !r.Dma.IsTerminalCount(2));
        });

        test("M4 Win95 82077AA SENSE retains independent head unit track0 and write-protect pins after actual seeks", () =>
        {
            using var r = new Rig(null, new Disk()); r.Select(1, false);
            r.Send(0x0f, 1, 3); r.Clock.AdvanceTo(6_000_000); Check(r.Fdc.InterruptAsserted);
            r.Send(8); Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { 0x21, 3 }));
            Check(r.Sense(5) == 0x2d, "Off-track writable B/head1 SENSE pins are incorrect.");
            r.Send(7, 1); r.Clock.AdvanceTo(12_000_000); Check(r.Fdc.InterruptAsserted);
            r.Send(8); Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { 0x21, 0 }));
            Check(r.Sense(1) == 0x39, "Actual recalibration did not restore track0 independently of motor readiness.");
        });

        foreach (int unit in new[] { 0, 1 })
        test($"M4 Win95 82077AA successful DMA read keeps ST0.3 zero and actual head/unit for drive{unit}", () =>
        {
            using var r = new Rig(unit == 0 ? Medium(1) : null, unit == 1 ? Medium(1) : null);
            r.Select(unit, true); r.ProgramDma(false); r.Guard(); byte head = (byte)unit;
            r.Send(0x46, (byte)(unit | head << 2), 0, head, 1, 2, 18, 0x1b, 0xff); r.NominalCompletion();
            Check(r.Fdc.InterruptAsserted); byte[] result = r.Result(7);
            Check(result[0] == (byte)(unit | head << 2) && result[1] == 0 && result[2] == 0);
            for (int i = 0; i < 512; i++) Check(r.Memory.ReadByte((uint)(0x4000 + i)) == (byte)(0x31 + head * 18 * 7 + i * 13));
            Check(r.Fdc.ReadSectorCount == 1 && r.Fdc.WrittenSectorCount == 0 && r.Dma.IsTerminalCount(2));
            r.CheckGuards(); Check(!r.Fdc.InterruptAsserted && !r.Fdc.Busy);
        });

        test("M4 Win95 82077AA successful DMA write keeps genuine backend commit and no reserved ST0 bit", () =>
        {
            var disk = new Disk(); using var r = new Rig(disk); r.Select(0, true); r.ProgramDma(true); r.Guard();
            byte[] expected = Enumerable.Range(0, 512).Select(i => (byte)(0x9b ^ i ^ (i >> 3))).ToArray();
            expected.CopyTo(r.Memory.Ram, 0x4000); r.Send(0x45, 0, 0, 0, 2, 2, 18, 0x1b, 0xff); r.NominalCompletion();
            Check(r.Fdc.InterruptAsserted); byte[] result = r.Result(7);
            Check(result[0] == 0 && result[1] == 0 && result[2] == 0 && disk.Writes == 1 && disk.Reads == 0);
            Check(disk.Bytes.AsSpan(512, 512).SequenceEqual(expected) && r.Fdc.WrittenSectorCount == 1 && r.Dma.IsTerminalCount(2));
            r.CheckGuards(); Check(!r.Fdc.InterruptAsserted && !r.Fdc.Busy);
        });

        // Section 6.1: ST0.3 is unused and always zero, including actual abnormal data-command results.
        foreach (string reason in new[] { "empty-read-A", "empty-write-B", "motor-off", "selection-mismatch", "write-protect", "wrong-rate", "wrong-cylinder", "masked-DMA" })
        test("M4 Win95 82077AA actual error keeps ST0.3 zero and rejects data effects: " + reason, () =>
        {
            int unit = reason is "empty-write-B" or "selection-mismatch" ? 1 : 0;
            bool empty = reason.StartsWith("empty-", StringComparison.Ordinal), write = reason is "empty-write-B" or "write-protect";
            Disk? writable = !empty && reason != "write-protect" ? new Disk() : null;
            IReadOnlyBlockDevice? medium = empty ? null : writable is not null ? writable : Medium(1);
            using var r = new Rig(unit == 0 ? medium : null, unit == 1 ? medium : null);
            r.Select(reason == "selection-mismatch" ? 0 : unit, reason != "motor-off"); r.ProgramDma(write); r.Guard();
            if (reason == "wrong-rate") r.Out(0x3f7, 2);
            if (reason == "masked-DMA") r.Out(0x0a, 6);
            r.Send(write ? (byte)0x45 : (byte)0x46, (byte)unit, reason == "wrong-cylinder" ? (byte)1 : (byte)0, 0, 1, 2, 18, 0x1b, 0xff);
            r.NominalCompletion(); Check(r.Fdc.InterruptAsserted && r.Fdc.Read(0x3f4, 1) == 0xd0);
            byte[] result = r.Result(7); byte expectedSt1 = reason == "write-protect" ? (byte)2 : reason == "masked-DMA" ? (byte)0x10 : (byte)4;
            Check((result[0] & 0xc0) == 0x40 && (result[0] & 8) == 0 && (result[0] & 7) == unit,
                $"Abnormal ST0 must retain unit and clear unused bit3: {result[0]:X2}");
            Check(result[1] == expectedSt1 && result[2] == (reason == "wrong-cylinder" ? 0x10 : 0), "Actual error lost its independent ST1/ST2 cause.");
            Check(r.Memory.Ram.AsSpan(0x4000, 512).ToArray().All(x => x == 0xa7) && !r.Dma.IsTerminalCount(2));
            Check(r.Fdc.ReadSectorCount == 0 && r.Fdc.WrittenSectorCount == 0 && (writable is null || (reason == "masked-DMA" || writable.Reads == 0) && writable.Writes == 0));
            r.CheckGuards(); Check(!r.Fdc.InterruptAsserted && !r.Fdc.Busy);
        });

        test("M4 Win95 82077AA reset polling IRQ remains separate from SENSE DRIVE status bits", () =>
        {
            using var r = new Rig(); r.Out(0x21, 0xbf); r.Out(0x3f2, 0); Check(r.Fdc.Read(0x3f4, 1) == 0);
            r.Out(0x3f2, 0x0c); r.Clock.AdvanceTo(999_999); Check(!r.Fdc.InterruptAsserted);
            r.Clock.AdvanceTo(1_000_000); Check(r.Fdc.InterruptAsserted && r.Pic.Acknowledge() == 0x0e);
            for (int unit = 0; unit < 4; unit++)
            { r.Send(8); Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { (byte)(0xc0 | unit), 0 })); }
            r.Send(8); Check(r.Result(1)[0] == 0x80 && !r.Fdc.InterruptAsserted && !r.Fdc.Busy);
            r.Out(0x20, 0x20); Check(!r.Pic.HasPending, "Drained reset senses left an invented IRQ6 request.");
            Check((r.Sense(0) & 0x28) == 0x28 && r.Fdc.ReadSectorCount == 0 && r.Fdc.WrittenSectorCount == 0);
        });

        test("M4 Win95 82077AA reset cancels own pending data callback without inventing DMA or old result IRQ", () =>
        {
            var disk = new Disk(); using var r = new Rig(disk); r.Out(0x21, 0xbf); r.Select(0, true); r.ProgramDma(false); r.Guard();
            r.Send(0x46, 0, 0, 0, 1, 2, 18, 0x1b, 0xff); r.Out(0x3f2, 0); r.Out(0x3f2, 0x0c);
            r.Clock.AdvanceTo(1_000_000); Check(r.Fdc.InterruptAsserted && r.Pic.Acknowledge() == 0x0e);
            for (int unit = 0; unit < 4; unit++)
            { r.Send(8); Check(r.Result(2).AsSpan().SequenceEqual(new byte[] { (byte)(0xc0 | unit), 0 })); }
            r.Out(0x20, 0x20); r.Clock.AdvanceTo(400_000_000);
            Check(!r.Fdc.InterruptAsserted && !r.Pic.HasPending && !r.Fdc.Busy && disk.Reads == 0 && disk.Writes == 0);
            Check(r.Fdc.ReadSectorCount == 0 && !r.Dma.IsTerminalCount(2) && r.Memory.Ram.AsSpan(0x4000, 512).ToArray().All(x => x == 0xa7));
            r.CheckGuards();
        });
    }
}
