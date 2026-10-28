// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Independent direct SYS calls; neither DOS nor MSCDEX is replaced by this narrow ABI fixture.</summary>
internal static class DosCdRomDriverTests
{
    /// <summary>Largest measured use of the caller stack, for independent DOS-stack risk review.</summary>
    public static int MaxStackBytes { get; private set; }
    /// <summary>Fails on an independently reconstructed register, memory or actual port effect.</summary>
    private static void Check(bool value, string message = "Own CD SYS ABI invariant")
    { if (!value) throw new InvalidOperationException(message); }

    /// <summary>Own deterministic cooked blocks and optional reproducible backend failure.</summary>
    private sealed class Disc : IReadOnlyOpticalDisc
    {
        /// <summary>Records real device reads separately from task-file polling.</summary>
        public readonly List<long> Reads = [];
        /// <summary>Own fixed extent includes first/last and range-fault controls.</summary>
        public long BlockCount => 73;
        /// <summary>Public media inventory contains no existing host file.</summary>
        public string Name => "Own independent SYS ABI cooked disc";
        /// <summary>Optional own injected I/O failure, distinct from address rejection.</summary>
        public long FailLba = -1;
        /// <summary>Derives every byte without consulting the driver or ATAPI response.</summary>
        public static byte Value(long lba, int offset) => (byte)(lba * 37 + offset * 13 + (offset >> 8));
        /// <summary>Supplies one immutable cooked sector with exact physical extent checking.</summary>
        public void ReadBlock(long lba, Span<byte> destination)
        {
            if (lba < 0 || lba >= BlockCount) throw new ArgumentOutOfRangeException(nameof(lba));
            if (destination.Length < 2048) throw new ArgumentException("Short own block");
            Reads.Add(lba); if (lba == FailLba) throw new IOException("Own selected sector failure");
            for (int i = 0; i < 2048; i++) destination[i] = Value(lba, i);
        }
        /// <summary>Creates only another own generated medium.</summary>
        public IReadOnlyOpticalDisc Reopen() => new Disc { FailLba = FailLba };
        /// <summary>The generated medium owns no host resource.</summary>
        public void Dispose() { }
    }

    /// <summary>Actual initialized PC with one secondary-master packet peer and a SYS loaded into low RAM.</summary>
    private sealed class Rig : IDisposable
    {
        /// <summary>SYS load segment is independent from caller, request, control buffer and stack.</summary>
        public const ushort SysSeg = 0x4000, CallerSeg = 0x1000, RequestSeg = 0x2000;
        /// <summary>Original request offset may cross a segment end while its conventional linear bytes remain contiguous.</summary>
        public readonly ushort RequestOff;
        /// <summary>Own complete PC, including original buses, scheduler and immutable packet medium.</summary>
        public readonly PcMachine Machine;
        /// <summary>Own optional medium remains observable for independent sector effects.</summary>
        public readonly Disc? Medium;
        /// <summary>Actual completed packet opcodes reconstructed from the six word outputs.</summary>
        public readonly List<byte> Packets = [];
        /// <summary>Actual data word reads indexed by the independently observed current packet opcode.</summary>
        public readonly Dictionary<byte, int> DataWords = [];
        private readonly List<byte> _packet = [];
        private bool _collectPacket;
        private byte _opcode;

        /// <summary>Constructs original peers and executes the real ROM POST before installing only own caller data.</summary>
        public Rig(byte[] sys, bool loaded = true, bool initialize = true, ushort requestOffset = 0x300)
        {
            RequestOff = requestOffset; Medium = loaded ? new Disc() : null;
            var profile = new MachineProfile { Id = MachineProfile.AtapiProfileId, Name = "Own SYS ABI", Cpu = MachineProfile.P54cCpuId,
                CpuHz = MachineProfile.FxHostCpuHz, RamMiB = MachineProfile.FxHostRamMiB,
                VideoCard = MachineProfile.S3Trio64VideoId, OpticalDrive = MachineProfile.AtapiCdRomId, AtapiSlot = 2 };
            Machine = PcMachine.CreateWithOpticalDiscs(profile, new IReadOnlyBlockDevice?[4], Medium);
            Machine.RunSteps(250000); Check(Machine.Fault is null, Machine.Fault ?? "ROM failed");
            Machine.Ports.Write(0x21, 0xff, 1); Machine.Ports.Write(0xa1, 0xff, 1);
            Check(sys.Length is >= 22 and < 65536, "SYS binary extent");
            sys.CopyTo(Machine.Memory.Ram, SysSeg * 16);
            Machine.Ports.Accessed += Trace;
            // Own safe print stub permits a driver initialization diagnostic without supplying a DOS kernel.
            Machine.Memory.WriteWord(0x21 * 4, 0x0200); Machine.Memory.WriteWord(0x21 * 4 + 2, CallerSeg);
            Machine.Memory.WriteByte(CallerSeg * 16u + 0x0200, 0xcf);
            if (initialize) Init();
            ClearTrace();
        }

        /// <summary>Observes completed actual packet writes and data words without reading or acknowledging a device.</summary>
        private void Trace(ushort port, uint value, int width, bool write)
        {
            if (write && port == 0x177 && value == 0xa0) { _packet.Clear(); _collectPacket = true; }
            if (write && port == 0x170 && _collectPacket)
            {
                Check(width == 2, "Packet was not word PIO"); _packet.Add((byte)value); _packet.Add((byte)(value >> 8));
                if (_packet.Count == 12) { _collectPacket = false; _opcode = _packet[0]; Packets.Add(_opcode); }
            }
            if (!write && port == 0x170)
            { Check(width == 2, "Data was not word PIO"); DataWords[_opcode] = DataWords.GetValueOrDefault(_opcode) + 1; }
        }

        /// <summary>Starts an independent request with guards and caller-supplied reserved bytes.</summary>
        public void Request(byte command, byte length = 27, byte unit = 0)
        {
            Machine.Memory.Ram.AsSpan(RequestSeg * 16 + RequestOff - 32, 128).Fill(0xa7);
            var r = Machine.Memory.Ram.AsSpan(RequestSeg * 16 + RequestOff, 27); r.Clear();
            r[0] = length; r[1] = unit; r[2] = command;
        }
        /// <summary>Writes a request byte at its specified ABI offset.</summary>
        public void Rb(int offset, byte value) => Machine.Memory.WriteByte(RequestSeg * 16u + RequestOff + (uint)offset, value);
        /// <summary>Writes a request word at its specified ABI offset.</summary>
        public void Rw(int offset, ushort value) => Machine.Memory.WriteWord(RequestSeg * 16u + RequestOff + (uint)offset, value);
        /// <summary>Writes a request dword at its specified ABI offset.</summary>
        public void Rd(int offset, uint value) => Machine.Memory.WriteDword(RequestSeg * 16u + RequestOff + (uint)offset, value);
        /// <summary>Reads an actual request result word.</summary>
        public ushort Word(int offset) => Machine.Memory.ReadWord(RequestSeg * 16u + RequestOff + (uint)offset);
        /// <summary>Reads the completed request status.</summary>
        public ushort Status => Word(3);
        /// <summary>Returns one independently checked request byte.</summary>
        public byte Byte(int offset) => Machine.Memory.ReadByte(RequestSeg * 16u + RequestOff + (uint)offset);

        /// <summary>Performs two actual far CALL instructions and requires real return to the independent caller.</summary>
        public void Call()
        {
            var m = Machine; m.Cpu.Reset(); m.Cpu.SetSegment(Segment.Cs, CallerSeg); m.Cpu.SetSegment(Segment.Ss, CallerSeg);
            m.Cpu.SetSegment(Segment.Ds, 0x3000); m.Cpu.SetSegment(Segment.Es, RequestSeg); m.Cpu.SetSegment(Segment.Fs, 0x6000); m.Cpu.SetSegment(Segment.Gs, 0x7000);
            uint[] input = [0x12341248, 0x23452581, 0x3456369a, 0x45670000u | RequestOff, 0x5678f000, 0x6789579a, 0x789a68ab, 0x89ab79bc];
            for (int i = 0; i < 8; i++) m.Cpu.SetRegister((Register)i, input[i]);
            const uint inputFlags = 0xed7; m.Cpu.Eflags = inputFlags; m.Cpu.Eip = 0x100;
            ushort strategy = m.Memory.ReadWord(SysSeg * 16u + 6), interrupt = m.Memory.ReadWord(SysSeg * 16u + 8);
            byte[] caller = [0x9a, (byte)strategy, (byte)(strategy >> 8), (byte)(SysSeg & 255), (byte)(SysSeg >> 8),
                0x9a, (byte)interrupt, (byte)(interrupt >> 8), (byte)(SysSeg & 255), (byte)(SysSeg >> 8), 0xfa, 0xf4];
            caller.CopyTo(m.Memory.Ram, CallerSeg * 16 + 0x100);
            uint? returnedFlags = null;
            // Records only executed caller flags and stack depth without reading or acknowledging a device.
            void Observe(uint address, byte opcode)
            {
                if (m.Cpu.GetSegment(Segment.Cs) == SysSeg) MaxStackBytes = Math.Max(MaxStackBytes, (ushort)(0xf000 - (ushort)m.Cpu.GetRegister(Register.Sp)));
                if (address == CallerSeg * 16u + 0x10a && opcode == 0xfa) returnedFlags = m.Cpu.Eflags;
            }
            m.Cpu.InstructionStarting += Observe;
            try { m.RunSteps(4000000); } finally { m.Cpu.InstructionStarting -= Observe; }
            Check(m.Fault is null, m.Fault ?? "SYS execution fault"); Check(returnedFlags == inputFlags, "SYS altered caller flags before return CLI");
            Check(m.Cpu.Halted && m.Cpu.GetSegment(Segment.Cs) == CallerSeg && m.Cpu.Eip == 0x10c, "SYS did not genuinely return");
            for (int i = 0; i < 8; i++) Check(m.Cpu.GetRegister((Register)i) == input[i], "Changed ABI register " + (Register)i);
            Check(m.Cpu.GetSegment(Segment.Ds) == 0x3000 && m.Cpu.GetSegment(Segment.Es) == RequestSeg && m.Cpu.GetSegment(Segment.Ss) == CallerSeg &&
                m.Cpu.GetSegment(Segment.Fs) == 0x6000 && m.Cpu.GetSegment(Segment.Gs) == 0x7000, "Changed ABI segment");
            Check((m.Cpu.Eflags & 0x400) != 0, "Caller DF was not restored");
            var ram = m.Memory.Ram;
            Check(ram.AsSpan(RequestSeg * 16 + RequestOff - 32, 32).IndexOfAnyExcept((byte)0xa7) < 0 &&
                ram.AsSpan(RequestSeg * 16 + RequestOff + 27, 32).IndexOfAnyExcept((byte)0xa7) < 0, "Request guard changed");
        }

        /// <summary>Calls the real initialization entry and supplies an own explicit command line.</summary>
        public void Init()
        {
            Request(0, 23); Rd(18, 0x25000100); byte[] line = Encoding.ASCII.GetBytes("C:\\DOSECD.SYS /D:DOSECD01\r\n");
            line.CopyTo(Machine.Memory.Ram, 0x25100); Call(); Check(Status == 0x100, "INIT status " + Status.ToString("X4"));
        }
        /// <summary>Builds an IOCTL input or output request without sharing its transfer bytes with the request.</summary>
        public void Ioctl(byte function, int length, bool write = false, byte argument = 0, byte envelopeLength = 26)
        {
            Request(write ? (byte)12 : (byte)3, envelopeLength); Rd(14, 0x30000500); Rw(18, (ushort)length);
            Machine.Memory.Ram.AsSpan(0x304e0, 96).Fill(0xb6); Machine.Memory.WriteByte(0x30500, function); Machine.Memory.WriteByte(0x30501, argument);
            byte[] before = Machine.Memory.Ram.AsSpan(0x304e0, 96).ToArray(); Call();
            Check(Machine.Memory.Ram.AsSpan(0x304e0, 32).SequenceEqual(before.AsSpan(0, 32)) && Machine.Memory.ReadByte(0x30500) == function, "IOCTL prefix guard changed");
            int tail = 32 + Math.Max(length, 2); Check(Machine.Memory.Ram.AsSpan(0x304e0 + tail, 96 - tail).SequenceEqual(before.AsSpan(tail)), "IOCTL suffix guard changed");
        }
        /// <summary>Builds one cooked HSG read request with independently chosen segment and offset.</summary>
        public void Read(long lba, ushort count, ushort segment, ushort offset, byte addressMode = 0, byte mode = 0, byte length = 27)
        {
            Request(0x80, length); Rb(13, addressMode); Rd(14, ((uint)segment << 16) | offset); Rw(18, count); Rd(20, (uint)lba); Rb(24, mode); Call();
        }
        /// <summary>Clears only own observation counters, preserving the actual guest device state.</summary>
        public void ClearTrace() { Packets.Clear(); DataWords.Clear(); Medium?.Reads.Clear(); }
        /// <summary>Releases exclusively own generated media and CPU/device resources.</summary>
        public void Dispose() => Machine.Dispose();
    }

    /// <summary>Uses exactly the assembled SYS embedded by the current build, rather than an external mutable driver path.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        using Stream stream = typeof(DosCdRomDriverTests).Assembly.GetManifestResourceStream("DOSECD.SYS")
            ?? throw new InvalidOperationException("Current build lacks the DOSECD.SYS resource.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); AddCases(test, bytes.ToArray());
    }
    /// <summary>Registers ABI probes before actual DOS/MSCDEX integration, without treating them as DOS acceptance.</summary>
    private static void AddCases(Action<string, Action> test, byte[] sys)
    {
        test("CD SYS original header and genuine far INIT return", () =>
        {
            using var r = new Rig(sys, initialize: false); var m = r.Machine;
            Check(m.Memory.ReadDword(Rig.SysSeg * 16u) == uint.MaxValue && m.Memory.ReadWord(Rig.SysSeg * 16u + 4) == 0xc800);
            Check(Encoding.ASCII.GetString(m.Memory.Ram, Rig.SysSeg * 16 + 10, 8) == "DOSECD01");
            Check(m.Memory.ReadWord(Rig.SysSeg * 16u + 18) == 0 && m.Memory.ReadByte(Rig.SysSeg * 16u + 20) == 0 && m.Memory.ReadByte(Rig.SysSeg * 16u + 21) == 1);
            r.Init(); Check(r.Byte(13) == 0 && r.Byte(22) == 0 && r.Word(16) == Rig.SysSeg && r.Word(14) <= sys.Length && r.Word(14) >= 22);
        });
        test("CD SYS IOCTL returns real resident header without ATA effects", () =>
        {
            using var r = new Rig(sys); r.Ioctl(0, 5); Check(r.Status == 0x100 && r.Machine.Memory.ReadDword(0x30501) == (uint)Rig.SysSeg << 16);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS cooked sector size and truthful default capabilities", () =>
        {
            using var r = new Rig(sys); r.Ioctl(7, 4); Check(r.Status == 0x100 && r.Machine.Memory.ReadWord(0x30502) == 2048);
            r.Ioctl(6, 5); Check(r.Status == 0x100 && r.Machine.Memory.ReadDword(0x30501) == 2, "Unexpected raw/audio/write/prefetch capability");
        });
        foreach (ushort offset in new ushort[] { 0x0200, 0xfffe, 0xffff })
        test("CD SYS actual cooked sector bytes and segment carry offset=" + offset.ToString("X4"), () =>
        {
            using var r = new Rig(sys); uint first = 0x70000u + offset; const int count = 3;
            r.Machine.Memory.Ram.AsSpan((int)first - 32, count * 2048 + 64).Fill(0xcd); r.Read(5, count, 0x7000, offset);
            Check(r.Status == 0x100, "Read status " + r.Status.ToString("X4"));
            for (int i = 0; i < count * 2048; i++) Check(r.Machine.Memory.ReadByte(first + (uint)i) == Disc.Value(5 + i / 2048, i % 2048), "Cooked data mismatch at " + i);
            Check(r.Machine.Memory.Ram.AsSpan((int)first - 32, 32).IndexOfAnyExcept((byte)0xcd) < 0 &&
                r.Machine.Memory.Ram.AsSpan((int)first + count * 2048, 32).IndexOfAnyExcept((byte)0xcd) < 0, "Transfer guards changed");
            Check(r.Medium!.Reads.SequenceEqual(new long[] { 5, 6, 7 }) && r.DataWords.GetValueOrDefault((byte)0x28) == count * 1024, "Read bypassed actual cooked word PIO");
        });
        foreach (byte command in new byte[] { 1, 2, 4, 5, 6, 8, 9, 10, 11, 15, 16, 0x81, 0x84, 0x86, 0x87, 0x88, 0xff })
        test("CD SYS unsupported request rejects before ATA command=" + command.ToString("X2"), () =>
        {
            using var r = new Rig(sys); r.Request(command); r.Call(); Check(r.Status == 0x8103, "Unsupported status " + r.Status.ToString("X4"));
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        foreach (byte length in new byte[] { 0, 4, 12, 13, 18, 26 })
        test("CD SYS short READ LONG length=" + length, () =>
        {
            using var r = new Rig(sys); r.Read(0, 1, 0x7000, 0x100, length: length); Check(r.Status == 0x8105);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        foreach ((byte function, int length) in new (byte, int)[] { (0, 4), (6, 4), (7, 3), (8, 4), (9, 1) })
        test("CD SYS short IOCTL function=" + function + " length=" + length, () =>
        {
            using var r = new Rig(sys); r.Ioctl(function, length); Check(r.Status == 0x8105);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS wrong subunit returns explicit error before hardware", () =>
        {
            using var r = new Rig(sys); r.Request(13, 13, 1); r.Call(); Check(r.Status == 0x8101);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS device header MSCDEX-owned drive letter survives later calls", () =>
        {
            using var r = new Rig(sys); r.Machine.Memory.WriteByte(Rig.SysSeg * 16u + 20, 4);
            r.Request(13, 13); r.Call(); Check(r.Status == 0x100); r.Ioctl(0, 5);
            Check(r.Machine.Memory.ReadByte(Rig.SysSeg * 16u + 20) == 4 && r.Machine.Memory.ReadByte(Rig.SysSeg * 16u + 21) == 1);
        });
        foreach ((byte address, byte mode) in new (byte, byte)[] { (1,0), (2,0), (255,0), (0,1), (0,2), (0,255) })
        test("CD SYS rejects unsupported addressing/read mode before ATAPI " + address + "/" + mode, () =>
        {
            using var r = new Rig(sys); r.Read(1, 1, 0x7000, 0x100, address, mode); Check(r.Status == 0x8103);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS unknown IOCTL command is explicit and leaves data guards", () =>
        {
            using var r = new Rig(sys); r.Ioctl(0xff, 5); Check(r.Status == 0x8103);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS media capacity last actual HSG sector and EOF agree", () =>
        {
            using var r = new Rig(sys); r.Ioctl(8, 5); Check(r.Status == 0x100 && r.Machine.Memory.ReadDword(0x30501) == 73);
            r.ClearTrace(); r.Read(72, 1, 0x7000, 0x100); Check(r.Status == 0x100 && r.Medium!.Reads.SequenceEqual(new long[] { 72 }));
            for (int i = 0; i < 2048; i++) Check(r.Machine.Memory.ReadByte(0x70100u + (uint)i) == Disc.Value(72, i));
            r.ClearTrace(); r.Machine.Memory.Ram.AsSpan(0x70100, 2048).Fill(0xd3); r.Read(73, 1, 0x7000, 0x100);
            Check(r.Status == 0x8108 && r.Medium!.Reads.Count == 0 && r.DataWords.GetValueOrDefault((byte)0x28) == 0);
            Check(r.Machine.Memory.Ram.AsSpan(0x70100, 2048).IndexOfAnyExcept((byte)0xd3) < 0, "EOF fabricated sector bytes");
        });
        test("CD SYS installed empty packet drive loads and read returns not ready", () =>
        {
            using var r = new Rig(sys, loaded: false); r.Machine.Memory.Ram.AsSpan(0x70100, 2048).Fill(0xd3);
            r.Read(0, 1, 0x7000, 0x100); Check(r.Status == 0x8102 && r.DataWords.GetValueOrDefault((byte)0x28) == 0);
            Check(r.Machine.Memory.Ram.AsSpan(0x70100, 2048).IndexOfAnyExcept((byte)0xd3) < 0);
        });
        foreach (byte command in new byte[] { 7, 13, 14, 0x85 })
        test("CD SYS supported no-data compatibility call=" + command.ToString("X2"), () =>
        {
            using var r = new Rig(sys); r.Request(command, 13); r.Call(); Check(r.Status == 0x100);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        foreach ((byte function, ushort size) in new (byte, ushort)[] { (0,5), (6,5), (7,4), (8,5), (9,2) })
        test("CD SYS oversized IOCTL reports actual transfer count function=" + function, () =>
        {
            using var r = new Rig(sys); r.Ioctl(function, 16); Check(r.Status == 0x100 && r.Word(18) == size, "Wrong actual IOCTL byte count");
            Check(r.Machine.Memory.Ram.AsSpan(0x30500 + size, 16 - size).IndexOfAnyExcept((byte)0xb6) < 0, "Driver wrote beyond actual control response");
        });
        test("CD SYS real backend read error leaves failed block intact and exact completed count", () =>
        {
            using var r = new Rig(sys); r.Medium!.FailLba = 6; r.Machine.Memory.Ram.AsSpan(0x70100, 3 * 2048).Fill(0xd3);
            r.Read(5, 3, 0x7000, 0x100); Check(r.Status == 0x810b && r.Word(18) == 1, "Wrong partial backend result");
            for (int i = 0; i < 2048; i++) Check(r.Machine.Memory.ReadByte(0x70100u + (uint)i) == Disc.Value(5, i));
            Check(r.Machine.Memory.Ram.AsSpan(0x70900, 2 * 2048).IndexOfAnyExcept((byte)0xd3) < 0, "Failure fabricated or partially copied block bytes");
            Check(r.Medium.Reads.SequenceEqual(new long[] { 5, 6 }) && r.DataWords.GetValueOrDefault((byte)0x28) == 1024);
            r.Medium.FailLba = -1; r.ClearTrace(); r.Read(6, 1, 0x7000, 0x100); Check(r.Status == 0x100 && r.Word(18) == 1, "Actual subsequent read did not recover");
        });
        test("CD SYS LBA overflow and over-conventional transfer are rejected before ATA", () =>
        {
            using var r = new Rig(sys); r.Read(uint.MaxValue, 2, 0x7000, 0x100); Check(r.Status == 0x8108 && r.Word(18) == 0);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0); r.Read(1, 2, 0xf000, 0xfffe); Check(r.Status == 0x8105 && r.Word(18) == 0);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0);
        });
        test("CD SYS exact conventional-memory end is a valid cooked target", () =>
        {
            using var r = new Rig(sys); r.Read(2, 1, 0x9000, 0xf800); Check(r.Status == 0x100 && r.Word(18) == 1);
            for (int i = 0; i < 2048; i++) Check(r.Machine.Memory.ReadByte(0x9f800u + (uint)i) == Disc.Value(2, i));
        });
        test("CD SYS lock status actual unlock-eject close and changed media sequence", () =>
        {
            using var r = new Rig(sys); r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 1);
            r.Ioctl(1, 2, true, 1); Check(r.Status == 0x100 && r.Machine.Atapi!.CapturePhase().PreventRemoval);
            r.Ioctl(6, 5); Check(r.Status == 0x100 && r.Machine.Memory.ReadDword(0x30501) == 0);
            r.ClearTrace(); r.Ioctl(0, 1, true); Check(r.Status == 0x100 && r.Machine.Atapi!.CapturePhase().TrayOpen && !r.Machine.Atapi.CapturePhase().PreventRemoval);
            Check(r.Packets.SequenceEqual(new byte[] { 0x1e, 0x1b }), "Eject did not actually unlock before ejecting");
            r.Ioctl(6, 5); Check(r.Status == 0x100 && r.Machine.Memory.ReadDword(0x30501) == 3);
            r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 0xff);
            r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 0);
            r.Ioctl(5, 1, true); Check(r.Status == 0x100 && !r.Machine.Atapi!.CapturePhase().TrayOpen);
            r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 0xff);
            r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 1);
            r.Read(3, 1, 0x7000, 0x100); Check(r.Status == 0x100 && r.Word(18) == 1);
        });
        test("CD SYS target-only reset re-identifies and clears reset attention without fake media change", () =>
        {
            using var r = new Rig(sys); ulong generation = r.Machine.Atapi!.CapturePhase().Generation;
            int resetWrites = 0, srstWrites = 0;
            r.Machine.Ports.Accessed += (p,v,_,w) => { if (w && p == 0x177 && v == 8) resetWrites++; if (w && p == 0x376 && (v & 4) != 0) srstWrites++; };
            r.Ioctl(2, 1, true); Check(r.Status == 0x100 && resetWrites == 1 && srstWrites == 0 && r.Machine.Atapi.CapturePhase().Generation > generation);
            r.Ioctl(9, 2); Check(r.Status == 0x100 && r.Machine.Memory.ReadByte(0x30501) == 1);
            r.Read(3, 1, 0x7000, 0x100); Check(r.Status == 0x100);
        });
        test("CD SYS zero sector read does not invent ATA or touch target", () =>
        {
            using var r = new Rig(sys); r.Machine.Memory.Ram.AsSpan(0x70100, 2048).Fill(0xd3); r.Read(uint.MaxValue, 0, 0xffff, 0xffff);
            Check(r.Status == 0x100 && r.Word(18) == 0 && r.Packets.Count == 0 && r.DataWords.Count == 0);
            Check(r.Machine.Memory.Ram.AsSpan(0x70100, 2048).IndexOfAnyExcept((byte)0xd3) < 0);
        });
        test("CD SYS unsupported short INIT22 returns bad length without extension write", () =>
        {
            using var r = new Rig(sys, initialize: false); r.Request(0, 22); r.Rb(22, 0x7d); r.Call();
            Check(r.Status == 0x8105 && r.Byte(22) == 0x7d, "Short INIT22 was accepted or extended outside its envelope");
        });
        test("CD SYS partial real EOF retains first completed block and exact count", () =>
        {
            using var r = new Rig(sys); r.Machine.Memory.Ram.AsSpan(0x70100, 4096).Fill(0xd3); r.Read(72, 2, 0x7000, 0x100);
            Check(r.Status == 0x8108 && r.Word(18) == 1 && r.Medium!.Reads.SequenceEqual(new long[] { 72 }));
            for (int i = 0; i < 2048; i++) Check(r.Machine.Memory.ReadByte(0x70100u + (uint)i) == Disc.Value(72, i));
            Check(r.Machine.Memory.Ram.AsSpan(0x70900, 2048).IndexOfAnyExcept((byte)0xd3) < 0, "EOF fabricated final block");
        });
        foreach ((byte function, ushort size) in new (byte, ushort)[] { (0,5), (6,5), (7,4), (8,5), (9,2) })
        test("CD SYS genuine DOS22 IOCTL envelope function=" + function, () =>
        {
            using var r = new Rig(sys); r.Ioctl(function, 16, envelopeLength: 22);
            Check(r.Status == 0x100 && r.Word(18) == size, "DOS22 IOCTL status/count " + r.Status.ToString("X4"));
            if (function == 0) Check(r.Machine.Memory.ReadDword(0x30501) == (uint)Rig.SysSeg << 16, "DOS22 failed to discover the genuine resident header");
        });
        foreach (byte length in new byte[] { 13, 19, 20, 21 })
        test("CD SYS IOCTL shorter than DOS22 rejects envelope=" + length, () =>
        {
            using var r = new Rig(sys); r.Ioctl(0, 5, envelopeLength: length); Check(r.Status == 0x8105);
            Check(r.Packets.Count == 0 && r.DataWords.Count == 0 && r.Machine.Memory.ReadDword(0x30501) == 0xb6b6b600u);
        });
        foreach (byte length in new byte[] { 23, 24, 26 })
        test("CD SYS accepts historical and actual DOS INIT envelope=" + length, () =>
        {
            using var r = new Rig(sys, initialize: false); r.Request(0, length); r.Rb(22, 0x7d); r.Call();
            Check(r.Status == 0x100 && r.Byte(13) == 0 && r.Byte(22) == 0 && r.Word(16) == Rig.SysSeg && r.Word(14) <= sys.Length);
        });
        foreach (ushort offset in new ushort[] { 0xffe5, 0xfff0, 0xffff })
        test("CD SYS conventional request far pointer segment normalization offset=" + offset.ToString("X4"), () =>
        {
            using var r = new Rig(sys, requestOffset: offset); r.Ioctl(0, 5, envelopeLength: 22); Check(r.Status == 0x100);
            r.Read(7, 1, 0x7000, 0x100); Check(r.Status == 0x100 && r.Word(18) == 1);
            for (int i = 0; i < 2048; i++) Check(r.Machine.Memory.ReadByte(0x70100u + (uint)i) == Disc.Value(7, i));
        });
        foreach ((byte size, byte skip) in new (byte, byte)[] { (1,3), (0,5) })
        test("CD SYS unadvertised interleave fields are ignored size=" + size + " skip=" + skip, () =>
        {
            using var r = new Rig(sys); r.Ioctl(6, 5); Check(r.Status == 0x100 && (r.Machine.Memory.ReadDword(0x30501) & 32) == 0);
            r.ClearTrace(); r.Machine.Memory.Ram.AsSpan(0x700e0, 4096 + 64).Fill(0xcd);
            r.Request(0x80); r.Rd(14, 0x70000100); r.Rw(18, 2); r.Rd(20, 5); r.Rb(25, size); r.Rb(26, skip); r.Call();
            Check(r.Status == 0x100 && r.Word(18) == 2 && r.Medium!.Reads.SequenceEqual(new long[] { 5, 6 }), "Unsupported interleave fields changed ordinary cooked read semantics");
            for (int i = 0; i < 4096; i++) Check(r.Machine.Memory.ReadByte(0x70100u + (uint)i) == Disc.Value(5 + i / 2048, i % 2048));
            Check(r.DataWords.GetValueOrDefault((byte)0x28) == 2048 && r.Machine.Memory.Ram.AsSpan(0x700e0, 32).IndexOfAnyExcept((byte)0xcd) < 0 &&
                r.Machine.Memory.Ram.AsSpan(0x71100, 32).IndexOfAnyExcept((byte)0xcd) < 0, "Interleave-ignore word/grant/guard mismatch");
        });
    }
}
