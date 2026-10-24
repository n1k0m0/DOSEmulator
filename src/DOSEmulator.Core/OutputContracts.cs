namespace DOSEmulator.Core;

public sealed record VideoFrame(long FrameId, int ModeGeneration, ulong VirtualNanoseconds, int Width, int Height, uint[] ArgbPixels);

public sealed record AudioBlock(ulong VirtualNanoseconds, int SampleRate, int Channels, float[] Samples)
{
    public void Validate()
    {
        if (SampleRate is < 8000 or > 192000 || Channels is < 1 or > 2 || Samples.Length % Channels != 0)
            throw new ArgumentException("Invalid interleaved PCM block.");
        if (Samples.Any(x => !float.IsFinite(x))) throw new ArgumentException("Audio samples must be finite.");
    }
}
