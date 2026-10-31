// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;
using DOSEmulator.Whpx;

/// <summary>Explicit offline native-CPU runner; existing launchers and deterministic software snapshots retain their own engine.</summary>
internal static class WhpxHeadless
{
    /// <summary>Runs one bounded host-monotonic window and reports native registers without presenting a clock ratio as verified workload throughput.</summary>
    internal static int Run(string[] args, Func<string, string?> get)
    {
        try
        {
            string[] unsupported = ["--steps", "--load-snapshot", "--capture-snapshot", "--trace", "--trace-tail", "--type-file", "--type-after", "--stop-text", "--swap-floppy", "--enable-network", "--reconnect-network"];
            if (unsupported.Any(args.Contains)) throw new ArgumentException("This explicit WHPX runner accepts a bounded --whpx-seconds window, native snapshots and offline media; software steps, scripts and networking require their existing runner.");
            double seconds = double.Parse(get("--whpx-seconds") ?? "10", CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds) || seconds < 0.01 || seconds > 3600) throw new ArgumentOutOfRangeException("--whpx-seconds", "Duration must be 0.01..3600 seconds.");
            string? source = get("--load-whpx-snapshot");
            if (source is not null && new[] { "--machine", "--profile", "--disk", "--overlay", "--cdrom", "--floppy-a", "--floppy-b" }.Any(o => get(o) is not null))
                throw new ArgumentException("A native snapshot supplies its complete hardware and media.");
            using var loaded = source is null ? null : WhpxSnapshotStore.Load(source,
                get("--snapshot-branch") ?? throw new ArgumentException("--load-whpx-snapshot requires a new --snapshot-branch directory."));
            if (source is null && get("--snapshot-branch") is not null) throw new ArgumentException("--snapshot-branch requires --load-whpx-snapshot in this runner.");
            using var pc = loaded?.Pc ?? MachineLaunch.Open(get);
            if (!pc.Profile.IsP54c) throw new InvalidOperationException("WHPX full-PC execution currently requires the P54C profile. The 486 flag/CPUID admission remains on the software engine.");
            using var started = loaded is null ? WhpxPcSession.Start(pc) : null;
            var session = loaded ?? started!;
            var frameBuffer = new VgaFrameBuffer(); long frames = 0, audioFrames = 0;
            using var images = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var audio = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Validates and hashes actual published PCM in RAM without opening a host output endpoint.
            void Audio(AudioBlock block)
            {
                block.Validate(); audioFrames += block.Samples.Length / block.Channels;
                audio.AppendData(MemoryMarshal.AsBytes(block.Samples.AsSpan()));
            }
            pc.Audio.AudioReady += Audio;
            WhpxRealtimeRunResult run;
            try
            {
                run = session.RunRealtime(TimeSpan.FromSeconds(seconds), _ =>
                {
                    var frame = pc.CaptureFrame(frameBuffer); frames++;
                    images.AppendData(MemoryMarshal.AsBytes(frame.ArgbPixels.AsSpan()));
                }, busWritesOnly: true);
            }
            finally { pc.Audio.AudioReady -= Audio; }
            var lastFrame = pc.CaptureFrame(frameBuffer);
            if (get("--frame") is { } framePath) WriteFrame(framePath, lastFrame);
            if (get("--capture-whpx-snapshot") is { } snapshot) WhpxSnapshotStore.Capture(session, snapshot);
            if (get("--capture-media") is { } media) MediaCheckpointStore.Capture(pc, media);
            var cpu = session.CaptureCpuState();
            var report = new
            {
                engine = "whpx", clockPolicy = cpu.ClockPolicy, profile = pc.Profile,
                run, retiredInstructions = (long?)null, workloadThroughputVerified = false,
                cpu, pc.Board.Post10Count, pc.HardwareResetCount, text = pc.Video.ReadText(),
                frame = new { lastFrame.Width, lastFrame.Height, lastFrame.VirtualNanoseconds, capturedFrames = frames, sequenceSha256 = Convert.ToHexString(images.GetHashAndReset()) },
                audio = new { publishedFrames = audioFrames, sha256 = Convert.ToHexString(audio.GetHashAndReset()), hostEndpointEnabled = false },
                scope = "Clock-follow percentage measures device time against active host time. Game/video throughput requires separate guest progress oracles; the nominal P54C TSC is not a measured hardware instruction retirement rate."
            };
            string output = JsonSerializer.Serialize(report, IpcProtocol.JsonOptions);
            if (get("--report") is { } path) File.WriteAllText(path, output);
            Console.WriteLine(output); return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("WHPX: " + error.Message); return 1; }
    }

    /// <summary>Writes one explicitly requested still image; no host window, GL context or video recording is created.</summary>
    private static void WriteFrame(string path, VideoFrame frame)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{frame.Width} {frame.Height}\n255\n"));
        byte[] row = new byte[frame.Width * 3];
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                uint pixel = frame.ArgbPixels[y * frame.Width + x];
                row[x * 3] = (byte)(pixel >> 16); row[x * 3 + 1] = (byte)(pixel >> 8); row[x * 3 + 2] = (byte)pixel;
            }
            file.Write(row);
        }
    }
}
