// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;
using DOSEmulator.Runtime;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DOSEmulator;

/// <summary>Bounded host playback, separate from guest sound generation and its virtual clock.</summary>
internal sealed class HostAudioOutput : IWaveProvider, IDisposable
{
    private readonly AudioRingBuffer _buffer = new(48000, 2, 12000);
    private readonly IWavePlayer? _output;
    private readonly MMDevice? _endpoint;
    private readonly string _backend;
    private readonly PcmWaveRecorder? _recording;
    private readonly object _callbackGate = new();
    private readonly TaskCompletionSource<bool> _playbackStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private float[] _scratch = [];
    private float _gain = 0.8f;
    private string? _fault;
    /// <summary>Native output-device teardown failure retained separately from normal playback status.</summary>
    public string? ShutdownFault { get; private set; }
    private long _readFrames, _nonSilentFrames;
    private double _energy;
    /// <summary>Provider-side 48-kHz stereo PCM16 format supplied to the host backend.</summary>
    public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
    /// <summary>Whether the host output initialized successfully and remains open without a playback fault.</summary>
    public bool Available => _output is not null && Fault is null && !Volatile.Read(ref _closed);
    /// <summary>Most recent component fault description, or null while no fault is recorded.</summary>
    public string? Fault => Volatile.Read(ref _fault);
    /// <summary>Number of provider frames containing a nonzero left-channel PCM component.</summary>
    public long NonSilentFrames => Interlocked.Read(ref _nonSilentFrames);
    /// <summary>Number of complete stereo frames requested by actual host output callbacks.</summary>
    public long ReadFrames => Interlocked.Read(ref _readFrames);
    /// <summary>Accumulated squared provider signal used by the independent gain/mute acceptance measurements.</summary>
    public double Energy => Volatile.Read(ref _energy);
    /// <summary>Initializes HostAudioOutput and connects the supplied dependencies before the component is used.</summary>
    public HostAudioOutput(string? recordingPath = null, string backend = "wasapi")
    {
        if (backend is not ("wasapi" or "waveout" or "none")) throw new ArgumentException("Audio backend must be wasapi, waveout or none.");
        if (backend == "none" && recordingPath is not null) throw new ArgumentException("Disabled host audio cannot record output.");
        _backend = backend == "none" ? "Disabled" : backend == "wasapi" ? "NAudio WASAPI shared" : "NAudio WaveOutEvent";
        if (backend == "none") return; // Guest synthesis remains active; no host device, worker or recorder is created.
        _recording = recordingPath is null ? null : new(recordingPath);
        IWavePlayer? candidate = null;
        MMDevice? endpoint = null;
        try
        {
            // Both NAudio players capture their construction context for PlaybackStopped.
            // Keep that notification independent of a GUI loop that is closing.
            candidate = Task.Run(() =>
            {
                if (backend == "wasapi")
                {
                    using var devices = new MMDeviceEnumerator();
                    endpoint = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    candidate = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 40);
                }
                else candidate = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
                candidate.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception is not null) Volatile.Write(ref _fault, e.Exception.Message);
                    _playbackStopped.TrySetResult(true);
                };
                candidate.Init(this); candidate.Play(); return candidate;
            }).GetAwaiter().GetResult();
            _output = candidate;
            _endpoint = endpoint;
        }
        catch (Exception ex) when (ex is MmException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            try { candidate?.Dispose(); }
            catch (Exception cleanup) when (cleanup is MmException or System.Runtime.InteropServices.ExternalException) { ShutdownFault = cleanup.ToString(); }
            endpoint?.Dispose();
            _fault = ex.Message;
        }
    }
    /// <summary>Applies host mute/master gain without modifying the guest sound-card registers.</summary>
    public void SetLevels(bool muted, int master) => Volatile.Write(ref _gain,
        muted ? 0 : Math.Clamp(master, 0, 100) / 100f);
    /// <summary>Copies a completed guest PCM block into the bounded host playback queue.</summary>
    public void Write(AudioBlock block) => _buffer.Write(block);
    /// <summary>Discards queued host playback frames while retaining guest sound hardware state.</summary>
    public void Clear() => _buffer.Clear();
    /// <summary>Supplies bounded stereo PCM16 to the output callback, applying host gain and recording under the callback gate.</summary>
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
    /// <summary>Captures backend/callback/fault diagnostics, retaining native teardown failure separately from playback success.</summary>
    public object Snapshot() => new { available = Available, fault = Fault, shutdownFault = ShutdownFault, backend = _backend, sampleRate = 48000, channels = 2,
        readFrames = ReadFrames, nonSilentFrames = NonSilentFrames, energy = Energy, gain = Volatile.Read(ref _gain),
        queuedFrames = _buffer.BufferedFrames, droppedFrames = _buffer.DroppedFrames, underflowFrames = _buffer.SilentFrames };
    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
    public void Dispose() => Dispose(null);
    /// <summary>Releases the resources or ownership lease held by this instance.</summary>
    internal void Dispose(Action<string>? progress)
    {
        progress?.Invoke("enter");
        lock (_callbackGate) { if (_closed) return; _closed = true; }
        progress?.Invoke("callback-closed");
        bool stopped = _output is null;
        try
        {
            progress?.Invoke("stop-request"); _output?.Stop(); progress?.Invoke("stop-returned");
            if (_output is not null && !_playbackStopped.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                ShutdownFault = _backend + " playback thread did not acknowledge Stop within two seconds; native buffers were not freed while still in use.";
                Volatile.Write(ref _fault, ShutdownFault); progress?.Invoke("stop-timeout"); return;
            }
            progress?.Invoke("playback-thread-stopped");
            stopped = true;
            _output?.Dispose(); progress?.Invoke("device-disposed");
        }
        catch (Exception ex) when (ex is MmException or System.Runtime.InteropServices.ExternalException)
        {
            // A native waveOutClose failure must not replace an intentional
            // window close with Program's modal startup-error dialog. Preserve
            // the failure separately; do not report clean host-device teardown.
            ShutdownFault = ex.ToString(); Volatile.Write(ref _fault, ex.Message);
            progress?.Invoke("device-close-fault");
        }
        finally
        {
            if (stopped)
            {
                try { _endpoint?.Dispose(); }
                catch (System.Runtime.InteropServices.ExternalException ex)
                { ShutdownFault = ex.ToString(); Volatile.Write(ref _fault, ex.Message); }
            }
            lock (_callbackGate) _recording?.Dispose(); progress?.Invoke("recorder-disposed");
        }
    }
}
