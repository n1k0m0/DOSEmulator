// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public enum OplEnvelopeStage { Off, Attack, Decay, Sustain, Release }
public sealed record OplOperatorStatus(OplEnvelopeStage Stage, double AttenuationDb, uint Phase, bool Keyed, double TotalLevelDb);

/// <summary>YM3812 development model with melodic/rhythm voices and timer-driven CSM keys/TL latches.
/// Synthesis uses native-sample timing; envelope microsteps, analog output and die pipelines remain approximate.
/// The FM clock is independent of the host mixer.</summary>
public sealed partial class Opl2 : IIoPortDevice
{
    public const uint MasterFrequency = 3_579_545;
    public const uint ClocksPerSample = 72;
    public const ulong AddressWaitNanoseconds = 3353, DataWaitNanoseconds = 23467;
    private readonly DeviceScheduler _clock;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private readonly ushort? _aliasBase;
    private readonly byte[] _registers = new byte[256];
    private readonly Timer[] _timers = [new(288), new(1152)];
    private readonly Operator[] _operators = Enumerable.Range(0, 18).Select(_ => new Operator()).ToArray();
    private readonly Channel[] _channels = Enumerable.Range(0, 9).Select(_ => new Channel()).ToArray();
    private byte _address;
    private ulong _busyUntil, _position, _nextSample = SampleTime(1);
    private long? _csmReleaseSample;
    private short _output;
    private uint _noise = 1;
    /// <summary>Diagnostic count of generated samples.</summary>
    public long GeneratedSamples { get; private set; }
    /// <summary>Diagnostic count of register writes.</summary>
    public long RegisterWrites { get; private set; }
    /// <summary>Diagnostic count of rejected writes.</summary>
    public long RejectedWrites { get; private set; }
    /// <summary>Diagnostic count of key on count.</summary>
    public long KeyOnCount { get; private set; }
    /// <summary>Diagnostic count of csm key pulses.</summary>
    public long CsmKeyPulses { get; private set; }
    /// <summary>Diagnostic count of timer1 overflows.</summary>
    public long Timer1Overflows => _timers[0].Overflows;
    /// <summary>Diagnostic count of timer2 overflows.</summary>
    public long Timer2Overflows => _timers[1].Overflows;
    /// <summary>FM timer status/output diagnostic; it is not an invented sound-card PIC IRQ connection.</summary>
    public bool TimerInterruptAsserted => _timers.Any(t => t.Flag);
    /// <summary>Whether the guest rhythm-enable register bit selects the implemented percussion topology.</summary>
    public bool RhythmEnabled => (_registers[0xbd] & 0x20) != 0;
    /// <summary>Deterministic reference noise-generator state used by rhythm synthesis.</summary>
    public uint NoiseState => _noise;
    /// <summary>Diagnostic indication that the guest selected rhythm outside the implemented reference contract.</summary>
    public bool UnsupportedRhythm => false;
    /// <summary>Whether the reference OPL2 CSM key-source mode is selected.</summary>
    public bool CsmEnabled => (_registers[8] & 0x80) != 0;
    /// <summary>Whether the sample-timed OPL2 CSM key pulse currently contributes a key source.</summary>
    public bool CsmPulseActive => _csmReleaseSample.HasValue;
    /// <summary>Diagnostic indication that the guest selected csm outside the implemented reference contract.</summary>
    public bool UnsupportedCsm => false;
    /// <summary>Diagnostic indication that the guest selected test outside the implemented reference contract.</summary>
    public bool UnsupportedTest => (_registers[1] & ~0x20) != 0;
    /// <summary>Diagnostic indication that the guest selected programming outside the implemented reference contract.</summary>
    public bool UnsupportedProgramming => UnsupportedRhythm || UnsupportedCsm || UnsupportedTest;
    /// <summary>Number of currently active reference FM operators for diagnostic reporting.</summary>
    public int ActiveOperators => _operators.Count(o => o.Stage != OplEnvelopeStage.Off);
    /// <summary>Current mono reference FM output sample.</summary>
    public short Output => _output;
    /// <summary>Host-side source gain, separate from guest DSP/mixer register state.</summary>
    public double Volume { get => _input.Gain; set => _input.Gain = value; }
    /// <summary>Initializes Opl2 and connects the supplied dependencies before the component is used.</summary>
    public Opl2(DeviceScheduler clock, PcAudioMixer audio, ushort? aliasBase = 0x228)
    {
        if (aliasBase is not (null or 0x228 or 0x248)) throw new ArgumentOutOfRangeException(nameof(aliasBase));
        _aliasBase = aliasBase;
        _clock = clock; _audio = audio; _input = audio.AddSource(Integrate, 0.5);
    }
    /// <summary>Returns the stored FM register for diagnostics without performing a guest port access.</summary>
    public byte InspectRegister(byte register) => _registers[register];
    /// <summary>Returns a diagnostic snapshot of the selected reference FM operator state.</summary>
    public OplOperatorStatus InspectOperator(int channel, bool carrier)
    {
        if ((uint)channel >= 9) throw new ArgumentOutOfRangeException(nameof(channel));
        Operator op = _operators[channel * 2 + (carrier ? 1 : 0)];
        return new(op.Stage, op.Attenuation, op.Phase, op.Keyed, op.TotalLevel);
    }
    /// <summary>Reads the reference OPL2 status through its implemented ISA aliases.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1 || (port != 0x388 && port != _aliasBase)) return 0xff;
        return (uint)(0x06 | (_timers[0].Flag ? 0x40 : 0) | (_timers[1].Flag ? 0x20 : 0) | (TimerInterruptAsserted ? 0x80 : 0));
    }
    /// <summary>Applies a guest address/data write to the reference OPL2 register and timer interface.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1 || (port is not (0x388 or 0x389) && port != _aliasBase && port != _aliasBase + 1)) return;
        if (_clock.Now < _busyUntil) { RejectedWrites++; return; }
        if ((port & 1) == 0) { _address = (byte)value; _busyUntil = checked(_clock.Now + AddressWaitNanoseconds); return; }
        _busyUntil = checked(_clock.Now + DataWaitNanoseconds);
        _audio.AdvanceTo(_clock.Now); RegisterWrites++;
        byte address = _address, data = (byte)value;
        if (address == 4) { TimerControl(data); return; }
        byte previous = _registers[address]; _registers[address] = data;
        if (address is 2 or 3) { _timers[address - 2].Preset = data; return; }
        if (address == 8)
        {
            if (!CsmEnabled) { ReleaseCsmKey(); LatchCsmLevels(); }
            for (int i = 0; i < 9; i++) UpdateChannel(i);
            return;
        }
        if (address == 0xbd)
        {
            UpdateRhythmKeys();
            return;
        }
        if (address is >= 0xa0 and <= 0xa8) { UpdateChannel(address - 0xa0); return; }
        if (address is >= 0xb0 and <= 0xb8)
        {
            int channel = address - 0xb0; UpdateChannel(channel);
            if (((previous ^ data) & 0x20) != 0) SetKey(channel, (data & 0x20) != 0 && (!RhythmEnabled || channel < 6));
            return;
        }
        if (address is >= 0xc0 and <= 0xc8)
        {
            Channel channel = _channels[address - 0xc0]; channel.Additive = (data & 1) != 0; channel.Feedback = (data >> 1) & 7; return;
        }
        int group = address & 0xe0, slot = Array.IndexOf(OperatorOffsets, address & 31);
        if (slot < 0 || group is not (0x20 or 0x40 or 0x60 or 0x80 or 0xe0)) return;
        UpdateOperator(slot); UpdateChannel(slot / 2);
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset()
    {
        _audio.AdvanceTo(_clock.Now); Array.Clear(_registers); _address = 0; _busyUntil = _clock.Now;
        foreach (Timer timer in _timers)
        {
            if (timer.Event != 0) _clock.Cancel(timer.Event);
            timer.Event = 0; timer.Preset = 0; timer.Running = timer.Masked = timer.Flag = false;
        }
        for (int i = 0; i < 18; i++) _operators[i] = new();
        for (int i = 0; i < 9; i++) _channels[i] = new();
        _output = 0; _csmReleaseSample = null; _noise = 1;
    }
    private sealed partial class Timer(uint unitClocks)
    {
        public readonly uint UnitClocks = unitClocks;
        public byte Preset;
        public bool Running, Masked, Flag;
        public long Event, Overflows;
        public ulong DeadlineClocks;
    }
    /// <summary>Applies timer start/mask/reset bits while retaining the model's separate sticky overflow state.</summary>
    private void TimerControl(byte data)
    {
        if ((data & 0x80) != 0) { foreach (Timer timer in _timers) timer.Flag = false; return; }
        _registers[4] = (byte)(data & 0x63);
        for (int i = 0; i < 2; i++)
        {
            Timer timer = _timers[i]; timer.Masked = (data & (0x40 >> i)) != 0;
            if (timer.Masked) timer.Flag = false;
            bool running = (data & (1 << i)) != 0;
            if (running == timer.Running) continue;
            timer.Running = running;
            if (!running) { if (timer.Event != 0) _clock.Cancel(timer.Event); timer.Event = 0; }
            else
            {
                timer.DeadlineClocks = (ulong)(((UInt128)_clock.Now * MasterFrequency + 999_999_999) / 1_000_000_000)
                    + (uint)(256 - timer.Preset) * timer.UnitClocks;
                ScheduleTimer(timer);
            }
        }
    }
    /// <summary>Schedules the next FM timer overflow on the chip's own reference clock.</summary>
    private void ScheduleTimer(Timer timer)
    {
        ulong deadline = (ulong)(((UInt128)timer.DeadlineClocks * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
        timer.Event = _clock.ScheduleAfter(deadline - _clock.Now, () =>
        {
            timer.Event = 0; timer.Overflows++; if (!timer.Masked) timer.Flag = true;
            if (ReferenceEquals(timer, _timers[0]) && CsmEnabled)
            {
                // Preserve the old waveform up to the timer edge. The model holds the CSM
                // key source through one native sample, then releases it. TL changes
                // become audible at the CSM edge. Subsample slot latency is not modeled.
                _audio.AdvanceTo(_clock.Now);
                LatchCsmLevels();
                foreach (Operator op in _operators) SetKeySource(op, 2, true);
                _csmReleaseSample = GeneratedSamples + 1; CsmKeyPulses++;
            }
            timer.DeadlineClocks += (uint)(256 - timer.Preset) * timer.UnitClocks;
            ScheduleTimer(timer);
        }, this, "event0", () => StateValues.Pack(Array.IndexOf(_timers, timer)));
    }
    /// <summary>Releases only the OPL2 CSM key source at its scheduled sample boundary.</summary>
    private void ReleaseCsmKey()
    {
        foreach (Operator op in _operators) SetKeySource(op, 2, false);
        _csmReleaseSample = null;
    }
    /// <summary>Captures the reference OPL2 CSM total levels at the documented timer/key boundary.</summary>
    private void LatchCsmLevels()
    {
        for (int i = 0; i < 18; i++) _operators[i].TotalLevel = (_registers[0x40 + OperatorOffsets[i]] & 63) * 0.75;
    }
    /// <summary>Converts a native FM sample index to virtual nanoseconds with integer rounding.</summary>
    private static ulong SampleTime(long sample) => (ulong)(((UInt128)(ulong)sample * ClocksPerSample * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
    /// <summary>Accumulates the reference source's signal area over the requested virtual interval.</summary>
    private Int128 Integrate(ulong start, ulong end)
    {
        if (start != _position) throw new InvalidOperationException("OPL integration must follow the shared mixer timeline.");
        Int128 area = 0;
        while (_position < end)
        {
            ulong boundary = Math.Min(end, _nextSample);
            area += (Int128)(boundary - _position) * PitSpeakerCounter.Frequency * _output;
            _position = boundary;
            if (boundary == _nextSample)
            {
                GenerateSample(); GeneratedSamples++; _nextSample = SampleTime(GeneratedSamples + 1);
            }
        }
        return area;
    }
}
