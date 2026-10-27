// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Time-integrated sources sharing one 48-kHz host timeline. Device clocks remain independent.</summary>
public sealed partial class PcAudioMixer(DeviceScheduler clock)
{
    public const int SampleRate = 48000;
    // Fixed-point signal area: nanoseconds * scale. The PIT frequency permits exact speaker integration;
    // the factor 32768 also represents every signed 16-bit source value without rounding.
    public const long IntegralScale = PitSpeakerCounter.Frequency * 32768L;
    private const int BlockFrames = 480;
    private readonly double _dcDecay = Math.Exp(-2 * Math.PI * 20 / SampleRate);
    private readonly List<Input> _inputs = [];
    private ulong _position, _sampleStart, _blockStart, _nextSample = 20834;
    private float[] _samples = new float[BlockFrames * 2];
    private int _count;
    public event Action<AudioBlock>? AudioReady;
    /// <summary>Diagnostic count of generated frames.</summary>
    public long GeneratedFrames { get; private set; }
    /// <summary>Number of sample frames advanced through the proven exactly silent integration path.</summary>
    public long SilentFramesSkipped { get; private set; }

    public sealed partial class Input
    {
        private readonly PcAudioMixer _mixer;
        internal readonly Func<ulong, ulong, Int128> Integrate;
        internal readonly double Amplitude;
        internal readonly Func<bool>? ExactlySilent;
        internal readonly int Channels;
        internal Int128 Area;
        internal double DeviceArea;
        internal bool UsesDeviceGain;
        internal double PreviousInput, Filtered;
        private double _gain = 1, _deviceGain = 1;
        /// <summary>Initializes Input and connects the supplied dependencies before the component is used.</summary>
        internal Input(PcAudioMixer mixer, Func<ulong, ulong, Int128> integrate, double amplitude, Func<bool>? exactlySilent, int channels)
        { _mixer = mixer; Integrate = integrate; Amplitude = amplitude; ExactlySilent = exactlySilent; Channels = channels; }
        /// <summary>Guest analog amplifier, separate from the host's 0..1 source volume.</summary>
        public double DeviceGain
        {
            get => _deviceGain;
            set
            {
                if (!double.IsFinite(value) || value is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(value));
                if (_deviceGain == value) return;
                _mixer.AdvanceTo(_mixer.CurrentTime);
                if (!UsesDeviceGain) { DeviceArea = (double)Area; Area = 0; UsesDeviceGain = true; }
                _deviceGain = value;
            }
        }
        /// <summary>Host source gain applied after guest signal integration, separate from the guest analog amplifier.</summary>
        public double Gain
        {
            get => _gain;
            set
            {
                if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
                if (_gain == value) return;
                _mixer.AdvanceTo(_mixer.CurrentTime); _gain = value;
            }
        }
    }
    /// <summary>Current virtual mixer time in nanoseconds.</summary>
    private ulong CurrentTime => clock.Now;
    /// <summary>Connects a mono signal-area source and its explicitly supplied exact-silence predicate to the mixer.</summary>
    public Input AddSource(Func<ulong, ulong, Int128> integrate, double amplitude) => AddSource(integrate, amplitude, null);
    /// <summary>The silence predicate guarantees zero integration without hidden source advancement.
    /// Advance the mixer before changing the source, as for every source transition.</summary>
    public Input AddSource(Func<ulong, ulong, Int128> integrate, double amplitude, Func<bool>? exactlySilent)
        => AddInput(integrate, amplitude, exactlySilent, 3);
    /// <summary>Connects distinct left/right integrated sources without collapsing their device gains or timing.</summary>
    public (Input Left, Input Right) AddStereoSource(Func<ulong, ulong, Int128> left, Func<ulong, ulong, Int128> right,
        double amplitude, Func<bool>? leftSilent = null, Func<bool>? rightSilent = null)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        return (AddInput(left, amplitude, leftSilent, 1), AddInput(right, amplitude, rightSilent, 2));
    }
    /// <summary>Validates and registers one channel-routed mixer input before the virtual machine starts.</summary>
    private Input AddInput(Func<ulong, ulong, Int128> integrate, double amplitude, Func<bool>? exactlySilent, int channels)
    {
        ArgumentNullException.ThrowIfNull(integrate);
        if (clock.Now != 0 || _position != 0) throw new InvalidOperationException("Audio sources must be installed before the machine starts.");
        if (!double.IsFinite(amplitude) || amplitude is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amplitude));
        var input = new Input(this, integrate, amplitude, exactlySilent, channels); _inputs.Add(input); return input;
    }
    /// <summary>Whether every source and filter state permits the proven zero-integration fast path.</summary>
    private bool ExactSilence => _inputs.All(s => s.ExactlySilent is not null && s.ExactlySilent() && s.Area == 0 && s.DeviceArea == 0 && s.PreviousInput == 0 && s.Filtered == 0);
    /// <summary>Advances only a proven exactly silent mixer/filter state while preserving frame counts and publication boundaries.</summary>
    private bool TryAdvanceSilence(ulong time)
    {
        if (!ExactSilence) return false;
        long endFrames = (long)((UInt128)time * SampleRate / 1_000_000_000);
        while (GeneratedFrames < endFrames)
        {
            int frames = (int)Math.Min(endFrames - GeneratedFrames, (_samples.Length - _count) / 2);
            if (_count == 0) _blockStart = _sampleStart;
            _samples.AsSpan(_count, frames * 2).Clear(); _count += frames * 2;
            GeneratedFrames += frames; SilentFramesSkipped += frames;
            _sampleStart = (ulong)(((UInt128)GeneratedFrames * 1_000_000_000 + SampleRate - 1) / SampleRate);
            _position = _sampleStart;
            _nextSample = (ulong)(((UInt128)(GeneratedFrames + 1) * 1_000_000_000 + SampleRate - 1) / SampleRate);
            if (_count == _samples.Length) { Publish(); if (!ExactSilence) return false; }
        }
        _position = time; return true;
    }
    /// <summary>Runs due callbacks in deadline/order sequence and advances the virtual clock without host-time dependence.</summary>
    public void AdvanceTo(ulong time)
    {
        if (time < _position || time > CurrentTime) throw new ArgumentOutOfRangeException(nameof(time));
        if (TryAdvanceSilence(time)) return;
        while (_position < time)
        {
            ulong end = Math.Min(time, _nextSample);
            foreach (Input input in _inputs)
            {
                Int128 area = input.Integrate(_position, end);
                if (input.UsesDeviceGain) input.DeviceArea += (double)area * input.DeviceGain;
                else input.Area += area;
            }
            _position = end;
            if (end != _nextSample) break;
            double mixedLeft = 0, mixedRight = 0;
            foreach (Input source in _inputs)
            {
                double input = (source.UsesDeviceGain ? source.DeviceArea : (double)source.Area) / ((double)(_nextSample - _sampleStart) * IntegralScale);
                source.Filtered = _dcDecay * (source.Filtered + input - source.PreviousInput); source.PreviousInput = input;
                double signal = source.Filtered * source.Amplitude * source.Gain;
                if ((source.Channels & 1) != 0) mixedLeft += signal;
                if ((source.Channels & 2) != 0) mixedRight += signal;
                source.Area = 0; source.DeviceArea = 0;
            }
            if (_count == 0) _blockStart = _sampleStart;
            _samples[_count++] = (float)Math.Clamp(mixedLeft, -1, 1);
            _samples[_count++] = (float)Math.Clamp(mixedRight, -1, 1);
            GeneratedFrames++; _sampleStart = _nextSample;
            _nextSample = (ulong)(((UInt128)(GeneratedFrames + 1) * 1_000_000_000 + SampleRate - 1) / SampleRate);
            if (_count == _samples.Length) Publish();
        }
    }
    /// <summary>Publishes a completed owned PCM block and retains a distinct buffer for subsequent guest output.</summary>
    private void Publish()
    {
        if (_count == 0) return;
        if (AudioReady is { } listener)
        {
            float[] block = _count == _samples.Length ? _samples : _samples[.._count];
            listener(new(_blockStart, SampleRate, 2, block)); _samples = new float[BlockFrames * 2];
        }
        _count = 0;
    }
    /// <summary>Flushes pending component output at its documented commit boundary.</summary>
    public void Flush() { AdvanceTo(CurrentTime); Publish(); }
}
