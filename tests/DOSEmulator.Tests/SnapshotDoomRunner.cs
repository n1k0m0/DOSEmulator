// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Runtime;

/// <summary>Captures the original DOS4GW game while a WAD-derived HUD is visible, then checks the actual remaining execution.</summary>
internal static class SnapshotDoomRunner
{
    private sealed record Pixel(int Index, uint Argb);
    private sealed record Input(string Machine, string Output, string Command, Pixel[] Hud);
    /// <summary>Requires an actual guest observation or exact original/resumed state equality.</summary>
    private static void Check(bool value, string reason) { if (!value) throw new InvalidDataException(reason); }
    /// <summary>Serializes complete private hardware data without executing a guest operation.</summary>
    private static byte[] State(PcMachine m) => JsonSerializer.SerializeToUtf8Bytes(m.CaptureHardwareState(), StateValues.Json);
    /// <summary>Hashes only an explicitly owned buffer.</summary>
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    /// <summary>Saves a real guest-rendered image; no host pixels or injected VRAM are used.</summary>
    private static void Picture(string path, VideoFrame frame)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
        foreach (uint pixel in frame.ArgbPixels) output.Write([(byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel]);
    }
    /// <summary>Runs one original private DOS start and resumes a snapshot of the actual protected-mode game.</summary>
    public static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(path), StateValues.Json)!;
        string root = Path.GetFullPath(input.Output); Directory.CreateDirectory(root); var timer = Stopwatch.StartNew();
        using var original = MachineFactory.FromDefinition(MachineDefinitionStore.Load(input.Machine));
        long boot = 0, total = 0;
        while (!original.Video.ReadText().Contains("VIDEO-PC-READY", StringComparison.Ordinal) && boot < 200000000 && original.Fault is null)
            boot += original.RunSteps(10000);
        Check(boot < 200000000 && original.Fault is null, "Saved video PC did not boot.");
        byte[] keys = GuestKeyboard.Encode(input.Command); int sent = 0; VideoFrame? cut = null;
        while (total < 500000000 && original.Fault is null && timer.Elapsed.TotalSeconds < 180)
        {
            if (sent < keys.Length && GuestKeyboard.TryInjectForBios(original, keys[sent])) sent++;
            total += original.RunSteps(10000);
            if (sent < keys.Length || !original.Video.GraphicsMode || !original.Cpu.ProtectedMode) continue;
            var frame = original.CaptureFrame();
            if (frame.Width == 320 && frame.Height == 200 && input.Hud.Length > 3000 && input.Hud.All(p => frame.ArgbPixels[p.Index] == p.Argb))
            { cut = frame; break; }
        }
        Check(cut is not null && original.Fault is null, "No actual WAD-verified protected-mode game cut.");
        Picture(Path.Combine(root, "cut.ppm"), cut!);
        var manifest = SnapshotStore.Capture(original, Path.Combine(root, "game.dossav"));
        byte[] captured = State(original); bool activeDma = original.SoundBlaster16?.RemainingSamples > 0;
        using var resumed = SnapshotStore.Load(Path.Combine(root, "game.dossav"), Path.Combine(root, "resumed.dosebranch"));
        Check(captured.SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Initial protected-mode snapshot differs.");
        using var pcmA = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var pcmB = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long framesA = 0, framesB = 0;
        original.Audio.AudioReady += b => { pcmA.AppendData(MemoryMarshal.AsBytes(b.Samples.AsSpan())); framesA += b.Samples.Length / 2; };
        resumed.Audio.AudioReady += b => { pcmB.AppendData(MemoryMarshal.AsBytes(b.Samples.AsSpan())); framesB += b.Samples.Length / 2; };
        int slices = 0, comparisons = 0, pictures = 0; long future = 0;
        while (future < 800000000 && timer.Elapsed.TotalSeconds < 420)
        {
            int a = original.RunSteps(200000), b = resumed.RunSteps(200000); future += a; slices++;
            Check(a == b && original.Fault is null && resumed.Fault is null, "Game CPU continuation diverged.");
            if (slices % 10 == 0)
            {
                Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Entire game state differs at slice " + slices); comparisons++;
                if (original.Video.GraphicsMode && pictures < 8)
                {
                    var fa = original.CaptureFrame(); var fb = resumed.CaptureFrame();
                    Check(fa.Width == fb.Width && fa.Height == fb.Height && fa.ArgbPixels.SequenceEqual(fb.ArgbPixels), "Actual game pixels differ.");
                    Picture(Path.Combine(root, "future-" + pictures++ + ".ppm"), fa);
                }
            }
            if (!original.Video.GraphicsMode && original.Video.ReadText().Contains("timed 32 gametics", StringComparison.Ordinal) && original.Video.ReadText().Contains("C:\\GAMES\\DOOM2>", StringComparison.Ordinal)) break;
        }
        Check(original.Video.ReadText().Contains("timed 32 gametics", StringComparison.Ordinal) && original.Video.ReadText().Contains("C:\\GAMES\\DOOM2>", StringComparison.Ordinal) && !resumed.Video.GraphicsMode, "Actual 32-tick game did not return to DOS.");
        original.RunSteps(30000); resumed.RunSteps(30000); original.Audio.Flush(); resumed.Audio.Flush();
        Check(State(original).SequenceEqual(State(resumed)) && original.Memory.Ram.SequenceEqual(resumed.Memory.Ram), "Final DOS state differs.");
        string pcm = Convert.ToHexString(pcmA.GetHashAndReset());
        Check(pcm == Convert.ToHexString(pcmB.GetHashAndReset()) && framesA == framesB, "Actual game PCM differs.");
        var mediaA = MediaCheckpointStore.Capture(original, Path.Combine(root, "final-a.dosemedia"));
        var mediaB = MediaCheckpointStore.Capture(resumed, Path.Combine(root, "final-b.dosemedia"));
        Check(JsonSerializer.Serialize(mediaA.Media, StateValues.Json) == JsonSerializer.Serialize(mediaB.Media, StateValues.Json), "Game media generations differ.");
        var final = State(original); File.WriteAllBytes(Path.Combine(root, "final-state.json"), final);
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, manifest, boot, total, future, slices, comparisons, pictures,
            activeDmaAtCut = activeDma, cutProtectedMode = true, staticHudPixels = input.Hud.Length, pcmFrames = framesA, pcmSha256 = pcm,
            capturedStateSha256 = Hash(captured), finalStateSha256 = Hash(final), finalRamSha256 = Hash(original.Memory.Ram),
            original.Cpu.ProtectedInstructions, original.Cpu.PageFaults, original.Cpu.X87Instructions, original.SoundBlaster16?.TransferredSamples,
            returnedToDos = true, media = mediaA.Media, seconds = timer.Elapsed.TotalSeconds }, new JsonSerializerOptions(StateValues.Json) { IgnoreReadOnlyProperties = false, WriteIndented = true }));
        Console.WriteLine("PASS protected-mode game snapshot " + root); return 0;
    }
}
