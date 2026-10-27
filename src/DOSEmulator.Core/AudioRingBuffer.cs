// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Core;

/// <summary>Bounded interleaved host PCM queue. Overflow drops oldest whole frames; underflow emits silence.</summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _samples;
    private readonly object _gate = new();
    private int _read, _count;
    /// <summary>Number of interleaved audio channels in each complete PCM frame.</summary>
    public int Channels { get; }
    /// <summary>Sample or report rate in hertz, using the surrounding audio/mouse component's native unit.</summary>
    public int SampleRate { get; }
    /// <summary>Number of complete host PCM frames discarded to keep the queue bounded.</summary>
    public long DroppedFrames { get; private set; }
    /// <summary>Number of complete PCM frames zero-filled because the consumer ran ahead of queued audio.</summary>
    public long SilentFrames { get; private set; }
    /// <summary>Number of complete PCM frames currently available under the queue lock.</summary>
    public int BufferedFrames { get { lock (_gate) return _count / Channels; } }

    /// <summary>Initializes AudioRingBuffer and connects the supplied dependencies before the component is used.</summary>
    public AudioRingBuffer(int sampleRate, int channels, int capacityFrames)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 2 || capacityFrames is < 1 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        SampleRate = sampleRate; Channels = channels; _samples = new float[checked(capacityFrames * channels)];
    }
    /// <summary>Appends whole PCM frames, discarding oldest whole frames on overflow without changing channel alignment.</summary>
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
            int first = Math.Min(length, _samples.Length - write);
            block.Samples.AsSpan(source, first).CopyTo(_samples.AsSpan(write));
            block.Samples.AsSpan(source + first, length - first).CopyTo(_samples);
            _count += length;
        }
    }
    /// <summary>Consumes queued whole PCM frames, copies across at most one wrap and zero-fills underflow.</summary>
    public void Read(Span<float> destination)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("Read length must contain whole frames.");
        lock (_gate)
        {
            int available = Math.Min(_count, destination.Length);
            int first = Math.Min(available, _samples.Length - _read);
            _samples.AsSpan(_read, first).CopyTo(destination);
            _samples.AsSpan(0, available - first).CopyTo(destination[first..]);
            destination[available..].Clear(); SilentFrames += (destination.Length - available) / Channels;
            _read = (_read + available) % _samples.Length; _count -= available;
        }
    }
    /// <summary>Clears the component's queued transient data while retaining its configuration.</summary>
    public void Clear() { lock (_gate) { _read = 0; _count = 0; } }
}
