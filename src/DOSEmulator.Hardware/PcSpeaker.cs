using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>AT port 61h and time-integrated PC Speaker at 48-kHz Float32 stereo.</summary>
public sealed class PcSpeaker : IIoPortDevice
{
    public const int SampleRate = PcAudioMixer.SampleRate;
    private readonly DeviceScheduler _clock;
    private readonly PitSpeakerCounter _counter;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private byte _control;
    public event Action<AudioBlock>? AudioReady { add => _audio.AudioReady += value; remove => _audio.AudioReady -= value; }
    public long GeneratedFrames => _audio.GeneratedFrames;
    public double Volume { get => _input.Gain; set => _input.Gain = value; }
    public byte Control => _control;
    public bool Enabled => (_control & 2) != 0;
    public PcSpeaker(DeviceScheduler clock, PitSpeakerCounter counter, PcAudioMixer? audio = null)
    {
        _clock = clock; _counter = counter; _audio = audio ?? new(clock);
        _input = _audio.AddSource((start, end) => Enabled ? (Int128)_counter.HighArea(start, end) * 32768 : 0, 0.25);
        _counter.BeforeChange += () => AdvanceTo(_clock.Now);
    }
    public uint Read(ushort port, int width) => port == 0x61 && width == 1
        ? (uint)(_control | (_counter.Output ? 32 : 0) | ((_clock.Now / 15000 & 1) != 0 ? 16 : 0)) : 255;
    public void Write(ushort port, uint value, int width)
    {
        if (port != 0x61 || width != 1) return;
        AdvanceTo(_clock.Now); _control = (byte)(value & 15); _counter.SetGate((_control & 1) != 0);
    }
    public void AdvanceTo(ulong time) => _audio.AdvanceTo(time);
    public void Flush() => _audio.Flush();
}
