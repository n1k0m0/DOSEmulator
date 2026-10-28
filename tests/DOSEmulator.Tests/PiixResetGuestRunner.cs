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

/// <summary>Actual CF9 INIT/reset requests and three build-bound full-PC continuations through original DOS.</summary>
internal static class PiixResetGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System);
    private sealed record ResetView(PiixResetRequest Pending, PiixResetPhase Phase, bool CpuHeld, bool PciIsaHeld, bool Ready,
        ulong AssertedAt, ulong PciReleaseAt, ulong CpuReleaseAt, byte Readback, byte Stored, long SoftEdges, long HardEdges);
    private sealed record PcmView(int Remaining, bool Active, long Transferred, long Dma8, long Dma16, short Left, short Right, long Blocks);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags, uint Cr0,
        uint Cr3, uint Cr4, bool V86, bool Halted, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase,
        long Resets, string? ResetSource, ulong ResetTime, ushort WarmFlag, PiixIdeChannelState Dma, ResetView Reset, PcmView Pcm);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Boundary(string Kind, ushort Offset, Point At, JsonElement Cpu, byte[] Proof, byte[] Window);
    private sealed record Service(string Kind, ushort Offset, Point Before)
    {
        /// <summary>Actual original resident service return before the guest reads carry or result registers.</summary>
        public Point? After { get; set; }
    }
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects a missing actual effect without correcting the running guest.</summary>
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
    /// <summary>Requires the constructor-bound physical reset owner.</summary>
    private static PiixResetController Reset(PcMachine m) => m.PiixReset ?? throw new InvalidDataException("CF9 reset owner absent.");
    /// <summary>Requires the constructor-bound function-one controller.</summary>
    private static Intel82371FbIde Ide(PcMachine m) => m.PiixIde ?? throw new InvalidDataException("PIIX IDE absent.");
    /// <summary>Encodes every registered CPU/device/scheduler field and invariant.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only explicitly selected own evidence bytes.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Publishes textual observation data, never a screen or audio recording.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    /// <summary>Reads the exact own assembler's versioned labels and complete extents.</summary>
    private static ushort[] Metadata(byte[] p)
    {
        int at = p.AsSpan().IndexOf("RESETM01"u8);
        Check(at >= 0 && at + 76 <= p.Length && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 8)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 10)) == 32, "Own reset metadata absent.");
        return Enumerable.Range(0, 32).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 12 + i * 2, 2))).ToArray();
    }
    /// <summary>Finds one exact immutable INT-site signature whose label need not change the guest binary.</summary>
    private static ushort Site(byte[] program, byte[] signature)
    {
        int at = program.AsSpan().IndexOf(signature);
        Check(at >= 0 && program.AsSpan().LastIndexOf(signature) == at, "Own original-service signature is not unique.");
        return checked((ushort)(at + signature.Length - 2 + 0x100));
    }
    /// <summary>Copies the public reset diagnostics without sampling the scheduler from callbacks.</summary>
    private static ResetView View(PcMachine m)
    {
        var r = Reset(m);
        return new(r.PendingRequest, r.Phase, r.CpuResetHeld, r.PciIsaResetHeld, r.CpuReleaseReady,
            r.AssertedAt, r.PciIsaReleaseAt, r.CpuReleaseAt, r.ControlReadback, r.ControlStored, r.SoftEdges, r.HardEdges);
    }
    /// <summary>Observes actual DSP sample commits and physical DMA grants, independently from continuing mixer/filter output.</summary>
    private static PcmView Pcm(PcMachine m)
    {
        var s = m.SoundBlaster16 ?? throw new InvalidDataException("Own PCM device absent.");
        return new(s.RemainingSamples, s.Active, s.TransferredSamples, s.Dma8Grants, s.Dma16Grants, s.DacLeft, s.DacRight, s.CompletedBlocks);
    }
    /// <summary>Observes actual architectural state and integer scheduler phase without advancing either.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] t)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(),
            m.Scheduler.ClockTicks(c.TimestampFrequency), c.TimestampCounter, c.Eflags, c.Cr0, c.Cr3, c.Cr4, c.Virtual8086, c.Halted,
            c.Eip, Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(), basis == 0 ? 0 : (int)m.Memory.ReadDword(basis + t[0] + 20u),
            m.HardwareResetCount, m.LastResetSource, m.LastResetTime, m.LastWarmBootFlag, Ide(m).GetChannelState(0), View(m), Pcm(m));
    }
    /// <summary>Read-only real instructions, CF9/PCI/ATA/ISA transfers, reset vectors and VDS/VCPI boundaries.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint initialBasis = 0)
    {
        /// <summary>Actual unchanged conventional DOS program base, retained only for observation after a full import.</summary>
        public uint ProgramBase { get; private set; } = initialBasis;
        /// <summary>Actual selected PCI, reset, DMA, RTC, keyboard, ATA and board port transfers.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Actual physical PIC acknowledgements, including post-reset keyboard IRQ1.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>Observes one real PIC acknowledgement without entering a CPU interrupt manually.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) => Acks.Add(new(irq, vector, spurious, Point()));
        /// <summary>Actual reset-vector, warm-stub and committed own boundary states including raw x87.</summary>
        public List<Boundary> Boundaries { get; } = [];
        /// <summary>Actual original monitor service entries and returns.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Actual changed DMA cursor and reset-domain states at owner boundaries.</summary>
        public List<Point> Changes { get; } = [];
        /// <summary>Actual DSP starts, observed without sampling time from the scheduled callbacks.</summary>
        public List<SoundBlaster16.Transfer> PcmTransfers { get; } = [];
        /// <summary>Actual accepted DAC sample publications with their native event timestamps and raw DMA values.</summary>
        public List<SoundBlaster16.Sample> PcmSamples { get; } = [];
        /// <summary>Retains the exact already-timestamped DSP start without host timing or controller effects.</summary>
        public void Transfer(SoundBlaster16.Transfer value) => PcmTransfers.Add(value);
        /// <summary>Retains the actual already-timestamped sample commit without calling any clock API in a callback.</summary>
        public void Sample(SoundBlaster16.Sample value) => PcmSamples.Add(value);
        private PiixIdeChannelState _dma = Ide(m).GetChannelState(0);
        private ResetView _reset = View(m);
        private PcmView _pcm = Pcm(m);
        private Service? _service;
        private readonly Dictionary<ushort, string> _sites = new()
        {
            [table[14]] = "vds-lock", [table[15]] = "vds-unlock", [table[31]] = "vds-disable",
            [table[12]] = "vcpi-enter", [table[13]] = "vcpi-return",
            [Site(program, [0xb8, 0x0c, 0x81, 0xcd, 0x4b])] = "vds-enable",
            [Site(program, [0xb8, 0x00, 0xde, 0xcd, 0x67])] = "vcpi-presence",
            [Site(program, [0xb8, 0x01, 0xde, 0xcd, 0x67])] = "vcpi-interface"
        };
        /// <summary>Reads one committed own proof dword without guest memory injection.</summary>
        public uint Dword(int offset) => ProgramBase == 0 ? 0 : m.Memory.ReadDword(ProgramBase + table[0] + (uint)offset);
        /// <summary>Returns the real current owner-boundary architecture and device diagnostics.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Copies the whole actual physical private window, including DMA data and PRDs.</summary>
        private byte[] Window()
        {
            uint buffer = Dword(16);
            return buffer >= 16 && buffer <= m.Memory.Ram.Length - 131088
                ? m.Memory.Ram.AsSpan((int)buffer - 16, 131104).ToArray() : [];
        }
        /// <summary>Records one raw CPU image before any observed instruction effects are executed.</summary>
        private void Mark(string kind, ushort offset)
        {
            Boundaries.Add(new(kind, offset, Point(), JsonSerializer.SerializeToElement(m.Cpu.SaveState(), StateValues.Json),
                ProgramBase == 0 ? [] : m.Memory.Ram.AsSpan((int)(ProgramBase + table[0]), table[1] - table[0]).ToArray(), Window()));
        }
        /// <summary>Observes actual port effects only; no pin, interrupt or controller is injected.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (address is >= 0xcf8 and <= 0xcff || address is >= 0xc000 and <= 0xc00f || address is >= 0x1f0 and <= 0x1f7 ||
                address is >= 0x170 and <= 0x177 || address is 0x376 or 0x3f6 or 0x20 or 0x21 or 0xa0 or 0xa1 or 0x40 or 0x43 or
                0x60 or 0x64 or 0x70 or 0x71 or 0x80 or 0xe9 || address is >= 0x220 and <= 0x22f || address < 16 || address is 0x83)
                Ports.Add(new(address, value, width, write, Point()));
        }
        /// <summary>Recognizes the real reset vector and unchanged own instructions, never original-monitor PCs.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            if (physical == 0xfffffff0 && m.HardwareResetCount != 0) { Mark("reset-vector", 0xfff0); return; }
            uint basis = m.Cpu.GetSegmentBase(Segment.Cs);
            if (basis > m.Memory.Ram.Length - table[3] || physical < basis || physical >= basis + table[3] ||
                !m.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            bool initialEntry = ProgramBase == 0;
            ProgramBase = basis; ushort offset = (ushort)(physical - basis);
            if (initialEntry && offset == 0x100) Mark("dos-entry", offset);
            if (_service is not null &&
                ((_service.Kind is not ("vcpi-enter" or "vcpi-return") && offset == _service.Offset + 2) ||
                 (_service.Kind == "vcpi-enter" && !m.Cpu.Virtual8086 && (m.Cpu.Cr0 & 1) != 0 && m.Cpu.GetSegment(Segment.Cs) == 8) ||
                 (_service.Kind == "vcpi-return" && offset == table[6])))
            { _service.After = Point(); _service = null; }
            if (_sites.TryGetValue(offset, out string? service)) { _service = new(service, offset, Point()); Services.Add(_service); }
            int index = Array.IndexOf(table, offset, 4, 8);
            if (index >= 4) Mark(new[] { "warm-stub", "pm-resume", "real-return", "soft-out", "same-high-out", "hard-out", "soft-done", "hard-done" }[index - 4], offset);
            if (offset == table[28] || offset == table[29] || offset == table[30])
                Mark(offset == table[28] ? "tsc-before" : offset == table[29] ? "tsc-soft" : "tsc-hard", offset);
            if (opcode == 0x0f && m.Memory.ReadByte(physical + 1) == 0x30) Mark("wrmsr", offset);
        }
        /// <summary>Records changed physical cursor/reset state after the completed CPU step and event publications.</summary>
        public void AfterStep()
        {
            var dma = Ide(m).GetChannelState(0); var reset = View(m); var pcm = Pcm(m);
            if (ProgramBase != 0 && (dma != _dma || reset != _reset || pcm != _pcm)) Changes.Add(Point());
            _dma = dma; _reset = reset; _pcm = pcm;
        }
    }
    /// <summary>One actual original or SnapshotStore-restored machine and its observation segment.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>The complete actual PC, with its constructor-bound CPU, reset owner and four drives.</summary>
        public PcMachine Machine { get; }
        /// <summary>Read-only observations attached after construction or full import.</summary>
        public Ledger Ledger { get; }
        /// <summary>Whether the own program executed DOS/AH4C.</summary>
        public bool Exited { get; private set; }
        private long _frames;
        private readonly IncrementalHash _pcm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        /// <summary>Attaches only observers; no device or CPU is replaced or restored manually.</summary>
        public Side(PcMachine m, byte[] p, ushort[] t, uint basis = 0)
        {
            Machine = m; Ledger = new(m, p, t, basis);
            m.Ports.Accessed += Ledger.Access; m.Pic.InterruptAcknowledged += Ledger.Acknowledge; m.Cpu.InstructionStarting += Ledger.Instruction; m.Cpu.InstructionStarting += Exit; m.Audio.AudioReady += Audio;
            m.SoundBlaster16!.TransferStarted += Ledger.Transfer; m.SoundBlaster16.SampleTransferred += Ledger.Sample;
        }
        /// <summary>Observes the actual own DOS exit separately from the later required prompt.</summary>
        private void Exit(uint physical, byte opcode)
        {
            if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && opcode == 0xcd &&
                Machine.Memory.ReadByte(physical + 1) == 0x21 && ((Machine.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) Exited = true;
        }
        /// <summary>Hashes exactly real published PCM without creating a recording.</summary>
        private void Audio(AudioBlock b) { _pcm.AppendData(MemoryMarshal.AsBytes(b.Samples.AsSpan())); _frames += b.Samples.Length / 2; }
        /// <summary>Finishes a deterministic future-publication segment without changing device state.</summary>
        public object FinishAudio() { var value = new { sha256 = Convert.ToHexString(_pcm.GetHashAndReset()), frames = _frames }; _frames = 0; return value; }
        /// <summary>Runs the real owner path up to an optional deadline, then observes its completion.</summary>
        public int Step(int count, ulong? until = null) { int n = Machine.RunSteps(count, until); Ledger.AfterStep(); return n; }
        /// <summary>Detaches only observers; the caller owns the PC lifetime.</summary>
        public void Dispose()
        { Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge; Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit; Machine.Audio.AudioReady -= Audio; Machine.SoundBlaster16!.TransferStarted -= Ledger.Transfer; Machine.SoundBlaster16.SampleTransferred -= Ledger.Sample; _pcm.Dispose(); }
    }
    /// <summary>Retains textual architecture, RAM and recent ports for a failed bound; never records an image.</summary>
    private static void Require(bool condition, string root, Side side, string why)
    {
        if (condition) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), screen = side.Machine.Video.ReadText(),
            debug = side.Machine.Board.DebugText, fault = side.Machine.Fault, lastPorts = side.Ledger.Ports.TakeLast(32),
            lastBoundaries = side.Ledger.Boundaries.TakeLast(8), lastChanges = side.Ledger.Changes.TakeLast(32) });
        File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram); File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine));
        throw new InvalidDataException(why + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Compares every registered CPU/device/scheduler value and all installed RAM bytes.</summary>
    private static void Equal(IReadOnlyList<Side> sides)
    {
        byte[] state = State(sides[0].Machine); byte[] ram = sides[0].Machine.Memory.Ram;
        foreach (var side in sides.Skip(1))
            Check(state.SequenceEqual(State(side.Machine)) && ram.SequenceEqual(side.Machine.Memory.Ram), "Entire reset graph/RAM differs.");
    }
    /// <summary>Encodes guest input for the original DOS prompt, with explicit KBC admission on every byte.</summary>
    private static byte[] StartCommand(bool german) => german ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rPIIXRST.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rPIIXRST.COM\r");
    /// <summary>Requires all four actual prompt characters and completed matching BIOS/CRTC cursor updates.</summary>
    private static bool PromptReady(PcMachine machine)
    {
        string[] lines = machine.Video.ReadText().Replace("\r", "", StringComparison.Ordinal).TrimEnd().Split('\n');
        int row = lines.Length - 1;
        if (row is < 0 or >= 25 || lines[row] != "A:\\>") return false;
        byte[] crtc = machine.Video.SaveState().Fields["_crtc"].GetBytesFromBase64();
        int start = crtc[12] << 8 | crtc[13], stride = crtc[0x13] * 2, cursor = crtc[14] << 8 | crtc[15];
        return stride == 80 && machine.Memory.ReadByte(0x449) == 3 && machine.Memory.ReadWord(0x450) == (row << 8 | 4) &&
            cursor == ((start + row * stride + 4) & 65535);
    }
    /// <summary>Runs three actual captures, four actual continuations and the original DOS/EMM386 command loop.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(!input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Evidence output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == MachineProfile.PiixResetProfileId && original.Cpu.TimestampFrequency == 100_000_000 &&
            original.Memory.Ram.Length == 32 * 1048576, "Wrong actual reset board/model/clock.");
        Reset(original); Ide(original);
        using var first = new Side(original, program, table);
        var sides = new List<Side> { first }; var machines = new List<PcMachine>(); var observers = new List<Side>();
        var captures = new List<object>(); var futures = new List<object>(); var cuts = new Dictionary<string, object>();
        long steps = 0; int comparisons = 0;
        try
        {
            while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && steps < 700000000)
            { Require(timer.Elapsed.TotalSeconds < 300, root, first, "Original reset DOS boot timeout."); int n = first.Step(10000); Check(n > 0, "DOS boot stopped."); steps += n; }
            Require(original.Fault is null && original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), root, first, "Original DOS prompt absent.");
            original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter(); original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
            long initialPost = original.Board.Post10Count; byte[] keys = StartCommand(input.GermanKeyboard); int sent = 0;
            while (Reset(original).PendingRequest != PiixResetRequest.Init && original.Fault is null && steps < 900000000)
            {
                Require(timer.Elapsed.TotalSeconds < 600, root, first, "Actual pending INIT cut timeout.");
                if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
                int n = first.Step(first.Ledger.ProgramBase == 0 ? 100 : 1); Check(n > 0, "INIT guest stopped."); steps += n;
            }
            Require(Reset(original).PendingRequest == PiixResetRequest.Init && sent == keys.Length && original.SoundBlaster16!.Active &&
                Ide(original).GetChannelState(0).BytesTransferred == 32 && first.Ledger.Dword(20) == 1, root, first, "Actual partial READ/PCM/pending INIT cut absent.");
            /// <summary>Captures a full actual container and starts a new actual constructor-bound branch.</summary>
            void Capture(string name)
            {
                Equal(sides); comparisons++;
                string save = Path.Combine(root, name + "-cut.dossav"); var manifest = SnapshotStore.Capture(original, save);
                Write(Path.Combine(root, name + "-cut-point.json"), first.Ledger.Point());
                cuts[name] = new { ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count, boundaries = first.Ledger.Boundaries.Count, services = first.Ledger.Services.Count, changes = first.Ledger.Changes.Count, pcmTransfers = first.Ledger.PcmTransfers.Count, pcmSamples = first.Ledger.PcmSamples.Count };
                object[] audio = sides.Select(s => s.FinishAudio()).ToArray();
                if (audio.Length > 1) Check(audio.Select(x => JsonSerializer.Serialize(x, Json)).Distinct().Count() == 1, "Reset future PCM segment differs.");
                if (audio.Length > 1) futures.Add(new { after = captures.Count == 1 ? "soft" : "hard", audio = audio[0] });
                var m = SnapshotStore.Load(save, Path.Combine(root, name + "-resumed.dosebranch")); machines.Add(m);
                var side = new Side(m, program, table, first.Ledger.ProgramBase); observers.Add(side); sides.Add(side); Equal(sides); comparisons++;
                captures.Add(new { name, manifest, stateSha256 = Hash(File.ReadAllBytes(Path.Combine(save, "state.json"))),
                    ramSha256 = Hash(File.ReadAllBytes(Path.Combine(save, "ram.bin"))) });
            }
            Capture("soft");
            while (Reset(original).PendingRequest != PiixResetRequest.Hard && original.Fault is null && steps < 950000000)
            {
                Require(timer.Elapsed.TotalSeconds < 900, root, first, "Actual pending hard-reset commit cut timeout.");
                int n = first.Step(1); foreach (var side in sides.Skip(1)) Check(side.Step(1) == n && side.Machine.Fault is null, "INIT restored count/fault differs.");
                Check(n > 0 && original.Fault is null, "INIT future stopped."); steps += n;
                if (steps % 1000 == 0 || first.Ledger.Changes.Count > 0 && first.Ledger.Changes[^1].Nanoseconds == original.Scheduler.Now) { Equal(sides); comparisons++; }
            }
            var dma = Ide(original).GetChannelState(0);
            Require(Reset(original).PendingRequest == PiixResetRequest.Hard && dma.WriteCommitPending && dma.BytesTransferred == 512 &&
                dma.DescriptorOffset == 512 && dma.DescriptorRemaining == 512 && original.SoundBlaster16!.Active && first.Ledger.Dword(20) == 2,
                root, first, "Actual open write commit/PCM/pending hard cut absent.");
            Capture("hard");
            ulong heldAt = checked(original.Scheduler.Now + 500000);
            int count = first.Step(1, heldAt);
            foreach (var side in sides.Skip(1)) Check(side.Step(1, heldAt) == count && side.Machine.Fault is null, "Hard hold owner count/fault differs.");
            Check(count == 1 && Reset(original).Phase == PiixResetPhase.AllAsserted && original.Scheduler.Now == heldAt &&
                Reset(original).PciIsaResetHeld && Reset(original).CpuResetHeld, "Actual half-millisecond physical reset hold absent.");
            Capture("held");
            /// <summary>Requires the actual own DOS exit and a subsequently executed original command prompt.</summary>
            bool Complete(Side side) => side.Exited && PromptReady(side.Machine);
            long future = 0;
            while (!sides.All(Complete) && future < 200000000)
            {
                Require(timer.Elapsed.TotalSeconds < 1400, root, first, "Four actual reset/DOS continuations timed out.");
                int budget = first.Ledger.Dword(20) >= 3 ? 10000 : 1; int n = first.Step(budget);
                foreach (var side in sides.Skip(1)) Check(side.Step(budget) == n && side.Machine.Fault is null, "Hard restored future count/fault differs.");
                Check(n > 0 && original.Fault is null, "Hard reset future stopped."); future += n;
                if (future % 1000 == 0 || first.Ledger.Dword(20) >= 3 || first.Ledger.Changes.Count > 0 && first.Ledger.Changes[^1].Nanoseconds == original.Scheduler.Now)
                { Equal(sides); comparisons++; }
            }
            Require(sides.All(Complete) && first.Ledger.Dword(24) == 2 && first.Ledger.Dword(28) == 1 && first.Ledger.Dword(32) == 0 &&
                original.HardwareResetCount == 2 && Reset(original).SoftEdges == 1 && Reset(original).HardEdges == 1 &&
                original.Board.DebugText.Contains("PIIX-RESET-DONE", StringComparison.Ordinal) && !original.Board.DebugText.Contains("PIIX-RESET-FAILED", StringComparison.Ordinal),
                root, first, "Actual INIT/hard warm rendezvous and original DOS return absent.");
            string beforeEcho = original.Video.ReadText();
            Require(!beforeEcho.Contains("RESETCONTINUED", StringComparison.Ordinal), root, first, "Post-reset echo sentinel must be absent before its command.");
            var echoAt = new { nanoseconds = original.Scheduler.Now, ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count };
            byte[] echo = GuestKeyboard.Encode("ECHO RESETCONTINUED\r"); int echoSent = 0; long echoSteps = 0;
            while ((!sides.All(s => s.Machine.Video.ReadText().Contains("\nRESETCONTINUED", StringComparison.Ordinal) && PromptReady(s.Machine)) ||
                    echoSent != echo.Length) && echoSteps < 5000000)
            {
                Require(timer.Elapsed.TotalSeconds < 1500, root, first, "Original post-reset DOS/KBC command timeout.");
                if (echoSent < echo.Length)
                {
                    bool admitted = GuestKeyboard.TryInjectForBios(original, echo[echoSent]);
                    foreach (var side in sides.Skip(1)) Check(GuestKeyboard.TryInjectForBios(side.Machine, echo[echoSent]) == admitted, "Restored KBC admission differs.");
                    if (admitted) echoSent++;
                }
                int n = first.Step(1000); foreach (var side in sides.Skip(1)) Check(side.Step(1000) == n && side.Machine.Fault is null, "Post-reset command future differs.");
                Check(n > 0 && original.Fault is null, "Post-reset DOS stopped."); echoSteps += n; Equal(sides); comparisons++;
            }
            Require(echoSent == echo.Length && sides.All(s => s.Machine.Video.ReadText().Contains("\nRESETCONTINUED", StringComparison.Ordinal) &&
                PromptReady(s.Machine)), root, first, "Actual post-reset original DOS echo/prompt absent.");
            foreach (var side in sides) side.Machine.Audio.Flush(); Equal(sides); comparisons++;
            object[] lastAudio = sides.Select(s => s.FinishAudio()).ToArray();
            Check(lastAudio.Select(x => JsonSerializer.Serialize(x, Json)).Distinct().Count() == 1, "Final physical reset future PCM differs.");
            var media = sides.Select((s, i) => MediaCheckpointStore.Capture(s.Machine, Path.Combine(root, "final-" + i + ".dosemedia"))).ToArray();
            Check(media.Select(x => JsonSerializer.Serialize(x.Media, StateValues.Json)).Distinct().Count() == 1, "Four actual final media generations differ.");
            File.WriteAllBytes(Path.Combine(root, "final-state.json"), State(original)); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
            Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, ports = first.Ledger.Ports, acks = first.Ledger.Acks, boundaries = first.Ledger.Boundaries,
                services = first.Ledger.Services, changes = first.Ledger.Changes, pcmTransfers = first.Ledger.PcmTransfers, pcmSamples = first.Ledger.PcmSamples, cuts, futures = observers.Select(s => new { ports = s.Ledger.Ports, acks = s.Ledger.Acks,
                    boundaries = s.Ledger.Boundaries, services = s.Ledger.Services, changes = s.Ledger.Changes, pcmTransfers = s.Ledger.PcmTransfers, pcmSamples = s.Ledger.PcmSamples }) });
            Write(Path.Combine(root, "result.json"), new { passed = true, returnedToDos = true, postResetEcho = true, exits = sides.Select(s => s.Exited),
                system = input.System, comparisons, futureSteps = future + echoSteps, captures, audioSegments = futures, audioFuture = lastAudio[0],
                finalStateSha256 = Hash(State(original)), finalRamSha256 = Hash(original.Memory.Ram), programBase = first.Ledger.ProgramBase,
                programSha256 = Hash(program), initialPost, finalPost = original.Board.Post10Count, beforeEcho, echoAt,
                finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
            Console.WriteLine("PASS actual CF9 INIT/hard reset three-cut four-PC original DOS continuation " + root); return 0;
        }
        finally { foreach (var side in observers) side.Dispose(); foreach (var machine in machines) machine.Dispose(); }
    }
}
