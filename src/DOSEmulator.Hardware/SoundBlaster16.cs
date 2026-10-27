// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Non-PnP CT1740 / DSP 4.05 reference PCM core. OPL3, MPU, CSP and FIFO bus timing are separate packages.</summary>
public sealed partial class SoundBlaster16 : IIoPortDevice
{
    public const string ProfileId = "sb16-ct1740-dsp405-development";
    public const string Model = "CT1740 / CT1741 DSP 4.05 / CT1745 PCM reference";
    private readonly DeviceScheduler _clock;
    private readonly Dma8237 _dma;
    private readonly PcAudioMixer _audio;
    private readonly PcAudioMixer.Input _left, _right;
    private readonly Action<int, bool> _irq;
    private readonly Queue<byte> _responses = new();
    private readonly byte[] _parameters = new byte[3];
    private byte _command, _mixerIndex, _interruptSources;
    private int _received, _expected, _blockLength, _legacyLength = 65536, _activeRate, _rateRemainder;
    private bool _ready, _resetHigh, _busy, _auto, _exitAuto, _recording, _signed, _stereo, _word, _rightNext, _halfPending;
    private bool _lineHigh;
    private Mpu401Uart? _midi;
    private ushort _partialRaw;
    private ulong _resetSince, _sampleDeadline, _pausedDelay;
    private ulong _inputPeriod, _outputPeriod, _activePeriod;
    private long _resetEvent, _commandEvent, _sampleEvent;
    /// <summary>Checks factory board links before excluding shared references from persisted state.</summary>
    public bool HasStatePeers(Opl3? fm, Mpu401Uart? midi) => ReferenceEquals(_fm, fm) && ReferenceEquals(_midi, midi);
    /// <summary>Current guest-visible ISA base port for this concrete sound-card model.</summary>
    public ushort IoBasePort { get; }
    /// <summary>Configured physical IRQ before the guest PIC's vector remapping.</summary>
    public int Irq { get; private set; }
    /// <summary>Selected byte-DMA channel for the current card resource configuration.</summary>
    public int Dma8Channel { get; private set; }
    /// <summary>Selected word-DMA channel, or the documented disabled/high-DMA fallback selection.</summary>
    public int Dma16Channel { get; private set; }
    /// <summary>Guest-programmed PCM playback rate in samples per second.</summary>
    public int OutputRate { get; private set; } = 22050;
    /// <summary>Guest-programmed virtual ADC rate in samples per second.</summary>
    public int InputRate { get; private set; } = 22050;
    /// <summary>Remaining sample/components in the current programmed DSP block.</summary>
    public int RemainingSamples { get; private set; }
    /// <summary>Whether a DSP transfer/sample engine is currently active.</summary>
    public bool Active => RemainingSamples > 0;
    /// <summary>Whether the active DSP engine is paused by a guest control command.</summary>
    public bool Paused { get; private set; }
    /// <summary>Whether the current DSP stream carries 16-bit sample components.</summary>
    public bool WordFormat => _word;
    /// <summary>Whether the current DSP stream alternates left/right sample components.</summary>
    public bool Stereo => _stereo;
    /// <summary>Whether the active DSP stream reads the virtual ADC input instead of playing PCM.</summary>
    public bool Recording => Active && _recording;
    /// <summary>DSP speaker-status latch, whose audible effect follows the selected card revision's contract.</summary>
    public bool SpeakerEnabled { get; private set; }
    /// <summary>Stored guest FIFO request bit; it is not a claim of implemented silicon prefetch/burst timing.</summary>
    public bool FifoRequested { get; private set; }
    /// <summary>Independent currently pending card IRQ-source bits, read without acknowledgement.</summary>
    public byte InterruptSources => _interruptSources;
    /// <summary>Connect the optional CT1740 MPU input source without merging its acknowledgement with either DSP width.</summary>
    public void AttachMidi(Mpu401Uart midi)
    {
        ArgumentNullException.ThrowIfNull(midi);
        if (_midi is not null) throw new InvalidOperationException("CT1740 MIDI is already attached.");
        _midi = midi;
        // Source bit 2 follows unread MPU input, independent from DSP reset and mixer reset.
        void Changed(bool high) { _interruptSources = (byte)((_interruptSources & 3) | (high ? 4 : 0)); UpdateIrq(); }
        midi.InterruptChanged += Changed; Changed(midi.InterruptAsserted);
    }
    /// <summary>Most recent left digital component held by the reference DAC.</summary>
    public short DacLeft { get; private set; }
    /// <summary>Most recent right digital component held by the reference DAC.</summary>
    public short DacRight { get; private set; }
    /// <summary>Diagnostic count of transferred samples.</summary>
    public long TransferredSamples { get; private set; }
    /// <summary>Diagnostic count of recorded samples.</summary>
    public long RecordedSamples { get; private set; }
    /// <summary>Diagnostic count of converted samples.</summary>
    public long ConvertedSamples { get; private set; }
    /// <summary>Diagnostic count of dma8 grants.</summary>
    public long Dma8Grants { get; private set; }
    /// <summary>Diagnostic count of dma16 grants.</summary>
    public long Dma16Grants { get; private set; }
    /// <summary>Diagnostic count of completed blocks.</summary>
    public long CompletedBlocks { get; private set; }
    /// <summary>Diagnostic count of irq8 requests.</summary>
    public long Irq8Requests { get; private set; }
    /// <summary>Counts F2h diagnostic requests separately from actual completed PCM blocks.</summary>
    public long SoftwareIrq8Requests { get; private set; }
    /// <summary>Counts F3h diagnostic requests separately from actual completed PCM blocks.</summary>
    public long SoftwareIrq16Requests { get; private set; }
    /// <summary>Diagnostic count of irq16 requests.</summary>
    public long Irq16Requests { get; private set; }
    /// <summary>Diagnostic count of reset count.</summary>
    public long ResetCount { get; private set; }
    /// <summary>Diagnostic count of rejected writes.</summary>
    public long RejectedWrites { get; private set; }
    /// <summary>Count of DSP parameters rejected before a transfer is started.</summary>
    public long RejectedParameters { get; private set; }
    /// <summary>Diagnostic indication that the guest selected commands outside the implemented reference contract.</summary>
    public long UnsupportedCommands { get; private set; }
    /// <summary>Most recent unsupported DSP command byte retained for diagnostics.</summary>
    public byte? LastUnsupportedCommand { get; private set; }
    /// <summary>Most recent actual DMA-grant result, separate from a completed sound sample.</summary>
    public DmaReadResult LastDmaResult { get; private set; } = DmaReadResult.Blocked;
    /// <summary>Virtual signal at the ADC input, after the not-yet-modeled external input matrix. Null is disconnected silence.</summary>
    public Func<ulong, bool, short>? AdcInput { get; set; }
    public sealed record Transfer(ulong Time, int Rate, bool Word, bool Stereo, bool Signed, bool Recording,
        bool AutoInit, bool FifoRequested, int Samples, int DmaChannel, ulong PeriodNanoseconds);
    public sealed record Sample(ulong Time, bool Word, bool Recording, bool Right, ushort Raw, short Value, string DmaResult);
    public event Action<Transfer>? TransferStarted;
    public event Action<Sample>? SampleTransferred;
    public event Action<ulong, bool>? BlockCompleted;
    /// <summary>Host-side source gain, separate from guest DSP/mixer register state.</summary>
    public double Volume
    {
        get => _left.Gain;
        set { if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value)); _left.Gain = _right.Gain = value; }
    }
    /// <summary>Initializes SoundBlaster16 and connects the supplied dependencies before the component is used.</summary>
    public SoundBlaster16(DeviceScheduler clock, Dma8237 dma, PcAudioMixer audio, Action<int, bool> irq,
        ushort basePort = 0x220, int irqNumber = 5, int dma8 = 1, int dma16 = 5)
    {
        if (basePort is not (0x220 or 0x240 or 0x260 or 0x280) || irqNumber is not (5 or 7 or 9 or 10) ||
            dma8 is not (0 or 1 or 3) || dma16 is not (5 or 6 or 7)) throw new ArgumentException("Invalid CT1740 resources.");
        IoBasePort = basePort; Irq = irqNumber; Dma8Channel = dma8; Dma16Channel = dma16;
        _clock = clock; _dma = dma; _audio = audio; _irq = irq;
        (_left, _right) = audio.AddStereoSource(
            (start, end) => (Int128)(end - start) * PitSpeakerCounter.Frequency * DacLeft,
            (start, end) => (Int128)(end - start) * PitSpeakerCounter.Frequency * DacRight,
            0.5, () => DacLeft == 0, () => DacRight == 0);
        ResetMixer();
    }
    /// <summary>Reads DSP/mixer ports while keeping IRQ8/IRQ16 acknowledgements separate at +E/+F.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1) return width == 2 ? 0xffffu : uint.MaxValue;
        switch (port - IoBasePort)
        {
            case 5: return ReadMixer();
            case 0x0a: return _responses.TryDequeue(out byte value) ? value : 0xffu;
            case 0x0c: return !_ready || _resetHigh || _busy ? 0x80u : 0;
            case 0x0e: Acknowledge(1); return _responses.Count > 0 ? 0x80u : 0;
            case 0x0f: Acknowledge(2); return 0xff;
            default: return 0xff;
        }
    }
    /// <summary>Processes the CT1740 DSP/mixer interface and the active half-duplex PCM stream.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        switch (port - IoBasePort)
        {
            case 4: _mixerIndex = (byte)value; return;
            case 5: WriteMixer((byte)value); return;
            case 6: Reset((value & 1) != 0); return;
        }
        if (port != IoBasePort + 0x0c) return;
        if (!_ready || _resetHigh || _busy) { RejectedWrites++; return; }
        _busy = true;
        byte data = (byte)value;
        _commandEvent = _clock.ScheduleAfter(SoundBlasterDsp.CommandNanoseconds, () =>
        { _commandEvent = 0; _busy = false; Receive(data); }, this, "event0", () => StateValues.Pack(data));
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    private void Reset(bool high)
    {
        if (high)
        {
            if (_resetHigh) return;
            _audio.AdvanceTo(_clock.Now); DacLeft = DacRight = 0;
            Cancel(ref _resetEvent); Cancel(ref _commandEvent); Stop();
            _resetHigh = true; _resetSince = _clock.Now; _ready = _busy = false;
            _responses.Clear(); _received = _expected = 0; _legacyLength = 65536;
            _word = _stereo = _signed = _recording = _rightNext = false; FifoRequested = false;
            InputRate = OutputRate = 22050; _inputPeriod = _outputPeriod = 0;
            SpeakerEnabled = false; _interruptSources &= 4; UpdateIrq(); ResetCount++;
        }
        else if (_resetHigh)
        {
            _resetHigh = false;
            if (_clock.Now - _resetSince < SoundBlasterDsp.ResetHoldNanoseconds) return;
            _resetEvent = _clock.ScheduleAfter(SoundBlasterDsp.ResetReadyNanoseconds, () => { _resetEvent = 0; _ready = true; Reply(0xaa); }, this, "event1");
        }
    }
    /// <summary>Consumes a DSP command/parameter byte under the current reset and command parser state.</summary>
    private void Receive(byte value)
    {
        if (_expected != 0)
        {
            _parameters[_received++] = value;
            if (_received < _expected) return;
            _received = _expected = 0; Execute(); return;
        }
        _command = value; _received = 0;
        _expected = value switch
        {
            >= 0xb0 and <= 0xcf => 3,
            0x41 or 0x42 or 0x14 or 0x24 or 0x48 => 2,
            0x10 or 0x40 or 0x38 => 1,
            _ => 0
        };
        if (_expected == 0) Execute();
    }
    /// <summary>Dispatches a decoded primary opcode, including prefix validity and operand-width rules.</summary>
    private void Execute()
    {
        if (_command is >= 0xb0 and <= 0xcf)
        {
            if ((_command & 1) != 0 || (_parameters[0] & 0xcf) != 0) { RejectedParameters++; return; }
            Start((_parameters[1] | _parameters[2] << 8) + 1, _command < 0xc0,
                (_parameters[0] & 0x20) != 0, (_parameters[0] & 0x10) != 0, (_command & 8) != 0, (_command & 4) != 0, (_command & 2) != 0);
            return;
        }
        switch (_command)
        {
            case 0xe1: Reply(4); Reply(5); break;
            case 0xf2: RequestInterrupt(false, true); break;
            case 0xf3: RequestInterrupt(true, true); break;
            case 0x41: case 0x42:
                int rate = _parameters[0] << 8 | _parameters[1];
                if (rate is < 5000 or > 44100) { RejectedParameters++; break; }
                if (_command == 0x41) { OutputRate = rate; _outputPeriod = 0; } else { InputRate = rate; _inputPeriod = 0; }
                break;
            case 0x40:
                InputRate = OutputRate = 1_000_000 / (256 - _parameters[0]);
                _inputPeriod = _outputPeriod = (ulong)(256 - _parameters[0]) * 1000; break;
            case 0x48: _legacyLength = (_parameters[0] | _parameters[1] << 8) + 1; break;
            case 0x14: case 0x24: Start((_parameters[0] | _parameters[1] << 8) + 1, false, false, false, _command == 0x24, false, false); break;
            case 0x1c: case 0x2c: Start(_legacyLength, false, false, false, _command == 0x2c, true, false); break;
            case 0x10: _audio.AdvanceTo(_clock.Now); DacLeft = DacRight = (short)((_parameters[0] - 128) * 256); break;
            case 0x20: Reply((byte)(((AdcInput?.Invoke(_clock.Now, false) ?? 0) >> 8) + 128)); ConvertedSamples++; break;
            case 0xd1: SpeakerEnabled = true; break;
            case 0xd3: SpeakerEnabled = false; break; // DSP4.xx flags only; CT1745 controls the analog path.
            case 0xd8: Reply(SpeakerEnabled ? (byte)255 : (byte)0); break;
            case 0xd0: case 0xd5:
                if (Active && !Paused && _word == (_command == 0xd5))
                { _pausedDelay = _sampleDeadline > _clock.Now ? _sampleDeadline - _clock.Now : 1; Cancel(ref _sampleEvent); Paused = true; }
                break;
            case 0xd4: case 0xd6:
                if (Active && Paused && _word == (_command == 0xd6)) { Paused = false; Schedule(_pausedDelay); }
                break;
            case 0xda: case 0xd9: if (Active && _auto && _word == (_command == 0xd9)) _exitAuto = true; break;
            default: UnsupportedCommands++; LastUnsupportedCommand = _command; break;
        }
    }
    /// <summary>Queues a device response through the ordinary controller-visible output path.</summary>
    private void Reply(byte value) { if (_responses.Count < 64) _responses.Enqueue(value); }
    /// <summary>Starts the validated generic PCM command with its width, channel count, rate and block counters.</summary>
    private void Start(int samples, bool word, bool stereo, bool signed, bool recording, bool autoInit, bool fifo)
    {
        Stop(); _word = word; _stereo = stereo; _signed = signed; _recording = recording; _auto = autoInit;
        FifoRequested = fifo; _blockLength = RemainingSamples = samples; _rightNext = false;
        _activeRate = recording ? InputRate : OutputRate; _rateRemainder = 0;
        _activePeriod = (recording ? _inputPeriod : _outputPeriod) * (stereo ? 2UL : 1UL);
        TransferStarted?.Invoke(new(_clock.Now, _activeRate, word, stereo, signed, recording, autoInit, fifo, samples,
            word && Dma16Channel >= 5 ? Dma16Channel : Dma8Channel, _activePeriod));
        ScheduleNext();
    }
    /// <summary>Stops the active reference PCM stream after integrating its held output through the current instant.</summary>
    private void Stop() { Cancel(ref _sampleEvent); RemainingSamples = 0; Paused = false; _auto = _exitAuto = _halfPending = false; }
    /// <summary>Schedules the active PCM component at its carried integer virtual sample deadline.</summary>
    private void Schedule(ulong delay) { _sampleDeadline = checked(_clock.Now + delay); _sampleEvent = _clock.ScheduleAfter(delay, Tick, this, "event2"); }
    /// <summary>Advances the PCM deadline using the programmed rate without accumulating host-time drift.</summary>
    private void ScheduleNext()
    {
        if (_activePeriod != 0) { Schedule(_activePeriod); return; }
        long numerator = 1_000_000_000L + _rateRemainder;
        _rateRemainder = (int)(numerator % _activeRate); Schedule((ulong)(numerator / _activeRate));
    }
    /// <summary>Requests the due byte/word DMA grant and commits a component/block IRQ only after a real accepted transfer.</summary>
    private void Tick()
    {
        _sampleEvent = 0;
        int components = _stereo && !_rightNext ? 2 : 1;
        for (int i = 0; i < components && Active; i++)
        {
            bool right = _rightNext;
            if (!TryTransfer(out ushort raw)) break;
            short sample = _word ? unchecked((short)(raw ^ (_signed ? 0 : 0x8000)))
                : (short)((_signed ? unchecked((sbyte)(byte)raw) : (byte)raw - 128) * 256);
            if (_recording) RecordedSamples++; else { SetDac(sample, right); TransferredSamples++; }
            SampleTransferred?.Invoke(new(_clock.Now, _word, _recording, right, raw, sample, LastDmaResult.ToString()));
            _rightNext = _stereo && !right;
            if (--RemainingSamples == 0)
            {
                CompletedBlocks++;
                RequestInterrupt(_word, false); BlockCompleted?.Invoke(_clock.Now, _word);
                if (_auto && !_exitAuto) RemainingSamples = _blockLength; else { Stop(); break; }
            }
        }
        if (Active) ScheduleNext();
    }
    /// <summary>Requests one device-paced DMA grant and applies data effects only when the controller accepts it.</summary>
    private bool TryTransfer(out ushort raw)
    {
        raw = _halfPending ? _partialRaw : (ushort)0;
        if (_recording && !_halfPending)
        {
            short input = AdcInput?.Invoke(_clock.Now, _rightNext) ?? 0; ConvertedSamples++;
            raw = _word ? unchecked((ushort)(input ^ (_signed ? 0 : 0x8000)))
                : unchecked((byte)((input >> 8) + (_signed ? 0 : 128)));
        }
        if (_word && Dma16Channel >= 5)
        {
            LastDmaResult = _recording ? _dma.TryWriteWord(Dma16Channel, raw) : _dma.TryReadWord(Dma16Channel, out raw);
            if (LastDmaResult == DmaReadResult.Transferred) Dma16Grants++;
            return LastDmaResult == DmaReadResult.Transferred;
        }
        if (Dma8Channel < 0) { LastDmaResult = DmaReadResult.Blocked; return false; }
        if (!_halfPending)
        {
            byte low = (byte)raw;
            LastDmaResult = _recording ? _dma.TryWriteByte(Dma8Channel, low) : _dma.TryReadByte(Dma8Channel, out low);
            if (LastDmaResult != DmaReadResult.Transferred) return false;
            Dma8Grants++; if (!_recording) raw = low;
            if (!_word) return true;
            _halfPending = true; _partialRaw = raw;
        }
        byte high = (byte)(raw >> 8);
        LastDmaResult = _recording ? _dma.TryWriteByte(Dma8Channel, high) : _dma.TryReadByte(Dma8Channel, out high);
        if (LastDmaResult != DmaReadResult.Transferred) return false;
        Dma8Grants++; if (!_recording) raw = (ushort)((raw & 255) | high << 8);
        _halfPending = false; return true;
    }
    /// <summary>Updates a DAC component and invalidates the derived palette/scanout state as required.</summary>
    private void SetDac(short value, bool right)
    {
        _audio.AdvanceTo(_clock.Now);
        if (!_stereo) DacLeft = DacRight = value;
        else if (right) DacRight = value; else DacLeft = value;
    }
    /// <summary>Clears only the selected card-source latch; PIC acknowledgment and EOIs remain separate.</summary>
    private void Acknowledge(byte bits) { _interruptSources &= (byte)~bits; UpdateIrq(); }
    /// <summary>Sets a width-specific card latch; software diagnostics do not invent DMA grants, samples or completed blocks.</summary>
    private void RequestInterrupt(bool word, bool software)
    {
        if (word) { Irq16Requests++; if (software) SoftwareIrq16Requests++; _interruptSources |= 2; }
        else { Irq8Requests++; if (software) SoftwareIrq8Requests++; _interruptSources |= 1; }
        UpdateIrq();
    }
    /// <summary>Recomputes the component's externally observable interrupt level after state changes.</summary>
    private void UpdateIrq()
    {
        bool high = _interruptSources != 0 && Irq >= 0;
        if (high == _lineHigh) return;
        _lineHigh = high; if (Irq >= 0) _irq(Irq, high);
    }
    /// <summary>Cancels the identified or currently scheduled component operation before it can publish another completion.</summary>
    private void Cancel(ref long id) { if (id != 0) _clock.Cancel(id); id = 0; }
}
