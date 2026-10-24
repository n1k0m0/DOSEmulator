using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>SB2 development DSP path: unsigned mono DAC, normal/high-speed 8-bit output DMA,
/// DSP blocks and IRQ5. ADPCM, recording and MIDI are pending; FM is a separate device.</summary>
public sealed class SoundBlasterDsp : IIoPortDevice
{
    public const ushort BasePort = 0x220;
    public const int DmaChannel = 1, Irq = 5;
    public const ulong ResetHoldNanoseconds = 3000, ResetReadyNanoseconds = 100000, CommandNanoseconds = 1000;
    private readonly DeviceScheduler _clock;
    private readonly Dma8237 _dma;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private readonly Action<bool> _irq;
    private readonly Queue<byte> _readBuffer = new();
    private readonly byte[] _parameters = new byte[2];
    private byte _command, _dac = 128, _timeConstant;
    private int _parameterCount, _parameterLength, _blockLength = 65536, _remaining;
    private int? _queuedSingleLength;
    private bool _resetHigh, _ready, _commandBusy, _autoInit, _exitAuto, _silence;
    private ulong _resetSince, _sampleDeadline, _pausedDelay;
    private long _resetEvent, _commandEvent, _sampleEvent;
    public bool SpeakerEnabled { get; private set; }
    public bool HighSpeed { get; private set; }
    public bool Active => _remaining > 0;
    public bool Paused { get; private set; }
    public bool InterruptAsserted { get; private set; }
    public int RemainingSamples => _remaining;
    public byte DacValue => _dac;
    public byte TimeConstant => _timeConstant;
    public ulong SamplePeriodNanoseconds => (ulong)(256 - _timeConstant) * 1000;
    public long TransferredSamples { get; private set; }
    public long HighSpeedSamples { get; private set; }
    public long HighSpeedTransfers { get; private set; }
    public long HighSpeedResets { get; private set; }
    public long IgnoredHighSpeedWrites { get; private set; }
    public long CompletedBlocks { get; private set; }
    public long InterruptRequests { get; private set; }
    public long ResetCount { get; private set; }
    public long RejectedWrites { get; private set; }
    public long UnsupportedCommands { get; private set; }
    public byte? LastUnsupportedCommand { get; private set; }
    public DmaReadResult LastDmaResult { get; private set; } = DmaReadResult.Blocked;
    public double Volume { get => _input.Gain; set => _input.Gain = value; }

    public SoundBlasterDsp(DeviceScheduler clock, Dma8237 dma, PcAudioMixer audio, Action<bool> irq)
    {
        _clock = clock; _dma = dma; _audio = audio; _irq = irq;
        _input = audio.AddSource((start, end) => SpeakerEnabled
            ? (Int128)(end - start) * PitSpeakerCounter.Frequency * (_dac - 128) * 256 : 0, 0.5);
    }
    public uint Read(ushort port, int width)
    {
        if (width != 1) return 0xff;
        switch (port - BasePort)
        {
            case 0x0a: return _readBuffer.TryDequeue(out byte value) ? value : 0xffu;
            case 0x0c: return !_ready || _resetHigh || _commandBusy || HighSpeed ? 0x80u : 0;
            case 0x0e: SetIrq(false); return _readBuffer.Count > 0 ? 0x80u : 0;
            default: return 0xff;
        }
    }
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == BasePort + 6) { Reset((value & 1) != 0); return; }
        if (port != BasePort + 0x0c) return;
        // The 2.01 DSP services only the DMA loop in this mode. No command or parameter
        // parser state may leak through writes made while the command port is busy.
        if (HighSpeed) { IgnoredHighSpeedWrites++; return; }
        if (!_ready || _resetHigh || _commandBusy) { RejectedWrites++; return; }
        _commandBusy = true;
        byte data = (byte)value;
        _commandEvent = _clock.ScheduleAfter(CommandNanoseconds, () =>
        {
            _commandEvent = 0; _commandBusy = false; Receive(data);
        });
    }
    private void Reset(bool high)
    {
        if (high)
        {
            if (_resetHigh) return;
            _audio.AdvanceTo(_clock.Now);
            bool preserveParameters = HighSpeed;
            if (preserveParameters) HighSpeedResets++;
            _resetHigh = true; _resetSince = _clock.Now; _ready = false; _commandBusy = false;
            Cancel(ref _resetEvent); Cancel(ref _commandEvent); Stop();
            _readBuffer.Clear(); _parameterCount = _parameterLength = 0;
            _queuedSingleLength = null;
            if (!preserveParameters) { _timeConstant = 0; _blockLength = 65536; _dac = 128; SpeakerEnabled = false; }
            SetIrq(false); ResetCount++;
        }
        else if (_resetHigh)
        {
            _resetHigh = false;
            if (_clock.Now - _resetSince < ResetHoldNanoseconds) return;
            _resetEvent = _clock.ScheduleAfter(ResetReadyNanoseconds, () =>
            {
                _resetEvent = 0; _ready = true; _readBuffer.Enqueue(0xaa);
            });
        }
    }
    private void Receive(byte value)
    {
        if (_parameterLength > 0)
        {
            _parameters[_parameterCount++] = value;
            if (_parameterCount < _parameterLength) return;
            _parameterCount = _parameterLength = 0; Execute(); return;
        }
        _command = value;
        _parameterLength = value switch
        {
            0x10 or 0x40 => 1,
            0x14 or 0x48 or 0x80 => 2,
            // Consume documented parameters even when their hardware function is still missing.
            0x16 or 0x17 or 0x24 or 0x74 or 0x75 or 0x76 or 0x77 => 2,
            0x38 => 1,
            _ => 0
        };
        _parameterCount = 0;
        if (_parameterLength == 0) Execute();
    }
    private void Execute()
    {
        int length = (_parameters[0] | _parameters[1] << 8) + 1;
        switch (_command)
        {
            case 0x10: SetDac(_parameters[0]); break;
            case 0x14:
                if (Active && _autoInit) { _queuedSingleLength = length; _exitAuto = true; }
                else Start(length, false, false);
                break;
            case 0x1c: Start(_blockLength, true, false); break;
            case 0x40: _timeConstant = _parameters[0]; break;
            case 0x48: _blockLength = length; break;
            case 0x80: Start(length, false, true); break;
            case 0x90: Start(_blockLength, true, false, true); break;
            case 0x91: Start(_blockLength, false, false, true); break;
            case 0xd0:
                if (Active && !Paused)
                {
                    _pausedDelay = _sampleDeadline > _clock.Now ? _sampleDeadline - _clock.Now : SamplePeriodNanoseconds;
                    Cancel(ref _sampleEvent); Paused = true;
                }
                break;
            case 0xd1: SetSpeaker(true); break;
            case 0xd3: SetSpeaker(false); break;
            case 0xd4:
                if (Active && Paused) { Paused = false; ScheduleSample(_pausedDelay); }
                break;
            case 0xd8: Reply(SpeakerEnabled ? (byte)255 : (byte)0); break;
            case 0xda: if (_autoInit) _exitAuto = true; break;
            case 0xe1: Reply(2); Reply(1); break;
            default: UnsupportedCommands++; LastUnsupportedCommand = _command; break;
        }
    }
    private void Reply(byte value)
    {
        // Finite response FIFO; a guest which never reads cannot grow host memory without bound.
        if (_readBuffer.Count < 64) _readBuffer.Enqueue(value);
    }
    private void Start(int length, bool autoInit, bool silence, bool highSpeed = false)
    {
        Stop(); _remaining = length; _autoInit = autoInit; _silence = silence;
        HighSpeed = highSpeed; if (highSpeed) HighSpeedTransfers++;
        _queuedSingleLength = null; _exitAuto = false; ScheduleSample(SamplePeriodNanoseconds);
    }
    private void Stop()
    {
        Cancel(ref _sampleEvent); _remaining = 0; Paused = false; _autoInit = false; _exitAuto = false;
        HighSpeed = false;
    }
    private void ScheduleSample(ulong delay)
    {
        _sampleDeadline = checked(_clock.Now + delay);
        _sampleEvent = _clock.ScheduleAfter(delay, Tick);
    }
    private void Tick()
    {
        _sampleEvent = 0;
        if (!_silence)
        {
            LastDmaResult = _dma.TryReadByte(DmaChannel, out byte value);
            if (LastDmaResult != DmaReadResult.Transferred) { ScheduleSample(SamplePeriodNanoseconds); return; }
            SetDac(value); TransferredSamples++; if (HighSpeed) HighSpeedSamples++;
        }
        if (--_remaining == 0)
        {
            CompletedBlocks++; InterruptRequests++; SetIrq(true);
            if (_queuedSingleLength is { } length) { Start(length, false, false); return; }
            if (!_autoInit || _exitAuto) { Stop(); return; }
            _remaining = _blockLength;
        }
        ScheduleSample(SamplePeriodNanoseconds);
    }
    private void SetDac(byte value) { _audio.AdvanceTo(_clock.Now); _dac = value; }
    private void SetSpeaker(bool enabled) { _audio.AdvanceTo(_clock.Now); SpeakerEnabled = enabled; }
    private void SetIrq(bool high)
    {
        if (InterruptAsserted == high) return;
        InterruptAsserted = high; _irq(high);
    }
    private void Cancel(ref long id) { if (id != 0) _clock.Cancel(id); id = 0; }
}
