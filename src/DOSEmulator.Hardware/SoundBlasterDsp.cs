// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>SB2 development DSP path: unsigned mono DAC/ADC, normal/high-speed 8-bit DMA,
/// Creative ADPCM, DSP blocks and configurable IRQ. FM is a separate device.</summary>
public sealed partial class SoundBlasterDsp : IIoPortDevice
{
    public const ushort BasePort = 0x220;
    public const int DmaChannel = 1, Irq = 5;
    public const ulong ResetHoldNanoseconds = 3000, ResetReadyNanoseconds = 100000, CommandNanoseconds = 1000;
    /// <summary>Current guest-visible ISA base port for this concrete sound-card model.</summary>
    public ushort IoBasePort { get; }
    private readonly DeviceScheduler _clock;
    private readonly Dma8237 _dma;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _input;
    private readonly Action<bool> _irq;
    private readonly Queue<byte> _readBuffer = new();
    private readonly byte[] _parameters = new byte[2];
    private byte _command, _dac = 128, _adc = 128, _timeConstant;
    private int _parameterCount, _parameterLength, _blockLength = 65536, _remaining;
    private int? _queuedSingleLength;
    private readonly CreativeAdpcmDecoder _adpcm = new();
    private int _adpcmBits, _adpcmIndex, _queuedSingleBits;
    private byte _adpcmPacked;
    private bool _adpcmReferencePending, _queuedSingleReference;
    private bool _resetHigh, _ready, _commandBusy, _autoInit, _exitAuto, _silence, _recording, _queuedSingleRecording;
    private ulong _resetSince, _sampleDeadline, _pausedDelay;
    private long _resetEvent, _commandEvent, _sampleEvent;
    /// <summary>DSP speaker-status latch, whose audible effect follows the selected card revision's contract.</summary>
    public bool SpeakerEnabled { get; private set; }
    /// <summary>Whether the SB2 DSP is in its restricted high-speed transfer command mode.</summary>
    public bool HighSpeed { get; private set; }
    /// <summary>Whether a DSP transfer/sample engine is currently active.</summary>
    public bool Active => _remaining > 0;
    /// <summary>Whether the active DSP engine is paused by a guest control command.</summary>
    public bool Paused { get; private set; }
    /// <summary>Externally observable interrupt line after the component's enable/mask policy.</summary>
    public bool InterruptAsserted { get; private set; }
    /// <summary>Remaining sample/components in the current programmed DSP block.</summary>
    public int RemainingSamples => _remaining;
    /// <summary>Most recent sample value held by the reference byte DAC.</summary>
    public byte DacValue => _dac;
    /// <summary>Most recent sample value produced by the virtual byte ADC.</summary>
    public byte AdcValue => _adc;
    /// <summary>Whether the active DSP stream reads the virtual ADC input instead of playing PCM.</summary>
    public bool Recording => Active && _recording;
    /// <summary>Active Creative ADPCM encoded component width.</summary>
    public int AdpcmBits => Active ? _adpcmBits : 0;
    /// <summary>Number of actual encoded ADPCM bytes obtained from device-paced DMA grants.</summary>
    public long AdpcmDmaBytes { get; private set; }
    /// <summary>Diagnostic count of adpcm decoded samples.</summary>
    public long AdpcmDecodedSamples { get; private set; }
    /// <summary>Machine-owned virtual analog source. Null is an unconnected midpoint input;
    /// no host microphone is opened. Evaluate against virtual time, not wall-clock time.</summary>
    public Func<ulong, byte>? AdcInput { get; set; }
    public event Action<ulong, byte, DmaReadResult?>? AdcSampled;
    /// <summary>Raw guest time-constant byte controlling the legacy DSP sample period.</summary>
    public byte TimeConstant => _timeConstant;
    /// <summary>Current integer legacy DSP sample period in virtual nanoseconds.</summary>
    public ulong SamplePeriodNanoseconds => (ulong)(256 - _timeConstant) * 1000;
    /// <summary>Diagnostic count of transferred samples.</summary>
    public long TransferredSamples { get; private set; }
    /// <summary>Diagnostic count of recorded samples.</summary>
    public long RecordedSamples { get; private set; }
    /// <summary>Diagnostic count of converted samples.</summary>
    public long ConvertedSamples { get; private set; }
    /// <summary>Diagnostic count of high speed recorded samples.</summary>
    public long HighSpeedRecordedSamples { get; private set; }
    /// <summary>Diagnostic count of high speed samples.</summary>
    public long HighSpeedSamples { get; private set; }
    /// <summary>Diagnostic count of high speed transfers.</summary>
    public long HighSpeedTransfers { get; private set; }
    /// <summary>Diagnostic count of high speed resets.</summary>
    public long HighSpeedResets { get; private set; }
    /// <summary>Diagnostic count of ignored high speed writes.</summary>
    public long IgnoredHighSpeedWrites { get; private set; }
    /// <summary>Diagnostic count of completed blocks.</summary>
    public long CompletedBlocks { get; private set; }
    /// <summary>Diagnostic count of interrupt requests.</summary>
    public long InterruptRequests { get; private set; }
    /// <summary>Diagnostic count of reset count.</summary>
    public long ResetCount { get; private set; }
    /// <summary>Diagnostic count of rejected writes.</summary>
    public long RejectedWrites { get; private set; }
    /// <summary>Diagnostic indication that the guest selected commands outside the implemented reference contract.</summary>
    public long UnsupportedCommands { get; private set; }
    /// <summary>Most recent unsupported DSP command byte retained for diagnostics.</summary>
    public byte? LastUnsupportedCommand { get; private set; }
    /// <summary>Most recent actual DMA-grant result, separate from a completed sound sample.</summary>
    public DmaReadResult LastDmaResult { get; private set; } = DmaReadResult.Blocked;
    /// <summary>Host-side source gain, separate from guest DSP/mixer register state.</summary>
    public double Volume { get => _input.Gain; set => _input.Gain = value; }

    /// <summary>Initializes SoundBlasterDsp and connects the supplied dependencies before the component is used.</summary>
    public SoundBlasterDsp(DeviceScheduler clock, Dma8237 dma, PcAudioMixer audio, Action<bool> irq, ushort basePort = BasePort)
    {
        if (basePort is not (0x220 or 0x240)) throw new ArgumentOutOfRangeException(nameof(basePort));
        IoBasePort = basePort;
        _clock = clock; _dma = dma; _audio = audio; _irq = irq;
        _input = audio.AddSource((start, end) => SpeakerEnabled
            ? (Int128)(end - start) * PitSpeakerCounter.Frequency * (_dac - 128) * 256 : 0, 0.5, () => !SpeakerEnabled || _dac == 128);
    }
    /// <summary>Reads DSP replies/status and acknowledges the implemented byte-DMA IRQ at its designated status port.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1) return 0xff;
        switch (port - IoBasePort)
        {
            case 0x0a: return _readBuffer.TryDequeue(out byte value) ? value : 0xffu;
            case 0x0c: return !_ready || _resetHigh || _commandBusy || HighSpeed ? 0x80u : 0;
            case 0x0e: SetIrq(false); return _readBuffer.Count > 0 ? 0x80u : 0;
            default: return 0xff;
        }
    }
    /// <summary>Processes the timed DSP command/reset interface and ordinary 8-bit DMA sound controls.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port == IoBasePort + 6) { Reset((value & 1) != 0); return; }
        if (port != IoBasePort + 0x0c) return;
        // The 2.01 DSP services only the DMA loop in this mode. No command or parameter
        // parser state may leak through writes made while the command port is busy.
        if (HighSpeed) { IgnoredHighSpeedWrites++; return; }
        if (!_ready || _resetHigh || _commandBusy) { RejectedWrites++; return; }
        _commandBusy = true;
        byte data = (byte)value;
        _commandEvent = _clock.ScheduleAfter(CommandNanoseconds, () =>
        {
            _commandEvent = 0; _commandBusy = false; Receive(data);
        }, this, "event0", () => StateValues.Pack(data));
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
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
            _adpcm.Reference(128);
            if (!preserveParameters) { _timeConstant = 0; _blockLength = 65536; _dac = _adc = 128; SpeakerEnabled = false; }
            SetIrq(false); ResetCount++;
        }
        else if (_resetHigh)
        {
            _resetHigh = false;
            if (_clock.Now - _resetSince < ResetHoldNanoseconds) return;
            _resetEvent = _clock.ScheduleAfter(ResetReadyNanoseconds, () =>
            {
                _resetEvent = 0; _ready = true; _readBuffer.Enqueue(0xaa);
            }, this, "event1");
        }
    }
    /// <summary>Consumes the SB2 command stream, respecting high-speed restrictions and expected parameters.</summary>
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
            0x14 or 0x24 or 0x48 or 0x80 => 2,
            0x16 or 0x17 or 0x74 or 0x75 or 0x76 or 0x77 => 2,
            // MIDI output framing is consumed even without an attached MIDI peripheral.
            0x38 => 1,
            _ => 0
        };
        _parameterCount = 0;
        if (_parameterLength == 0) Execute();
    }
    /// <summary>Dispatches a decoded primary opcode, including prefix validity and operand-width rules.</summary>
    private void Execute()
    {
        int length = (_parameters[0] | _parameters[1] << 8) + 1;
        switch (_command)
        {
            case 0x10: SetDac(_parameters[0]); break;
            case 0x20:
                _adc = AdcInput?.Invoke(_clock.Now) ?? 128; ConvertedSamples++;
                Reply(_adc); AdcSampled?.Invoke(_clock.Now, _adc, null); break;
            case 0x14:
            case 0x24:
                if (Active && _autoInit) { QueueSingle(length, _command == 0x24, 0, false); }
                else Start(length, false, false, recording: _command == 0x24);
                break;
            case 0x16: case 0x17: case 0x74: case 0x75: case 0x76: case 0x77:
                int bits = _command < 0x70 ? 2 : _command < 0x76 ? 4 : 3;
                bool reference = (_command & 1) != 0;
                if (Active && _autoInit) QueueSingle(length, false, bits, reference);
                else Start(length, false, false, adpcmBits: bits, reference: reference);
                break;
            case 0x1f: Start(_blockLength, true, false, adpcmBits: 2, reference: true); break;
            case 0x7d: Start(_blockLength, true, false, adpcmBits: 4, reference: true); break;
            case 0x7f: Start(_blockLength, true, false, adpcmBits: 3, reference: true); break;
            case 0x1c: Start(_blockLength, true, false); break;
            case 0x2c: Start(_blockLength, true, false, recording: true); break;
            case 0x40: _timeConstant = _parameters[0]; break;
            case 0x48: _blockLength = length; break;
            case 0x80: Start(length, false, true); break;
            case 0x90: Start(_blockLength, true, false, true); break;
            case 0x91: Start(_blockLength, false, false, true); break;
            case 0x98: Start(_blockLength, true, false, true, true); break;
            case 0x99: Start(_blockLength, false, false, true, true); break;
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
    /// <summary>Queues a device response through the ordinary controller-visible output path.</summary>
    private void Reply(byte value)
    {
        // Finite response FIFO; a guest which never reads cannot grow host memory without bound.
        if (_readBuffer.Count < 64) _readBuffer.Enqueue(value);
    }
    /// <summary>Queues a single-cycle DSP response/sample operation through the normal device timing path.</summary>
    private void QueueSingle(int length, bool recording, int bits, bool reference)
    {
        _queuedSingleLength = length; _queuedSingleRecording = recording; _queuedSingleBits = bits;
        _queuedSingleReference = reference; _exitAuto = true;
    }
    /// <summary>Starts a validated SB2 DMA/ADC/ADPCM block with the selected legacy timing mode.</summary>
    private void Start(int length, bool autoInit, bool silence, bool highSpeed = false, bool recording = false,
        int adpcmBits = 0, bool reference = false)
    {
        Stop(); _remaining = length; _autoInit = autoInit; _silence = silence; _recording = recording;
        _adpcmBits = adpcmBits; _adpcmReferencePending = reference;
        if (adpcmBits != 0 && !reference) _adpcm.ContinueBlock(adpcmBits);
        HighSpeed = highSpeed; if (highSpeed) HighSpeedTransfers++;
        _queuedSingleLength = null; _exitAuto = false; ScheduleSample(SamplePeriodNanoseconds);
    }
    /// <summary>Stops the active SB2 stream while retaining the model's held DAC/status contract.</summary>
    private void Stop()
    {
        Cancel(ref _sampleEvent); _remaining = 0; Paused = false; _autoInit = false; _exitAuto = false;
        HighSpeed = false; _recording = false;
        _adpcmBits = _adpcmIndex = 0; _adpcmReferencePending = false;
    }
    /// <summary>Schedules the next legacy DSP sample deadline in virtual nanoseconds.</summary>
    private void ScheduleSample(ulong delay)
    {
        _sampleDeadline = checked(_clock.Now + delay);
        _sampleEvent = _clock.ScheduleAfter(delay, Tick, this, "event2");
    }
    /// <summary>Processes one due reference DSP sample and its actual DMA/ADPCM/ADC completion effects.</summary>
    private void Tick()
    {
        _sampleEvent = 0;
        if (_adpcmBits != 0)
        {
            if (!TickAdpcm()) { ScheduleSample(SamplePeriodNanoseconds); return; }
        }
        else if (!_silence)
        {
            byte value = 0;
            if (_recording)
            {
                _adc = AdcInput?.Invoke(_clock.Now) ?? 128; ConvertedSamples++;
                LastDmaResult = _dma.TryWriteByte(DmaChannel, _adc);
                AdcSampled?.Invoke(_clock.Now, _adc, LastDmaResult);
            }
            else LastDmaResult = _dma.TryReadByte(DmaChannel, out value);
            if (LastDmaResult != DmaReadResult.Transferred) { ScheduleSample(SamplePeriodNanoseconds); return; }
            if (_recording) { RecordedSamples++; if (HighSpeed) HighSpeedRecordedSamples++; }
            else { SetDac(value); TransferredSamples++; if (HighSpeed) HighSpeedSamples++; }
        }
        if (--_remaining == 0)
        {
            CompletedBlocks++; InterruptRequests++; SetIrq(true);
            if (_queuedSingleLength is { } length)
            {
                Start(length, false, false, recording: _queuedSingleRecording,
                    adpcmBits: _queuedSingleBits, reference: _queuedSingleReference); return;
            }
            if (!_autoInit || _exitAuto) { Stop(); return; }
            _remaining = _blockLength;
            if (_adpcmBits != 0) _adpcm.ContinueBlock(_adpcmBits);
        }
        ScheduleSample(SamplePeriodNanoseconds);
    }
    // A DMA byte contains several DAC deadlines. Count/IRQ a block only after its
    // last code has reached the DAC; a mask/TC stalls the next fetch, not buffered codes.
    // This is the reference board's sample-level timing contract, not DSP-ROM timing.
    private bool TickAdpcm()
    {
        if (_adpcmIndex == 0)
        {
            LastDmaResult = _dma.TryReadByte(DmaChannel, out _adpcmPacked);
            if (LastDmaResult != DmaReadResult.Transferred) return false;
            TransferredSamples++; AdpcmDmaBytes++;
            if (_adpcmReferencePending)
            {
                _adpcm.Reference(_adpcmPacked); _adpcmReferencePending = false;
                SetDac(_adpcmPacked); AdpcmDecodedSamples++; return true;
            }
        }
        SetDac(_adpcm.Decode(_adpcmPacked, _adpcmBits, _adpcmIndex++)); AdpcmDecodedSamples++;
        if (_adpcmIndex < CreativeAdpcmDecoder.SamplesPerByte(_adpcmBits)) return false;
        _adpcmIndex = 0; return true;
    }
    /// <summary>Updates a DAC component and invalidates the derived palette/scanout state as required.</summary>
    private void SetDac(byte value) { _audio.AdvanceTo(_clock.Now); _dac = value; }
    /// <summary>Applies the selected DSP revision's speaker-status/output behavior at the current virtual time.</summary>
    private void SetSpeaker(bool enabled) { _audio.AdvanceTo(_clock.Now); SpeakerEnabled = enabled; }
    /// <summary>Publishes the sound device's interrupt latch through its configured physical IRQ route.</summary>
    private void SetIrq(bool high)
    {
        if (InterruptAsserted == high) return;
        InterruptAsserted = high; _irq(high);
    }
    /// <summary>Cancels the identified or currently scheduled component operation before it can publish another completion.</summary>
    private void Cancel(ref long id) { if (id != 0) _clock.Cancel(id); id = 0; }
}
