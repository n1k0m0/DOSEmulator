// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Runtime;

/// <summary>Actual 32-MiB 82437FX DOS/VCPI/PAM guest, complete readbacks and full running-state continuation.</summary>
internal static class FxHostGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Counter, ulong Ticks,
        uint Cr0, uint Cr3, uint Cr4, uint Flags, bool V86, uint Eip, uint[] Registers);
    private sealed record Port(ushort Address, uint Value, int Width, bool Write, Point At);
    private sealed record Bank(uint Number, uint Phase, uint Address, uint Length, uint Config, uint Buffer,
        byte[] Readback, byte[] Hidden, byte[] LowGuard, byte[] HighGuard, Point At);
    private sealed record Timestamp(ushort Offset, Point Before, Point After);

    /// <summary>Rejects any missing actual guest effect or inconsistent private artifact.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    /// <summary>Serializes every registered field at a legal common owner boundary.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only an explicitly supplied owned byte array.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    /// <summary>Writes observations including ordinary read-only record properties.</summary>
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value,
        new JsonSerializerOptions(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false }));
    /// <summary>Reads only the own COM's fixed assembler-label table.</summary>
    private static ushort[] Metadata(byte[] program)
    {
        int at = program.AsSpan().IndexOf("FXMETA01"u8); Check(at >= 0 && at + 28 <= program.Length, "Own FX metadata missing.");
        return Enumerable.Range(0, 10).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8 + i * 2, 2))).ToArray();
    }
    /// <summary>Observes the current CPU and persisted integer virtual phase without host clocks or guest mutation.</summary>
    private static Point Observe(PcMachine m)
    {
        var c = m.Cpu;
        return new(m.Scheduler.Now, c.Instructions, c.SaveState().Fields["_tscEpochTicks"].GetUInt64(), c.TimestampCounter,
            m.Scheduler.ClockTicks(c.TimestampFrequency), c.Cr0, c.Cr3, c.Cr4, c.Eflags, c.Virtual8086, c.Eip,
            Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray());
    }
    /// <summary>Records only CPU-executed configuration accesses and actual guest buffer copies.</summary>
    private sealed class Ledger(PcMachine machine, byte[] program, ushort[] metadata)
    {
        /// <summary>Actual DOS-owned code base established from unchanged loaded NASM bytes.</summary>
        public uint ProgramBase { get; private set; }
        /// <summary>Complete real PCI accesses after original DOS has booted.</summary>
        public List<Port> Ports { get; } = [];
        /// <summary>All bytes read by actual guest REP MOVSB operations, with hidden physical backing for independent checks.</summary>
        public List<Bank> Banks { get; } = [];
        /// <summary>Actually completed own RDTSC instructions.</summary>
        public List<Timestamp> Timestamps { get; } = [];
        private Point? _timestamp;
        private ushort _offset;
        /// <summary>Records actual bus values after their hardware effects, never synthesizing I/O.</summary>
        public void PortAccess(ushort address, uint value, int width, bool write)
        {
            if (address is >= 0xcf8 and <= 0xcff) Ports.Add(new(address, value, width, write, Observe(machine)));
        }
        /// <summary>Observes only the own static labels after a complete readback and at real timestamp boundaries.</summary>
        public void Instruction(uint physical, byte opcode)
        {
            uint basis = machine.Cpu.GetSegmentBase(Segment.Cs);
            if (basis + 0x110 >= machine.Memory.Ram.Length || physical < basis || physical >= basis + metadata[6] ||
                !machine.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis;
            ushort offset = (ushort)(physical - basis);
            if (_timestamp is not null)
            { Timestamps.Add(new(_offset, _timestamp, Observe(machine))); _timestamp = null; }
            if (offset == metadata[3] || offset == metadata[4])
            { Check(opcode == 0x0f, "Own timestamp bytes differ."); _timestamp = Observe(machine); _offset = offset; }
            if (offset == metadata[5]) throw new InvalidDataException("Actual private PAM client fault at " + machine.Cpu.Position);
            if (offset != metadata[2]) return;
            uint proof = basis + metadata[0];
            // Read one own already-stored physical proof word without any guest bus effect.
            uint Word(uint at) => BinaryPrimitives.ReadUInt32LittleEndian(machine.Memory.Ram.AsSpan((int)(proof + at), 4));
            uint number = Word(20), phase = Word(24), address = Word(28), count = Word(32), buffer = Word(16);
            Check(number < 13 && phase < 9 && count is 16384 or 65536 && buffer > 0 && buffer + 65536 <= 0xa0000,
                "Own bank report/buffer outside DOS-owned conventional memory.");
            Banks.Add(new(number, phase, address, count, Word(36), buffer,
                machine.Memory.Ram.AsSpan((int)buffer, (int)count).ToArray(),
                machine.Memory.Ram.AsSpan((int)address, (int)count).ToArray(),
                machine.Memory.Ram.AsSpan((int)address - 16, 16).ToArray(),
                machine.Memory.Ram.AsSpan((int)(address + count), 16).ToArray(), Observe(machine)));
        }
    }
    /// <summary>Boots original DOS entirely through the strict board/profile/media factory.</summary>
    private static (PcMachine Machine, long Steps) Boot(Input input, Stopwatch timer)
    {
        var m = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(m.Profile.Id == "dose-430fx-host-dev-v1" && m.Cpu.Model == X86CpuModel.IntelP54cB5Subset &&
            m.Cpu.TimestampFrequency == 100_000_000 && m.Memory.Ram.Length == 32 * 1048576, "Wrong constructed 430FX host subset.");
        long steps = 0;
        while (!m.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && steps < 500000000 && m.Fault is null)
        {
            Check(timer.Elapsed.TotalSeconds < 240, "FX DOS boot timeout: " + m.Video.ReadText());
            int count = m.RunSteps(10000); Check(count > 0, "FX DOS boot stopped."); steps += count;
        }
        Check(m.Fault is null && m.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal), "Actual FX DOS prompt missing: " + m.Fault + m.Video.ReadText());
        Check(m.Memory.Ram.AsSpan(0xc0000, 0x40000).IndexOfAnyExcept((byte)0) < 0,
            "Fresh hidden PAM backing must be zero before this narrowly scoped restoring probe.");
        return (m, steps);
    }
    /// <summary>Encodes internal virtual guest scan codes without touching native Windows input.</summary>
    private static byte[] Command(bool german) => german
        ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rFXHOST.COM\r")).ToArray()
        : GuestKeyboard.Encode("A:\rFXHOST.COM\r");
    /// <summary>Runs one real DOS cold start and original/restored complete-PC future.</summary>
    public static int Run(string config)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(config), StateValues.Json)!;
        Check(!input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        byte[] program = File.ReadAllBytes(input.Probe); Check(program.Length is >= 512 and <= 49152, "Own FX COM size differs.");
        ushort[] metadata = Metadata(program);
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Existing evidence target."); Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew(); var boot = Boot(input, timer); using var original = boot.Machine;
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter();
        original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        byte[] lowGuard = original.Memory.Ram.AsSpan(0xbfff0, 16).ToArray(), highGuard = original.Memory.Ram.AsSpan(0x100000, 16).ToArray();
        byte[] firmware = Enumerable.Range(0, 65536).Select(i => original.Memory.ReadByte(0xf0000u + (uint)i)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "firmware.bin"), firmware);
        var before = new Ledger(original, program, metadata); original.Cpu.InstructionStarting += before.Instruction; original.Ports.Accessed += before.PortAccess;
        byte[] keys = Command(input.GermanKeyboard); int sent = 0; long steps = boot.Steps;
        while (!original.Board.DebugText.Contains("FXHOST-CUT", StringComparison.Ordinal) && steps < 700000000 && original.Fault is null)
        {
            Check(timer.Elapsed.TotalSeconds < 300, "FX own client timeout: " + original.Video.ReadText());
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int count = original.RunSteps(100); Check(count > 0, "FX guest stopped."); steps += count;
        }
        original.Cpu.InstructionStarting -= before.Instruction; original.Ports.Accessed -= before.PortAccess;
        Check(original.Fault is null && sent == keys.Length && original.Board.DebugText.Contains("FXHOST-CUT", StringComparison.Ordinal) &&
            !original.Board.DebugText.Contains("FXHOST-FAILED", StringComparison.Ordinal) && before.ProgramBase != 0, "Actual FX cut missing: " + original.Fault + original.Video.ReadText());
        Check(before.Banks.Count == 3 && original.SoundBlaster16!.Active && original.Cpu.ProtectedMode && !original.Cpu.Virtual8086,
            "Cut must be actual WE-only PAM after full ROM copy, active PCM and private protected client.");
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved);
        byte[] captured = State(original); byte[] rawRam = original.Memory.Ram.ToArray();
        using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
        Check(captured.SequenceEqual(State(resumed)) && rawRam.SequenceEqual(resumed.Memory.Ram), "Initial entire FX PC differs.");
        var futureA = new Ledger(original, program, metadata); var futureB = new Ledger(resumed, program, metadata);
        original.Cpu.InstructionStarting += futureA.Instruction; resumed.Cpu.InstructionStarting += futureB.Instruction;
        original.Ports.Accessed += futureA.PortAccess; resumed.Ports.Accessed += futureB.PortAccess;
        bool exitA = false, exitB = false;
        // The following actual DOS prompt, not this intermediate INT21 decode alone, determines completion.
        void ExitA(uint at, byte op) { if (op == 0xcd && original.Memory.ReadByte(at + 1) == 0x21 && ((original.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) exitA = true; }
        // The restored PC must independently execute the same real original DOS return path.
        void ExitB(uint at, byte op) { if (op == 0xcd && resumed.Memory.ReadByte(at + 1) == 0x21 && ((resumed.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x4c) exitB = true; }
        original.Cpu.InstructionStarting += ExitA; resumed.Cpu.InstructionStarting += ExitB;
        using var pcmA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var pcmB = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long framesA = 0, framesB = 0;
        original.Audio.AudioReady += block => { pcmA.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesA += block.Samples.Length / 2; };
        resumed.Audio.AudioReady += block => { pcmB.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesB += block.Samples.Length / 2; };
        int comparisons = 0; long futureSteps = 0;
        while (!(exitA && exitB && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal) &&
            resumed.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal)) && futureSteps < 100000000)
        {
            Check(timer.Elapsed.TotalSeconds < 900, "FX full DOS continuation timeout.");
            int a = original.RunSteps(50000), b = resumed.RunSteps(50000); futureSteps += a;
            Check(a == b && a > 0 && original.Fault is null && resumed.Fault is null, "FX future counts/faults differ.");
            Check(State(original).SequenceEqual(State(resumed)), "FX whole CPU/device/scheduler differs at slice " + comparisons);
            Check(original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Entire 32-MiB FX RAM differs at slice " + comparisons); comparisons++;
        }
        if (!(exitA && exitB && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal)))
        {
            File.WriteAllBytes(Path.Combine(root, "failed-state.json"), State(original));
            File.WriteAllBytes(Path.Combine(root, "failed-ram.bin"), original.Memory.Ram);
            Write(Path.Combine(root, "failed-result.json"), new { exitA, exitB, futureSteps, comparisons,
                position = original.Cpu.Position, screen = original.Video.ReadText(), debug = original.Board.DebugText,
                banks = before.Banks.Concat(futureA.Banks).Select(row => new { row.Number, row.Phase, row.Address, row.Length, row.Config, row.At }),
                ports = before.Ports.Concat(futureA.Ports), timestamps = before.Timestamps.Concat(futureA.Timestamps) });
        }
        Check(exitA && exitB && original.Board.DebugText.Contains("SNAPSHOT-DONE", StringComparison.Ordinal) &&
            !original.Board.DebugText.Contains("FXHOST-FAILED", StringComparison.Ordinal) && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal),
            "Actual following FX DOS prompt missing.");
        Check(before.Banks.Count + futureA.Banks.Count == 117 && before.Timestamps.Count + futureA.Timestamps.Count == 2, "Actual PAM-byte/timestamp observations incomplete.");
        Check(JsonSerializer.Serialize(futureA.Banks, StateValues.Json) == JsonSerializer.Serialize(futureB.Banks, StateValues.Json) &&
            JsonSerializer.Serialize(futureA.Ports, StateValues.Json) == JsonSerializer.Serialize(futureB.Ports, StateValues.Json) &&
            JsonSerializer.Serialize(futureA.Timestamps, StateValues.Json) == JsonSerializer.Serialize(futureB.Timestamps, StateValues.Json), "Future actual FX publications differ.");
        original.Audio.Flush(); resumed.Audio.Flush();
        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final entire FX graph differs.");
        Check(original.Memory.Ram.AsSpan(0xc0000, 0x40000).IndexOfAnyExcept((byte)0) < 0, "Guest failed to restore hidden physical PAM backing.");
        string audioA = Convert.ToHexString(pcmA.GetHashAndReset()), audioB = Convert.ToHexString(pcmB.GetHashAndReset());
        Check(audioA == audioB && framesA == framesB && framesA > 480 && original.SoundBlaster16!.TransferredSamples == 512, "FX actual PCM/DMA future differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
        var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "FX final media differ.");
        byte[] finalState = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), finalState);
        File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = before.ProgramBase, banks = before.Banks.Concat(futureA.Banks),
            ports = before.Ports.Concat(futureA.Ports), timestamps = before.Timestamps.Concat(futureA.Timestamps),
            beforeCutBanks = before.Banks.Count, beforeCutPorts = before.Ports.Count, beforeCutTimestamps = before.Timestamps.Count,
            futureBanks = futureA.Banks, restoredFutureBanks = futureB.Banks, futurePorts = futureA.Ports, restoredFuturePorts = futureB.Ports,
            futureTimestamps = futureA.Timestamps, restoredFutureTimestamps = futureB.Timestamps });
        Write(Path.Combine(root, "result.json"), new { passed = true, returnedToDos = true, exitA, exitB, comparisons, futureSteps,
            manifest, capturedStateSha256 = Hash(captured), capturedRamSha256 = Hash(rawRam), finalStateSha256 = Hash(finalState),
            finalRamSha256 = Hash(original.Memory.Ram), pcmSha256 = audioA, pcmFrames = framesA, dma = original.SoundBlaster16!.TransferredSamples,
            programSha256 = Hash(program), programBase = before.ProgramBase, proofOffset = metadata[0], proofBytes = metadata[1] - metadata[0],
            lowBoundaryGuard = lowGuard, highBoundaryGuard = highGuard, firmwareSha256 = Hash(firmware), hostSeconds = timer.Elapsed.TotalSeconds, finalScreen = original.Video.ReadText(),
            protectedMode = original.Cpu.ProtectedInstructions, v86 = original.Cpu.Virtual8086Instructions });
        Console.WriteLine("PASS actual 82437FX DOS/PAM/full-PC continuation " + root); return 0;
    }
}
