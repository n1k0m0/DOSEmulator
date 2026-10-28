// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Hardware;

/// <summary>Complete pixel/state and reader-lease controls for joined, disjoint host scanout workers.</summary>
internal static class ParallelScanoutTests
{
    /// <summary>Rejects any differing pixel, hardware member, metadata or incomplete leased output.</summary>
    private static void Check(bool value, string why)
    { if (!value) throw new Exception(why); }

    /// <summary>Builds only private deterministic VGA/ET4000/Trio register, font, palette and VRAM bytes.</summary>
    private static VgaAdapter Make(int model, int format, bool blank, bool cursor)
    {
        var video = new VgaAdapter(null, model == 1, model == 2);
        var state = video.SaveState();
        byte[] seq = state.Fields["_seq"].Deserialize<byte[]>(StateValues.Json)!;
        byte[] gc = state.Fields["_gc"].Deserialize<byte[]>(StateValues.Json)!;
        byte[] crtc = state.Fields["_crtc"].Deserialize<byte[]>(StateValues.Json)!;
        byte[] attr = state.Fields["_attr"].Deserialize<byte[]>(StateValues.Json)!;
        byte[][] planes = state.Fields["_planes"].Deserialize<byte[][]>(StateValues.Json)!;
        seq[1] = (byte)(1 | (blank ? 32 : 0)); crtc[1] = (byte)(format == 2 && model != 2 ? 199 : 99);
        crtc[7] = 0x40; crtc[0x12] = 0x57; crtc[9] = (byte)(format == 0 ? 15 : 0); crtc[0x13] = 100;
        gc[6] = (byte)(format == 0 ? 0 : 1); gc[5] = (byte)(format == 2 ? 0x40 : 0);
        attr[0x10] = 0x0c; attr[0x12] = 15; attr[0x13] = 3;
        if (model == 2 && format >= 8)
        {
            int bytesPerPixel = format == 8 ? 1 : format == 24 ? 4 : 2;
            int stride = 800 * bytesPerPixel / 8;
            crtc[0x31] = 8; crtc[0x3a] = 0x10; crtc[0x13] = (byte)stride;
            crtc[0x51] = (byte)((stride >> 4) & 0x30); crtc[0x67] = (byte)(format == 8 ? 0 : format == 15 ? 0x30 : format == 16 ? 0x50 : 0xd0);
            crtc[0x45] = (byte)(cursor ? 1 : 0); crtc[0x4e] = 3; crtc[0x4f] = 5;
            state.Fields["_trioAdvanced"] = JsonSerializer.SerializeToElement((ushort)1, StateValues.Json);
        }
        for (int p = 0; p < planes.Length; p++)
            for (int i = 0; i < planes[p].Length; i++) planes[p][i] = (byte)((i * 37 + (i >> 8) * 19 + p * 53) ^ (i >> 4));
        state.Fields["_seq"] = JsonSerializer.SerializeToElement(seq, StateValues.Json);
        state.Fields["_gc"] = JsonSerializer.SerializeToElement(gc, StateValues.Json);
        state.Fields["_crtc"] = JsonSerializer.SerializeToElement(crtc, StateValues.Json);
        state.Fields["_attr"] = JsonSerializer.SerializeToElement(attr, StateValues.Json);
        state.Fields["_planes"] = JsonSerializer.SerializeToElement(planes, StateValues.Json);
        video.LoadState(state); return video;
    }

    /// <summary>Compares all output pixels, public frame metadata and the complete serialized video state.</summary>
    private static void Equal(VgaAdapter a, VgaAdapter b, VgaFrameBuffer left, VgaFrameBuffer right, ulong now, JoinedWorker worker)
    {
        var x = a.Capture(now, left); var y = b.Capture(now, right, worker);
        Check(x.Width == y.Width && x.Height == y.Height && x.FrameId == y.FrameId && x.VirtualNanoseconds == y.VirtualNanoseconds &&
            x.ModeGeneration == y.ModeGeneration, "Parallel frame metadata differs.");
        Check(x.ArgbPixels.AsSpan().SequenceEqual(y.ArgbPixels), "Parallel scanout changed a pixel or returned before workers completed.");
        Check(JsonSerializer.Serialize(a.SaveState(), StateValues.Json) == JsonSerializer.Serialize(b.SaveState(), StateValues.Json),
            "Joined scanout changed saved hardware state or capture counters.");
    }

    /// <summary>Registers actual large-output worker paths, cache/phase changes and concurrent reader protection.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (int model in new[] { 0, 1, 2 })
        foreach (int format in model == 2 ? new[] { 0, 1, 2, 8, 15, 16, 24 } : new[] { 0, 1, 2 })
        foreach (bool blank in new[] { false, true })
            test($"Parallel scanout every pixel and hardware field model{model} format{format} blank{blank}", () =>
            {
                var a = Make(model, format, blank, true); var b = Make(model, format, blank, true);
                var left = new VgaFrameBuffer(); var right = new VgaFrameBuffer();
                using var worker = new JoinedWorker("DOSE scanout test");
                Equal(a, b, left, right, 0, worker); Check(left.RenderedFrames == 1 && right.RenderedFrames == 1, "Initial scanout missing.");
                Equal(a, b, left, right, 1, worker); Check(left.RenderedFrames == 1 && right.RenderedFrames == 1, "Unchanged scanout was rerendered.");
                Equal(a, b, left, right, 533333344, worker); Equal(a, b, left, right, 1066666688, worker);
            });
        test("Parallel scanout preserves a reader lease through subsequent complete publications", () =>
        {
            using var worker = new JoinedWorker("DOSE lease test");
            var video = Make(2, 16, false, true); var frames = new LatestBufferExchange<VgaFrameBuffer>(() => new());
            Check(frames.TryPublish((video, worker), static (surface, source) => source.video.Capture(0, surface, source.worker)), "Initial publication rejected.");
            using var held = frames.AcquireLatest()!; uint[] original = (uint[])held.Value.Frame!.ArgbPixels.Clone();
            for (int i = 0; i < 12; i++)
            {
                video.ApplyPiixHardReset();
                Check(frames.TryPublish((video, worker), static (surface, source) => source.video.Capture(1, surface, source.worker)), "Unleased publication rejected.");
                Check(original.AsSpan().SequenceEqual(held.Value.Frame!.ArgbPixels), "Parallel worker overwrote a held surface.");
            }
        });
    }
}
