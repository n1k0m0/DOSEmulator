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

/// <summary>Actual original-DOS Trio64 PCI/shared-bank/LFB scanout and a complete active full-PC continuation.</summary>
internal static class S3Trio64GuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string System);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Ticks, ulong Counter, uint Flags,
        uint Cr0, uint Cr3, uint Cr4, bool V86, uint Eip, uint[] Registers, ushort[] Segments, uint[] Bases, int Phase);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Ack(int Irq, byte Vector, bool Spurious, Point At);
    private sealed record Readback(int Phase, int Bank, byte[] Bytes, Point At);
    private sealed record Scanout(int Ordinal, int Width, int Height, long FrameId, int Generation, string PixelsSha256,
        Dictionary<string, JsonElement> Registers, Point At);
    private sealed record PcmDigest(long Frames, string Sha256);
    private sealed record Boundary(string Kind, Point At);
    private sealed record Service(string Kind, ushort Offset, Point Before)
    {
        /// <summary>Actual original resident service return, observed before its next own instruction effects.</summary>
        public Point? After { get; set; }
    }
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false };
    /// <summary>Rejects a missing real guest/resource/continuation effect.</summary>
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
    /// <summary>Hashes only explicitly observed own bytes, without recording a screen or sound stream.</summary>
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Serializes the complete registered CPU/RAM/device/scheduler graph.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Writes bounded textual observation data; no image or recording is produced.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    /// <summary>Reads the unchanged own versioned metadata and labels.</summary>
    private static ushort[] Metadata(byte[] p)
    {
        int at = p.AsSpan().IndexOf("S3META01"u8);
        Check(at >= 0 && at + 76 <= p.Length && BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 8)) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 10)) == 32, "Own Trio64 metadata absent.");
        return Enumerable.Range(0, 32).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(at + 12 + i * 2, 2))).ToArray();
    }
    /// <summary>Finds one exact original-service call in the unchanged own executable, without altering its bytes.</summary>
    private static ushort Site(byte[] p, byte[] signature)
    {
        int at = p.AsSpan().IndexOf(signature);
        Check(at >= 0 && p.AsSpan().LastIndexOf(signature) == at, "Own service signature not unique.");
        return checked((ushort)(at + signature.Length - 2 + 0x100));
    }
    /// <summary>Observes true architectural and complete integer virtual phase values outside scheduled callbacks.</summary>
    private static Point Observe(PcMachine m, uint basis, ushort[] t)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(),
            m.Scheduler.ClockTicks(c.TimestampFrequency), c.TimestampCounter, c.Eflags, c.Cr0, c.Cr3, c.Cr4, c.Virtual8086,
            c.Eip, Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegmentBase((Segment)i)).ToArray(), basis == 0 ? 0 : (int)m.Memory.ReadDword(basis + t[0] + 20u));
    }
    /// <summary>Read-only actual guest I/O, readbacks, native PCM and in-memory renderer hashes.</summary>
    private sealed class Ledger(PcMachine m, byte[] program, ushort[] table, uint initialBasis = 0)
    {
        /// <summary>Actual unchanged conventional load base; imports retain this only as observation metadata.</summary>
        public uint ProgramBase { get; private set; } = initialBasis;
        /// <summary>Every selected real VGA/PCI/DSP transfer after the own program is actually executing.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>Actual physical IRQ acknowledgements, including the new post-graphics keyboard command.</summary>
        public List<Ack> Acks { get; } = [];
        /// <summary>All64 actual complete guest64KiB readbacks into DOS-owned RAM.</summary>
        public List<Readback> Readbacks { get; } = [];
        /// <summary>Actual renderer hashes at three concrete guest-programmed configurations; pixels never leave memory.</summary>
        public List<Scanout> Frames { get; } = [];
        /// <summary>Actual original VDS/VCPI calls and returns.</summary>
        public List<Service> Services { get; } = [];
        /// <summary>Actual native DSP starts, retaining supplied virtual timestamps without clock sampling in callbacks.</summary>
        public List<SoundBlaster16.Transfer> PcmTransfers { get; } = [];
        /// <summary>Actual accepted raw DMA sample publications.</summary>
        public List<SoundBlaster16.Sample> PcmSamples { get; } = [];
        /// <summary>Actual points at the original DOS entry, private client and two RDTSC instructions.</summary>
        public List<Boundary> Boundaries { get; } = [];
        /// <summary>Whether the CPU completed the real NOP while the DAC and ATC phases are still open.</summary>
        public bool CutSeen { get; private set; }
        private Service? _service;
        private readonly Dictionary<ushort, string> _sites = new()
        {
            [table[9]] = "vcpi-enter", [table[10]] = "vcpi-return", [table[11]] = "vds-lock",
            [table[12]] = "vds-disable", [table[13]] = "vds-enable", [table[14]] = "vds-unlock",
            [Site(program, [0xb8, 0x00, 0xde, 0xcd, 0x67])] = "vcpi-presence",
            [Site(program, [0xb8, 0x01, 0xde, 0xcd, 0x67])] = "vcpi-interface"
        };
        /// <summary>Returns the actual present CPU and virtual clock point.</summary>
        public Point Point() => Observe(m, ProgramBase, table);
        /// <summary>Reads one actual already-stored proof dword without guest memory effects.</summary>
        public uint Dword(int offset) => ProgramBase == 0 ? 0 : m.Memory.ReadDword(ProgramBase + table[0] + (uint)offset);
        /// <summary>Records one actual PIC acknowledgement without injecting an interrupt.</summary>
        public void Acknowledge(int irq, byte vector, bool spurious) { if (ProgramBase != 0) Acks.Add(new(irq, vector, spurious, Point())); }
        /// <summary>Retains the native DSP transfer descriptor without sampling inside its event callback.</summary>
        public void Transfer(SoundBlaster16.Transfer value) => PcmTransfers.Add(value);
        /// <summary>Retains the native already-timestamped sample publication without host timing.</summary>
        public void Sample(SoundBlaster16.Sample value) => PcmSamples.Add(value);
        /// <summary>Records exact selected port transactions; repetitive zero-status polling does not create huge trace payloads.</summary>
        public void Access(ushort address, uint value, int width, bool write)
        {
            if (ProgramBase == 0) return;
            if (address is >= 0x3b4 and <= 0x3cf || address is 0x3d4 or 0x3d5 or 0x3da or 0x46e8 or 0x102 or 0x4ae8 ||
                address is >= 0xcf8 and <= 0xcff || address == 0x22c && write || address == 0x22e && !write ||
                address == 0x60 && !write || address == 8 && !write && value != 0)
                Ports.Add(new(address, value, width, write, Point()));
        }
        /// <summary>Observes unchanged own code and completed guest readbacks; never writes a device or RAM.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            uint basis = m.Cpu.GetSegmentBase(Segment.Cs);
            if (basis > m.Memory.Ram.Length - table[3] || physical < basis || physical >= basis + table[3] ||
                !m.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            bool first = ProgramBase == 0; ProgramBase = basis; ushort offset = (ushort)(physical - basis);
            if (first && offset == 0x100) Boundaries.Add(new("dos-entry", Point()));
            if (_service is not null &&
                ((_service.Kind is not ("vcpi-enter" or "vcpi-return") && offset == _service.Offset + 2) ||
                 (_service.Kind == "vcpi-enter" && !m.Cpu.Virtual8086 && (m.Cpu.Cr0 & 1) != 0 && m.Cpu.GetSegment(Segment.Cs) == 8) ||
                 (_service.Kind == "vcpi-return" && offset == table[8])))
            { _service.After = Point(); _service = null; }
            if (_sites.TryGetValue(offset, out string? kind)) { _service = new(kind, offset, Point()); Services.Add(_service); }
            if (offset == table[31]) throw new InvalidDataException("Actual own Trio64 private-client fault at " + m.Cpu.Position);
            if (offset == table[7]) Boundaries.Add(new("private-entry", Point()));
            if (offset == table[28] || offset == table[29]) Boundaries.Add(new(offset == table[28] ? "tsc-before" : "tsc-after", Point()));
            if (offset == table[6]) CutSeen = true;
            if (offset == table[4])
            {
                uint buffer = Dword(16); Check(buffer >= 16 && buffer <= m.Memory.Ram.Length - 65552, "Own readback outside RAM.");
                Readbacks.Add(new((int)Dword(20), (int)Dword(24), m.Memory.Ram.AsSpan((int)buffer, 65536).ToArray(), Point()));
            }
            if (offset == table[5])
            {
                var point = Point(); var frame = m.CaptureFrame(); var source = m.Video.SaveState().Fields;
                string[] keys = ["_seq", "_gc", "_crtc", "_attr", "_dac", "_misc", "_pelMask", "_attributeData", "_displayEnabled", "_writeIndex", "_writeComponent", "_trioAdvanced", "_trioSetup", "_trioPos"];
                Frames.Add(new((int)Dword(68), frame.Width, frame.Height, frame.FrameId, frame.ModeGeneration,
                    Hash(MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan())), keys.ToDictionary(k => k, k => source[k]), point));
            }
        }
    }
    /// <summary>One complete actual original or full-SnapshotStore-restored PC.</summary>
    private sealed class Side : IDisposable
    {
        /// <summary>Actual constructor-bound machine owned by the caller.</summary>
        public PcMachine Machine { get; }
        /// <summary>Read-only selected actual observations.</summary>
        public Ledger Ledger { get; }
        /// <summary>Actual own DOS/AH4C has executed; the later completed prompt is independently required.</summary>
        public bool Exited { get; private set; }
        private long _frames;
        private readonly IncrementalHash _audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        /// <summary>Attaches only observers to the already constructed or fully imported graph.</summary>
        public Side(PcMachine m, byte[] p, ushort[] t, uint basis = 0)
        {
            Machine = m; Ledger = new(m, p, t, basis);
            m.Ports.Accessed += Ledger.Access; m.Pic.InterruptAcknowledged += Ledger.Acknowledge;
            m.Cpu.InstructionStarting += Ledger.Instruction; m.Cpu.InstructionStarting += Exit;
            m.SoundBlaster16!.TransferStarted += Ledger.Transfer; m.SoundBlaster16.SampleTransferred += Ledger.Sample; m.Audio.AudioReady += Audio;
        }
        /// <summary>Observes actual own DOS exit before requiring the original command loop and fresh echo.</summary>
        private void Exit(uint physical, byte opcode)
        {
            if (Ledger.ProgramBase != 0 && Machine.Cpu.GetSegmentBase(Segment.Cs) == Ledger.ProgramBase && opcode == 0xcd &&
                Machine.Memory.ReadByte(physical + 1) == 0x21 && ((Machine.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) Exited = true;
        }
        /// <summary>Hashes real published PCM, retaining no recording or raw audio file.</summary>
        private void Audio(AudioBlock block) { _audio.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); _frames += block.Samples.Length / 2; }
        /// <summary>Starts another observation interval without altering the emulated mixer or device.</summary>
        public PcmDigest FinishAudio() { var result = new PcmDigest(_frames, Convert.ToHexString(_audio.GetHashAndReset())); _frames = 0; return result; }
        /// <summary>Runs only real CPU/scheduler owner steps.</summary>
        public int Step(int count) => Machine.RunSteps(count);
        /// <summary>Detaches observers; machine lifetime remains with the caller.</summary>
        public void Dispose()
        {
            Machine.Ports.Accessed -= Ledger.Access; Machine.Pic.InterruptAcknowledged -= Ledger.Acknowledge;
            Machine.Cpu.InstructionStarting -= Ledger.Instruction; Machine.Cpu.InstructionStarting -= Exit;
            Machine.SoundBlaster16!.TransferStarted -= Ledger.Transfer; Machine.SoundBlaster16.SampleTransferred -= Ledger.Sample;
            Machine.Audio.AudioReady -= Audio; _audio.Dispose();
        }
    }
    /// <summary>Retains concrete text/CPU/port/RAM failure evidence without a screenshot or image.</summary>
    private static void Require(bool condition, string root, Side side, string why)
    {
        if (condition) return;
        Write(Path.Combine(root, "failed-result.json"), new { why, point = side.Ledger.Point(), fault = side.Machine.Fault,
            screen = side.Machine.Video.ReadText(), debug = side.Machine.Board.DebugText, lastPorts = side.Ledger.Ports.TakeLast(24),
            reads = side.Ledger.Readbacks.Select(r => new { r.Phase, r.Bank, r.At }), frames = side.Ledger.Frames, services = side.Ledger.Services });
        File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), side.Machine.Memory.Ram); File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(side.Machine));
        throw new InvalidDataException(why + " See " + Path.Combine(root, "failed-result.json"));
    }
    /// <summary>Compares every registered CPU/RAM/VRAM/device/scheduler value, not a selected device subset.</summary>
    private static void Equal(PcMachine a, PcMachine b)
    { Check(State(a).SequenceEqual(State(b)) && a.Memory.Ram.SequenceEqual(b.Memory.Ram), "Entire Trio64 PC graph/RAM/VRAM differs."); }
    /// <summary>Requires all actual prompt cells and completed matching four-character BDA/CRTC cursor updates.</summary>
    private static bool PromptReady(PcMachine m)
    {
        string[] lines = m.Video.ReadText().Replace("\r", "", StringComparison.Ordinal).TrimEnd().Split('\n'); int row = lines.Length - 1;
        if (row is < 0 or >= 25 || lines[row] != "A:\\>") return false;
        byte[] crtc = m.Video.SaveState().Fields["_crtc"].GetBytesFromBase64();
        int start = crtc[12] << 8 | crtc[13], cursor = crtc[14] << 8 | crtc[15];
        return crtc[0x13] * 2 == 80 && m.Memory.ReadWord(0x450) == (row << 8 | 4) && cursor == ((start + row * 80 + 4) & 65535);
    }
    /// <summary>Encodes balanced internal guest scancodes with explicit original keyboard-layout handling.</summary>
    private static byte[] Command(bool german) => german ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rS3TRIO.COM\r")).ToArray() : GuestKeyboard.Encode("A:\rS3TRIO.COM\r");
    /// <summary>Executes one actual original-DOS boot, full active snapshot continuation and independent fresh command.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        Check(!input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Output must be new."); Directory.CreateDirectory(root);
        byte[] program = File.ReadAllBytes(input.Probe); ushort[] table = Metadata(program); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(original.Profile.Id == MachineProfile.S3Trio64ProfileId && original.Video.IsTrio64 && original.Memory.Ram.Length == 32 * 1048576 &&
            original.Cpu.TimestampFrequency == 100_000_000, "Wrong constructor-bound S3 board/model/clock.");
        long total = 0;
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && total < 700000000)
        { Check(timer.Elapsed.TotalSeconds < 300, "Original S3 DOS boot timeout."); int n = original.RunSteps(10000); Check(n > 0, "S3 DOS boot stopped."); total += n; }
        Check(original.Fault is null && original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), "Original S3 DOS prompt missing.");
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter(); original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        using var first = new Side(original, program, table); byte[] keys = Command(input.GermanKeyboard); int sent = 0;
        while (!first.Ledger.CutSeen && original.Fault is null && total < 950000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1000, root, first, "Actual S3 full-VRAM/open-DAC/ATC cut timeout.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int budget = first.Ledger.Readbacks.Count >= 63 ? 1 : first.Ledger.ProgramBase == 0 ? 100 : 10000;
            int n = first.Step(budget); Check(n > 0, "S3 guest stopped before cut."); total += n;
        }
        Require(first.Ledger.CutSeen && original.Fault is null && sent == keys.Length && first.Ledger.Readbacks.Count == 64 &&
            first.Ledger.Frames.Count == 1 && first.Ledger.Dword(20) == 6 && original.SoundBlaster16!.Active,
            root, first, "Actual entire VRAM readbacks/first scanout/PCM/open DAC-ATC cut missing.");
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved);
        Write(Path.Combine(root, "cut-point.json"), first.Ledger.Point());
        using var restored = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
        using var second = new Side(restored, program, table, first.Ledger.ProgramBase); Equal(original, restored);
        int ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count, frames = first.Ledger.Frames.Count,
            reads = first.Ledger.Readbacks.Count, services = first.Ledger.Services.Count, points = first.Ledger.Boundaries.Count,
            transfers = first.Ledger.PcmTransfers.Count, samples = first.Ledger.PcmSamples.Count;
        long audioCutGenerated = original.Audio.GeneratedFrames;
        int audioCutBuffered = original.Audio.SaveState().Fields["_count"].GetInt32() / 2;
        first.FinishAudio(); long future = 0; int comparisons = 1;
        /// <summary>Requires actual own DOS exit, complete512-sample DMA and fully committed original command prompt.</summary>
        bool Complete(Side side) => side.Exited && side.Machine.SoundBlaster16!.TransferredSamples == 512 && PromptReady(side.Machine);
        while (!(Complete(first) && Complete(second)) && future < 250000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1500, root, first, "Actual original/restored S3 DOS continuation timeout.");
            int n = first.Step(10000), other = second.Step(10000); Check(n == other && n > 0 && original.Fault is null && restored.Fault is null, "S3 future count/fault differs.");
            future += n; Equal(original, restored); comparisons++;
        }
        Require(Complete(first) && Complete(second) && first.Ledger.Dword(28) == 1 && first.Ledger.Dword(32) == 0 &&
            first.Ledger.Frames.Count == 3 && first.Ledger.Dword(72) == 64, root, first, "Actual three rendered configurations/whole bank readbacks/DOS return missing.");
        string beforeEcho = original.Video.ReadText(); Check(!beforeEcho.Contains("TRIOCONTINUED", StringComparison.Ordinal), "Fresh S3 echo sentinel already present.");
        var echoAt = new { nanoseconds = original.Scheduler.Now, ports = first.Ledger.Ports.Count, acks = first.Ledger.Acks.Count };
        byte[] echo = GuestKeyboard.Encode("ECHO TRIOCONTINUED\r"); int echoSent = 0;
        /// <summary>Requires a new executed original-shell output and its complete following cursor/command prompt.</summary>
        bool EchoDone(Side side) => side.Machine.Video.ReadText().Contains("\nTRIOCONTINUED", StringComparison.Ordinal) && PromptReady(side.Machine);
        while (!(EchoDone(first) && EchoDone(second) && echoSent == echo.Length) && future < 270000000)
        {
            Require(timer.Elapsed.TotalSeconds < 1600, root, first, "Actual fresh S3 original DOS/KBC echo timeout.");
            if (echoSent < echo.Length)
            {
                bool accepted = GuestKeyboard.TryInjectForBios(original, echo[echoSent]);
                Check(GuestKeyboard.TryInjectForBios(restored, echo[echoSent]) == accepted, "S3 restored KBC admission differs."); if (accepted) echoSent++;
            }
            int n = first.Step(1000); Check(second.Step(1000) == n && n > 0 && original.Fault is null && restored.Fault is null, "S3 echo future differs.");
            future += n; Equal(original, restored); comparisons++;
        }
        Require(EchoDone(first) && EchoDone(second), root, first, "Actual new S3 DOS echo/prompt missing.");
        original.Audio.Flush(); restored.Audio.Flush(); Equal(original, restored); comparisons++;
        PcmDigest audioA = first.FinishAudio(), audioB = second.FinishAudio();
        long expectedFutureFrames = original.Audio.GeneratedFrames - audioCutGenerated + audioCutBuffered;
        Check(audioA.Frames == expectedFutureFrames && audioB.Frames == expectedFutureFrames && expectedFutureFrames > 480 &&
            audioA.Sha256.Length == 64 && audioA.Sha256.All(Uri.IsHexDigit) && audioA.Sha256 != Hash(ReadOnlySpan<byte>.Empty),
            "Actual positive future PCM duration/hash missing.");
        Check(JsonSerializer.Serialize(audioA, Json) == JsonSerializer.Serialize(audioB, Json), "Actual future S3 PCM differs.");
        Check(JsonSerializer.Serialize(first.Ledger.Ports.Skip(ports), Json) == JsonSerializer.Serialize(second.Ledger.Ports, Json) &&
            JsonSerializer.Serialize(first.Ledger.Frames.Skip(frames), Json) == JsonSerializer.Serialize(second.Ledger.Frames, Json), "Actual future S3 ports/renderer publications differ.");
        Check(JsonSerializer.Serialize(first.Ledger.Acks.Skip(acks), Json) == JsonSerializer.Serialize(second.Ledger.Acks, Json) &&
            JsonSerializer.Serialize(first.Ledger.Readbacks.Skip(reads), Json) == JsonSerializer.Serialize(second.Ledger.Readbacks, Json) &&
            JsonSerializer.Serialize(first.Ledger.Services.Skip(services), Json) == JsonSerializer.Serialize(second.Ledger.Services, Json) &&
            JsonSerializer.Serialize(first.Ledger.Boundaries.Skip(points), Json) == JsonSerializer.Serialize(second.Ledger.Boundaries, Json) &&
            JsonSerializer.Serialize(first.Ledger.PcmTransfers.Skip(transfers), Json) == JsonSerializer.Serialize(second.Ledger.PcmTransfers, Json) &&
            JsonSerializer.Serialize(first.Ledger.PcmSamples.Skip(samples), Json) == JsonSerializer.Serialize(second.Ledger.PcmSamples, Json),
            "Actual future IRQ, service, CPU-boundary or native PCM publication differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
        var mediaB = MediaCheckpointStore.Capture(restored, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "All six S3 media generations differ.");
        File.WriteAllBytes(Path.Combine(root, "final-state.json"), State(original)); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = first.Ledger.ProgramBase, ports = first.Ledger.Ports, acks = first.Ledger.Acks,
            readbacks = first.Ledger.Readbacks, frames = first.Ledger.Frames, services = first.Ledger.Services, boundaries = first.Ledger.Boundaries,
            pcmTransfers = first.Ledger.PcmTransfers, pcmSamples = first.Ledger.PcmSamples,
            cut = new { ports, acks, frames, readbacks = reads, services, boundaries = points, pcmTransfers = transfers, pcmSamples = samples },
            future = new { ports = second.Ledger.Ports, acks = second.Ledger.Acks, readbacks = second.Ledger.Readbacks, frames = second.Ledger.Frames,
                services = second.Ledger.Services, boundaries = second.Ledger.Boundaries, pcmTransfers = second.Ledger.PcmTransfers, pcmSamples = second.Ledger.PcmSamples } });
        Write(Path.Combine(root, "result.json"), new { passed = true, returnedToDos = true, postGraphicsEcho = true, exits = new[] { first.Exited, second.Exited },
            system = input.System, manifest, comparisons, futureSteps = future, capturedStateSha256 = Hash(File.ReadAllBytes(Path.Combine(saved, "state.json"))),
            capturedRamSha256 = Hash(File.ReadAllBytes(Path.Combine(saved, "ram.bin"))), finalStateSha256 = Hash(State(original)), finalRamSha256 = Hash(original.Memory.Ram),
            programBase = first.Ledger.ProgramBase, programSha256 = Hash(program), audioFuture = audioA, restoredAudioFuture = audioB, beforeEcho, echoAt,
            finalScreen = original.Video.ReadText(), hostSeconds = timer.Elapsed.TotalSeconds });
        Console.WriteLine("PASS actual S3 Trio64 PCI/bank/LFB full-PC original DOS continuation " + root); return 0;
    }
}
