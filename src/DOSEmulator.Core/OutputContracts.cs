// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

public sealed record VideoFrame(long FrameId, int ModeGeneration, ulong VirtualNanoseconds, int Width, int Height, uint[] ArgbPixels);

public sealed record AudioBlock(ulong VirtualNanoseconds, int SampleRate, int Channels, float[] Samples)
{
    /// <summary>Requires a finite, correctly interleaved PCM block with a supported sample rate and channel count.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Samples);
        if (SampleRate is < 8000 or > 192000 || Channels is < 1 or > 2 || Samples.Length % Channels != 0)
            throw new ArgumentException("Invalid interleaved PCM block.");
        foreach (float sample in Samples)
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.");
    }
}
