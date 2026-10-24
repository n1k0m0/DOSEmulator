using System.Buffers.Binary;
using DOSEmulator.Core;
using DOSEmulator.Runtime;
using NAudio;
using NAudio.Wave;

namespace DOSEmulator;

/// <summary>Bounded host playback, separate from guest sound generation and its virtual clock.</summary>
internal sealed class HostAudioOutput : IWaveProvider, IDisposable
{
    private readonly AudioRingBuffer _buffer = new(48000, 2, 12000);
    private readonly WaveOutEvent? _output;
    private readonly PcmWaveRecorder? _recording;
    private readonly object _callbackGate = new();
    private bool _closed;
    private float[] _scratch = [];
    private float _gain = 0.8f;
    private string? _fault;
    private long _readFrames, _nonSilentFrames;
    private double _energy;
    public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
    public bool Available => _output is not null && Fault is null && !Volatile.Read(ref _closed);
    public string? Fault => Volatile.Read(ref _fault);
    public long NonSilentFrames => Interlocked.Read(ref _nonSilentFrames);
    public long ReadFrames => Interlocked.Read(ref _readFrames);
    public double Energy => Volatile.Read(ref _energy);
    public HostAudioOutput(string? recordingPath = null)
    {
        _recording = recordingPath is null ? null : new(recordingPath);
        WaveOutEvent? candidate = null;
        try
        {
            candidate = new() { DesiredLatency = 80, NumberOfBuffers = 3 };
            candidate.PlaybackStopped += (_, e) => { if (e.Exception is not null) Volatile.Write(ref _fault, e.Exception.Message); };
            candidate.Init(this); candidate.Play(); _output = candidate;
        }
        catch (Exception ex) when (ex is MmException or InvalidOperationException)
        { candidate?.Dispose(); _fault = ex.Message; }
    }
    public void SetLevels(bool muted, int master) => Volatile.Write(ref _gain,
        muted ? 0 : Math.Clamp(master, 0, 100) / 100f);
    public void Write(AudioBlock block) => _buffer.Write(block);
    public void Clear() => _buffer.Clear();
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_callbackGate)
        {
        if (_closed) { Array.Clear(buffer, offset, count); return count; }
        count -= count % 4; int samples = count / 2;
        if (_scratch.Length < samples) _scratch = new float[samples];
        _buffer.Read(_scratch.AsSpan(0, samples)); float gain = Volatile.Read(ref _gain);
        double energy = 0; long nonSilent = 0;
        for (int i = 0; i < samples; i++)
        {
            float value = Math.Clamp(_scratch[i] * gain, -1, 1);
            short pcm = (short)Math.Round(value * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + i * 2), pcm);
            if ((i & 1) == 0) { energy += value * value; if (pcm != 0) nonSilent++; }
        }
        _recording?.WritePcm16(buffer.AsSpan(offset, count));
        Volatile.Write(ref _energy, _energy + energy); Interlocked.Add(ref _readFrames, samples / 2); Interlocked.Add(ref _nonSilentFrames, nonSilent);
        return count;
        }
    }
    public object Snapshot() => new { available = Available, fault = Fault, backend = "NAudio WaveOutEvent", sampleRate = 48000, channels = 2,
        readFrames = ReadFrames, nonSilentFrames = NonSilentFrames, energy = Energy, gain = Volatile.Read(ref _gain),
        queuedFrames = _buffer.BufferedFrames, droppedFrames = _buffer.DroppedFrames, underflowFrames = _buffer.SilentFrames };
    public void Dispose()
    {
        lock (_callbackGate) { if (_closed) return; _closed = true; }
        try { _output?.Stop(); _output?.Dispose(); }
        finally { lock (_callbackGate) _recording?.Dispose(); }
    }
}
