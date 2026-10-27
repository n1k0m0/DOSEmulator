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

/// <summary>Actual sustained DMA/OPL3/MPU execution, external framed input and file-backed full-state continuation.</summary>
internal static class CombinedAudioGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard, int ProofOffset, int Card, int Irq, int Mpu);
    private static readonly JsonSerializerOptions Json = new(StateValues.Json) { IgnoreReadOnlyProperties = false, WriteIndented = false };

    private sealed class Trace : IDisposable
    {
        private readonly StreamWriter _events, _io;
        private readonly FileStream _pcm, _quad;
        private (ushort Port, uint Value, int Width, bool Write)? _last;
        private ulong _first, _end;
        private long _count;
        public long AudioFrames, NativeFrames;
        /// <summary>Installs passive source observers only; input remains an explicit separate owner action.</summary>
        public Trace(PcMachine m, string directory, int card, int irq, int mpu)
        {
            Directory.CreateDirectory(directory);
            _events = new(new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.CreateNew, FileAccess.Write));
            _io = new(new FileStream(Path.Combine(directory, "io.jsonl"), FileMode.CreateNew, FileAccess.Write));
            _pcm = new(Path.Combine(directory, "audio.f32"), FileMode.CreateNew, FileAccess.Write);
            _quad = new(Path.Combine(directory, "opl3.s16"), FileMode.CreateNew, FileAccess.Write);
            m.Ports.Accessed += (p, v, w, write) =>
            {
                if (p is >= 0x20 and <= 0x21 || p is >= 0xa0 and <= 0xa1 || p < 16 || p is >= 0x80 and <= 0x8f ||
                    p is >= 0xc0 and <= 0xdf || p is 0xe9 or 0x61 || p >= card && p <= card + 15 || p >= mpu && p <= mpu + 1)
                    Port(m.Scheduler.Now, p, v, w, write, !write && (p == card + 12 || p == mpu + 1));
            };
            m.SoundBlaster16!.TransferStarted += v => Event("transfer", v);
            m.SoundBlaster16.SampleTransferred += v => Event("sample", v);
            m.SoundBlaster16.BlockCompleted += (time, word) => Event("block", new { time, word });
            m.Midi!.Transmitted += v => Event("midi-out", v);
            m.Midi.Received += v => Event("midi-in", v);
            m.Midi.InputQueued += (time, data) => Event("outer", new { time, data });
            m.Pic.InterruptAcknowledged += (line, vector, spurious) => { if (line == irq) Event("irq", new { time = m.Scheduler.Now, line, vector, spurious }); };
            m.Opl3!.SampleGenerated += v =>
            {
                Span<byte> raw = stackalloc byte[8];
                BinaryPrimitives.WriteInt16LittleEndian(raw, v.A); BinaryPrimitives.WriteInt16LittleEndian(raw[2..], v.B);
                BinaryPrimitives.WriteInt16LittleEndian(raw[4..], v.C); BinaryPrimitives.WriteInt16LittleEndian(raw[6..], v.D);
                _quad.Write(raw); NativeFrames++;
            };
            m.Audio.AudioReady += b =>
            {
                Event("pcm", new { time = b.VirtualNanoseconds, frames = b.Samples.Length / 2 });
                _pcm.Write(MemoryMarshal.AsBytes(b.Samples.AsSpan())); AudioFrames += b.Samples.Length / 2;
            };
        }
        /// <summary>Stores one passive typed event at the actual device publication boundary.</summary>
        private void Event<T>(string kind, T value) => _events.WriteLine(JsonSerializer.Serialize(new { kind, value }, Json));
        /// <summary>Run-length encodes only side-effect-free ready/busy polls; acknowledgments and data bytes remain distinct.</summary>
        private void Port(ulong time, ushort port, uint value, int width, bool write, bool compress)
        {
            var key = (port, value, width, write);
            if (compress && _last == key) { _end = time; _count++; return; }
            FlushPort(); _last = key; _first = _end = time; _count = 1;
            if (!compress) FlushPort();
        }
        /// <summary>Finishes one exact poll run with its original first/last virtual observation times.</summary>
        private void FlushPort()
        {
            if (_last is not { } row) return;
            _io.WriteLine(JsonSerializer.Serialize(new { first = _first, last = _end, count = _count, port = row.Port, value = row.Value, width = row.Width, write = row.Write }, Json));
            _last = null;
        }
        /// <summary>Flushes passive output files after execution; never invokes a guest controller or host endpoint.</summary>
        public void Dispose() { FlushPort(); _events.Dispose(); _io.Dispose(); _pcm.Dispose(); _quad.Dispose(); }
    }

    private sealed class Future : IDisposable
    {
        public readonly IncrementalHash Audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public readonly IncrementalHash Signals = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Frames;
        /// <summary>Hashes actual future publication boundaries, preserving time, shape and raw sample bytes.</summary>
        public Future(PcMachine m)
        {
            m.Audio.AudioReady += b =>
            {
                Span<byte> header = stackalloc byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(header, b.VirtualNanoseconds);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], b.Samples.Length); Audio.AppendData(header);
                Audio.AppendData(MemoryMarshal.AsBytes(b.Samples.AsSpan())); Frames += b.Samples.Length / 2;
            };
            m.SoundBlaster16!.SampleTransferred += v => Signal("sample", v);
            m.SoundBlaster16.BlockCompleted += (time, word) => Signal("block", new { time, word });
            m.Opl3!.SampleGenerated += v => Signal("quad", v);
            m.Midi!.Transmitted += v => Signal("out", v);
            m.Midi.Received += v => Signal("in", v);
        }
        /// <summary>Hashes typed callback data without adding callbacks to the device scheduler.</summary>
        private void Signal<T>(string kind, T value) => Signals.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { kind, value }, Json));
        /// <summary>Releases only local incremental digest resources.</summary>
        public void Dispose() { Audio.Dispose(); Signals.Dispose(); }
    }

    /// <summary>Requires independently observed guest progress or exact original/restored equality.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    /// <summary>Encodes every explicit private state block and scheduler event at the current owner boundary.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes one explicitly owned buffer, retaining raw float and extended-register bit patterns.</summary>
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    /// <summary>Requires the shell's actual later prompt, rather than treating the guest's pre-INT marker as DOS completion.</summary>
    private static bool Returned(PcMachine? machine)
    {
        if (machine is null) return false;
        string text = machine.Board.DebugText;
        int done = text.LastIndexOf("COMB-DONE", StringComparison.Ordinal);
        return done >= 0 && text.LastIndexOf("A:\\>", StringComparison.Ordinal) > done;
    }
    /// <summary>Copies only this newly created private snapshot to exercise a deliberately invalid import beside a live workload.</summary>
    private static void CopyPrivateSnapshot(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), false);
    }
    /// <summary>Runs one real DOS start and a sustained original-versus-restored workload on new private media leases.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), Json)!;
        string root = Path.GetFullPath(input.Output); Directory.CreateDirectory(root); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        using var trace = new Trace(original, Path.Combine(root, "trace"), input.Card, input.Irq, input.Mpu);
        ulong? longStart = null; bool residentA = false, residentB = false; uint proofAddress = 0;
        original.SoundBlaster16!.TransferStarted += t => { if (t.AutoInit) longStart = t.Time; };
        // Observe decoded TSR termination, including EMM386's reflected V86 interrupt path.
        void TerminateA(uint physical, byte opcode)
        {
            if (opcode == 0xcd && original.Memory.ReadByte(physical + 1) == 0x21 && ((original.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x31)
            { residentA = true; proofAddress = ((uint)original.Cpu.GetSegment(Segment.Ds) << 4) + 0x100u + (uint)input.ProofOffset; }
        }
        original.Cpu.InstructionStarting += TerminateA;
        long steps = 0;
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && original.Fault is null && timer.Elapsed.TotalSeconds < 90)
            steps += original.RunSteps(10000);
        Check(original.Fault is null && timer.Elapsed.TotalSeconds < 90, "DOS boot failed.");
        residentA = false; proofAddress = 0; // Boot-time DOS driver TSRs are outside this own caller.
        byte[] keys = input.GermanKeyboard ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rCOMBINED.COM\r")).ToArray()
            : GuestKeyboard.Encode("A:\rCOMBINED.COM\r");
        int sent = 0, packets = 0, comparisons = 0, rejectedImports = 0; bool joint = false;
        ulong lastCompare = 0; SnapshotManifest? manifest = null;
        PcMachine? resumed = null; Future? futureA = null, futureB = null; byte[]? captured = null;
        try
        {
            while (!(residentA && residentB && Returned(original) && Returned(resumed)) && timer.Elapsed.TotalSeconds < 600)
            {
                Check(original.Fault is null && resumed?.Fault is null, "Guest fault.");
                Check(!original.Board.DebugText.Contains("COMB-FAILED", StringComparison.Ordinal), "Combined guest failed: " + original.Cpu.Position);
                if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
                if (!joint && original.Board.DebugText.Contains("COMB-JOINT", StringComparison.Ordinal))
                { original.Midi!.QueueReceive(new MidiInput([0x69])); joint = true; }
                if (longStart is { } start && packets < 512 && original.Scheduler.Now >= start + (ulong)packets * 32000000)
                {
                    var packet = new MidiInput([0x80, packets & 127, (packets * 29 + 7) & 127]);
                    original.Midi!.QueueReceive(packet); resumed?.Midi!.QueueReceive(packet); packets++;
                    if (packets == 171 && resumed is null)
                    {
                        manifest = SnapshotStore.Capture(original, Path.Combine(root, "active.dossav")); captured = State(original);
                        string ramHash = Hash(original.Memory.Ram), invalid = Path.Combine(root, "invalid.dossav");
                        string rejectedBranch = Path.Combine(root, "rejected.dosebranch");
                        CopyPrivateSnapshot(Path.Combine(root, "active.dossav"), invalid);
                        File.WriteAllText(Path.Combine(invalid, "snapshot.json"), JsonSerializer.Serialize(manifest with { StateSha256 = new string('0', 64) }, StateValues.Json));
                        try { using var unexpected = SnapshotStore.Load(invalid, rejectedBranch); }
                        catch (InvalidDataException) { rejectedImports++; }
                        Check(rejectedImports == 1 && !Directory.Exists(rejectedBranch) && captured.SequenceEqual(State(original)) && ramHash == Hash(original.Memory.Ram),
                            "Rejected import changed the live combined workload or created a branch.");
                        resumed = SnapshotStore.Load(Path.Combine(root, "active.dossav"), Path.Combine(root, "resumed.dosebranch"));
                        Check(captured.SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Initial combined state differs.");
                        // Install exactly the same CPU observer policy on the restored instruction path.
                        resumed.Cpu.InstructionStarting += (physical, opcode) =>
                        { if (opcode == 0xcd && resumed.Memory.ReadByte(physical + 1) == 0x21 && ((resumed.Cpu.GetRegister(Register.Ax) >> 8) & 255) == 0x31) residentB = true; };
                        futureA = new(original); futureB = new(resumed); lastCompare = original.Scheduler.Now;
                        Console.WriteLine("Captured actual combined DMA/FM/MIDI at " + lastCompare);
                    }
                }
                ulong deadline = original.Scheduler.Now + 1000000;
                if (longStart is { } begin && packets < 512) deadline = Math.Min(deadline, begin + (ulong)packets * 32000000);
                int a = original.RunSteps(10000, deadline); steps += a;
                if (resumed is not null)
                {
                    int b = resumed.RunSteps(10000, deadline); Check(a == b, "Instruction-element count differs.");
                    if (original.Scheduler.Now >= lastCompare + 50000000)
                    {
                        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Combined hardware future differs.");
                        lastCompare = original.Scheduler.Now; comparisons++;
                    }
                }
            }
            Check(residentA && residentB && Returned(original) && Returned(resumed), "Combined probe did not reach real DOS TSR return: " + original.Cpu.Position);
            original.RunSteps(30000); resumed!.RunSteps(30000); original.Audio.Flush(); resumed.Audio.Flush();
            Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final combined state differs.");
            string audioA = Convert.ToHexString(futureA!.Audio.GetHashAndReset()), audioB = Convert.ToHexString(futureB!.Audio.GetHashAndReset());
            string signalsA = Convert.ToHexString(futureA.Signals.GetHashAndReset()), signalsB = Convert.ToHexString(futureB.Signals.GetHashAndReset());
            Check(audioA == audioB && signalsA == signalsB && futureA.Frames == futureB.Frames, "Continued PCM/FM/MIDI publication differs.");
            var sb = original.SoundBlaster16!;
            Check(sb.SoftwareIrq8Requests == 0 && sb.SoftwareIrq16Requests == 0 && sb.CompletedBlocks == 514 &&
                sb.Dma8Grants == 16 && sb.Dma16Grants == 262160 && original.Midi!.ReceivedBytes == 1537 && original.Midi.Overruns == 0 && original.Midi.TransmittedBytes == 1536,
                "Actual DMA/block/frame totals differ.");
            var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
            var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
            Check(JsonSerializer.Serialize(mediaA.Media, Json) == JsonSerializer.Serialize(mediaB.Media, Json), "Continued media generations differ.");
            byte[] final = State(original); File.WriteAllBytes(Path.Combine(root, "final-b-state.json"), State(resumed));
            File.WriteAllBytes(Path.Combine(root, "final-b-ram.bin"), resumed.Memory.Ram);
            File.WriteAllBytes(Path.Combine(root, "final-state.json"), final);
            File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, manifest, longStart, packets, comparisons, rejectedImports, steps, proofAddress,
                futureFrames = futureA.Frames, futureAudioSha256 = audioA, restoredAudioSha256 = audioB, futureSignalsSha256 = signalsA, restoredSignalsSha256 = signalsB, capturedStateSha256 = Hash(captured!),
                finalStateSha256 = Hash(final), finalRamSha256 = Hash(original.Memory.Ram), text = original.Video.ReadText(), debug = original.Board.DebugText, pcmFrames = trace.AudioFrames, nativeFrames = trace.NativeFrames,
                original.Cpu.ProtectedInstructions, original.Cpu.Virtual8086Instructions, sb.Dma8Grants, sb.Dma16Grants, sb.CompletedBlocks,
                sb.Irq8Requests, sb.Irq16Requests, sb.SoftwareIrq8Requests, sb.SoftwareIrq16Requests,
                hostSeconds = timer.Elapsed.TotalSeconds }, new JsonSerializerOptions(Json) { WriteIndented = true }));
            Console.WriteLine("PASS sustained combined audio/MIDI snapshot " + root); return 0;
        }
        finally { futureA?.Dispose(); futureB?.Dispose(); resumed?.Dispose(); }
    }
}
