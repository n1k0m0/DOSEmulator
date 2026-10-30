// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual cold POST masks and the installed IVT0Eh BIOS path, without injected IRQs or software-unmasking IRQ6.</summary>
internal static class Windows95FloppyBootIrqTests
{
    private const uint Caller = 0x6000;
    private const uint StackTop = 0x39000;

    /// <summary>Requires an independently observed CPU, port, stack, controller or firmware result.</summary>
    private static void Check(bool condition, string why = "Windows95 floppy boot IRQ invariant failed.")
    { if (!condition) throw new InvalidOperationException(why); }

    /// <summary>Separates installed physical drives from the contents of their two independent trays.</summary>
    private sealed record Configuration(string Name, int Drives, int Media);

    /// <summary>Uses only the two fixed Windows tuples or an unchanged earlier real POST as the control.</summary>
    private static MachineProfile Profile(bool fast, int drives, int legacy = 0)
    {
        MachineProfile profile = legacy == 1 ? new() : new()
        {
            Id = legacy == 2 ? MachineProfile.PciBiosProfileId :
                fast ? MachineProfile.Windows95Profile133Id : MachineProfile.Windows95ProfileId,
            Name = "Own floppy boot IRQ fixture", Cpu = MachineProfile.P54cCpuId,
            CpuHz = fast && legacy == 0 ? MachineProfile.Windows95Cpu133Hz : 100_000_000,
            RamMiB = 32, VideoCard = MachineProfile.S3Trio64VideoId,
            OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2
        };
        return profile with { FloppyDriveA = drives > 0 ? "1440k" : "none",
            FloppyDriveB = drives > 1 ? "1440k" : "none" };
    }

    /// <summary>Builds a private immutable disk whose own CLI/HLT sector is an unambiguous completed POST sentinel.</summary>
    private static MemoryDisk BootDisk()
    {
        byte[] bytes = new byte[128 * 512];
        new byte[] { 0xfa, 0xf4, 0xeb, 0xfd }.CopyTo(bytes, 0);
        bytes[510] = 0x55; bytes[511] = 0xaa;
        return new(bytes, "Own floppy IRQ hard-disk boot sector");
    }

    /// <summary>Creates only new synthetic readonly sectors; the tray has no effect on physical drive installation.</summary>
    private static MemoryDisk FloppyDisk(int unit)
    {
        byte[] bytes = new byte[2880 * 512];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(0x43 + unit * 31 + i * 7);
        return new(bytes, "Own floppy IRQ medium " + unit);
    }

    /// <summary>Constructs the complete actual factory graph around independently generated HDD/floppy media.</summary>
    private static PcMachine Machine(bool fast, Configuration configuration, int legacy = 0) =>
        PcMachine.CreateWithDisks(Profile(fast, configuration.Drives, legacy), [BootDisk(), null, null, null],
            (configuration.Media & 1) != 0 ? FloppyDisk(0) : null,
            (configuration.Media & 2) != 0 ? FloppyDisk(1) : null);

    /// <summary>A completed real I/O transaction and its executing instruction, without any endpoint reread.</summary>
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, uint Instruction);

    /// <summary>One real PIC acknowledgement; CPU exceptions with vector0Eh do not produce this event.</summary>
    private sealed record Ack(int Irq, byte Vector, bool Spurious);

    /// <summary>Passive actual bus/PIC observations, bounded independently from the CPU execution limit.</summary>
    private sealed class Trace : IDisposable
    {
        private readonly PcMachine _machine;
        /// <summary>Completed PIC and FDC command/result transactions.</summary>
        public readonly List<Port> Ports = [];
        /// <summary>Actual owner-driven PIC acknowledgements, never invoked by the fixture itself.</summary>
        public readonly List<Ack> Acks = [];

        /// <summary>Attaches only passive observations to the already fully constructed PC.</summary>
        public Trace(PcMachine machine)
        {
            _machine = machine; machine.Ports.Accessed += Access;
            machine.Pic.InterruptAcknowledged += Acknowledged;
        }

        /// <summary>Records only relevant completed accesses without consuming a device register.</summary>
        private void Access(ushort port, uint value, int width, bool write)
        {
            if (!(port is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x3f2 or 0x3f5)) return;
            Check(Ports.Count < 8192, "The bounded own floppy/PIC port ledger overflowed.");
            Ports.Add(new(port, value, width, write, _machine.Cpu.LinearInstructionAddress));
        }

        /// <summary>Records a real interrupt-controller acknowledge separately from all firmware port traffic.</summary>
        private void Acknowledged(int irq, byte vector, bool spurious)
        { Check(Acks.Count < 256, "The bounded PIC ledger overflowed."); Acks.Add(new(irq, vector, spurious)); }

        /// <summary>Removes the passive observers without changing device state.</summary>
        public void Dispose()
        {
            _machine.Ports.Accessed -= Access;
            _machine.Pic.InterruptAcknowledged -= Acknowledged;
        }
    }

    /// <summary>Requires the real firmware to reach the own HDD caller rather than a BIOS error or reset stop.</summary>
    private static void Boot(PcMachine machine)
    {
        machine.RunSteps(1_000_000);
        Check(machine.Fault is null && !machine.Cpu.TripleFaulted && machine.Cpu.Halted &&
            machine.Cpu.GetSegment(Segment.Cs) == 0 && machine.Cpu.Eip == 0x7c02,
            machine.Fault ?? "Actual POST did not boot the own sector: " + machine.Cpu.Position);
    }

    /// <summary>Checks the installed actual IVT entry and physical-drive BDA inventory, independent from insertion state.</summary>
    private static uint CheckVector(PcMachine machine, int drives)
    {
        ushort offset = machine.Memory.ReadWord(0x38), segment = machine.Memory.ReadWord(0x3a);
        Check(segment == 0xf000 && offset is > 0 and < 0xfff0, "IVT0Eh does not point to the installed ROM handler.");
        Check((machine.Memory.ReadWord(0x410) & 0xc1) == (drives == 0 ? 0 : drives == 1 ? 1 : 0x41),
            "The physical floppy inventory was inferred from media insertion instead of drive geometry.");
        return (uint)(segment * 16 + offset);
    }

    /// <summary>Checks actual ICW/OCW1 writes and their ROM provenance, rather than accepting a host-created PIC mask.</summary>
    private static void CheckPost(PcMachine machine, Trace trace, int drives, bool windows)
    {
        byte mask = windows ? (drives > 0 ? (byte)0xb8 : (byte)0xf8) : (byte)0xfc;
        Port[] master = trace.Ports.Where(p => p.Write && p.Address == 0x21).ToArray();
        Port[] slave = trace.Ports.Where(p => p.Write && p.Address == 0xa1).ToArray();
        Check(master.Select(p => p.Value).SequenceEqual(new uint[] { 8, 4, 1, mask }),
            "Actual POST mask differs; Windows cascade and installed IRQ6 must be unmasked.");
        Check(slave.Select(p => p.Value).SequenceEqual(new uint[] { 0x70, 2, 1, 0xff }),
            "The slave PIC initialization or mask changed.");
        Check(master.Concat(slave).All(p => p.Width == 1 && p.Instruction is >= 0xf0000 and <= 0xfffff),
            "The expected masks were not actual byte writes executed by the ROM.");
        Check(machine.Ports.Read(0x21, 1) == mask && machine.Ports.Read(0xa1, 1) == 0xff);
        Check((machine.Pic.LevelMask & 0x40) == 0, "FDC IRQ6 must remain the original edge input.");
        CheckVector(machine, drives);
        Check(machine.Floppy.ReadSectorCount == 0 && machine.Floppy.WrittenSectorCount == 0 &&
            !machine.Dma.IsTerminalCount(2), "The own HDD boot manufactured a floppy transfer.");
        if (windows)
            Check(machine.Ports.Read(0xcf8, 4) == 0 && machine.S3!.ReadConfiguration(0x3c, 2) == 0x010b &&
                machine.PiixIrq!.ReadConfiguration(0x60, 1) == 11 && machine.PiixIrq.Elcr == 0x0800,
                "The IRQ6 mask change damaged the separate S3/PCI resource allocation.");
    }

    /// <summary>Creates a normal real-mode own caller while leaving the powered PIC, FDC, firmware and clock intact.</summary>
    private static void Prepare(PcMachine machine, ReadOnlySpan<byte> code)
    {
        machine.Cpu.Reset();
        machine.Cpu.SetSegment(Segment.Cs, 0); machine.Cpu.SetSegment(Segment.Ss, 0);
        machine.Cpu.SetSegment(Segment.Ds, 0); machine.Cpu.SetRegister(Register.Sp, 0x9000);
        machine.Cpu.Eflags = 2; machine.Cpu.Eip = Caller; code.CopyTo(machine.Memory.Ram.AsSpan((int)Caller));
    }

    /// <summary>Executes a bounded genuine own caller through its CLI/HLT sentinel, allowing the ordinary PC interrupt owner.</summary>
    private static void RunPrepared(PcMachine machine)
    {
        machine.RunSteps(256);
        Check(machine.Fault is null && !machine.Cpu.TripleFaulted && machine.Cpu.Halted &&
            machine.Cpu.GetSegment(Segment.Cs) == 0,
            machine.Fault ?? "The actual own port/IRQ caller stopped unexpectedly: " + machine.Cpu.Position);
    }

    /// <summary>Runs actual own IN/OUT code under CLI, without replacing a service or directly operating the PIC.</summary>
    private static void Ports(PcMachine machine, params byte[] code)
    {
        byte[] caller = [.. code, 0xfa, 0xf4];
        Prepare(machine, caller); RunPrepared(machine);
    }

    /// <summary>Masks only unrelated IRQ0/1 through real byte IN/OUT; the decisive POST IRQ6 bit is preserved exactly.</summary>
    private static void Isolate(PcMachine machine)
    {
        uint before = machine.Ports.Read(0x21, 1);
        Ports(machine, 0xba, 0x21, 0, 0xec, 0x0c, 3, 0xee);
        Check(machine.Ports.Read(0x21, 1) == (before | 3) &&
            (machine.Ports.Read(0x21, 1) & 0x40) == (before & 0x40),
            "The fixture changed the POST IRQ6 mask.");
        Prepare(machine, [0xfb, 0x90, 0xfa, 0xf4]); RunPrepared(machine);
    }

    /// <summary>Issues the actual DOR assert/release and advances only the existing nominal reset completion deadline.</summary>
    private static void ResetFloppy(PcMachine machine)
    {
        Ports(machine, 0xba, 0xf2, 3, 0xb0, 0, 0xee, 0xb0, 0x0c, 0xee);
        Check(!machine.Floppy.InterruptAsserted, "A reset IRQ appeared before the existing reset callback.");
        machine.Scheduler.AdvanceTo(machine.Scheduler.Now + 1_000_000);
        Check(machine.Floppy.InterruptAsserted && (machine.Pic.InputLines & 0x40) != 0,
            "The real FDC reset did not assert its ordinary IRQ6 line.");
    }

    /// <summary>Observes the first real BIOS handler instruction and the genuine six-byte interrupt frame before its pushes.</summary>
    private sealed class HandlerFrame : IDisposable
    {
        private readonly PcMachine _machine;
        private readonly uint _address;
        /// <summary>Number of actual entries at the IVT0Eh ROM address.</summary>
        public int Entries { get; private set; }

        /// <summary>Attaches a passive exact-address observer; no frame, register or instruction is modified.</summary>
        public HandlerFrame(PcMachine machine, uint address)
        { _machine = machine; _address = address; machine.Cpu.InstructionStarting += Starting; }

        /// <summary>Requires the true IRQ frame, restored-caller target and original data selector at actual entry.</summary>
        private void Starting(uint address, byte opcode)
        {
            if (address != _address) return;
            Entries++;
            Check(opcode == 0x50 && _machine.Cpu.GetSegment(Segment.Cs) == 0xf000 &&
                _machine.Cpu.GetSegment(Segment.Ss) == 0x3000 && _machine.Cpu.GetRegister(Register.Sp) == 0xa5a58ffau,
                "Actual ROM IRQ6 entry or hidden-stack width differs.");
            Check(_machine.Memory.ReadWord(StackTop - 6) == Caller + 2 &&
                _machine.Memory.ReadWord(StackTop - 4) == 0 &&
                _machine.Memory.ReadWord(StackTop - 2) == 0x0ed7 &&
                _machine.Cpu.GetSegment(Segment.Ds) == 0x1234 && (_machine.Cpu.Eflags & 0x200) == 0,
                "IRQ6 did not enter with the caller's real IP/CS/FLAGS and clear IF.");
        }

        /// <summary>Detaches the passive instruction observer.</summary>
        public void Dispose() => _machine.Cpu.InstructionStarting -= Starting;
    }

    /// <summary>Executes STI/NOP/CLI/HLT with distinctive full GPRs, selectors, flags and independently guarded stack bytes.</summary>
    private static void Deliver(PcMachine machine, uint vector, bool expected)
    {
        Prepare(machine, [0xfb, 0x90, 0xfa, 0xf4]);
        uint[] registers = [0x9a5bc37d, 0x6d31e29f, 0x53b8d740, 0xd268ac17,
            0xa5a59000, 0x73b6d921, 0x29a7c536, 0xe48b316f];
        ushort[] segments = [0x2345, 0, 0x3000, 0x1234, 0x3456, 0x4567];
        for (int i = 0; i < registers.Length; i++) machine.Cpu.SetRegister((Register)i, registers[i]);
        for (int i = 0; i < segments.Length; i++) machine.Cpu.SetSegment((Segment)i, segments[i]);
        machine.Cpu.Eflags = 0x0cd7;
        machine.Memory.Ram.AsSpan((int)StackTop - 32, 64).Fill(0xa7);
        machine.Memory.WriteByte(0x43d, 0x69); machine.Memory.WriteByte(0x43e, 0x35); machine.Memory.WriteByte(0x43f, 0x96);
        using var trace = new Trace(machine); using var frame = new HandlerFrame(machine, vector);
        RunPrepared(machine);
        Check(machine.Cpu.Eip == Caller + 4 && machine.Cpu.Eflags == 0x0cd7,
            "IRET/CLI did not preserve the actual caller's instruction, arithmetic flags or DF.");
        for (int i = 0; i < registers.Length; i++)
            Check(machine.Cpu.GetRegister((Register)i) == registers[i], "IRQ6 changed full GPR index " + i);
        for (int i = 0; i < segments.Length; i++)
            Check(machine.Cpu.GetSegment((Segment)i) == segments[i], "IRQ6 changed segment index " + i);
        Check(machine.Memory.Ram.AsSpan((int)StackTop - 32, 22).IndexOfAnyExcept((byte)0xa7) < 0 &&
            machine.Memory.Ram.AsSpan((int)StackTop, 32).IndexOfAnyExcept((byte)0xa7) < 0,
            "IRQ6 wrote outside its actual six-byte frame plus AX/DS pushes.");
        Check(machine.Memory.ReadByte(0x43d) == 0x69 && machine.Memory.ReadByte(0x43f) == 0x96 &&
            machine.Memory.ReadByte(0x43e) == (expected ? 0xb5 : 0x35),
            "The actual BIOS IRQ6 did not preserve independent BDA status bits/guards.");
        Check(frame.Entries == (expected ? 1 : 0) &&
            trace.Acks.SequenceEqual(expected ? new[] { new Ack(6, 0x0e, false) } : Array.Empty<Ack>()),
            "The physical reset did not take precisely the expected real PIC IRQ6 path.");
        Port[] eois = trace.Ports.Where(p => p.Write && p.Address == 0x20 && p.Value == 0x20).ToArray();
        Check(eois.Length == (expected ? 1 : 0) && eois.All(p => p.Width == 1 && p.Instruction is >= 0xf0000 and <= 0xfffff),
            "IRQ6 did not send exactly one actual ROM master EOI.");
        Check(!trace.Ports.Any(p => p.Address == 0x3f5),
            "The BIOS IRQ handler consumed a SENSE/result byte instead of leaving it for polling.");
        Check(machine.Floppy.InterruptAsserted && (machine.Pic.InputLines & 0x40) != 0,
            "A PIC acknowledgement/EOI incorrectly cleared the independent FDC result source.");
        if (expected) Check(!machine.Pic.HasPending, "The held edge source manufactured a second interrupt after EOI.");
    }

    /// <summary>Reads exactly the four original reset SENSE records via CPU ports and then their ordinary empty-queue status.</summary>
    private static void PollResetResults(PcMachine machine)
    {
        var code = new List<byte> { 0xba, 0xf5, 3 };
        for (int unit = 0; unit < 4; unit++)
        {
            code.AddRange(new byte[] { 0xb0, 8, 0xee, 0xec, 0xa2, (byte)(unit * 2), 0x20,
                0xec, 0xa2, (byte)(unit * 2 + 1), 0x20 });
        }
        Ports(machine, code.ToArray());
        Check(machine.Memory.Ram.AsSpan(0x2000, 8).SequenceEqual(new byte[] { 0xc0, 0, 0xc1, 0, 0xc2, 0, 0xc3, 0 }),
            "The ROM IRQ handler lost or changed the four polling reset results.");
        Ports(machine, 0xba, 0xf5, 3, 0xb0, 8, 0xee, 0xec, 0xa2, 8, 0x20);
        Check(machine.Memory.ReadByte(0x2008) == 0x80 && !machine.Floppy.InterruptAsserted && !machine.Floppy.Busy &&
            (machine.Pic.InputLines & 0x40) == 0 && machine.Floppy.ReadSectorCount == 0 &&
            machine.Floppy.WrittenSectorCount == 0 && !machine.Dma.IsTerminalCount(2),
            "Reset-result polling did not remain independent from IRQ delivery and DMA.");
    }

    /// <summary>Registers actual installed/absent/media-independent POST and BIOS-IRQ controls; Windows acceptance is separate.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        Configuration[] configurations =
        [
            new("no physical floppy", 0, 0), new("empty A", 1, 0), new("own medium A", 1, 1),
            new("empty A and B", 2, 0), new("empty A and own medium B", 2, 2), new("own media A and B", 2, 3)
        ];
        foreach (bool fast in new[] { false, true })
        {
            foreach (var configuration in configurations)
                test("M4 Windows95 floppy POST real mask and IVT fast=" + fast + " " + configuration.Name, () =>
                {
                    using var machine = Machine(fast, configuration); using var trace = new Trace(machine);
                    Boot(machine); CheckPost(machine, trace, configuration.Drives, true);
                });
            foreach (var configuration in configurations.Where(c => c.Media == 0 || c.Media == 2))
                test("M4 Windows95 floppy reset real IRQ6 BIOS frame EOI and polling fast=" + fast + " " + configuration.Name, () =>
                {
                    using var machine = Machine(fast, configuration); Boot(machine); uint vector = CheckVector(machine, configuration.Drives);
                    Isolate(machine);
                    if (configuration.Drives > 0)
                        Check((machine.Pic.Read(0x20, 1) & 0x40) == 0, "A previous pending IRQ6 was not serviced by the real ROM.");
                    ResetFloppy(machine); Deliver(machine, vector, configuration.Drives > 0);
                    PollResetResults(machine);
                });
        }
        foreach (int legacy in new[] { 1, 2 })
            test("M4 Windows95 floppy earlier POST keeps polling-only mask legacy=" + legacy, () =>
            {
                var configuration = new Configuration("own physical A", 1, 1);
                using var machine = Machine(false, configuration, legacy); using (var trace = new Trace(machine))
                { Boot(machine); CheckPost(machine, trace, 1, false); }
                Isolate(machine); ResetFloppy(machine); Deliver(machine, CheckVector(machine, 1), false); PollResetResults(machine);
            });
    }
}
