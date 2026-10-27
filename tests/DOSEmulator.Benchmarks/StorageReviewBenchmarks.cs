// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Fixed-work allocation/time probes for independently identified host-side overhead.</summary>
internal static class StorageReviewBenchmarks
{
    /// <summary>Measures setup-free runs with deterministic checksums; these are host microbenchmarks, not guest speed claims.</summary>
    public static void Run(int repeats, string directory)
    {
        Directory.CreateDirectory(directory); var rows = new List<object>(); long checksum = 0;
        // Measures the explicitly delimited phase without including fixture construction in its timing window.
        void Measure(string name, int iterations, Func<Action<int>> setup)
        {
            setup()(Math.Min(iterations, 1000));
            var times = new List<double>(); var allocations = new List<long>(); var sums = new List<long>();
            for (int r = 0; r < repeats; r++)
            {
                var run = setup(); GC.Collect(); GC.WaitForPendingFinalizers();
                long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp(); run(iterations);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated); sums.Add(checksum);
            }
            if (sums.Distinct().Count() != 1) throw new InvalidOperationException("Unstable benchmark checksum: " + name);
            rows.Add(new { name, iterations, milliseconds = times, medianMilliseconds = times.Order().ElementAt(times.Count / 2), allocatedBytes = allocations, checksum });
        }
        Measure("unchanged-debug-text", 10000, () =>
        {
            var board = new DevelopmentBoardPorts(new MemoryBus(1048576));
            for (int i = 0; i < 8192; i++) board.Write(0xe9, 0x41, 1);
            return n => { long sum = 0; for (int i = 0; i < n; i++) sum += board.DebugText.Length + board.DebugText[0]; checksum = sum; };
        });
        Measure("audio-ring-960-components", 30000, () =>
        {
            var ring = new AudioRingBuffer(48000, 2, 12000); float[] samples = Enumerable.Range(0, 960).Select(i => (i % 31) / 32f).ToArray();
            var block = new AudioBlock(0, 48000, 2, samples); float[] output = new float[960];
            return n => { for (int i = 0; i < n; i++) { ring.Write(block); ring.Read(output); } checksum = BitConverter.SingleToInt32Bits(output[959]) + ring.SilentFrames + ring.DroppedFrames; };
        });
        Measure("frame-buffer-reader-leases", 500000, () =>
        {
            var exchange = new LatestBufferExchange<int[]>(() => new int[1]); exchange.TryPublish(a => a[0] = 123);
            return n => { long sum = 0; for (int i = 0; i < n; i++) { using var lease = exchange.AcquireLatest(); sum += lease!.Value[0]; } checksum = sum; };
        });
        string raw = Path.Combine(directory, "readonly-" + Guid.NewGuid().ToString("N") + ".img"); File.WriteAllBytes(raw, new byte[1048576]);
        Measure("readonly-sector-count", 500000, () =>
        {
            var disk = new ReadOnlyRawDisk(raw);
            return n => { long sum = 0; for (int i = 0; i < n; i++) sum += disk.SectorCount; checksum = sum; disk.Dispose(); };
        });
        Measure("pcm-wave-block-recording", 4000, () =>
        {
            var recorder = new PcmWaveRecorder(Path.Combine(directory, "recording-" + Guid.NewGuid().ToString("N") + ".wav"));
            var block = new AudioBlock(0, 48000, 2, Enumerable.Repeat(.25f, 960).ToArray());
            return n => { for (int i = 0; i < n; i++) recorder.Write(block); recorder.Complete(); checksum = recorder.Frames; recorder.Dispose(); };
        });
        Console.WriteLine(JsonSerializer.Serialize(new { repeats, method = "fixed host work; setup and warmup excluded; no guest timing changes", rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
