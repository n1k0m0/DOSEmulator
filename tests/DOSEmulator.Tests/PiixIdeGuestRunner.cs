// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Actual DOS/EMM386 ATA DMA and two full-PC cuts, with no CPU/controller injection.</summary>
internal static class PiixIdeGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0, uint Cr3,
        bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase, bool PrimaryIrq, bool SecondaryIrq, PiixIdeChannelState Dma);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Marker(bool Complete, int Phase, Point At, byte[] Window);
    private sealed record Service(string Kind, Point Before)
    {
        /// <summary>Actual VDS return state before the guest consumes its carry result.</summary>
        public Point? After { get; set; }
    }
    private sealed record Boundary(string Kind, ushort Offset, Point Before, byte[] Frame)
    {
        /// <summary>Exact completed own IRET before the next hardware interrupt recognition.</summary>
        public Point? After { get; set; }
    }
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects missing effects without recovering or rewriting the guest.</summary>
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
    /// <summary>Requires the constructor-fixed actual function-one controller.</summary>
    private static Intel82371FbIde Ide(PcMachine m) => m.PiixIde ?? throw new InvalidDataException("PIIX IDE board absent.");
    /// <summary>Encodes the entire registered raw CPU/device/scheduler graph.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes explicitly selected own data.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes complete observations, including readonly diagnostics.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    /// <summary>Reads the own assembler's unique fixed labels.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("IDEMETA1"u8); Check(at >= 0 && at + 50 <= program.Length, "Own DMA metadata absent.");
        return Enumerable.Range(0, 21).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8 + i * 2, 2))).ToArray();
    }
    /// <summary>Observes real architectural/device values without sampling inside a callback or changing time.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] table)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(), m.Scheduler.ClockTicks(c.TimestampFrequency), c.TimestampCounter,
            c.Eflags, c.Cr0, c.Cr3, c.Virtual8086, c.Eip, Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(), Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(),
            basis == 0 ? 0 : m.Memory.ReadWord(basis + table[0] + 10u), m.Ata.InterruptAsserted, m.SecondaryAta.InterruptAsserted, Ide(m).GetChannelState(0));
    }
    /// <summary>Walks original classic page tables to read the actual small DOS frame without setting A/D bits.</summary>
    private static byte[] Frame(PcMachine m)
    {
        var c = m.Cpu; uint sp = c.GetRegister(Register.Sp) & (c.GetSegmentCache(Segment.Ss).Default32 ? uint.MaxValue : 0xffff);
        uint linear = c.GetSegmentBase(Segment.Ss) + sp; byte[] frame = new byte[6];
        for (uint i = 0; i < 6; i++)
        {
            uint physical = linear + i;
            if (c.PagingEnabled)
            {
                uint pde = m.Memory.ReadDword((c.Cr3 & 0xfffff000) + (physical >> 22) * 4);
                Check((pde & 0x81) == 1, "Original frame PDE unavailable.");
                uint pte = m.Memory.ReadDword((pde & 0xfffff000) + ((physical >> 12) & 1023) * 4);
                Check((pte & 1) != 0, "Original frame PTE unavailable."); physical = (pte & 0xfffff000) | (physical & 4095);
            }
            frame[i] = m.Memory.ReadByte(physical);
        }
        return frame;
    }
    /// <summary>Read-only actual CPU/port/PIC/DMA ledger; never publishes pins or controller writes.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint initialBasis = 0)
    {
        /// <summary>Actual unchanged loaded DOS code base, retained only as observation metadata.</summary>
        public uint ProgramBase { get; private set; } = initialBasis;
        /// <summary>Every actual selected PCI, ATA, BMIDE and PIC port transfer.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Actual physical PIC acknowledgements, distinct from software interrupts.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Whole physical DMA windows at real own ready/completed OUT labels.</summary>
        public List<Marker> Markers { get; } = [];
        /// <summary>Own handler entry, committed record and IRET boundaries.</summary>
        public List<Boundary> Boundaries { get; } = [];
        /// <summary>Actual calls and returns through the original resident VDS interface.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Changed real descriptor, sector, busy and completion states.</summary>
        public List<Point> DmaStates { get; } = [];
        private Boundary? _iret;
        private Service? _service;
        private PiixIdeChannelState _previous = Ide(m).GetChannelState(0);
        /// <summary>Reads one committed own proof word using established physical backing.</summary>
        public ushort Word(int offset) => ProgramBase == 0 ? (ushort)0 : m.Memory.ReadWord(ProgramBase + table[0] + (uint)offset);
        /// <summary>Observes the current owner-boundary state.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Records physical PIC acknowledgements, separately from own software instructions.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) => Acks.Add(new(irq, vector, spurious, Point()));
        /// <summary>Records real ports and complete DMA windows only at the actual own marker OUT labels.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (address is 0x20 or 0x21 or 0xa0 or 0xa1 or 0x3f6 or 0x376 || address is >= 0x1f2 and <= 0x1f7 ||
                address is >= 0x172 and <= 0x177 || address is >= 0xcf8 and <= 0xcff || address is >= 0xc000 and <= 0xc00f)
                Ports.Add(new(address, value, width, write, Point()));
            bool complete = value is >= 0xc1 and <= 0xc6;
            if (address != 0xe9 || !write || width != 1 || ProgramBase == 0 || m.Cpu.GetSegmentBase(Segment.Cs) != ProgramBase ||
                (!complete && value is not (>= 0xb1 and <= 0xb7)) || m.Cpu.Eip != table[complete ? 5 : 4] + 2) return;
            int phase = (int)value - (complete ? 0xc0 : 0xb0); Check(phase == Word(10), "Actual DMA marker/proof differs.");
            uint buffer = m.Memory.ReadDword(ProgramBase + table[0] + 20u); Check(buffer <= m.Memory.Ram.Length - 131072, "DMA window outside RAM.");
            Markers.Add(new(complete, phase, Point(), m.Memory.Ram.AsSpan((int)buffer, 131072).ToArray()));
        }
        /// <summary>Observes unchanged handler and real resident VDS service boundaries.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            uint basis = m.Cpu.GetSegmentBase(Segment.Cs);
            if (basis + 0x110 >= m.Memory.Ram.Length || physical < basis || physical >= basis + table[3] ||
                !m.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis; ushort offset = (ushort)(physical - basis);
            if (_service is not null && (offset == table[19] + 2 || offset == table[20] + 2)) { _service.After = Point(); _service = null; }
            if (offset == table[19] || offset == table[20]) { _service = new(offset == table[19] ? "lock" : "unlock", Point()); Services.Add(_service); }
            int index = Array.IndexOf(table, offset, 6, 3); if (index < 6) return;
            string kind = new[] { "entry", "commit", "iret" }[index - 6]; var row = new Boundary(kind, offset, Point(), kind == "commit" ? [] : Frame(m));
            Boundaries.Add(row); if (kind == "iret") _iret = row;
        }
        /// <summary>Observes real completed IRET and changed DMA state only between CPU steps.</summary>
        public void AfterStep()
        {
            if (_iret is not null) { _iret.After = Point(); _iret = null; }
            var c = Ide(m).GetChannelState(0);
            if (ProgramBase != 0 && (c.BytesTransferred != _previous.BytesTransferred || c.DescriptorAddress != _previous.DescriptorAddress ||
                c.DescriptorOffset != _previous.DescriptorOffset || c.Status != _previous.Status || c.DmaReady != _previous.DmaReady ||
                c.AtaBusy != _previous.AtaBusy || c.WriteCommitPending != _previous.WriteCommitPending)) DmaStates.Add(Point());
            _previous = c;
        }
    }
    /// <summary>One independently executing complete machine and its PCM/CPU observation segment.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>The complete actual original or SnapshotStore-restored PC.</summary>
        public PcMachine Machine { get; }
        /// <summary>Read-only CPU, port, IRQ and DMA observations for this PC.</summary>
        public Ledger Ledger { get; }
        /// <summary>Whether this own COM actually executed DOS/AH4C.</summary>
        public bool Exited { get; private set; }
        private long _pcmFrames;
        private readonly IncrementalHash _pcm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        /// <summary>Attaches observations only; restored data and devices already came from SnapshotStore.</summary>
        public Side(PcMachine m, byte[] program, ushort[] table, uint basis = 0)
        {
            Machine = m; Ledger = new(m, program, table, basis); m.Ports.Accessed += Ledger.Access; m.Pic.InterruptAcknowledged += Ledger.Acknowledge;
            m.Cpu.InstructionStarting += Ledger.Instruction; m.Cpu.InstructionStarting += Exit; m.Audio.AudioReady += Audio;
        }
        /// <summary>Observes an actual own AH4C instruction independently from the required following prompt.</summary>
        private void Exit(uint physical, byte op)
        {
            if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && op == 0xcd &&
                Machine.Memory.ReadByte(physical + 1) == 0x21 && ((Machine.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) Exited = true;
        }
        /// <summary>Hashes exactly real published interleaved stereo samples.</summary>
        private void Audio(AudioBlock block) { _pcm.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); _pcmFrames += block.Samples.Length / 2; }
        /// <summary>Ends an observation segment without changing audio state.</summary>
        public object FinishAudio() { var result = new { sha256 = Convert.ToHexString(_pcm.GetHashAndReset()), frames = _pcmFrames }; _pcmFrames = 0; return result; }
        /// <summary>Runs real CPU/scheduler steps, then observes their actual completion.</summary>
        public int Step(int budget) { int n = Machine.RunSteps(budget); Ledger.AfterStep(); return n; }
        /// <summary>Detaches only observers; caller owns machine lifetime.</summary>
        public void Dispose()
        { Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge; Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit; Machine.Audio.AudioReady -= Audio; _pcm.Dispose(); }
    }
    /// <summary>Retains concrete bounded failure data rather than concealing a missing effect.</summary>
    private static void Require(bool condition, string root, Side side, string why)
    {
        if (condition) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), screen = side.Machine.Video.ReadText(), debug = side.Machine.Board.DebugText,
            fault = side.Machine.Fault?.ToString(), lastPorts = side.Ledger.Ports.TakeLast(24), lastAcks = side.Ledger.Acks.TakeLast(16),
            lastMarkers = side.Ledger.Markers.TakeLast(2), lastFrames = side.Ledger.Boundaries.TakeLast(9), lastDma = side.Ledger.DmaStates.TakeLast(24) });
        File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram); File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine));
        throw new InvalidDataException(why + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Compares the whole actual graph and every installed RAM byte across all active continuations.</summary>
    private static void Equal(IReadOnlyList<Side> sides)
    {
        byte[] state = State(sides[0].Machine); var ram = sides[0].Machine.Memory.Ram;
        foreach (var s in sides.Skip(1)) Check(state.SequenceEqual(State(s.Machine)) && ram.SequenceEqual(s.Machine.Memory.Ram), "Entire IDE graph/RAM differs.");
    }
    /// <summary>Encodes internal guest input and preserves actual KBC admission.</summary>
    private static byte[] Command(bool german) => german ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rPIIXIDE.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rPIIXIDE.COM\r");
    /// <summary>Runs original DOS, actual read/write SnapshotStore cuts and both complete restores to DOS prompts.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(!input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == "dose-430fx-piix-ide-dev-v1" && original.Cpu.TimestampFrequency == 100_000_000 && original.Memory.Ram.Length == 32 * 1048576, "Wrong IDE board/model."); Ide(original);
        using var first = new Side(original, program, table); long total = 0;
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && total < 700000000)
        { Require(timer.Elapsed.TotalSeconds < 300, root, first, "Original IDE DOS boot timeout."); int n = first.Step(10000); Check(n > 0, "DOS boot stopped."); total += n; }
        Require(original.Fault is null && original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), root, first, "Original DOS prompt absent.");
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter(); original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        byte[] keys = Command(input.GermanKeyboard); int sent = 0;
        /// <summary>Requires the real own polling client and private CALL stack, never an original monitor wait.</summary>
        bool OwnWait()
        {
            uint waiting = table[9], basis = first.Ledger.ProgramBase; var c = original.Cpu;
            return basis != 0 && c.GetSegmentBase(Segment.Cs) == basis && c.GetSegmentBase(Segment.Ss) == basis &&
                c.GetSegment(Segment.Cs) == basis >> 4 && c.GetSegment(Segment.Ss) == basis >> 4 &&
                (c.GetRegister(Register.Sp) & 65535) == table[11] - 2 && !c.Halted &&
                c.Virtual8086 == (first.Ledger.Word(8) != 0) && new[] { waiting - 2, waiting - 1, waiting, waiting + 1, waiting + 4, waiting + 8 }.Contains(c.Eip);
        }
        /// <summary>Selects the first real 32-byte READ DMA grant with both descriptor and sector remainder.</summary>
        bool ReadCut() { var c = Ide(original).GetChannelState(0); return first.Ledger.Word(10) == 1 && c.Active && c.BytesTransferred == 32 && c.SectorOffset == 32 && c.DescriptorOffset == 32 && c.DescriptorRemaining == 478 && OwnWait(); }
        while (!ReadCut() && original.Fault is null && total < 900000000)
        {
            Require(timer.Elapsed.TotalSeconds < 600, root, first, "Actual partial READ DMA cut timeout.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int n = first.Step(first.Ledger.ProgramBase == 0 ? 100 : 1); Check(n > 0, "READ DMA guest stopped."); total += n;
        }
        Require(ReadCut() && sent == keys.Length && original.Fault is null && original.SoundBlaster16!.Active, root, first, "Partial READ DMA/PCM cut absent.");
        string readSave = Path.Combine(root, "read-cut.dossav"); var readManifest = SnapshotStore.Capture(original, readSave);
        using var restoredRead = SnapshotStore.Load(readSave, Path.Combine(root, "read-resumed.dosebranch"));
        using var readSide = new Side(restoredRead, program, table, first.Ledger.ProgramBase); var sides = new List<Side> { first, readSide }; Equal(sides);
        int readPorts = first.Ledger.Ports.Count, readAcks = first.Ledger.Acks.Count, readMarkers = first.Ledger.Markers.Count, readFrames = first.Ledger.Boundaries.Count, readDma = first.Ledger.DmaStates.Count, readServices = first.Ledger.Services.Count;
        first.FinishAudio(); int comparisons = 0; long futureSteps = 0, previousBytes = -1;
        /// <summary>Selects the real first full write-sector commit while the second PRD is partial.</summary>
        bool WriteCut() { var c = Ide(original).GetChannelState(0); return first.Ledger.Word(10) == 2 && c.WriteCommitPending && c.BytesTransferred == 512 && c.DescriptorOffset == 2 && c.DescriptorRemaining == 512 && OwnWait(); }
        while (!WriteCut() && original.Fault is null && futureSteps < 100000000)
        {
            Require(timer.Elapsed.TotalSeconds < 900, root, first, "Actual WRITE DMA pending-commit cut timeout.");
            int a = first.Step(1), b = readSide.Step(1); Check(a == b && a > 0 && restoredRead.Fault is null && original.Fault is null, "First IDE future count/fault differs."); futureSteps += a;
            var c = Ide(original).GetChannelState(0); if (c.BytesTransferred != previousBytes || futureSteps % 1000 == 0) { Equal(sides); comparisons++; previousBytes = c.BytesTransferred; }
        }
        Require(WriteCut() && original.SoundBlaster16!.Active && original.Fault is null, root, first, "Partial PRD/write-commit cut absent."); Equal(sides);
        string writeSave = Path.Combine(root, "write-cut.dossav"); var writeManifest = SnapshotStore.Capture(original, writeSave);
        using var restoredWrite = SnapshotStore.Load(writeSave, Path.Combine(root, "write-resumed.dosebranch"));
        using var writeSide = new Side(restoredWrite, program, table, first.Ledger.ProgramBase); sides.Add(writeSide); Equal(sides);
        object audioReadA = first.FinishAudio(), audioReadB = readSide.FinishAudio(); Check(JsonSerializer.Serialize(audioReadA, Json) == JsonSerializer.Serialize(audioReadB, Json), "First future PCM differs.");
        int writePorts = first.Ledger.Ports.Count, writeAcks = first.Ledger.Acks.Count, writeMarkers = first.Ledger.Markers.Count, writeFrames = first.Ledger.Boundaries.Count, writeDma = first.Ledger.DmaStates.Count, writeServices = first.Ledger.Services.Count;
        int readWritePorts = readSide.Ledger.Ports.Count, readWriteAcks = readSide.Ledger.Acks.Count, readWriteMarkers = readSide.Ledger.Markers.Count, readWriteFrames = readSide.Ledger.Boundaries.Count, readWriteDma = readSide.Ledger.DmaStates.Count;
        /// <summary>Requires both actual own DOS exit and a subsequently executed DOS command prompt.</summary>
        bool Complete(Side s) => s.Exited && s.Machine.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal);
        while (!sides.All(Complete) && futureSteps < 250000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1500, root, first, "Actual three-PC DMA/DOS continuation timeout.");
            int budget = first.Ledger.Word(10) >= 7 ? 10000 : 1; int n = first.Step(budget);
            foreach (var s in sides.Skip(1)) Check(s.Step(budget) == n && s.Machine.Fault is null, "Restored IDE future count/fault differs.");
            Check(n > 0 && original.Fault is null, "Original IDE future stopped/faulted."); futureSteps += n;
            var c = Ide(original).GetChannelState(0); if (c.BytesTransferred != previousBytes || futureSteps % 1000 == 0 || first.Ledger.Word(10) >= 7) { Equal(sides); comparisons++; previousBytes = c.BytesTransferred; }
        }
        Require(sides.All(Complete) && first.Ledger.Word(12) == 6 && first.Ledger.Word(64) == 1 && first.Ledger.Word(66) == 0 &&
            original.Board.DebugText.Contains("PIIX-IDE-DONE", StringComparison.Ordinal) && !original.Board.DebugText.Contains("PIIX-IDE-FAILED", StringComparison.Ordinal), root, first, "Six actual DMA IRQ/EOI/IRET paths and DOS return missing.");
        foreach (var s in sides) s.Machine.Audio.Flush(); Equal(sides);
        object[] audio = sides.Select(s => s.FinishAudio()).ToArray(); Check(audio.Select(a => JsonSerializer.Serialize(a, Json)).Distinct().Count() == 1, "Final future PCM differs.");
        Check(original.SoundBlaster16!.TransferredSamples == 512, "PCM DMA block incomplete.");
        var finalMedia = sides.Select((s, i) => MediaCheckpointStore.Capture(s.Machine, Path.Combine(root, "final-" + i + ".dosemedia"))).ToArray();
        Check(finalMedia.Select(m => JsonSerializer.Serialize(m.Media, StateValues.Json)).Distinct().Count() == 1, "Final media generations differ.");
        File.WriteAllBytes(Path.Combine(root, "final-state.json"), State(original)); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, ports = first.Ledger.Ports, acks = first.Ledger.Acks, markers = first.Ledger.Markers, boundaries = first.Ledger.Boundaries, services = first.Ledger.Services, dmaStates = first.Ledger.DmaStates,
            readCut = new { ports = readPorts, acks = readAcks, markers = readMarkers, frames = readFrames, dma = readDma, services = readServices }, writeCut = new { ports = writePorts, acks = writeAcks, markers = writeMarkers, frames = writeFrames, dma = writeDma, services = writeServices },
            readFuture = new { ports = readSide.Ledger.Ports, acks = readSide.Ledger.Acks, markers = readSide.Ledger.Markers, boundaries = readSide.Ledger.Boundaries, dmaStates = readSide.Ledger.DmaStates, services = readSide.Ledger.Services },
            readWriteCut = new { ports = readWritePorts, acks = readWriteAcks, markers = readWriteMarkers, frames = readWriteFrames, dma = readWriteDma },
            writeFuture = new { ports = writeSide.Ledger.Ports, acks = writeSide.Ledger.Acks, markers = writeSide.Ledger.Markers, boundaries = writeSide.Ledger.Boundaries, dmaStates = writeSide.Ledger.DmaStates, services = writeSide.Ledger.Services } });
        Write(Path.Combine(root, "result.json"), new { passed = true, returnedToDos = true, exits = sides.Select(s => s.Exited), system = input.System, readManifest, writeManifest, comparisons, futureSteps,
            readStateSha256 = Hash(File.ReadAllBytes(Path.Combine(readSave, "state.json"))), readRamSha256 = Hash(File.ReadAllBytes(Path.Combine(readSave, "ram.bin"))),
            writeStateSha256 = Hash(File.ReadAllBytes(Path.Combine(writeSave, "state.json"))), writeRamSha256 = Hash(File.ReadAllBytes(Path.Combine(writeSave, "ram.bin"))),
            finalStateSha256 = Hash(State(original)), finalRamSha256 = Hash(original.Memory.Ram), audioRead = audioReadA, audioFuture = audio[0], dma = original.SoundBlaster16!.TransferredSamples,
            programBase = first.Ledger.ProgramBase, programSha256 = Hash(program), finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Console.WriteLine("PASS actual PIIX IDE DMA/DOS two-cut three-PC continuation " + root); return 0;
    }
}
