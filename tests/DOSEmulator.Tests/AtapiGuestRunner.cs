// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual original-DOS PACKET PIO, two complete PC restores and independent observed guest effects.</summary>
internal static class AtapiGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System, string Iso, int FirstCutTimeoutSeconds = 120);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0, uint Cr3,
        bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase, bool PrimaryIrq, bool SecondaryIrq, JsonElement Optical);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Word(ushort Value, bool Write, ulong Nanoseconds, long Instructions, uint Eip, ushort Cs, int Phase);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Readback(int Phase, Point At, byte[] Data);
    private sealed record Service(string Kind, Point Before)
    {
        /// <summary>Actual original resident service return before the caller consumes carry.</summary>
        public Point? After { get; set; }
    }
    private sealed record Boundary(string Kind, Point Before, byte[] Frame)
    {
        /// <summary>Actual completed IRET, observed before another owner step.</summary>
        public Point? After { get; set; }
    }
    private sealed record PcmDigest(long Frames, string Sha256);
    private sealed record TimestampTrap(Point Attempt, Point FaultDelivery);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects a missing real effect instead of repairing guest/controller state.</summary>
    private static void Check(bool condition, string why) { if (!condition) throw new InvalidDataException(why); }
    /// <summary>Encodes the entire registered raw graph without changing fields or queues.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes one explicitly selected own byte sequence.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes textual observations only, never screenshots or image data.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    /// <summary>Reads the exact metadata emitted by the own assembler.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("ATMETA01"u8);
        Check(at >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8, 2)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 10, 2)) == 28, "Own ATAPI metadata absent.");
        return Enumerable.Range(0, 28).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 12 + i * 2, 2))).ToArray();
    }
    /// <summary>Observes actual CPU, clock and immutable optical diagnostics at a CPU owner boundary.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] table)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(), m.Scheduler.ClockTicks(c.TimestampFrequency),
            c.TimestampCounter, c.Eflags, c.Cr0, c.Cr3, c.Virtual8086, c.Eip,
            Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(),
            basis == 0 ? 0 : m.Memory.ReadWord(basis + table[0] + 10u), m.Ata.InterruptAsserted, m.SecondaryAta.InterruptAsserted,
            JsonSerializer.SerializeToElement(m.Atapi!.CapturePhase(), StateValues.Json));
    }
    /// <summary>Reads the genuine six-byte DOS IRQ frame through the current original page tables without setting A/D.</summary>
    private static byte[] Frame(PcMachine m)
    {
        var c = m.Cpu; uint linear = c.GetSegmentBase(Segment.Ss) + (c.GetRegister(Register.Sp) & 65535); byte[] bytes = new byte[6];
        for (uint i = 0; i < bytes.Length; i++)
        {
            uint physical = linear + i;
            if (c.PagingEnabled)
            {
                uint pde = m.Memory.ReadDword((c.Cr3 & 0xfffff000) + (physical >> 22) * 4);
                Check((pde & 0x81) == 1, "Original IRQ PDE unavailable.");
                uint pte = m.Memory.ReadDword((pde & 0xfffff000) + ((physical >> 12) & 1023) * 4);
                Check((pte & 1) != 0, "Original IRQ PTE unavailable."); physical = (pte & 0xfffff000) | (physical & 4095);
            }
            bytes[i] = m.Memory.ReadByte(physical);
        }
        return bytes;
    }
    /// <summary>Read-only instruction, phase, port, physical IRQ and native PCM ledger for one actual PC.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint initialBasis = 0)
    {
        /// <summary>Actual unchanged loaded COM segment, inferred from its original bytes.</summary>
        public uint ProgramBase { get; private set; } = initialBasis;
        /// <summary>Actual task/config/PIC/KBC/DSP transfers, excluding repeated idle poll reads.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Every actual CD data word, including the twelve-byte CDB and gated ignored words.</summary>
        public List<Word> Words { get; } = [];
        /// <summary>Physical PIC acknowledgements, separate from guest software interrupts.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Complete own PIO readbacks at the unchanged completion instructions.</summary>
        public List<Readback> Readbacks { get; } = [];
        /// <summary>Original VDS call and return points.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Actual IRQ entry/commit/IRET and own RDTSC sites.</summary>
        public List<Boundary> Boundaries { get; } = [];
        /// <summary>Native timed DAC publications, independently bound to actual DMA bytes.</summary>
        public List<SoundBlaster16.Sample> Samples { get; } = [];
        /// <summary>Actual native DMA grants, never host-injected sample values.</summary>
        public List<SoundBlaster16.Transfer> Transfers { get; } = [];
        /// <summary>Whether the first cut NOP really executed in the own real/V86 context.</summary>
        public bool PacketCut { get; private set; }
        /// <summary>Whether the first actual partial Data-In NOP really executed.</summary>
        public bool DataCut { get; private set; }
        /// <summary>Stops only diagnostic recording at a hard aggregate record bound; the owner reports the unmodified guest state.</summary>
        public bool LimitReached { get; private set; }
        /// <summary>Absolute bound shared by all retained diagnostic lists.</summary>
        private const int MaximumRecords = 100_000;
        /// <summary>Actual vector13 delivery notifications, including legitimate original V86 I/O emulation.</summary>
        public long GeneralProtectionCount { get; private set; }
        /// <summary>Attempts at the two unchanged own RDTSC opcodes, before any commit or fault.</summary>
        public long TimestampAttempts { get; private set; }
        /// <summary>Actual vector13 deliveries during an own RDTSC step.</summary>
        public long TimestampGeneralProtections { get; private set; }
        /// <summary>Last own timestamp attempt and actual vector13 delivery notification before protected handler entry.</summary>
        public TimestampTrap? LastTimestampGeneralProtection { get; private set; }
        private Point? _timestampAttempt;
        private bool _timestampPending;
        /// <summary>Bounds all observation lists before allocating another point or data copy.</summary>
        private bool Reserve()
        {
            if (LimitReached) return false;
            if ((long)Ports.Count + Words.Count + Acks.Count + Readbacks.Count + Services.Count + Boundaries.Count + Samples.Count + Transfers.Count < MaximumRecords) return true;
            LimitReached = true; return false;
        }
        /// <summary>Copies only the bounded own proof area for textual diagnosis.</summary>
        public byte[] ProofBytes() => ProgramBase == 0 ? [] : m.Memory.Ram.AsSpan((int)ProgramBase + table[0], table[1] - table[0]).ToArray();
        private Service? _service;
        private Boundary? _iret;
        private int _pendingCut;
        /// <summary>Reads one own committed proof word without changing mappings.</summary>
        public ushort Proof(int offset) => ProgramBase == 0 ? (ushort)0 : m.Memory.ReadWord(ProgramBase + table[0] + (uint)offset);
        /// <summary>Captures one actual owner-boundary point.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Records real task/config/PIC and compact complete PIO data-word observations.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (ProgramBase == 0 || LimitReached) return;
            if (address == 0x170 && width == 2)
            {
                if (!Reserve()) return;
                Words.Add(new((ushort)value, write, m.Scheduler.Now, m.Cpu.Instructions, m.Cpu.Eip,
                    m.Cpu.GetSegment(Segment.Cs), Proof(10))); return;
            }
            bool selected = address is >= 0x171 and <= 0x177 || address is >= 0xcf8 and <= 0xcff ||
                address is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x376 or 0x60 or 0x64 or 0x226 or 0x22a or 0x22c or 0x22e or 0x8;
            if (!selected || (!write && address == 0x64) || (!write && address == 8 && value == 0)) return;
            if (!write && address == 0x376 && m.Cpu.GetSegmentBase(Segment.Cs) == ProgramBase &&
                m.Cpu.Eip != table[8] + 1 && value == 0x80) return;
            if (Reserve()) Ports.Add(new(address, value, width, write, Point()));
        }
        /// <summary>Records the physical interrupt path before the CPU creates its actual frame.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) { if (ProgramBase != 0 && Reserve()) Acks.Add(new(irq, vector, spurious, Point())); }
        /// <summary>Associates only an actual vector13 delivery notification in the same CPU step with an own timestamp attempt.</summary>
        public void Interrupt(byte vector)
        {
            if (vector != 13) return;
            GeneralProtectionCount++;
            if (_timestampPending && _timestampAttempt is not null)
            {
                TimestampGeneralProtections++;
                LastTimestampGeneralProtection = new(_timestampAttempt, Point());
            }
        }
        /// <summary>Observes native actual sample timestamps directly, without sampling the scheduler inside a callback.</summary>
        public void Sample(SoundBlaster16.Sample sample) { if (Reserve()) Samples.Add(sample); }
        /// <summary>Observes actual DMA bus-grant publication without affecting a callback or its time.</summary>
        public void Transferred(SoundBlaster16.Transfer transfer) { if (Reserve()) Transfers.Add(transfer); }
        /// <summary>Binds markers and services to immutable own instruction bytes and real original segment backing.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            if (LimitReached) return;
            uint basis = m.Cpu.GetSegmentBase(Segment.Cs);
            if (physical < basis || physical >= basis + table[3] || basis + 0x110 >= m.Memory.Ram.Length ||
                !m.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis; ushort offset = (ushort)(physical - basis);
            if (_service is not null && new[] { table[18] + 2, table[19] + 2, table[20] + 2, table[21] + 2 }.Contains(offset))
            { _service.After = Point(); _service = null; }
            int svc = Array.IndexOf(table, offset, 18, 4);
            if (svc >= 18 && Reserve()) { _service = new(new[] { "lock", "disable", "enable", "unlock" }[svc - 18], Point()); Services.Add(_service); }
            if (offset == table[4]) _pendingCut = 1;
            if (offset == table[5]) _pendingCut = 2;
            if ((offset == table[6] || offset == table[7] || offset == table[24]) && Reserve())
            {
                int count = m.Memory.ReadWord(basis + table[17]); uint buffer = m.Memory.ReadDword(basis + table[0] + 32u);
                Check(count <= 32768 && buffer <= m.Memory.Ram.Length - 131072, "Own actual PIO readback outside reserved RAM.");
                Readbacks.Add(new(Proof(10), Point(), m.Memory.Ram.AsSpan((int)buffer, count).ToArray()));
            }
            int boundary = Array.IndexOf(table, offset, 8, 3);
            if (boundary >= 8 && Reserve())
            {
                var row = new Boundary(new[] { "entry", "commit", "iret" }[boundary - 8], Point(), boundary == 9 ? [] : Frame(m));
                Boundaries.Add(row); if (boundary == 10) _iret = row;
            }
            if (offset == table[26] || offset == table[27])
            {
                TimestampAttempts++; _timestampAttempt = Point(); _timestampPending = true;
                if (Reserve()) Boundaries.Add(new(offset == table[26] ? "tsc-before" : "tsc-after", _timestampAttempt, []));
            }
        }
        /// <summary>Confirms actual instruction completion rather than a prefetched marker.</summary>
        public void AfterStep()
        {
            if (_iret is not null) { _iret.After = Point(); _iret = null; }
            if (_pendingCut == 1) PacketCut = true;
            if (_pendingCut == 2) DataCut = true;
            _pendingCut = 0; _timestampPending = false;
        }
    }
    /// <summary>One actual full PC and its removable read-only observation hooks.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>Complete original or genuinely SnapshotStore-restored machine.</summary>
        public PcMachine Machine { get; }
        /// <summary>Actual instruction/port/IRQ/native sample observations.</summary>
        public Ledger Ledger { get; }
        /// <summary>Own actual AH4C completion observed before original DOS continues.</summary>
        public bool Exited { get; private set; }
        private readonly IncrementalHash _audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _frames;
        private readonly string _output;
        /// <summary>Attaches observers only to an already constructed/restored PC.</summary>
        public Side(PcMachine m, byte[] program, ushort[] table, string output, uint basis = 0)
        {
            _output = output; Machine = m; Ledger = new(m, program, table, basis); m.Ports.Accessed += Ledger.Access; m.Pic.InterruptAcknowledged += Ledger.Acknowledge;
            m.Cpu.InstructionStarting += Ledger.Instruction; m.Cpu.InstructionStarting += Exit; m.Cpu.InterruptEntered += Ledger.Interrupt; m.Audio.AudioReady += Audio;
            m.SoundBlaster16!.SampleTransferred += Ledger.Sample; m.SoundBlaster16.TransferStarted += Ledger.Transferred;
        }
        /// <summary>Observes actual own exit, including the AH mask independent from upper EAX.</summary>
        private void Exit(uint physical, byte op)
        {
            if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && op == 0xcd &&
                Machine.Memory.ReadByte(physical + 1) == 0x21 && ((Machine.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) Exited = true;
        }
        /// <summary>Hashes exactly actual published stereo samples.</summary>
        private void Audio(AudioBlock block) { _audio.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); _frames += block.Samples.Length / 2; }
        /// <summary>Closes only the observation digest without touching buffered mixer state.</summary>
        public PcmDigest FinishAudio() { var result = new PcmDigest(_frames, Convert.ToHexString(_audio.GetHashAndReset())); _frames = 0; return result; }
        /// <summary>Executes original CPU and scheduler work, then observes the completed owner boundary.</summary>
        public int Step(int budget) { int n = Machine.RunSteps(budget); Ledger.AfterStep(); Require(!Ledger.LimitReached, _output, this, "ATAPI diagnostic ledger reached its 100000-record bound."); return n; }
        /// <summary>Detaches observers without owning the lifetime of the full machine.</summary>
        public void Dispose()
        {
            Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge;
            Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit; Machine.Cpu.InterruptEntered -= Ledger.Interrupt; Machine.Audio.AudioReady -= Audio;
            Machine.SoundBlaster16!.SampleTransferred -= Ledger.Sample; Machine.SoundBlaster16.TransferStarted -= Ledger.Transferred; _audio.Dispose();
        }
    }
    /// <summary>Retains textual CPU/port/RAM diagnostics on a real bounded failure; no screenshot is created.</summary>
    private static void Require(bool condition, string root, Side side, string why)
    {
        if (condition) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), screen = side.Machine.Video.ReadText(),
            debug = side.Machine.Board.DebugText, fault = side.Machine.Fault?.ToString(), lastPorts = side.Ledger.Ports.TakeLast(32),
            lastWords = side.Ledger.Words.TakeLast(20), lastBoundaries = side.Ledger.Boundaries.TakeLast(12), readbacks = side.Ledger.Readbacks,
            ports = side.Ledger.Ports.Count, words = side.Ledger.Words.Count, acks = side.Ledger.Acks.Count, proof = side.Ledger.ProofBytes(), ledgerLimitReached = side.Ledger.LimitReached,
            generalProtections = side.Ledger.GeneralProtectionCount, timestampAttempts = side.Ledger.TimestampAttempts,
            timestampGeneralProtections = side.Ledger.TimestampGeneralProtections, lastTimestampGeneralProtection = side.Ledger.LastTimestampGeneralProtection });
        File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine)); File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram);
        throw new InvalidDataException(why + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Publishes bounded textual progress from the unchanged current CPU/proof/port state, without a screenshot.</summary>
    private static void Progress(string root, Side side, string stage, long steps, double seconds)
    {
        Write(Path.Combine(root, "progress.json"), new { stage, steps, seconds, point = side.Ledger.Point(), proof = side.Ledger.ProofBytes(),
            ports = side.Ledger.Ports.Count, words = side.Ledger.Words.Count, acks = side.Ledger.Acks.Count,
            readbacks = side.Ledger.Readbacks.Count, services = side.Ledger.Services.Count, boundaries = side.Ledger.Boundaries.Count,
            samples = side.Ledger.Samples.Count, generalProtections = side.Ledger.GeneralProtectionCount,
            timestampAttempts = side.Ledger.TimestampAttempts, timestampGeneralProtections = side.Ledger.TimestampGeneralProtections,
            lastTimestampGeneralProtection = side.Ledger.LastTimestampGeneralProtection, lastBoundaries = side.Ledger.Boundaries.TakeLast(8),
            screen = side.Machine.Video.ReadText(), fault = side.Machine.Fault?.ToString() });
    }
    /// <summary>Compares every raw registered field, all installed RAM and all saved VRAM across the real PCs.</summary>
    private static void Equal(IEnumerable<Side> sides)
    {
        var array = sides.ToArray(); byte[] state = State(array[0].Machine); var ram = array[0].Machine.Memory.Ram;
        foreach (var side in array.Skip(1)) Check(state.SequenceEqual(State(side.Machine)) && ram.SequenceEqual(side.Machine.Memory.Ram), "Whole ATAPI graph/RAM differs.");
    }
    /// <summary>Requires visible original prompt bytes and the completed BDA/CRTC cursor after all four characters.</summary>
    private static bool PromptReady(PcMachine m)
    {
        string[] lines = m.Video.ReadText().Replace("\r", "", StringComparison.Ordinal).TrimEnd().Split('\n'); int row = lines.Length - 1;
        if (row < 0 || lines[row] != "A:\\>" || m.Memory.ReadByte(0x450) != 4 || m.Memory.ReadByte(0x451) != row) return false;
        byte[] crtc = m.Video.SaveState().Fields["_crtc"].GetBytesFromBase64();
        return ((crtc[14] << 8) | crtc[15]) == row * 80 + 4;
    }
    /// <summary>Encodes actual internal keyboard input, preserving the German colon policy and KBC admission.</summary>
    private static byte[] Command(bool german) => german ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rATAPI.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rATAPI.COM\r");
    /// <summary>Runs one original DOS boot, two real full-PC captures, three genuine continuations and a fresh echo command.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(input.FirstCutTimeoutSeconds is >= 10 and <= 900, "First-cut diagnostic timeout outside 10..900 seconds.");
        foreach (string own in new[] { input.Machine, input.Output, input.Probe, input.Iso })
            Check(!own.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal overlay path.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == "dose-430fx-piix-s3-atapi-dev-v1" && original.Cpu.TimestampFrequency == 100_000_000 &&
            original.Memory.Ram.Length == 32 * 1048576 && original.Atapi is not null, "Wrong actual ATAPI profile.");
        long total = 0;
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && total < 700000000)
        { Check(timer.Elapsed.TotalSeconds < 300, "Original ATAPI DOS boot timeout."); int n = original.RunSteps(10000); Check(n > 0, "Original boot stopped."); total += n; }
        Check(original.Fault is null && original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), "Original ATAPI DOS prompt absent.");
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter();
        original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        using var first = new Side(original, program, table, root); long progressAt = total, failureCheckAt = total;
        Progress(root, first, "original-dos-booted", total, timer.Elapsed.TotalSeconds); byte[] keys = Command(input.GermanKeyboard); int sent = 0;
        while (!first.Ledger.PacketCut && original.Fault is null && total < 950000000)
        {
            Require(timer.Elapsed.TotalSeconds < input.FirstCutTimeoutSeconds, root, first, "Actual CDB10/12 cut timeout.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int n = first.Step(first.Ledger.ProgramBase == 0 ? 100 : 1); Check(n > 0, "Actual packet guest stopped."); total += n;
            if (total - failureCheckAt >= 10000)
            {
                Require(!first.Exited && first.Ledger.Proof(138) == 0 && !original.Video.ReadText().Contains("ATAPI-FAILED", StringComparison.Ordinal),
                    root, first, "Own ATAPI guest reported failure or exited before its packet cut.");
                failureCheckAt = total;
            }
            if (total - progressAt >= 200000) { Progress(root, first, "awaiting-packet-cut", total, timer.Elapsed.TotalSeconds); progressAt = total; }
        }
        Require(first.Ledger.PacketCut && sent == keys.Length && original.Fault is null && original.SoundBlaster16!.Active,
            root, first, "Own partial CDB/PCM cut absent.");
        Progress(root, first, "packet-cut-reached", total, timer.Elapsed.TotalSeconds);
        byte[] packetPoint = JsonSerializer.SerializeToUtf8Bytes(first.Ledger.Point(), Json);
        string packetSave = Path.Combine(root, "packet-cut.dossav"); var packetManifest = SnapshotStore.Capture(original, packetSave);
        using var resumedPacket = SnapshotStore.Load(packetSave, Path.Combine(root, "packet-resumed.dosebranch"));
        using var packetSide = new Side(resumedPacket, program, table, root, first.Ledger.ProgramBase);
        var sides = new List<Side> { first, packetSide }; Equal(sides); first.FinishAudio();
        int[] packetCounts = Counts(first.Ledger); long steps = 0; int comparisons = 1, previousWords = first.Ledger.Words.Count;
        while (!first.Ledger.DataCut && original.Fault is null && steps < 20000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1100, root, first, "Actual partial Data-In cut timeout.");
            int n = first.Step(1); Check(packetSide.Step(1) == n && n > 0 && resumedPacket.Fault is null, "Packet future step/fault differs."); steps += n;
            if (first.Ledger.Words.Count != previousWords || steps % 1000 == 0) { Equal(sides); comparisons++; previousWords = first.Ledger.Words.Count; }
        }
        Require(first.Ledger.DataCut && packetSide.Ledger.DataCut && original.SoundBlaster16!.Active, root, first, "Actual partial Data-In/PCM cut absent.");
        Equal(sides); var packetAudioA = first.FinishAudio(); var packetAudioB = packetSide.FinishAudio();
        Check(packetAudioA == packetAudioB, "First true future PCM differs.");
        Progress(root, first, "data-cut-reached", total + steps, timer.Elapsed.TotalSeconds);
        byte[] dataPoint = JsonSerializer.SerializeToUtf8Bytes(first.Ledger.Point(), Json);
        string dataSave = Path.Combine(root, "data-cut.dossav"); var dataManifest = SnapshotStore.Capture(original, dataSave);
        using var resumedData = SnapshotStore.Load(dataSave, Path.Combine(root, "data-resumed.dosebranch"));
        using var dataSide = new Side(resumedData, program, table, root, first.Ledger.ProgramBase); sides.Add(dataSide); Equal(sides);
        int[] dataCounts = Counts(first.Ledger), packetDataCounts = Counts(packetSide.Ledger);
        long generatedAtData = original.Audio.GeneratedFrames; int bufferedAtData = original.Audio.SaveState().Fields["_count"].GetInt32() / 2;
        /// <summary>Requires actual own exit, completed native DMA and a newly completed original prompt.</summary>
        bool Complete(Side side) => side.Exited && side.Machine.SoundBlaster16!.TransferredSamples == 512 && PromptReady(side.Machine);
        while (!sides.All(Complete) && steps < 250000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1800, root, first, "Actual three-PC ATAPI/DOS continuation timeout.");
            int budget = first.Ledger.Proof(10) >= 15 ? 10000 : 1; int n = first.Step(budget);
            foreach (var side in sides.Skip(1)) Check(side.Step(budget) == n && side.Machine.Fault is null, "Data future step/fault differs.");
            Check(n > 0 && original.Fault is null, "Original data future stopped."); steps += n;
            if (total + steps - progressAt >= 200000) { Progress(root, first, "awaiting-original-dos-return", total + steps, timer.Elapsed.TotalSeconds); progressAt = total + steps; }
            if (steps % 10000 < n || sides.All(Complete)) { Equal(sides); comparisons++; }
        }
        Require(sides.All(Complete), root, first, "Actual three original DOS returns missing."); Equal(sides);
        string beforeEcho = original.Video.ReadText(); Check(!beforeEcho.Contains("ATAPICONTINUED", StringComparison.Ordinal), "Fresh echo sentinel already visible.");
        var echoAt = new { nanoseconds = original.Scheduler.Now, ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count };
        byte[] echo = GuestKeyboard.Encode("ECHO ATAPICONTINUED\r"); int echoed = 0;
        /// <summary>Requires a new output line and coherent cursor after the subsequent original prompt.</summary>
        bool EchoDone(Side side) => side.Machine.Video.ReadText().Contains("\nATAPICONTINUED", StringComparison.Ordinal) && PromptReady(side.Machine);
        while (!sides.All(EchoDone) && steps < 270000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1900, root, first, "Actual post-ATAPI echo timeout.");
            if (echoed < echo.Length)
            {
                bool accepted = GuestKeyboard.TryInjectForBios(original, echo[echoed]);
                foreach (var side in sides.Skip(1)) Check(GuestKeyboard.TryInjectForBios(side.Machine, echo[echoed]) == accepted, "Future KBC admission differs.");
                if (accepted) echoed++;
            }
            int n = first.Step(1000); foreach (var side in sides.Skip(1)) Check(side.Step(1000) == n && side.Machine.Fault is null, "Echo future differs.");
            steps += n;
        }
        Require(echoed == echo.Length && sides.All(EchoDone), root, first, "Actual fresh ECHO/complete prompt absent."); Equal(sides);
        foreach (var side in sides) side.Machine.Audio.Flush();
        var audioA = first.FinishAudio(); var audioB = packetSide.FinishAudio(); var audioC = dataSide.FinishAudio();
        long expectedFrames = original.Audio.GeneratedFrames - generatedAtData + bufferedAtData;
        Check(audioA == audioB && audioA == audioC && audioA.Frames == expectedFrames && expectedFrames > 480 &&
            audioA.Sha256.Length == 64 && audioA.Sha256 != Hash([]), "Positive actual future PCM count/digests differ.");
        Equal(sides);
        CompareFuture(first.Ledger, packetCounts, packetSide.Ledger, new int[8], dataCounts, packetDataCounts);
        CompareFuture(first.Ledger, dataCounts, dataSide.Ledger, new int[8], Counts(first.Ledger), Counts(dataSide.Ledger));
        var finalMedia = new List<object>(); string? mediaEncoding = null;
        for (int i = 0; i < sides.Count; i++)
        {
            var media = MediaCheckpointStore.Capture(sides[i].Machine, Path.Combine(root, "final-" + i + ".dosemedia")); finalMedia.Add(media);
            string encoded = JsonSerializer.Serialize(media.Media, StateValues.Json); if (mediaEncoding is not null) Check(encoded == mediaEncoding, "Entire final media generations differ."); mediaEncoding = encoded;
        }
        byte[] finalState = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), finalState);
        File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        File.WriteAllBytes(Path.Combine(root, "packet-point.json"), packetPoint); File.WriteAllBytes(Path.Combine(root, "data-point.json"), dataPoint);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, original = first.Ledger,
            packetFuture = packetSide.Ledger, dataFuture = dataSide.Ledger, packetCounts, dataCounts, packetDataCounts });
        Write(Path.Combine(root, "result.json"), new { passed = true, system = input.System, returnedToDos = true, postAtapiEcho = true,
            exits = sides.Select(side => side.Exited).ToArray(), programBase = first.Ledger.ProgramBase, programSha256 = Hash(program),
            isoSha256 = Hash(File.ReadAllBytes(input.Iso)), packetManifest, dataManifest, comparisons, futureSteps = steps,
            packetStateSha256 = Hash(File.ReadAllBytes(Path.Combine(packetSave, "state.json"))), packetRamSha256 = Hash(File.ReadAllBytes(Path.Combine(packetSave, "ram.bin"))),
            dataStateSha256 = Hash(File.ReadAllBytes(Path.Combine(dataSave, "state.json"))), dataRamSha256 = Hash(File.ReadAllBytes(Path.Combine(dataSave, "ram.bin"))),
            finalStateSha256 = Hash(finalState), finalRamSha256 = Hash(original.Memory.Ram), packetAudio = packetAudioA,
            audioFuture = audioA, restoredAudioFuture = new[] { audioB, audioC }, echoAt, echoBytes = echoed,
            finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Progress(root, first, "completed", total + steps, timer.Elapsed.TotalSeconds);
        Console.WriteLine("Actual ATAPI DOS/three-PC continuation passed; independent oracle remains required."); return 0;
    }
    /// <summary>Retains exact ledger slice boundaries for each real capture.</summary>
    private static int[] Counts(Ledger ledger) => [ledger.Ports.Count, ledger.Words.Count, ledger.Acks.Count, ledger.Readbacks.Count,
        ledger.Services.Count, ledger.Boundaries.Count, ledger.Samples.Count, ledger.Transfers.Count];
    /// <summary>Compares every actual future observation list for a selected real continuation interval.</summary>
    private static void CompareFuture(Ledger a, int[] beginA, Ledger b, int[] beginB, int[] endA, int[] endB)
    {
        object[] left = [a.Ports, a.Words, a.Acks, a.Readbacks, a.Services, a.Boundaries, a.Samples, a.Transfers];
        object[] right = [b.Ports, b.Words, b.Acks, b.Readbacks, b.Services, b.Boundaries, b.Samples, b.Transfers];
        for (int i = 0; i < left.Length; i++)
        {
            var x = ((System.Collections.IEnumerable)left[i]).Cast<object>().Skip(beginA[i]).Take(endA[i] - beginA[i]);
            var y = ((System.Collections.IEnumerable)right[i]).Cast<object>().Skip(beginB[i]).Take(endB[i] - beginB[i]);
            Check(JsonSerializer.Serialize(x, Json) == JsonSerializer.Serialize(y, Json), "Actual future ledger " + i + " differs.");
        }
    }
}
