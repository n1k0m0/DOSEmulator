// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Bounded continuation of one actual running PC against its one complete, normally validated snapshot import.</summary>
internal static class Windows95Continuation
{
    private const int RequiredRamBytes = 32 * 1024 * 1024, MaximumAudioBlocks = 2048;
    private static readonly JsonSerializerOptions Json = new(IpcProtocol.JsonOptions) { WriteIndented = true, IncludeFields = true };
    private sealed record PcmBlock(ulong VirtualNanoseconds, int SampleRate, int Channels, int Frames, int Bytes, string Sha256);
    private sealed record Scanout(int Width, int Height, int ModeGeneration, long FrameId, ulong VirtualNanoseconds,
        int Pixels, string PixelsSha256, bool Graphics, string Text);
    private sealed record Comparison(string Stage, ulong VirtualNanoseconds, long OriginalInstructions, long RestoredInstructions,
        int StateBytes, string StateSha256, int RamBytes, string RamSha256, Scanout Scanout);
    private sealed record Slice(int Index, int OriginalStepElements, int RestoredStepElements, Comparison Comparison);
    private sealed record SealedMedia(string Directory, string Id, string ManifestSha256, string ComparedMetadataSha256, MediaGeneration[] Media);

    /// <summary>Rejects a failed comparison without changing either PC's architecture or rewriting its state.</summary>
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    /// <summary>Hashes the complete supplied byte sequence, including raw sample encodings.</summary>
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Hashes an actual already-published private artifact through a protected read handle.</summary>
    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>Writes one immutable evidence file; an existing destination is always an error.</summary>
    private static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes); file.Flush(true);
    }

    /// <summary>Writes a new structured report without reusing the stricter hardware-state serialization options.</summary>
    private static void WriteJson(string path, object value) => WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    /// <summary>Serializes the complete explicit hardware/scheduler graph with the identical snapshot contract.</summary>
    private static byte[] State(PcMachine machine) => JsonSerializer.SerializeToUtf8Bytes(machine.CaptureHardwareState(), StateValues.Json);

    /// <summary>Finds the first differing byte to make a failure diagnostic precise without retaining another complete dump.</summary>
    private static int FirstDifference(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        int common = Math.Min(first.Length, second.Length);
        for (int i = 0; i < common; i++) if (first[i] != second[i]) return i;
        return first.Length == second.Length ? -1 : common;
    }

    /// <summary>Requires literal byte identity, without dropping CPU diagnostics, caches or scheduler entries.</summary>
    private static void SameBytes(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, string what)
    {
        if (!first.SequenceEqual(second)) throw new InvalidDataException(what + " differs at byte " + FirstDifference(first, second) +
            "; original=" + Hash(first) + "; restored=" + Hash(second));
    }

    /// <summary>Rejects identical failures as successful continuation rather than accepting two equally faulted PCs.</summary>
    private static void Healthy(PcMachine machine, string side) => Require(machine.Fault is null && !machine.Cpu.TripleFaulted,
        side + " has a guest fault or triple fault: " + machine.Fault + " at " + machine.Cpu.Position);

    /// <summary>Records real completed mixer blocks without retaining samples, opening audio output or flushing an incomplete block.</summary>
    private sealed class PcmObserver : IDisposable
    {
        private readonly PcMachine _machine;
        private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly List<PcmBlock> _blocks = [];
        /// <summary>Actual finite PCM block metadata in publication order.</summary>
        public IReadOnlyList<PcmBlock> Blocks => _blocks;
        /// <summary>Actual interleaved frame count published after attachment.</summary>
        public long Frames { get; private set; }
        /// <summary>Actual number of raw float bytes included in the streaming digest.</summary>
        public long Bytes { get; private set; }
        /// <summary>Bounded diagnostic failure, detected outside the device callback without faulting the guest.</summary>
        public string? Failure { get; private set; }

        /// <summary>Attaches one passive owner-thread mixer observer after both PCs reached the same initial boundary.</summary>
        public PcmObserver(PcMachine machine) { _machine = machine; machine.Audio.AudioReady += Published; }

        /// <summary>Hashes the actual owned sample array and captures its clock/format while leaving publication and samples untouched.</summary>
        private void Published(AudioBlock block)
        {
            if (Failure is not null) return;
            if (_blocks.Count >= MaximumAudioBlocks) { Failure = "PCM publication diagnostic capacity exceeded."; return; }
            if (block.SampleRate != PcAudioMixer.SampleRate || block.Channels != 2 || block.Samples.Length != 960 ||
                block.VirtualNanoseconds > _machine.Scheduler.Now || block.Samples.Any(sample => !float.IsFinite(sample)))
            { Failure = "Unexpected real PCM format, timestamp, incomplete publication or nonfinite sample."; return; }
            var bytes = MemoryMarshal.AsBytes(block.Samples.AsSpan());
            _blocks.Add(new(block.VirtualNanoseconds, block.SampleRate, block.Channels, block.Samples.Length / block.Channels, bytes.Length, Hash(bytes)));
            _digest.AppendData(bytes); Frames += block.Samples.Length / block.Channels; Bytes += bytes.Length;
        }

        /// <summary>Returns the cumulative actual sample hash without resetting its ongoing digest.</summary>
        public string Digest() => Convert.ToHexString(_digest.GetCurrentHash());

        /// <summary>Detaches only this helper's passive mixer observation and releases its host hash state.</summary>
        public void Dispose() { _machine.Audio.AudioReady -= Published; _digest.Dispose(); }
    }

    /// <summary>Compares actual PCM clock/format/count/sample digests, including silence, without reconstructing the mixer.</summary>
    private static void SameAudio(PcmObserver original, PcmObserver restored)
    {
        Require(original.Failure is null && restored.Failure is null, original.Failure ?? restored.Failure ?? "PCM observation failed.");
        Require(original.Frames == restored.Frames && original.Bytes == restored.Bytes && original.Blocks.SequenceEqual(restored.Blocks) &&
            original.Digest() == restored.Digest(), "Actual future PCM publications differ.");
    }

    /// <summary>Returns the saved partial mixer frame count, without advancing audio to a different boundary.</summary>
    private static int BufferedFrames(PcMachine machine)
    {
        int samples = machine.Audio.SaveState().Fields["_count"].GetInt32();
        Require(samples is >= 0 and < 960 && (samples & 1) == 0, "Invalid actual partial PCM block size.");
        return samples / 2;
    }

    /// <summary>Binds complete publication counts and block timestamps to the retained partial buffer and generated frame sequence.</summary>
    private static object AudioClock(PcMachine machine, PcmObserver pcm, long initialGenerated, int initialBuffered, ulong initialBlockStart)
    {
        long generated = machine.Audio.GeneratedFrames, expected = initialBuffered + generated - initialGenerated - BufferedFrames(machine);
        Require(generated >= initialGenerated && pcm.Frames == expected, "PCM publications do not match generated frames and the full saved partial buffer.");
        long firstFrame = initialGenerated - initialBuffered;
        Require(firstFrame >= 0, "The retained partial PCM buffer predates the actual generated frame sequence.");
        for (int i = 0; i < pcm.Blocks.Count; i++)
        {
            ulong expectedTime = i == 0 && initialBuffered != 0 ? initialBlockStart :
                (ulong)(((UInt128)(ulong)(firstFrame + (long)i * 480) * 1_000_000_000 + PcAudioMixer.SampleRate - 1) / PcAudioMixer.SampleRate);
            Require(pcm.Blocks[i].VirtualNanoseconds == expectedTime, "PCM block start is not the exact retained sample-clock boundary.");
        }
        return new { initialGeneratedFrames = initialGenerated, initialBufferedFrames = initialBuffered, generatedFrames = generated,
            finalBufferedFrames = BufferedFrames(machine), publishedFrames = pcm.Frames, publishedBytes = pcm.Bytes,
            pcmSha256 = pcm.Digest(), blocks = pcm.Blocks,
            scope = "Exact actual PCM bytes, clocks and side equality; no new independent synthesis/filter reconstruction or host audio test." };
    }

    /// <summary>Compares every raw state/RAM byte, then complete real scanouts and all state again after both render counters advance.</summary>
    private static Comparison Compare(string stage, PcMachine original, PcMachine restored, VgaFrameBuffer originalBuffer, VgaFrameBuffer restoredBuffer,
        PcmObserver? originalPcm = null, PcmObserver? restoredPcm = null, string? finalDirectory = null)
    {
        Healthy(original, "Original"); Healthy(restored, "Restored");
        SameBytes(State(original), State(restored), stage + " raw full hardware/scheduler graph");
        SameBytes(original.Memory.Ram, restored.Memory.Ram, stage + " full RAM");
        var first = original.CaptureFrame(originalBuffer); var second = restored.CaptureFrame(restoredBuffer);
        Require(first.Width == second.Width && first.Height == second.Height && first.ModeGeneration == second.ModeGeneration &&
            first.FrameId == second.FrameId && first.VirtualNanoseconds == second.VirtualNanoseconds &&
            first.ArgbPixels.Length == checked(first.Width * first.Height) && second.ArgbPixels.Length == checked(second.Width * second.Height) &&
            original.Video.GraphicsMode == restored.Video.GraphicsMode && original.Video.ReadText() == restored.Video.ReadText(), stage + " actual scanout metadata differs.");
        SameBytes(MemoryMarshal.AsBytes(first.ArgbPixels.AsSpan()), MemoryMarshal.AsBytes(second.ArgbPixels.AsSpan()), stage + " complete scanout pixels");
        byte[] state = State(original); SameBytes(state, State(restored), stage + " raw full graph after scanout");
        SameBytes(original.Memory.Ram, restored.Memory.Ram, stage + " full RAM after scanout");
        if (originalPcm is not null && restoredPcm is not null) SameAudio(originalPcm, restoredPcm);
        if (finalDirectory is not null)
        {
            WriteNew(Path.Combine(finalDirectory, "final-state.json"), state);
            WriteNew(Path.Combine(finalDirectory, "final-ram.bin"), original.Memory.Ram);
        }
        return new(stage, original.Scheduler.Now, original.Cpu.Instructions, restored.Cpu.Instructions, state.Length, Hash(state),
            original.Memory.Ram.Length, Hash(original.Memory.Ram), new(first.Width, first.Height, first.ModeGeneration, first.FrameId,
                first.VirtualNanoseconds, first.ArgbPixels.Length, Hash(MemoryMarshal.AsBytes(first.ArgbPixels.AsSpan())), original.Video.GraphicsMode, original.Video.ReadText()));
    }

    /// <summary>Removes exactly the one independently validated capture UUID while retaining every other actual manifest field and value.</summary>
    private static byte[] MediaWithoutCaptureId(JsonElement document, string id)
    {
        Require(document.ValueKind == JsonValueKind.Object && Guid.TryParseExact(id, "N", out _), "Invalid actual media capture ID.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); int ids = 0;
            foreach (var property in document.EnumerateObject())
            {
                if (property.NameEquals("id")) { Require(property.Value.GetString() == id, "Actual media capture ID differs from returned manifest."); ids++; }
                else property.WriteTo(writer);
            }
            Require(ids == 1, "Actual media metadata must contain exactly one capture ID."); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Checks actual saved metadata against its returned manifest before preparing the one-ID-only comparison representation.</summary>
    private static (SealedMedia Report, byte[] Compared) Seal(PcMachine machine, string directory)
    {
        var manifest = MediaCheckpointStore.Capture(machine, directory);
        string file = Path.Combine(directory, "media.json"); byte[] raw = File.ReadAllBytes(file);
        using var document = JsonDocument.Parse(raw);
        byte[] compared = MediaWithoutCaptureId(document.RootElement, manifest.Id);
        SameBytes(compared, MediaWithoutCaptureId(JsonSerializer.SerializeToElement(manifest, Json), manifest.Id), "Published final media versus returned metadata");
        return (new(directory, manifest.Id, Hash(raw), Hash(compared), manifest.Media), compared);
    }

    /// <summary>Advances one actual original PC and its single strict full import with identical bounded regular owner batches and no inputs.</summary>
    public static object Run(PcMachine original, string directory, int slices, int stepsPerSlice, int maximumMilliseconds,
        ulong maximumVirtualNanoseconds, Action<object> progress, Action<int> originalExecuted)
    {
        if (slices is < 1 or > 64 || stepsPerSlice is < 1 or > 500000 || (long)slices * stepsPerSlice * 2 > 1_000_000 ||
            maximumMilliseconds is < 100 or > 60000 || maximumVirtualNanoseconds is < 1000000 or > 1000000000)
            throw new ArgumentException("Continuation requires 1..64 slices, <=1,000,000 combined step elements, <=60s host budget and 1ms..1s virtual budget.");
        if (original.Memory.Ram.Length != RequiredRamBytes || original.CaptureDefinition().BuiltInTestDisk)
            throw new ArgumentException("Continuation requires the actual 32-MiB PC with regular file-backed private media.");
        if (original.Scheduler.Now > ulong.MaxValue - maximumVirtualNanoseconds) throw new ArgumentException("Virtual deadline would overflow.");
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) || File.Exists(root) || !Directory.Exists(Path.GetDirectoryName(root)))
            throw new ArgumentException("Continuation output must be a new private directory with an existing parent.");
        Directory.CreateDirectory(root);
        var timer = Stopwatch.StartNew(); var comparisons = new List<Slice>(); string stage = "capture", stopReason = "slice-budget";
        long originalSteps = 0, restoredSteps = 0; ulong startedAt = original.Scheduler.Now, until = startedAt + maximumVirtualNanoseconds;
        long startedInstructions = original.Cpu.Instructions;
        try
        {
            Healthy(original, "Original"); progress(new { stage = "continuation-capture", output = root, virtualNanoseconds = startedAt });
            string saved = Path.Combine(root, "cut.dossav"); var manifest = SnapshotStore.Capture(original, saved);
            Require(manifest.RamBytes == RequiredRamBytes && manifest.StateSha256 == Hash(State(original)) &&
                manifest.RamSha256 == Hash(original.Memory.Ram), "The saved cut does not describe the actual running original.");
            using var cutMediaDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(saved, "media.dosemedia", "media.json")));
            string cutMediaId = cutMediaDocument.RootElement.GetProperty("id").GetString() ?? "";
            Require(Guid.TryParseExact(cutMediaId, "N", out _) && Guid.TryParseExact(manifest.Id, "N", out _) &&
                cutMediaId != manifest.Id && HashFile(Path.Combine(saved, "media.dosemedia", "media.json")) == manifest.MediaSha256,
                "Cut container/media IDs or actual sealed metadata hash differ.");
            stage = "load"; progress(new { stage = "continuation-load", snapshot = saved });
            using var restored = SnapshotStore.Load(saved, Path.Combine(root, "restored.dosebranch"));
            var originalBuffer = new VgaFrameBuffer(); var restoredBuffer = new VgaFrameBuffer();
            stage = "initial-comparison"; var initial = Compare("initial", original, restored, originalBuffer, restoredBuffer);
            long initialGenerated = original.Audio.GeneratedFrames; int initialBuffered = BufferedFrames(original);
            ulong initialBlockStart = original.Audio.SaveState().Fields["_blockStart"].GetUInt64();
            using var originalPcm = new PcmObserver(original); using var restoredPcm = new PcmObserver(restored);
            for (int i = 0; i < slices; i++)
            {
                if (timer.ElapsedMilliseconds >= maximumMilliseconds) { stopReason = "host-deadline"; break; }
                if (original.Scheduler.Now >= until) { stopReason = "virtual-deadline"; break; }
                stage = "slice-" + i;
                progress(new { stage = "continuation-slice", slice = i, slices, originalStepElements = originalSteps,
                    restoredStepElements = restoredSteps, virtualNanoseconds = original.Scheduler.Now, hostMilliseconds = timer.ElapsedMilliseconds });
                int first = original.RunSteps(stepsPerSlice, until); originalSteps += first; originalExecuted(first);
                int second = restored.RunSteps(stepsPerSlice, until); restoredSteps += second;
                Require(first == second, "Actual normal RunSteps return values differ at slice " + i);
                comparisons.Add(new(i, first, second, Compare(stage, original, restored, originalBuffer, restoredBuffer, originalPcm, restoredPcm)));
                if (first == 0) { stopReason = original.Scheduler.Now >= until ? "virtual-deadline" : "no-executable-progress"; break; }
            }
            stage = "pcm-clock";
            object originalAudio = AudioClock(original, originalPcm, initialGenerated, initialBuffered, initialBlockStart);
            object restoredAudio = AudioClock(restored, restoredPcm, initialGenerated, initialBuffered, initialBlockStart);
            SameAudio(originalPcm, restoredPcm);
            stage = "seal-media"; progress(new { stage = "continuation-seal-media", originalStepElements = originalSteps, restoredStepElements = restoredSteps });
            var firstMedia = Seal(original, Path.Combine(root, "final-original.dosemedia"));
            var secondMedia = Seal(restored, Path.Combine(root, "final-restored.dosemedia"));
            SameBytes(firstMedia.Compared, secondMedia.Compared, "Every final sealed media field except the actual capture UUID");
            Require(new[] { manifest.Id, cutMediaId, firstMedia.Report.Id, secondMedia.Report.Id }.Distinct(StringComparer.Ordinal).Count() == 4,
                "The four real capture IDs must be individually valid and distinct.");
            stage = "final-comparison";
            var final = Compare("final-after-media-sealing", original, restored, originalBuffer, restoredBuffer, originalPcm, restoredPcm, root);
            bool observedFuture = originalSteps > 0 || original.Scheduler.Now > startedAt;
            var result = new { passed = observedFuture, allComparedDataEqual = true, scope = "Bounded continuation of the actual running guest; a Mini-Setup waiting cut does not certify an installed or shut-down Windows 95.",
                originalWasControllerPc = true, originalCaptureCount = 1, fullSnapshotImports = 1, traceCapacity = 0,
                inputPacketsInjected = 0, audioFlushes = 0, capturedStateFieldsExcluded = 0, mediaFieldsExcluded = new[] { "/id" },
                profileId = original.Profile.Id, output = root, snapshot = saved, manifest, cutMediaId,
                snapshotManifestSha256 = HashFile(Path.Combine(saved, "snapshot.json")), initial, final,
                requestedSlices = slices, completedSlices = comparisons.Count, stepsPerSlice, combinedStepBudget = (long)slices * stepsPerSlice * 2,
                originalStepElements = originalSteps, restoredStepElements = restoredSteps, combinedStepElements = originalSteps + restoredSteps,
                originalCpuInstructions = original.Cpu.Instructions - startedInstructions, restoredCpuInstructions = restored.Cpu.Instructions - startedInstructions,
                maximumHostMilliseconds = maximumMilliseconds, maximumVirtualNanoseconds, startedAt, endedAt = original.Scheduler.Now,
                hostMilliseconds = timer.Elapsed.TotalMilliseconds, stopReason, completedRequestedSlices = comparisons.Count == slices,
                futureExecutionObserved = observedFuture, pcmPublicationsObserved = originalPcm.Frames > 0,
                fullComparisons = comparisons.Count + 2, slices = comparisons, originalAudio, restoredAudio,
                originalMedia = firstMedia.Report, restoredMedia = secondMedia.Report };
            WriteJson(Path.Combine(root, "result.json"), result);
            progress(new { stage = "idle", command = "CompareContinuation", passed = observedFuture, output = root, stopReason,
                originalStepElements = originalSteps, restoredStepElements = restoredSteps, completedSlices = comparisons.Count });
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OverflowException)
        {
            WriteJson(Path.Combine(root, "failure.json"), new { passed = false, stage, error = error.GetType().Name, message = error.Message,
                originalStepElements = originalSteps, restoredStepElements = restoredSteps, startedAt, originalNow = original.Scheduler.Now,
                originalPosition = original.Cpu.Position, originalFault = original.Fault, originalTripleFaulted = original.Cpu.TripleFaulted,
                hostMilliseconds = timer.Elapsed.TotalMilliseconds, completedSlices = comparisons });
            throw;
        }
    }
}