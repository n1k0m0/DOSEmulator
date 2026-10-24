using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public enum OplEnvelopeStage { Off, Attack, Decay, Sustain, Release }
public sealed record OplOperatorStatus(OplEnvelopeStage Stage, double AttenuationDb, uint Phase, bool Keyed);

/// <summary>Own YM3812 development model. Nine melodic two-operator voices; analog/die-exact output,
/// rhythm and exact CSM pipeline/TL latching are pending. Timer-driven CSM key pulses are modeled.
/// The FM clock is independent of the host mixer.</summary>
public sealed partial class Opl2 : IIoPortDevice
{
    public const uint MasterFrequency = 3_579_545;
    public const uint ClocksPerSample = 72;
    public const ulong AddressWaitNanoseconds = 3353, DataWaitNanoseconds = 23467;
    private readonly DeviceScheduler _clock;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private readonly byte[] _registers = new byte[256];
    private readonly Timer[] _timers = [new(288), new(1152)];
    private readonly Operator[] _operators = Enumerable.Range(0, 18).Select(_ => new Operator()).ToArray();
    private readonly Channel[] _channels = Enumerable.Range(0, 9).Select(_ => new Channel()).ToArray();
    private byte _address;
    private ulong _busyUntil, _position, _nextSample = SampleTime(1);
    private long? _csmReleaseSample;
    private short _output;
    public long GeneratedSamples { get; private set; }
    public long RegisterWrites { get; private set; }
    public long RejectedWrites { get; private set; }
    public long KeyOnCount { get; private set; }
    public long CsmKeyPulses { get; private set; }
    public long Timer1Overflows => _timers[0].Overflows;
    public long Timer2Overflows => _timers[1].Overflows;
    public bool TimerInterruptAsserted => _timers.Any(t => t.Flag);
    public bool UnsupportedRhythm => (_registers[0xbd] & 0x20) != 0;
    public bool CsmEnabled => (_registers[8] & 0x80) != 0;
    public bool CsmPulseActive => _csmReleaseSample.HasValue;
    // The keying path works; full CSM accuracy still needs independent silicon evidence.
    public bool UnsupportedCsm => CsmEnabled;
    public bool UnsupportedTest => (_registers[1] & ~0x20) != 0;
    public bool UnsupportedProgramming => UnsupportedRhythm || UnsupportedCsm || UnsupportedTest;
    public int ActiveOperators => _operators.Count(o => o.Stage != OplEnvelopeStage.Off);
    public short Output => _output;
    public double Volume { get => _input.Gain; set => _input.Gain = value; }
    public Opl2(DeviceScheduler clock, PcAudioMixer audio)
    {
        _clock = clock; _audio = audio; _input = audio.AddSource(Integrate, 0.5);
    }
    public byte InspectRegister(byte register) => _registers[register];
    public OplOperatorStatus InspectOperator(int channel, bool carrier)
    {
        if ((uint)channel >= 9) throw new ArgumentOutOfRangeException(nameof(channel));
        Operator op = _operators[channel * 2 + (carrier ? 1 : 0)];
        return new(op.Stage, op.Attenuation, op.Phase, op.Keyed);
    }
    public uint Read(ushort port, int width)
    {
        if (width != 1 || port is not (0x388 or 0x228)) return 0xff;
        return (uint)(0x06 | (_timers[0].Flag ? 0x40 : 0) | (_timers[1].Flag ? 0x20 : 0) | (TimerInterruptAsserted ? 0x80 : 0));
    }
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1 || port is not (0x388 or 0x389 or 0x228 or 0x229)) return;
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
            if (!CsmEnabled) ReleaseCsmKey();
            for (int i = 0; i < 9; i++) UpdateChannel(i);
            return;
        }
        if (address == 0xbd)
        {
            for (int i = 6; i < 9; i++) SetKey(i, !UnsupportedRhythm && (_registers[0xb0 + i] & 0x20) != 0);
            return;
        }
        if (address is >= 0xa0 and <= 0xa8) { UpdateChannel(address - 0xa0); return; }
        if (address is >= 0xb0 and <= 0xb8)
        {
            int channel = address - 0xb0; UpdateChannel(channel);
            if (((previous ^ data) & 0x20) != 0) SetKey(channel, (data & 0x20) != 0 && (!UnsupportedRhythm || channel < 6));
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
        _output = 0; _csmReleaseSample = null;
    }
    private sealed class Timer(uint unitClocks)
    {
        public readonly uint UnitClocks = unitClocks;
        public byte Preset;
        public bool Running, Masked, Flag;
        public long Event, Overflows;
        public ulong DeadlineClocks;
    }
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
    private void ScheduleTimer(Timer timer)
    {
        ulong deadline = (ulong)(((UInt128)timer.DeadlineClocks * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
        timer.Event = _clock.ScheduleAfter(deadline - _clock.Now, () =>
        {
            timer.Event = 0; timer.Overflows++; if (!timer.Masked) timer.Flag = true;
            if (ReferenceEquals(timer, _timers[0]) && CsmEnabled)
            {
                // Preserve the old waveform up to the timer edge. The model holds the CSM
                // key source through one native sample, then releases it. Exact slot-level
                // pulse latency/width and CSM total-level latching are not calibrated yet.
                _audio.AdvanceTo(_clock.Now);
                foreach (Operator op in _operators) SetKeySource(op, 2, true);
                _csmReleaseSample = GeneratedSamples + 1; CsmKeyPulses++;
            }
            timer.DeadlineClocks += (uint)(256 - timer.Preset) * timer.UnitClocks;
            ScheduleTimer(timer);
        });
    }
    private void ReleaseCsmKey()
    {
        foreach (Operator op in _operators) SetKeySource(op, 2, false);
        _csmReleaseSample = null;
    }
    private static ulong SampleTime(long sample) => (ulong)(((UInt128)(ulong)sample * ClocksPerSample * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
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
