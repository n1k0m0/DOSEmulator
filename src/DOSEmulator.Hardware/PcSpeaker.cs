// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>AT port 61h and time-integrated PC Speaker at 48-kHz Float32 stereo.</summary>
public sealed partial class PcSpeaker : IIoPortDevice
{
    public const int SampleRate = PcAudioMixer.SampleRate;
    private readonly DeviceScheduler _clock;
    private readonly PitSpeakerCounter _counter;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private byte _control;
    public event Action<AudioBlock>? AudioReady { add => _audio.AudioReady += value; remove => _audio.AudioReady -= value; }
    /// <summary>Diagnostic count of generated frames.</summary>
    public long GeneratedFrames => _audio.GeneratedFrames;
    /// <summary>Host-side source gain, separate from guest DSP/mixer register state.</summary>
    public double Volume { get => _input.Gain; set => _input.Gain = value; }
    /// <summary>Current guest-programmed component control latch.</summary>
    public byte Control => _control;
    /// <summary>Whether this component's active configuration permits its normal data/sample path.</summary>
    public bool Enabled => (_control & 2) != 0;
    /// <summary>Initializes PcSpeaker and connects the supplied dependencies before the component is used.</summary>
    public PcSpeaker(DeviceScheduler clock, PitSpeakerCounter counter, PcAudioMixer? audio = null)
    {
        _clock = clock; _counter = counter; _audio = audio ?? new(clock);
        _input = _audio.AddSource((start, end) => Enabled ? (Int128)_counter.HighArea(start, end) * 32768 : 0, 0.25, () => !Enabled);
        _counter.BeforeChange += () => AdvanceTo(_clock.Now);
    }
    /// <summary>Reads the reference port-61 control/status bits associated with the PIT speaker channel.</summary>
    public uint Read(ushort port, int width) => port == 0x61 && width == 1
        ? (uint)(_control | (_counter.Output ? 32 : 0) | ((_clock.Now / 15000 & 1) != 0 ? 16 : 0)) : 255;
    /// <summary>Changes speaker gating/control only after integrating the old signal through the current virtual instant.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (port != 0x61 || width != 1) return;
        AdvanceTo(_clock.Now); _control = (byte)(value & 15); _counter.SetGate((_control & 1) != 0);
    }
    /// <summary>Runs due callbacks in deadline/order sequence and advances the virtual clock without host-time dependence.</summary>
    public void AdvanceTo(ulong time) => _audio.AdvanceTo(time);
    /// <summary>Flushes pending component output at its documented commit boundary.</summary>
    public void Flush() => _audio.Flush();
}
