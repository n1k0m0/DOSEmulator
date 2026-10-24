namespace DOSEmulator.Core;

/// <summary>Bounded interleaved host PCM queue. Overflow drops oldest whole frames; underflow emits silence.</summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly object _gate = new();
    private int _read, _count;
    public int Channels { get; }
    public int SampleRate { get; }
    public long DroppedFrames { get; private set; }
    public long SilentFrames { get; private set; }
    public int BufferedFrames { get { lock (_gate) return _count / Channels; } }

    public AudioRingBuffer(int sampleRate, int channels, int capacityFrames)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2 || capacityFrames is < 1 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        SampleRate = sampleRate; Channels = channels; _samples = new float[checked(capacityFrames * channels)];
    }
    public void Write(AudioBlock block)
    {
        block.Validate();
        if (block.SampleRate != SampleRate || block.Channels != Channels) throw new ArgumentException("PCM format mismatch.");
        lock (_gate)
        {
            int source = Math.Max(0, block.Samples.Length - _samples.Length);
            DroppedFrames += source / Channels;
            int length = block.Samples.Length - source;
            int discard = Math.Max(0, _count + length - _samples.Length);
            _read = (_read + discard) % _samples.Length; _count -= discard; DroppedFrames += discard / Channels;
            int write = (_read + _count) % _samples.Length;
            for (int i = 0; i < length; i++) _samples[(write + i) % _samples.Length] = block.Samples[source + i];
            _count += length;
        }
    }
    public void Read(Span<float> destination)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("Read length must contain whole frames.");
        lock (_gate)
        {
            int available = Math.Min(_count, destination.Length);
            for (int i = 0; i < available; i++) destination[i] = _samples[(_read + i) % _samples.Length];
            destination[available..].Clear(); SilentFrames += (destination.Length - available) / Channels;
            _read = (_read + available) % _samples.Length; _count -= available;
        }
    }
    public void Clear() { lock (_gate) { _read = 0; _count = 0; } }
}
