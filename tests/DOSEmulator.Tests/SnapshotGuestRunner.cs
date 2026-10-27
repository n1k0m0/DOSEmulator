// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Original-versus-restored execution oracle using actual DOS, private media and guest instruction boundaries.</summary>
internal static class SnapshotGuestRunner
{
    private sealed record Input(string Machine, string Output, bool GermanKeyboard);
    /// <summary>Requires the guest or independently compared continuation to satisfy its exact contract.</summary>
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    /// <summary>Serializes all explicit machine fields including descriptors, raw FPU and scheduler conversion remainders.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes an owned byte sequence without converting any floating-point value.</summary>
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    /// <summary>Runs a bounded original DOS start, captures once, then compares actual resumed execution and output byte for byte.</summary>
    public static int Run(string config)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(config), StateValues.Json)!;
        string root = Path.GetFullPath(input.Output); Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        original.ConnectSerialEcho(); original.Parallel!.ConnectPrinter();
        original.Gameport!.SetInput(new() { AxOhms = 100000, AyOhms = 17000, BxOhms = 0, Buttons = 5 });
        long steps = 0;
        // Bound both CPU progress and wall time so a broken guest oracle cannot loop forever.
        int Advance(int maximum)
        {
            if (timer.Elapsed.TotalSeconds > 120) throw new InvalidDataException("Guest timeout: " + original.GetStatus() + " SCREEN=" + original.Video.ReadText());
            int count = original.RunSteps(maximum);
            if (count == 0) throw new InvalidDataException("Guest stopped at " + original.Cpu.Position + " SCREEN=" + original.Video.ReadText());
            return count;
        }
        while (!original.Video.ReadText().Contains("C:\\>", StringComparison.Ordinal) && steps < 300000000 && original.Fault is null)
            steps += Advance(10000);
        Check(original.Fault is null && steps < 300000000, "DOS boot did not reach the real C prompt: " + original.Fault);
        Console.WriteLine("DOS boot ready " + steps + " steps / " + timer.Elapsed.TotalSeconds + " seconds");
        byte[] keys = input.GermanKeyboard
            ? GuestKeyboard.Encode("A").Concat(new byte[] { 0x2a, 0x34, 0xb4, 0xaa }).Concat(GuestKeyboard.Encode("\rSNAPTEST.COM\r")).ToArray()
            : GuestKeyboard.Encode("A:\rSNAPTEST.COM\r"); int sent = 0;
        while (!original.Board.DebugText.Contains("SNAPSHOT-CUT", StringComparison.Ordinal) && steps < 500000000 && original.Fault is null)
        {
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            steps += Advance(100);
        }
        Check(original.Fault is null && sent == keys.Length && original.Board.DebugText.Contains("SNAPSHOT-CUT", StringComparison.Ordinal), "Guest did not reach snapshot cut: " + original.Board.DebugText[^Math.Min(160, original.Board.DebugText.Length)..]);
        Console.WriteLine("Guest snapshot cut reached " + steps);
        string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved);
        byte[] captured = State(original), rawRam = original.Memory.Ram.ToArray();
        using var resumed = SnapshotStore.Load(saved, Path.Combine(root, "resumed.dosebranch"));
        Check(captured.SequenceEqual(State(resumed)) && rawRam.SequenceEqual(resumed.Memory.Ram), "Initial full state differs.");
        bool exitA = false, exitB = false;
        // Observe the original guest's decoded DOS termination request, including V86 reflection.
        void ObserveA(uint physical, byte opcode) { if (opcode == 0xcd && original.Memory.ReadByte(physical + 1) == 0x21 && (original.Cpu.GetRegister(Register.Ax) >> 8) == 0x4c) exitA = true; }
        // Observe the resumed guest through the same instruction path and fallback policy.
        void ObserveB(uint physical, byte opcode) { if (opcode == 0xcd && resumed.Memory.ReadByte(physical + 1) == 0x21 && (resumed.Cpu.GetRegister(Register.Ax) >> 8) == 0x4c) exitB = true; }
        original.Cpu.InstructionStarting += ObserveA; resumed.Cpu.InstructionStarting += ObserveB;
        using var pcmA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var pcmB = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long framesA = 0, framesB = 0;
        original.Audio.AudioReady += block => { pcmA.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesA += block.Samples.Length / 2; };
        resumed.Audio.AudioReady += block => { pcmB.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan())); framesB += block.Samples.Length / 2; };
        byte[] resumeKeys = GuestKeyboard.Encode("k"); int accepted = 0, comparisons = 0; long futureSteps = 0;
        while (!(exitA && exitB) && futureSteps < 50000000)
        {
            if (accepted < resumeKeys.Length)
            {
                bool a = GuestKeyboard.TryInjectForBios(original, resumeKeys[accepted]), b = GuestKeyboard.TryInjectForBios(resumed, resumeKeys[accepted]);
                Check(a == b, "KBC acceptance differs."); if (a) accepted++;
            }
            int countA = original.RunSteps(10000), countB = resumed.RunSteps(10000); futureSteps += countA;
            Check(countA == countB && original.Fault is null && resumed.Fault is null, "CPU continuation count/fault differs.");
            Check(State(original).SequenceEqual(State(resumed)), "CPU/device/scheduler continuation differs at slice " + comparisons);
            Check(original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Entire RAM differs at slice " + comparisons); comparisons++;
        }
        Check(exitA && exitB && original.Board.DebugText.Contains("SNAPSHOT-DONE", StringComparison.Ordinal) && !original.Board.DebugText.Contains("SNAPSHOT-FAILED", StringComparison.Ordinal), "Actual DOS AH4C return missing.");
        original.RunSteps(30000); resumed.RunSteps(30000); original.Audio.Flush(); resumed.Audio.Flush();
        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final state differs.");
        string audioA = Convert.ToHexString(pcmA.GetHashAndReset()), audioB = Convert.ToHexString(pcmB.GetHashAndReset());
        Check(audioA == audioB && framesA == framesB && framesA > 480, "Stereo PCM continuation differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
        var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "Final sealed journal bytes differ.");
        var media = mediaA.Media;
        Check(original.SoundBlaster16!.TransferredSamples == 512 && resumed.SoundBlaster16!.TransferredSamples == 512, "Actual PCM DMA count missing.");
        var finalState = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), finalState);
        File.WriteAllBytes(Path.Combine(root, "final-ram.bin"), original.Memory.Ram);
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, manifest, bootSteps = steps, futureSteps, comparisons, exitA, exitB,
            capturedStateSha256 = Hash(captured), capturedRamSha256 = Hash(rawRam), finalStateSha256 = Hash(finalState), finalRamSha256 = Hash(original.Memory.Ram),
            pcmSha256 = audioA, pcmFrames = framesA, media, hostSeconds = timer.Elapsed.TotalSeconds, protectedMode = original.Cpu.ProtectedInstructions,
            v86 = original.Cpu.Virtual8086Instructions, dma = original.SoundBlaster16.TransferredSamples,
            pendingEvents = original.CaptureHardwareState().Scheduler.Events.Length }, new JsonSerializerOptions(StateValues.Json) { WriteIndented = true, IgnoreReadOnlyProperties = false }));
        Console.WriteLine("PASS actual DOS snapshot continuation " + root); return 0;
    }
}
