// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Explicit single-command PIO phases of the named SFF-8020i revision-2.5 subset.</summary>
public enum AtapiPhase { Idle, PacketReadyWait, PacketOut, CommandWait, DataInWait, DataIn, StatusWait, SoftResetWait, HardwareResetHeld, SrstHeld }

/// <summary>Read-only task-file/packet/timer observation; constructing it never consumes data or acknowledges INTRQ.</summary>
public sealed record AtapiPhaseDiagnostic(AtapiPhase Phase, byte Status, byte Error, byte Reason, ushort ByteCount,
    int PacketOffset, int Offset, int Length, long Lba, uint RemainingBlocks, bool PendingInterrupt, bool Selected,
    bool TrayOpen, bool PreventRemoval, bool Started, bool UnitAttention, byte SenseKey, byte SenseAsc, byte SenseAscq,
    long EventToken, ulong Deadline, ulong Generation);

/// <summary>Own cooked Mode-1 CD-ROM: original twelve-byte PACKET, PIO words and a limited read-only command set.
/// No DMA, overlap, CD audio/raw sectors, physical host drive or complete SFF/MSCDEX/Windows acceptance is implied.</summary>
public sealed partial class AtapiCdRom : IIoPortDevice
{
    /// <summary>Own nominal phase preparation time, without calibrated mechanical or bus latency.</summary>
    public const ulong PhaseDelayNanoseconds = 5_000;
    private readonly DeviceScheduler _scheduler;
    private readonly IReadOnlyOpticalDisc? _disc;
    private readonly byte[] _task = new byte[8], _packet = new byte[12], _data = new byte[2048];
    private byte _features, _control;
    private AtapiPhase _phase;
    private int _packetOffset, _payloadLength, _payloadOffset, _phaseLength, _offset;
    private ushort _byteLimit;
    private long _readLba;
    private uint _remainingBlocks;
    private bool _readCommand, _identify, _pendingInterrupt, _selected, _hardwareResetHeld, _srstActive, _srstPreserved;
    private bool _trayOpen, _preventRemoval, _started = true, _unitAttention = true;
    private byte _uaAsc = 0x29, _senseKey, _senseAsc, _senseAscq;
    private bool _clearSenseOnCompletion, _clearUaOnCompletion, _shadowAbort;
    private long _event;
    private ulong _deadline;

    public event Action? InterruptChanged;
    /// <summary>Constructor invariant: an installed drive remains present even when there is no inserted image.</summary>
    public bool HasDisc { get; }
    /// <summary>Constructor-fixed cooked capacity, independent of tray visibility and the current READ cursor.</summary>
    public long DiscBlocks { get; }
    /// <summary>Physical master/slave identity of this one fixed channel peer.</summary>
    public int DeviceNumber { get; }
    /// <summary>Actual task-file status; reading this diagnostic never acknowledges the device interrupt.</summary>
    public byte Status { get; private set; }
    /// <summary>Current explicit transfer phase.</summary>
    public AtapiPhase Phase => _phase;
    /// <summary>Selected ordinary task-file ownership, including an announced data/packet word phase.</summary>
    public bool OwnsTaskFile => _phase is not (AtapiPhase.Idle or AtapiPhase.HardwareResetHeld or AtapiPhase.SrstHeld);
    /// <summary>Internal INTRQ source latch retained behind selection and nIEN.</summary>
    public bool PendingInterrupt => _pendingInterrupt;
    /// <summary>Actual selected/nIEN/reset-gated physical ATAPI INTRQ output.</summary>
    public bool InterruptAsserted => _pendingInterrupt && _selected && (_control & 2) == 0 && !_hardwareResetHeld && !_srstActive;
    /// <summary>Current external channel selection, preserved separately from reset signature register bytes.</summary>
    public bool Selected => _selected;
    /// <summary>Current software-reset pin, independent from a selected drive's visible status.</summary>
    internal bool SrstActive => _srstActive;
    /// <summary>Virtual mechanical tray position; electrical reset does not move an open tray.</summary>
    public bool TrayOpen => _trayOpen;
    /// <summary>Guest's prevent-removal latch.</summary>
    public bool PreventRemoval => _preventRemoval;
    /// <summary>Whether media access is enabled by the supported START/STOP subset.</summary>
    public bool Started => _started;
    /// <summary>One pending attention, with reset and possible-medium-change reasons kept separate from ordinary sense.</summary>
    public bool UnitAttention => _unitAttention;
    /// <summary>Monotonic transaction/reset generation; diagnostics saturate at their representable maximum.</summary>
    public ulong Generation { get; private set; } = 1;
    /// <summary>Accepted task-file command count, saturating rather than changing command semantics on diagnostic overflow.</summary>
    public long CommandsReceived { get; private set; }
    /// <summary>Completed PACKET count, including CHECK status.</summary>
    public long PacketsCompleted { get; private set; }
    /// <summary>Complete cooked READ blocks actually consumed by the guest.</summary>
    public long ReadBlockCount { get; private set; }
    /// <summary>Actual useful PIO data bytes consumed, excluding an odd final word's padding byte.</summary>
    public long DataBytesRead { get; private set; }

    /// <summary>Constructs one fixed read-only optical peer without opening or copying any media itself.</summary>
    public AtapiCdRom(DeviceScheduler scheduler, IReadOnlyOpticalDisc? disc, int deviceNumber)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (deviceNumber is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(deviceNumber));
        if (disc is not null && (disc.BlockCount <= 0 || disc.BlockCount > 0x1_0000_0000L)) throw new ArgumentOutOfRangeException(nameof(disc));
        _scheduler = scheduler; _disc = disc; DeviceNumber = deviceNumber;
        HasDisc = disc is not null; DiscBlocks = disc?.BlockCount ?? 0;
        InitializeSignature(0); _selected = deviceNumber == 0;
    }

    /// <summary>Checks constructor-fixed media/clock identity without reopening or reading the image.</summary>
    public bool HasBindings(DeviceScheduler scheduler, IReadOnlyOpticalDisc? disc) => ReferenceEquals(_scheduler, scheduler) &&
        ReferenceEquals(_disc, disc) && HasDisc == (disc is not null) && DiscBlocks == (disc?.BlockCount ?? 0);
    /// <summary>Returns a fresh complete phase observation with no task-file, IRQ or media side effects.</summary>
    public AtapiPhaseDiagnostic CapturePhase() => new(_phase, Status, _task[1], _task[2], (ushort)(_task[4] | _task[5] << 8),
        _packetOffset, _offset, _phaseLength, _readLba, _remainingBlocks, _pendingInterrupt, _selected, _trayOpen,
        _preventRemoval, _started, _unitAttention, _senseKey, _senseAsc, _senseAscq, _event, _deadline, Generation);
    /// <summary>Changes only the shared channel selection gate; a pending interrupt is published when the peer becomes selected again.</summary>
    public void Select(bool selected) { if (_selected == selected) return; _selected = selected; InterruptChanged?.Invoke(); }
    /// <summary>Copies the fixed CDB staging bytes for diagnostics without lending writable device storage.</summary>
    public byte[] CapturePacket() => _packet.ToArray();
    /// <summary>Returns a complete un-driven value for an unsupported width.</summary>
    private static uint Float(int width) => width switch { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Advances a saturating diagnostic count without introducing command faults on host-sized limits.</summary>
    private static long Add(long value, int count = 1) => value > long.MaxValue - count ? long.MaxValue : value + count;
    /// <summary>Starts another transaction generation independently from virtual time and scheduler IDs.</summary>
    private void NextGeneration() { if (Generation != ulong.MaxValue) Generation++; }
    /// <summary>Changes the internal interrupt source and lets the fixed channel recompute its physical output.</summary>
    private void SetInterrupt(bool value) { if (_pendingInterrupt == value) return; _pendingInterrupt = value; InterruptChanged?.Invoke(); }

    /// <summary>Reads actual word data or byte task registers; regular status alone acknowledges a selected drive's INTRQ.</summary>
    public uint Read(ushort port, int width)
    {
        uint floating = Float(width);
        if (!_selected) return floating;
        if (_hardwareResetHeld || _srstActive) return (port is 0x1f7 or 0x3f6) && width == 1 ? 0x80u : floating;
        if (port == 0x1f0)
        {
            if (width != 2 || _phase != AtapiPhase.DataIn || _offset >= _phaseLength) return floating;
            int at = _payloadOffset + _offset;
            uint value = _data[at];
            int count = Math.Min(2, _phaseLength - _offset);
            if (count == 2) value |= (uint)_data[at + 1] << 8;
            _offset += count; DataBytesRead = Add(DataBytesRead, count);
            if (_offset == _phaseLength) FinishDataPhase();
            return value;
        }
        if (width != 1) return floating;
        if (port == 0x3f6) return Status;
        int index = port - 0x1f0;
        if (index == 7) { SetInterrupt(false); return Status; }
        return index is >= 1 and <= 6 ? _task[index] : floating;
    }

    /// <summary>Provides the original master-only shadow signature without consuming data or acknowledging the real master.</summary>
    internal uint ReadShadow(ushort port, int width)
    {
        uint floating = Float(width);
        if (width != 1 || port == 0x1f0) return floating;
        if (port is 0x1f7 or 0x3f6) return (uint)(Status | (_shadowAbort ? 1 : 0));
        int index = port - 0x1f0;
        return index == 1 && _shadowAbort ? 4u : index is >= 1 and <= 6 ? _task[index] : floating;
    }
    /// <summary>Aborts only a command addressed to the physically absent shadow slave, leaving the actual master's work intact.</summary>
    internal void WriteShadowCommand() => _shadowAbort = true;

    /// <summary>Accepts only word CDB data and byte task/control writes; DMA/overlap cannot consume a packet.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Float(width);
        if (port == 0x3f6 && width == 1) { WriteControl((byte)value); return; }
        if (_hardwareResetHeld) return;
        if (width == 1 && port is >= 0x1f1 and <= 0x1f6)
        {
            int index = port - 0x1f0;
            if (index == 6) _task[6] = (byte)value;
            else if ((Status & 0x88) == 0 && !_hardwareResetHeld && !_srstActive)
            { if (index == 1) _features = (byte)value; else _task[index] = (byte)value; }
            return;
        }
        if (!_selected || _hardwareResetHeld || _srstActive) return;
        if (port == 0x1f0 && width == 2)
        {
            if (_phase != AtapiPhase.PacketOut || _packetOffset >= 12) return;
            _packet[_packetOffset++] = (byte)value; _packet[_packetOffset++] = (byte)(value >> 8);
            if (_packetOffset == 12) { Status = 0x80; _phase = AtapiPhase.CommandWait; Schedule(); }
            return;
        }
        if (port != 0x1f7 || width != 1) return;
        CommandsReceived = Add(CommandsReceived);
        if (value == 8) { SoftReset(); return; }
        if (OwnsTaskFile) { NextGeneration(); CancelTransfer(); CheckCondition(5, 0x2c, 0, true); return; }
        SetInterrupt(false); NextGeneration();
        if (value == 0xa0)
        {
            if (_features != 0) { AtaAbort(); return; }
            ushort limit = (ushort)(_task[4] | _task[5] << 8);
            CancelTransfer(); _byteLimit = limit;
            _phase = AtapiPhase.PacketReadyWait; Status = 0x80; Schedule();
        }
        else if (value == 0xa1)
        {
            CancelTransfer(); _identify = true; _phase = AtapiPhase.DataInWait; Status = 0x80;
            BuildIdentify(); _payloadOffset = 0; _payloadLength = 512; _byteLimit = 512; Schedule();
        }
        else if (value == 0xef && _features == 3 && _task[2] is 0 or 8)
        { Status = 0x40; _task[1] = 0; SetInterrupt(true); }
        else AtaAbort();
    }

    /// <summary>Creates one explicit nominal owner deadline; the callback reads only persisted phase state.</summary>
    private void Schedule()
    {
        if (_event != 0) _scheduler.Cancel(_event);
        _deadline = checked(_scheduler.Now + PhaseDelayNanoseconds);
        _event = _scheduler.ScheduleAfter(PhaseDelayNanoseconds, () =>
        { _event = 0; _deadline = 0; AdvancePhase(); }, this, "phase");
    }
    /// <summary>Runs one restored or original phase callback without generating hidden data-port transfers.</summary>
    private void AdvancePhase()
    {
        switch (_phase)
        {
            case AtapiPhase.PacketReadyWait: _phase = AtapiPhase.PacketOut; _task[2] = 1; Status = 0x48; SetInterrupt(true); break;
            case AtapiPhase.CommandWait: ExecutePacket(); break;
            case AtapiPhase.DataInWait: PrepareDataPhase(); break;
            case AtapiPhase.StatusWait: CompletePacket(); break;
            case AtapiPhase.SoftResetWait: InitializeSignature((byte)(_task[6] & 0x10)); _phase = AtapiPhase.Idle; break;
            default: throw new InvalidOperationException("Unexpected ATAPI phase callback.");
        }
    }
    /// <summary>Announces one at-most2048-byte PIO phase, keeping an odd byte only for the final transfer.</summary>
    private void PrepareDataPhase()
    {
        if (_readCommand && _payloadOffset == _payloadLength)
        {
            if (_byteLimit < 2) { CheckCondition(5, 0x24); return; }
            try { _disc!.ReadBlock(_readLba, _data); }
            catch (IOException) { CheckCondition(3, 0x11); return; }
            _payloadLength = 2048; _payloadOffset = 0;
        }
        int remaining = _payloadLength - _payloadOffset;
        int limit = Math.Min((int)_byteLimit, 2048);
        if (remaining > limit || _readCommand && _remainingBlocks > 1) limit &= ~1;
        if (limit == 0 && remaining != 0) { CheckCondition(5, 0x24); return; }
        _phaseLength = Math.Min(remaining, limit); _offset = 0;
        _task[4] = (byte)_phaseLength; _task[5] = (byte)(_phaseLength >> 8); _task[2] = 2;
        _phase = AtapiPhase.DataIn; Status = 0x48; SetInterrupt(true);
    }
    /// <summary>Commits one consumed phase before scheduling the next actual payload or independent terminal status.</summary>
    private void FinishDataPhase()
    {
        _payloadOffset += _phaseLength; Status = 0x80;
        if (_payloadOffset == _payloadLength && _readCommand)
        { ReadBlockCount = Add(ReadBlockCount); _readLba++; _remainingBlocks--; }
        bool more = _payloadOffset < _payloadLength || _readCommand && _remainingBlocks != 0;
        if (_identify)
        { _identify = false; _phase = AtapiPhase.Idle; Status = 0x40; return; }
        _phase = more ? AtapiPhase.DataInWait : AtapiPhase.StatusWait; Schedule();
    }
    /// <summary>Returns a distinct terminal PACKET interrupt only after all useful bytes have been consumed.</summary>
    private void CompletePacket()
    {
        if (_clearSenseOnCompletion) _senseKey = _senseAsc = _senseAscq = 0;
        if (_clearUaOnCompletion) _unitAttention = false;
        _clearSenseOnCompletion = _clearUaOnCompletion = false;
        _readCommand = false; _remainingBlocks = 0; _task[2] = 3; _task[4] = _task[5] = 0;
        _task[1] = 0; _phase = AtapiPhase.Idle; Status = 0x40; PacketsCompleted = Add(PacketsCompleted); SetInterrupt(true);
    }
    /// <summary>Cancels just this drive's own scheduled phase while retaining external media and mechanical state.</summary>
    private void CancelTransfer()
    {
        if (_event != 0) _scheduler.Cancel(_event);
        _event = 0; _deadline = 0; _phase = AtapiPhase.Idle;
        _packetOffset = _payloadLength = _payloadOffset = _phaseLength = _offset = 0;
        _remainingBlocks = 0; _readLba = 0; _byteLimit = 0; _readCommand = _identify = false;
        _clearSenseOnCompletion = _clearUaOnCompletion = false;
        Array.Clear(_packet); Array.Clear(_data); SetInterrupt(false);
    }
    /// <summary>Reports a CDB CHECK with fixed sense; ATA-wrapper aborts additionally set ABRT.</summary>
    private void CheckCondition(byte key, byte asc, byte ascq = 0, bool abort = false)
    {
        CancelTransfer(); _senseKey = key; _senseAsc = asc; _senseAscq = ascq;
        _task[1] = (byte)((key << 4) | (abort ? 4 : 0)); _task[2] = 3; _task[4] = _task[5] = 0;
        Status = 0x41; PacketsCompleted = Add(PacketsCompleted); SetInterrupt(true);
    }
    /// <summary>Rejects an unsupported ATA wrapper without consuming any CDB or contacting media.</summary>
    private void AtaAbort()
    {
        CancelTransfer(); _task[1] = 4; Status = 0x41; SetInterrupt(true);
        _task[4] = 0x14; _task[5] = 0xeb;
    }
    /// <summary>Loads the original reset signature; selection gating remains controlled by the channel owner.</summary>
    private void InitializeSignature(byte drive)
    {
        Array.Clear(_task); _task[1] = _task[2] = _task[3] = 1;
        _task[4] = 0x14; _task[5] = 0xeb; _task[6] = drive;
        Status = 0; SetInterrupt(false); _shadowAbort = false;
    }
    /// <summary>Resets interface work from any busy phase without publishing an unsolicited completion interrupt.</summary>
    private void SoftReset()
    { CancelTransfer(); NextGeneration(); _features = 0; _phase = AtapiPhase.SoftResetWait; Status = 0x80; Schedule(); }
    /// <summary>Asserts only this optical drive's physical board domain, preserving the immutable image and actual tray position.</summary>
    public void ApplyPiixHardReset()
    {
        CancelTransfer(); NextGeneration(); _hardwareResetHeld = true; _srstActive = _srstPreserved = false; _control = 4; _features = 0;
        _preventRemoval = false; _started = !_trayOpen; _unitAttention = true; _uaAsc = 0x29;
        _senseKey = _senseAsc = _senseAscq = 0; InitializeSignature(0); Status = 0x80; _phase = AtapiPhase.HardwareResetHeld;
        InterruptChanged?.Invoke();
    }
    /// <summary>Deasserts the physical board pin with its initialized signature and no synthetic reset interrupt.</summary>
    public void ReleasePiixHardReset()
    { _hardwareResetHeld = false; _control = 0; _phase = AtapiPhase.Idle; InitializeSignature(0); InterruptChanged?.Invoke(); }
    /// <summary>Separates SRST from hard reset, preserving an unselected active command and all media/mode/sense latches.</summary>
    private void WriteControl(byte value)
    {
        if (_hardwareResetHeld) { if ((value & 4) == 0) ReleasePiixHardReset(); return; }
        bool before = _srstActive; _control = (byte)(value & 6);
        bool asserted = (value & 4) != 0;
        if (asserted && !before)
        {
            NextGeneration(); _srstActive = true; _srstPreserved = !_selected && OwnsTaskFile;
            if (!_srstPreserved) { CancelTransfer(); _phase = AtapiPhase.SrstHeld; Status = 0x80; }
        }
        else if (!asserted && before)
        {
            _srstActive = false;
            if (!_srstPreserved) { _phase = AtapiPhase.Idle; InitializeSignature(0); }
            _srstPreserved = false;
        }
        InterruptChanged?.Invoke();
    }
}
