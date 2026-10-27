// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>YMF262 functional reference. Native-sample mathematical synthesis, not a die/slot or DAC model.</summary>
public sealed partial class Opl3 : IIoPortDevice
{
    public const string Model = "YMF262 / 4MF262A6 functional reference";
    public const uint MasterFrequency = 14_318_180, ClocksPerSample = 288;
    private readonly DeviceScheduler _clock;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _left, _right;
    private readonly ushort _base;
    private readonly byte[] _registers = new byte[512];
    private readonly Operator[] _operators = Enumerable.Range(0, 36).Select(_ => new Operator()).ToArray();
    private readonly Channel[] _channels = Enumerable.Range(0, 18).Select(_ => new Channel()).ToArray();
    private readonly Timer[] _timers = [new(1152), new(4608)];
    private readonly short[] _outputs = new short[4];
    private readonly Int128[] _areas = new Int128[4];
    private ushort _address;
    private byte _pairMask;
    private uint _noise = 1;
    private ulong _position, _nextSample = SampleTime(1), _lastStart = ulong.MaxValue, _lastEnd;
    /// <summary>Diagnostic count of generated samples.</summary>
    public long GeneratedSamples { get; private set; }
    /// <summary>Diagnostic count of register writes.</summary>
    public long RegisterWrites { get; private set; }
    /// <summary>Diagnostic count of rejected writes.</summary>
    public long RejectedWrites { get; private set; }
    /// <summary>Diagnostic count of ignored bank writes.</summary>
    public long IgnoredBankWrites { get; private set; }
    /// <summary>Diagnostic count of compatibility aliases.</summary>
    public long CompatibilityAliases { get; private set; }
    /// <summary>Diagnostic count of unsupported writes.</summary>
    public long UnsupportedWrites { get; private set; }
    /// <summary>Diagnostic count of key on count.</summary>
    public long KeyOnCount { get; private set; }
    /// <summary>Whether the guest enabled the YMF262 NEW register mode.</summary>
    public bool NewMode => (_registers[0x105] & 1) != 0;
    /// <summary>Six-bit guest selection of the supported OPL3 four-operator channel pairs.</summary>
    public byte FourOperatorMask => _pairMask;
    /// <summary>Whether the guest rhythm-enable register bit selects the implemented percussion topology.</summary>
    public bool RhythmEnabled => (_registers[0xbd] & 0x20) != 0;
    /// <summary>FM timer status/output diagnostic; it is not an invented sound-card PIC IRQ connection.</summary>
    public bool TimerInterruptAsserted => _timers.Any(t => t.Flag);
    /// <summary>Diagnostic count of timer1 overflows.</summary>
    public long Timer1Overflows => _timers[0].Overflows;
    /// <summary>Diagnostic count of timer2 overflows.</summary>
    public long Timer2Overflows => _timers[1].Overflows;
    /// <summary>Number of currently active reference FM operators for diagnostic reporting.</summary>
    public int ActiveOperators => _operators.Count(o => o.Stage != OplEnvelopeStage.Off);
    /// <summary>Answers the existing synthesis guard without counting every active operator for diagnostics.</summary>
    private bool HasActiveOperators
    {
        get
        {
            foreach (Operator op in _operators) if (op.Stage != OplEnvelopeStage.Off) return true;
            return false;
        }
    }
    /// <summary>Deterministic reference noise-generator state used by rhythm synthesis.</summary>
    public uint NoiseState => _noise;
    /// <summary>Guest analog FM/voice gain applied to the card's left output route.</summary>
    public double LeftMixerGain => _left.DeviceGain;
    /// <summary>Guest analog FM/voice gain applied to the card's right output route.</summary>
    public double RightMixerGain => _right.DeviceGain;
    public sealed record QuadSample(ulong Time, short A, short B, short C, short D);
    public event Action<QuadSample>? SampleGenerated;
    /// <summary>Host-side source gain, separate from guest DSP/mixer register state.</summary>
    public double Volume { get => _left.Gain; set { _left.Gain = value; _right.Gain = value; } }
    /// <summary>Applies separate left/right guest analog gains at the current virtual-time boundary.</summary>
    public void SetMixerGains(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right) || left is < 0 or > 64 || right is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(left));
        _left.DeviceGain = left; _right.DeviceGain = right;
    }
    /// <summary>Initializes Opl3 and connects the supplied dependencies before the component is used.</summary>
    public Opl3(DeviceScheduler clock, PcAudioMixer audio, ushort basePort = 0x220)
    {
        if (basePort is not (0x220 or 0x240 or 0x260 or 0x280)) throw new ArgumentOutOfRangeException(nameof(basePort));
        _clock = clock; _audio = audio; _base = basePort;
        (_left, _right) = audio.AddStereoSource((start, end) => Integrate(start, end, 0), (start, end) => Integrate(start, end, 1), 0.5);
    }
    /// <summary>Returns the selected raw OPL3 bus diagnostic independently of the board's stereo wiring.</summary>
    public short InspectOutput(int output) => _outputs[output];
    /// <summary>Returns the stored FM register for diagnostics without performing a guest port access.</summary>
    public byte InspectRegister(int address) => _registers[address];
    /// <summary>Returns a diagnostic snapshot of the selected reference FM operator state.</summary>
    public OplOperatorStatus InspectOperator(int channel, bool carrier)
    {
        if ((uint)channel >= 18) throw new ArgumentOutOfRangeException(nameof(channel));
        Operator op = _operators[channel * 2 + (carrier ? 1 : 0)];
        return new(op.Stage, op.Attenuation, op.Phase, op.Keyed, op.TotalLevel);
    }
    /// <summary>Converts a checked VGA plane/address pair to its backing-store offset.</summary>
    private int Offset(ushort port)
        => port is >= 0x388 and <= 0x38b ? port - 0x388
            : port >= _base && port < _base + 4 ? port - _base
            : port >= _base + 8 && port < _base + 10 ? port - _base - 8 : -1;
    /// <summary>Reads the reference YMF262 timer/status interface through the board's shared port aliases.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1 || Offset(port) != 0) return 0xff;
        return (uint)((_timers[0].Flag ? 0x40 : 0) | (_timers[1].Flag ? 0x20 : 0) | (TimerInterruptAsserted ? 0x80 : 0));
    }
    /// <summary>Applies a guest address/data write with the shared nine-bit latch and OPL2/NEW compatibility rules.</summary>
    public void Write(ushort port, uint value, int width)
    {
        int offset = Offset(port);
        if (width != 1 || offset < 0) { RejectedWrites++; return; }
        if ((offset & 1) == 0)
        {
            bool upper = offset == 2 && (NewMode || (byte)value == 5);
            if (offset == 2 && !upper) CompatibilityAliases++;
            _address = (ushort)((byte)value | (upper ? 0x100 : 0)); return;
        }
        // A single 9-bit latch is shared by every board alias and both data ports.
        _audio.AdvanceTo(_clock.Now);
        int address = _address; byte data = (byte)value;
        if (address >= 0x100 && address != 0x105 && !NewMode) { IgnoredBankWrites++; return; }
        RegisterWrites++;
        if (address == 4) { TimerControl(data); return; }
        _registers[address] = data;
        if (address is 2 or 3) { _timers[address - 2].Preset = data; return; }
        if (address == 0x105) { if ((data & ~1) != 0) UnsupportedWrites++; return; }
        if (address == 0x104) { _pairMask = (byte)(data & 63); RefreshTopology(); return; }
        if (address == 1) { if ((data & ~0x20) != 0) UnsupportedWrites++; return; }
        if (address == 8)
        {
            if ((data & ~0x40) != 0) UnsupportedWrites++; // YMF262 has no CSM key source.
            RefreshTopology(); return;
        }
        if (address == 0xbd) { UpdateRhythmKeys(); return; }
        int bank = address >> 8, reg = address & 255;
        if (reg is >= 0xa0 and <= 0xa8 or >= 0xb0 and <= 0xb8)
        { RefreshTopology(); return; }
        if (reg is >= 0xc0 and <= 0xc8)
        {
            Channel ch = _channels[bank * 9 + reg - 0xc0]; ch.Additive = (data & 1) != 0;
            ch.Feedback = (data >> 1) & 7; ch.Routing = NewMode ? data >> 4 : 3; return;
        }
        int group = reg & 0xe0, slot = Array.IndexOf(OperatorOffsets, reg & 31);
        if (slot < 0 || group is not (0x20 or 0x40 or 0x60 or 0x80 or 0xe0)) { UnsupportedWrites++; return; }
        slot += bank * 18; UpdateOperator(slot);
        if (group == 0xe0) _operators[slot].Wave = data & (NewMode ? 7 : 3);
        RefreshTopology();
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset()
    {
        _audio.AdvanceTo(_clock.Now); Array.Clear(_registers); _address = 0; _pairMask = 0;
        foreach (Timer timer in _timers)
        {
            if (timer.Event != 0) _clock.Cancel(timer.Event);
            timer.Event = 0; timer.Preset = 0; timer.Running = timer.Masked = timer.Flag = false;
        }
        for (int i = 0; i < 36; i++) _operators[i] = new();
        for (int i = 0; i < 18; i++) _channels[i] = new();
        Array.Clear(_outputs); _noise = 1;
    }
    private sealed partial class Timer(uint clocks)
    {
        public readonly uint Clocks = clocks;
        public byte Preset;
        public bool Running, Masked, Flag;
        public long Event, Overflows;
        public ulong Deadline;
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
            bool start = (data & (1 << i)) != 0;
            if (start == timer.Running) continue;
            timer.Running = start;
            if (!start) { if (timer.Event != 0) _clock.Cancel(timer.Event); timer.Event = 0; }
            else
            {
                timer.Deadline = (ulong)(((UInt128)_clock.Now * MasterFrequency + 999_999_999) / 1_000_000_000)
                    + (uint)(256 - timer.Preset) * timer.Clocks;
                ScheduleTimer(timer);
            }
        }
    }
    /// <summary>Schedules the next FM timer overflow on the chip's own reference clock.</summary>
    private void ScheduleTimer(Timer timer)
    {
        ulong due = (ulong)(((UInt128)timer.Deadline * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
        timer.Event = _clock.ScheduleAfter(due - _clock.Now, () =>
        {
            timer.Event = 0; timer.Overflows++; if (!timer.Masked) timer.Flag = true;
            timer.Deadline += (uint)(256 - timer.Preset) * timer.Clocks; ScheduleTimer(timer);
        }, this, "event0", () => StateValues.Pack(Array.IndexOf(_timers, timer)));
    }
    /// <summary>Converts a native FM sample index to virtual nanoseconds with integer rounding.</summary>
    private static ulong SampleTime(long sample) => (ulong)(((UInt128)(ulong)sample * ClocksPerSample * 1_000_000_000 + MasterFrequency - 1) / MasterFrequency);
    /// <summary>Accumulates the reference source's signal area over the requested virtual interval.</summary>
    private Int128 Integrate(ulong start, ulong end, int output)
    {
        if (start == _lastStart && end == _lastEnd) return _areas[output];
        if (start != _position) throw new InvalidOperationException("OPL3 must follow the shared stereo mixer timeline.");
        _lastStart = start; _lastEnd = end; Array.Clear(_areas);
        while (_position < end)
        {
            ulong boundary = Math.Min(end, _nextSample);
            for (int i = 0; i < 4; i++) _areas[i] += (Int128)(boundary - _position) * PitSpeakerCounter.Frequency * _outputs[i];
            _position = boundary;
            if (boundary != _nextSample) continue;
            GenerateSample(); GeneratedSamples++;
            SampleGenerated?.Invoke(new(boundary, _outputs[0], _outputs[1], _outputs[2], _outputs[3]));
            _nextSample = SampleTime(GeneratedSamples + 1);
        }
        return _areas[output];
    }
}
