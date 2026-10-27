// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Explicit synthetic raw-byte sink with documented handshake deadlines, not an emulation of a physical printer engine.</summary>
public sealed partial class VirtualParallelPrinter
{
    public const ulong MinimumSetup = 500, MinimumStrobe = 500, MinimumHold = 500;
    public const ulong AckDelay = 10000, AckWidth = 5000, InitializeDelay = 50000;
    private readonly ParallelPrinterPort _port;
    private readonly DeviceScheduler _scheduler;
    private byte _lastControl;
    private bool _busy = true, _ack = true, _reset, _pulse, _valid, _holding;
    private byte _sample, _sampleControl;
    private ulong _asserted, _released;
    private long _holdEvent, _ackLowEvent, _ackHighEvent, _initializeEvent;
    /// <summary>Successfully committed raw bytes; resets cannot retract bytes already accepted.</summary>
    public long AcceptedBytes { get; private set; }
    /// <summary>Strobes rejected for timing, readiness, reset or data instability.</summary>
    public long RejectedStrobes { get; private set; }
    /// <summary>Observed assertions of the physical active-low INIT line, including the initial attached reset state.</summary>
    public long Initializations { get; private set; }
    /// <summary>Whether the synthetic peer is currently busy or initializing.</summary>
    public bool Busy => _busy;
    /// <summary>Current physical active-low acknowledge level.</summary>
    public bool AckHigh => _ack;
    /// <summary>One raw byte after a complete valid strobe and hold interval, with its observed scheduler boundaries.</summary>
    public event Action<ParallelByte>? Accepted;
    /// <summary>Attach a peer to current actual outputs, keeping a low INIT line in reset until the guest releases it.</summary>
    internal VirtualParallelPrinter(ParallelPrinterPort port, DeviceScheduler scheduler)
    {
        _port = port; _scheduler = scheduler; _lastControl = port.Control;
        _reset = (port.Control & 4) == 0; _busy = _reset; Initializations = _reset ? 1 : 0;
        port.OutputsChanged += Observe; Pins();
    }
    /// <summary>Publish fixed selected/healthy/paper-present inputs together with the timed BUSY and ACK lines.</summary>
    private void Pins() => _port.SetPrinterPins((byte)(0x18 | (_busy ? 128 : 0) | (_ack ? 64 : 0)));
    /// <summary>Cancel a live deadline before reset or a rejected held byte can publish stale completions.</summary>
    private void Cancel(ref long token)
    { if (token != 0) { _scheduler.Cancel(token); token = 0; } }
    /// <summary>Abort pending handshake work while retaining already emitted bytes and the guest's output latches.</summary>
    private void Reset()
    {
        Cancel(ref _holdEvent); Cancel(ref _ackLowEvent); Cancel(ref _ackHighEvent); Cancel(ref _initializeEvent);
        if (_pulse || _holding) RejectedStrobes++;
        _pulse = _holding = _valid = false; _reset = true; _busy = _ack = true; Initializations++; Pins();
    }
    /// <summary>Interpret real output transitions, checking setup, pulse width, hold and active-low initialization separately.</summary>
    private void Observe(ParallelOutputs output)
    {
        byte previous = _lastControl; _lastControl = output.Control;
        if ((output.Control & 4) == 0)
        { if (!_reset) Reset(); return; }
        if ((previous & 4) == 0)
        {
            _reset = false; _busy = true;
            _initializeEvent = _scheduler.ScheduleAfter(InitializeDelay, () =>
            { _initializeEvent = 0; _busy = false; Pins(); }, this, "event0");
        }
        if ((_pulse || _holding) && (output.Data != _sample || (output.Control & 8) == 0)) _valid = false;
        if ((previous & 1) == 0 && (output.Control & 1) != 0)
        {
            if (_busy || (output.Control & 8) == 0) { RejectedStrobes++; return; }
            _pulse = true; _asserted = output.Time; _sample = output.Data; _sampleControl = output.Control;
            _valid = output.Time - _port.DataChangedAt >= MinimumSetup; _busy = true; Pins();
        }
        else if ((previous & 1) != 0 && (output.Control & 1) == 0 && _pulse)
        {
            _pulse = false; _holding = true; _released = output.Time;
            _valid &= output.Time - _asserted >= MinimumStrobe;
            _holdEvent = _scheduler.ScheduleAfter(MinimumHold, Commit, this, "event1");
        }
    }
    /// <summary>Commit exactly one byte after the hold deadline or reject it before any ACK or data event.</summary>
    private void Commit()
    {
        _holdEvent = 0; _holding = false;
        if (!_valid || (_port.Control & 13) != 12)
        { RejectedStrobes++; _busy = false; Pins(); return; }
        AcceptedBytes++;
        _ackLowEvent = _scheduler.ScheduleAfter(AckDelay, () =>
        {
            _ackLowEvent = 0; _ack = false; Pins();
            _ackHighEvent = _scheduler.ScheduleAfter(AckWidth, () =>
            { _ackHighEvent = 0; _ack = true; _busy = false; Pins(); }, this, "event3");
        }, this, "event2");
        Accepted?.Invoke(new(_sample, _sampleControl, _asserted, _released, _scheduler.Now));
    }
}

/// <summary>One observed valid strobe and the raw byte accepted after its hold deadline.</summary>
public readonly record struct ParallelByte(byte Data, byte Control, ulong Asserted, ulong Released, ulong AcceptedAt);
