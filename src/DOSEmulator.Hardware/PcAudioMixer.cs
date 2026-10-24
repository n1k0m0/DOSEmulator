using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Time-integrated sources sharing one 48-kHz host timeline. Device clocks remain independent.</summary>
public sealed class PcAudioMixer(DeviceScheduler clock)
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
    public long GeneratedFrames { get; private set; }

    public sealed class Input
    {
        private readonly PcAudioMixer _mixer;
        internal readonly Func<ulong, ulong, Int128> Integrate;
        internal readonly double Amplitude;
        internal Int128 Area;
        internal double PreviousInput, Filtered;
        private double _gain = 1;
        internal Input(PcAudioMixer mixer, Func<ulong, ulong, Int128> integrate, double amplitude)
        { _mixer = mixer; Integrate = integrate; Amplitude = amplitude; }
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
    private ulong CurrentTime => clock.Now;
    public Input AddSource(Func<ulong, ulong, Int128> integrate, double amplitude)
    {
        ArgumentNullException.ThrowIfNull(integrate);
        if (clock.Now != 0 || _position != 0) throw new InvalidOperationException("Audio sources must be installed before the machine starts.");
        if (!double.IsFinite(amplitude) || amplitude is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amplitude));
        var input = new Input(this, integrate, amplitude); _inputs.Add(input); return input;
    }
    public void AdvanceTo(ulong time)
    {
        if (time < _position || time > CurrentTime) throw new ArgumentOutOfRangeException(nameof(time));
        while (_position < time)
        {
            ulong end = Math.Min(time, _nextSample);
            foreach (Input input in _inputs) input.Area += input.Integrate(_position, end);
            _position = end;
            if (end != _nextSample) break;
            double mixed = 0;
            foreach (Input source in _inputs)
            {
                double input = (double)source.Area / ((double)(_nextSample - _sampleStart) * IntegralScale);
                source.Filtered = _dcDecay * (source.Filtered + input - source.PreviousInput); source.PreviousInput = input;
                mixed += source.Filtered * source.Amplitude * source.Gain; source.Area = 0;
            }
            float output = (float)Math.Clamp(mixed, -1, 1);
            if (_count == 0) _blockStart = _sampleStart;
            _samples[_count++] = output; _samples[_count++] = output;
            GeneratedFrames++; _sampleStart = _nextSample;
            _nextSample = (ulong)(((UInt128)(GeneratedFrames + 1) * 1_000_000_000 + SampleRate - 1) / SampleRate);
            if (_count == _samples.Length) Publish();
        }
    }
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
    public void Flush() { AdvanceTo(CurrentTime); Publish(); }
}
