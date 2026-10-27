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

/// <summary>Actual full-PC DOS/P54C execution, independent instruction ledger and exact complete-state continuation.</summary>
internal static class P54cPcGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, string Probe, string? FaultInstruction = null);
    private sealed record Point(ulong Nanoseconds, long Instructions, ulong Epoch, ulong Counter, ulong Ticks,
        uint Flags, uint Cr0, uint Cr4, bool V86, ushort Cs, uint[] Registers, ushort[] Segments, ulong Operand);
    private sealed record Entry(string Kind, ushort Offset, Point Before)
    {
        /// <summary>Actual following own instruction boundary after the observed operation has completed.</summary>
        public Point? After { get; set; }
    }

    /// <summary>Rejects a guest or continuation that does not satisfy its declared bounded contract.</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    /// <summary>Serializes the complete registered hardware graph without converting raw x87 values.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes owned bytes without opening any unrelated media or journal.</summary>
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    /// <summary>Writes a bounded actual proof document with every read-only observation included.</summary>
    private static void Write(string path, object data) => File.WriteAllText(path, JsonSerializer.Serialize(data,
        new JsonSerializerOptions(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false }));
    /// <summary>Finds only the own assembler-provided metadata table in the exact test program bytes.</summary>
    private static ushort[] Metadata(byte[] program, bool fault)
    {
        int at = program.AsSpan().IndexOf(fault ? "P54FAULT"u8 : "P54META1"u8); int count = fault ? 1 : 14;
        Check(at >= 0 && at + 8 + count * 2 <= program.Length, "Own program metadata missing.");
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(at + 8 + i * 2, 2))).ToArray();
    }
    /// <summary>Samples architectural registers at a real instruction boundary, without changing clocks or rights.</summary>
    private static Point Observe(PcMachine m, uint operand = 0)
    {
        var c = m.Cpu; var fields = c.SaveState().Fields;
        ulong value = operand == 0 ? 0 : (ulong)m.Memory.ReadDword(operand + 4) << 32 | m.Memory.ReadDword(operand);
        return new(m.Scheduler.Now, c.Instructions, fields["_tscEpochTicks"].GetUInt64(), c.TimestampCounter,
            m.Scheduler.ClockTicks(c.TimestampFrequency), c.Eflags, c.Cr0, c.Cr4, c.Virtual8086, c.GetSegment(Segment.Cs),
            Enumerable.Range(0, 8).Select(i => c.GetRegister((Register)i)).ToArray(),
            Enumerable.Range(0, 6).Select(i => c.GetSegment((Segment)i)).ToArray(), value);
    }
    /// <summary>Collects only actual own-program CPU opcodes; host observers never execute or replace them.</summary>
    private sealed class Ledger(PcMachine machine, byte[] program, ushort[] metadata)
    {
        /// <summary>Actual observed own instruction transfers, never synthesized guest operations.</summary>
        public List<Entry> Entries { get; } = [];
        /// <summary>Actual DOS-loaded own program segment base, established from unchanged loaded instruction bytes.</summary>
        public uint ProgramBase { get; private set; }
        private Entry? _pending;
        /// <summary>Finishes the last real instruction before observing the next own instruction.</summary>
        public void ObserveInstruction(uint physical, byte opcode)
        {
            uint basis = machine.Cpu.GetSegmentBase(Segment.Cs);
            if (_pending is not null && basis == ProgramBase && physical >= basis + 0x100 && physical < basis + metadata[0])
            { _pending.After = Observe(machine, ProgramBase + metadata[4]); _pending = null; }
            if (opcode != 0x0f || physical < basis || physical - basis > ushort.MaxValue) return;
            ushort offset = (ushort)(physical - basis);
            int index = Array.IndexOf(metadata, offset, 5, 8);
            if (index < 5) return;
            if (basis + 0x100 + 16 >= machine.Memory.Ram.Length || !machine.Memory.Ram.AsSpan((int)basis + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            ProgramBase = basis;
            string kind = new[] { "cpuid0", "cpuid1", "cx8match", "cx8miss", "wrmsr", "rdmsr", "rdtsc", "futureTsc" }[index - 5];
            _pending = new(kind, offset, P54cPcGuestRunner.Observe(machine, basis + metadata[4])); Entries.Add(_pending);
        }
    }
    /// <summary>Boots the original operating system through the actual profile, firmware and media factories.</summary>
    private static (PcMachine Machine, long Steps) Boot(Input input, Stopwatch timer)
    {
        var machine = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        Check(machine.Cpu.Model == X86CpuModel.IntelP54cB5Subset && machine.Cpu.TimestampFrequency == 90_000_000,
            "Own guest requires the fully constructed explicit P54C PC.");
        long steps = 0;
        while (!machine.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && steps < 300000000 && machine.Fault is null)
        {
            Check(timer.Elapsed.TotalSeconds < 180, "P54C DOS boot timeout: " + machine.Video.ReadText());
            int count = machine.RunSteps(10000); Check(count > 0, "P54C DOS boot stopped."); steps += count;
        }
        Check(machine.Fault is null && steps < 300000000 && machine.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal),
            "Actual P54C DOS C prompt missing: " + machine.Fault + machine.Video.ReadText());
        return (machine, steps);
    }
    /// <summary>Encodes one own DOS command without using native host input or focusing a window.</summary>
    private static byte[] Command(bool german, string name) => german
        ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\r" + name + "\r")).ToArray()
        : GuestKeyboard.Encode("A:\r" + name + "\r");
    /// <summary>Runs the bounded positive continuation or a separately counted actual monitor-fault cut.</summary>
    public static int Run(string config)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(config), StateValues.Json)!;
        Check(!input.Probe.Contains("doom2-play", StringComparison.OrdinalIgnoreCase) && !input.Output.Contains("doom2-play", StringComparison.OrdinalIgnoreCase), "Excluded personal path.");
        byte[] program = File.ReadAllBytes(input.Probe); Check(program.Length is >= 64 and <= 16384, "Own COM size differs.");
        string root = Path.GetFullPath(input.Output); Check(!Directory.Exists(root) && !File.Exists(root), "Evidence target exists."); Directory.CreateDirectory(root);
        return input.FaultInstruction is null ? Positive(input, program, root) : Fault(input, program, root);
    }
    /// <summary>Captures actual P54C instructions and active devices, then requires exact whole-PC future and DOS return.</summary>
    private static int Positive(Input input, byte[] program, string root)
    {
        ushort[] metadata = Metadata(program, false); var timer = Stopwatch.StartNew(); var boot = Boot(input, timer);
        using var original = boot.Machine; original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter();
        original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        long steps = boot.Steps; byte[] keys = Command(input.GermanKeyboard, "P54CPC.COM"); int sent = 0;
        var ledger = new Ledger(original, program, metadata); original.Cpu.InstructionStarting += ledger.ObserveInstruction;
        while (!original.Board.DebugText.Contains("SNAPSHOT-CUT", StringComparison.Ordinal) && steps < 500000000 && original.Fault is null)
        {
            Check(timer.Elapsed.TotalSeconds < 180, "P54C own guest timeout: " + original.Video.ReadText());
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int count = original.RunSteps(100); Check(count > 0, "P54C own guest stopped."); steps += count;
        }
        original.Cpu.InstructionStarting -= ledger.ObserveInstruction;
        Check(original.Fault is null && sent == keys.Length && original.Board.DebugText.Contains("SNAPSHOT-CUT", StringComparison.Ordinal) && !original.Board.DebugText.Contains("SNAPSHOT-FAILED", StringComparison.Ordinal),
            "P54C guest did not reach cut: fault=" + original.Fault + " CPU=" + original.Cpu.Position + " DSP=" + JsonSerializer.Serialize(original.SoundBlaster16?.SaveState(), StateValues.Json) + " SCREEN=" + original.Video.ReadText());
        Check(ledger.ProgramBase != 0 && ledger.Entries.All(e => e.After is not null), "Actual own instruction ledger incomplete.");
        bool v86 = ledger.Entries[0].Before.V86; Check(ledger.Entries.Count == (v86 ? 4 : 7), "Wrong actual own CPU instruction count.");
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved);
        byte[] captured = State(original), rawRam = original.Memory.Ram.ToArray();
        using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
        Check(captured.SequenceEqual(State(resumed)) && rawRam.SequenceEqual(resumed.Memory.Ram), "Initial full P54C state differs.");
        bool exitA = false, exitB = false;
        // Decoded AH4C is only an intermediate signal; the actual following DOS prompt is also required below.
        void ObserveA(uint physical, byte opcode) { if (opcode == 0xcd && original.Memory.ReadByte(physical + 1) == 0x21 && (original.Cpu.GetRegister(Register.Ax) >> 8) == 0x4c) exitA = true; }
        // The restored PC follows the same real DOS entry and memory path.
        void ObserveB(uint physical, byte opcode) { if (opcode == 0xcd && resumed.Memory.ReadByte(physical + 1) == 0x21 && (resumed.Cpu.GetRegister(Register.Ax) >> 8) == 0x4c) exitB = true; }
        var futureA = new Ledger(original, program, metadata); var futureB = new Ledger(resumed, program, metadata);
        original.Cpu.InstructionStarting += ObserveA; resumed.Cpu.InstructionStarting += ObserveB;
        original.Cpu.InstructionStarting += futureA.ObserveInstruction; resumed.Cpu.InstructionStarting += futureB.ObserveInstruction;
        using var pcmA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var pcmB = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long framesA = 0, framesB = 0;
        original.Audio.AudioReady += block => { pcmA.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesA += block.Samples.Length / 2; };
        resumed.Audio.AudioReady += block => { pcmB.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesB += block.Samples.Length / 2; };
        byte[] resumeKeys = GuestKeyboard.Encode("k"); int accepted = 0, comparisons = 0; long futureSteps = 0;
        while (!(exitA && exitB && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal) && resumed.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal)) && futureSteps < 50000000)
        {
            Check(timer.Elapsed.TotalSeconds < 240, "P54C DOS continuation timeout.");
            if (accepted < resumeKeys.Length)
            {
                bool a = GuestKeyboard.TryInjectForBios(original, resumeKeys[accepted]), b = GuestKeyboard.TryInjectForBios(resumed, resumeKeys[accepted]);
                Check(a == b, "P54C KBC acceptance differs."); if (a) accepted++;
            }
            int countA = original.RunSteps(10000), countB = resumed.RunSteps(10000); futureSteps += countA;
            Check(countA == countB && countA > 0 && original.Fault is null && resumed.Fault is null, "P54C continuation count/fault differs.");
            Check(State(original).SequenceEqual(State(resumed)), "P54C CPU/device/scheduler differs at slice " + comparisons);
            Check(original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Entire P54C RAM differs at slice " + comparisons); comparisons++;
        }
        Check(exitA && exitB && original.Board.DebugText.Contains("SNAPSHOT-DONE", StringComparison.Ordinal) && !original.Board.DebugText.Contains("SNAPSHOT-FAILED", StringComparison.Ordinal) && original.Video.ReadText().TrimEnd().EndsWith("A:\\>", StringComparison.Ordinal), "Actual post-AH4C P54C DOS prompt missing.");
        original.Cpu.InstructionStarting -= futureA.ObserveInstruction; resumed.Cpu.InstructionStarting -= futureB.ObserveInstruction;
        Check(JsonSerializer.Serialize(futureA.Entries, StateValues.Json) == JsonSerializer.Serialize(futureB.Entries, StateValues.Json), "Future actual P54C CPU publications differ.");
        original.Audio.Flush(); resumed.Audio.Flush();
        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final full P54C state differs.");
        string audioA = Convert.ToHexString(pcmA.GetHashAndReset()), audioB = Convert.ToHexString(pcmB.GetHashAndReset());
        Check(audioA == audioB && framesA == framesB && framesA > 480, "P54C stereo PCM continuation differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
        var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "P54C final journals differ.");
        Check(original.SoundBlaster16!.TransferredSamples == 512 && resumed.SoundBlaster16!.TransferredSamples == 512, "P54C actual PCM DMA count missing.");
        var finalState = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), finalState); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "ledger.json"), new { programBase = ledger.ProgramBase, beforeCut = ledger.Entries, future = futureA.Entries, restoredFuture = futureB.Entries });
        Write(Path.Combine(root, "result.json"), new { passed = true, mode = "positive", returnedToDos = true, manifest, bootSteps = steps, futureSteps, comparisons, exitA, exitB,
            capturedStateSha256 = Hash(captured), capturedRamSha256 = Hash(rawRam), finalStateSha256 = Hash(finalState), finalRamSha256 = Hash(original.Memory.Ram),
            pcmSha256 = audioA, pcmFrames = framesA, media = mediaA.Media, hostSeconds = timer.Elapsed.TotalSeconds, protectedMode = original.Cpu.ProtectedInstructions,
            v86 = original.Cpu.Virtual8086Instructions, dma = original.SoundBlaster16.TransferredSamples, pendingEvents = original.CaptureHardwareState().Scheduler.Events.Length,
            cpuProofOffset = metadata[0], cpuProofBytes = metadata[1] - metadata[0], programSha256 = Hash(program), programBase = ledger.ProgramBase,
            actualInstructions = ledger.Entries.Count + futureA.Entries.Count, finalScreen = original.Video.ReadText() });
        Console.WriteLine("PASS full P54C DOS continuation " + root); return 0;
    }
    /// <summary>Observes original EMM386's actual #GP frame and full-PC restoration without substituting its handler.</summary>
    private static int Fault(Input input, byte[] program, string root)
    {
        Check(input.FaultInstruction is "rdtsc" or "rdmsr" or "wrmsr", "Unknown bounded fault probe.");
        ushort offset = Metadata(program, true)[0]; var timer = Stopwatch.StartNew(); var boot = Boot(input, timer); using var original = boot.Machine;
        long steps = boot.Steps; byte[] keys = Command(input.GermanKeyboard, "P54CFLT.COM"); int sent = 0;
        while (!original.Board.DebugText.Contains("P54C-FAULT-READY", StringComparison.Ordinal) && steps < 500000000 && original.Fault is null)
        {
            Check(timer.Elapsed.TotalSeconds < 180, "P54C fault setup timeout.");
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            int count = original.RunSteps(100); Check(count > 0, "P54C fault setup stopped."); steps += count;
        }
        Check(sent == keys.Length && original.Fault is null && original.Board.DebugText.Contains("P54C-FAULT-READY", StringComparison.Ordinal), "Actual V86 fault setup missing.");
        StateBlock? before = null; Point? point = null; uint basis = 0;
        // Mark only the actual own opcode; the enclosing RunSteps(1) completes real hardware fault delivery.
        void ObserveFault(uint physical, byte opcode)
        {
            uint start = original.Cpu.GetSegmentBase(Segment.Cs);
            if (opcode != 0x0f || physical != start + offset || start + 0x110 >= original.Memory.Ram.Length || !original.Memory.Ram.AsSpan((int)start + 0x100, 16).SequenceEqual(program.AsSpan(0, 16))) return;
            Check(original.Cpu.Virtual8086, "The negative own instruction must really execute in original EMM386 V86 mode.");
            basis = start; before = original.Cpu.SaveState(); point = Observe(original);
        }
        original.Cpu.InstructionStarting += ObserveFault; byte[] release = GuestKeyboard.Encode("k"); int accepted = 0;
        for (int count = 0; before is null && count < 2000000; count++)
        {
            if (accepted < release.Length && GuestKeyboard.TryInjectForBios(original, release[accepted])) accepted++;
            Check(original.RunSteps(1) == 1 && original.Fault is null, "Actual V86 fault run stopped.");
        }
        original.Cpu.InstructionStarting -= ObserveFault;
        Check(before is not null && point is not null && !original.Cpu.Virtual8086 && original.Cpu.CurrentPrivilegeLevel == 0 && original.Fault is null, "Historical P54C instruction did not enter the original V86 monitor.");
        Check(original.Cpu.GetRegister(Register.Ax) == 0xa55a0ff0 && original.Cpu.GetRegister(Register.Dx) == 0x12345678 && original.Cpu.GetRegister(Register.Cx) == 0x10 && original.Cpu.GetRegister(Register.Bx) == 0xabcddcba, "Fault changed guest operand registers.");
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved); byte[] captured = State(original);
        using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
        Check(captured.SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Actual monitor cut failed full restoration.");
        Write(Path.Combine(root, "before-cpu.json"), before!); Write(Path.Combine(root, "before-point.json"), point!);
        for (int i = 0; i < 8; i++)
        {
            Check(original.RunSteps(1) == resumed.RunSteps(1) && original.Fault is null && resumed.Fault is null, "Original monitor future fault differs.");
            Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Original monitor future differs.");
        }
        File.WriteAllBytes(Path.Combine(root, "final-state.json"), State(original)); File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        Write(Path.Combine(root, "result.json"), new { passed = true, mode = "monitorFault", instruction = input.FaultInstruction, returnedToDos = false,
            comparisons = 8, manifest, programSha256 = Hash(program), programBase = basis, faultOffset = offset,
            capturedStateSha256 = Hash(captured), capturedRamSha256 = manifest.RamSha256, hostSeconds = timer.Elapsed.TotalSeconds,
            limitation = "Bounded actual original-EMM386 #GP frame and eight monitor instructions, not a completed DOS return." });
        Console.WriteLine("PASS full P54C original monitor #GP cut " + input.FaultInstruction + " " + root); return 0;
    }
}
