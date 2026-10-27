// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Functional National PC16550D reference with an explicit COM1/IRQ4 board contract.
/// Framed-byte wire endpoints and nominal clocks are modeled; electrical RXD sampling and bus propagation are not.</summary>
public sealed partial class Uart16550(DeviceScheduler scheduler, Action<bool> interrupt) : IIoPortDevice
{
    public const string ProfileId = "pc16550d-com1-development";
    public const string Model = "National PC16550D / COM1 functional reference";
    public const uint ClockHz = 1_843_200;
    public const ushort BasePort = 0x3f8;
    private struct Received { public byte Data, Errors; }
    private readonly Received[] _rx = new Received[16];
    private readonly Queue<byte> _tx = new();
    private readonly Queue<(byte Data, byte Errors)> _wire = new();
    private int _rxHead, _rxCount;
    private byte _ier, _lcr, _mcr, _fcr, _scratch, _physicalModem, _effectiveModem, _modemDelta;
    private ushort _divisor;
    private bool _overrun, _timeout, _thre = true, _threPending = true, _txBusy, _rxBusy, _burst, _breakDelivered;
    private long _startEvent, _txEvent, _rxEvent, _timeoutEvent, _threEvent, _breakEvent;
    private ulong _txRemainder, _rxRemainder;
    /// <summary>One actual completed non-loopback output character, after its full nominal frame duration.</summary>
    public event Action<UartFrame>? Transmitted;
    /// <summary>Current programmed line format; DLAB remains visible as written.</summary>
    public byte LineControl => _lcr;
    /// <summary>Current five meaningful modem-control bits, including local loopback.</summary>
    public byte ModemControl => _mcr;
    /// <summary>Current four meaningful interrupt enables, independent from the board OUT2 gate.</summary>
    public byte InterruptEnable => _ier;
    /// <summary>Stored FIFO enable, DMA-request mode and receiver threshold; reset command bits self-clear.</summary>
    public byte FifoControl => _fcr;
    /// <summary>Current divisor; zero deliberately stops new nominal framed work rather than dividing by zero.</summary>
    public ushort Divisor => _divisor;
    /// <summary>Current receiver FIFO/holding occupancy, excluding a still incoming frame.</summary>
    public int ReceivedCount => _rxCount;
    /// <summary>Queued transmitter FIFO/holding occupancy, excluding the active shift register.</summary>
    public int TransmitCount => _tx.Count;
    /// <summary>Current receiver threshold; a non-FIFO holding register has capacity and threshold one.</summary>
    public int ReceiveTrigger => FifoEnabled ? (_fcr >> 6) switch { 0 => 1, 1 => 4, 2 => 8, _ => 14 } : 1;
    /// <summary>Whether both sixteen-byte FIFOs are enabled.</summary>
    public bool FifoEnabled => (_fcr & 1) != 0;
    /// <summary>Whether a transmitter shift frame is currently in flight.</summary>
    public bool Transmitting => _txBusy;
    /// <summary>Actual gated board IRQ4 level; no fictitious IRQ is generated for a register read.</summary>
    public bool InterruptAsserted { get; private set; }
    /// <summary>Actual low-to-high board IRQ edges, distinct from CPU exception vectors.</summary>
    public long InterruptEdges { get; private set; }
    /// <summary>Completed transmitter frames, including local diagnostic loopback frames.</summary>
    public long TransmittedCharacters { get; private set; }
    /// <summary>Frames delivered to the receiver, including frames that cause an overrun.</summary>
    public long ReceivedCharacters { get; private set; }
    /// <summary>Actual receiver overrun occurrences, independent from sticky LSR acknowledgement.</summary>
    public long Overruns { get; private set; }
    /// <summary>Writes beyond the available transmitter holding/FIFO capacity; undefined physical overwrite effects are not promised.</summary>
    public long RejectedTransmitWrites { get; private set; }
    /// <summary>Number of explicitly supplied external framed characters.</summary>
    public long ExternalCharactersQueued { get; private set; }
    /// <summary>Number of external frames discarded while local loopback disconnects the receiver pin.</summary>
    public long ExternalCharactersIgnored { get; private set; }
    /// <summary>Physical logical modem inputs before local loopback substitution.</summary>
    public byte PhysicalModem => _physicalModem;
    /// <summary>Current logical modem inputs after local loopback substitution.</summary>
    public byte EffectiveModem => _effectiveModem;
    /// <summary>Side-effect-free live LSR for diagnostics; only a guest LSR read acknowledges errors.</summary>
    public byte LineStatus => Status();
    /// <summary>Side-effect-free IIR source for diagnostics; only the port read acknowledges a THRE source.</summary>
    public byte InterruptIdentification => (byte)((FifoEnabled ? 0xc0 : 0) | Source());

    /// <summary>Convert exact input-clock cycles to nanoseconds with a carried remainder for consecutive frames.</summary>
    private static ulong Delay(ulong clocks, ref ulong remainder)
    {
        ulong numerator = checked(clocks * 1_000_000_000 + remainder);
        remainder = numerator % ClockHz; return numerator / ClockHz;
    }
    /// <summary>Count a complete start/data/parity/stop frame in half-bit units, including the 5-bit 1.5-stop form.</summary>
    public static int HalfBits(byte lcr) => 2 * (1 + 5 + (lcr & 3) + ((lcr & 8) != 0 ? 1 : 0)) + StopHalfBits(lcr);
    /// <summary>Count the programmed stop interval without approximating a half stop bit as a whole bit.</summary>
    private static int StopHalfBits(byte lcr) => (lcr & 4) == 0 ? 2 : (lcr & 3) == 0 ? 3 : 4;
    /// <summary>Convert a selected frame or interval independently from the continuous transmit/receive remainder.</summary>
    private ulong Interval(int halfBits)
    { ulong remainder = 0; return Delay((ulong)halfBits * 8 * _divisor, ref remainder); }
    /// <summary>Cancel only a live queued event; fired callbacks clear their token before manipulating later work.</summary>
    private void Cancel(ref long token)
    { if (token != 0) { scheduler.Cancel(token); token = 0; } }
    /// <summary>Compute current LSR including head-character errors and the separate FIFO-wide error summary.</summary>
    private byte Status()
    {
        int status = (_rxCount > 0 ? 1 | _rx[_rxHead].Errors : 0) | (_overrun ? 2 : 0) | (_thre ? 0x20 : 0);
        if (_tx.Count == 0 && !_txBusy && _startEvent == 0) status |= 0x40;
        if (FifoEnabled)
            for (int i = 0; i < _rxCount; i++) if (_rx[(_rxHead + i) & 15].Errors != 0) { status |= 0x80; break; }
        return (byte)status;
    }
    /// <summary>Select one prioritized source without acknowledging unrelated lower-priority conditions.</summary>
    private byte Source()
    {
        if ((_ier & 4) != 0 && (Status() & 0x1e) != 0) return 6;
        if ((_ier & 1) != 0 && _rxCount >= ReceiveTrigger) return 4;
        if ((_ier & 1) != 0 && FifoEnabled && _rxCount > 0 && _timeout) return 12;
        if ((_ier & 2) != 0 && _thre && _threPending) return 2;
        if ((_ier & 8) != 0 && _modemDelta != 0) return 0;
        return 1;
    }
    /// <summary>Apply the DOSE COM-board OUT2 software gate after the UART's own prioritized source selection.</summary>
    private void UpdateInterrupt()
    {
        bool high = Source() != 1 && (_mcr & 8) != 0;
        if (high == InterruptAsserted) return;
        InterruptAsserted = high; if (high) InterruptEdges++; interrupt(high);
    }
    /// <summary>Restart the four-character receiver inactivity timer; it runs without consuming host wall-clock time.</summary>
    private void RestartTimeout()
    {
        Cancel(ref _timeoutEvent); _timeout = false;
        if (FifoEnabled && _rxCount > 0 && _divisor != 0)
            _timeoutEvent = scheduler.ScheduleAfter(Interval(HalfBits(_lcr) * 4), () =>
            { _timeoutEvent = 0; _timeout = true; UpdateInterrupt(); }, this, "event0");
    }
    /// <summary>Commit one complete frame to the receiver; FIFO overrun retains old entries, character mode retains the newest byte.</summary>
    private void Receive(byte value, byte errors)
    {
        ReceivedCharacters++;
        if ((errors & 16) != 0) { value = 0; errors |= 8; }
        if (_rxCount == (FifoEnabled ? 16 : 1))
        {
            _overrun = true; Overruns++;
            if (!FifoEnabled) _rx[_rxHead] = new() { Data = value, Errors = errors };
        }
        else { _rx[(_rxHead + _rxCount) & 15] = new() { Data = value, Errors = errors }; _rxCount++; }
        RestartTimeout(); UpdateInterrupt();
    }
    /// <summary>Queue bounded external framed bytes, preserving input ownership and rejecting the entire request before mutation.</summary>
    public void QueueReceive(ReadOnlySpan<byte> bytes, byte errors = 0)
    {
        if ((errors & ~0x1c) != 0 || bytes.Length > 4096 - _wire.Count - (_rxBusy ? 1 : 0))
            throw new ArgumentException("Serial input requires at most 4096 pending frames and only PE/FE/BI error bits.");
        foreach (byte value in bytes) _wire.Enqueue((value, errors));
        ExternalCharactersQueued += bytes.Length; StartReceive();
    }
    /// <summary>Clock the next external character across a complete nominal frame, sampling its format and loopback connection at start.</summary>
    private void StartReceive()
    {
        if (_rxBusy || _wire.Count == 0 || _divisor == 0) return;
        var frame = _wire.Dequeue(); bool loop = (_mcr & 16) != 0; byte lcr = _lcr;
        _rxBusy = true;
        _rxEvent = scheduler.ScheduleAfter(Delay((ulong)HalfBits(lcr) * 8 * _divisor, ref _rxRemainder), () =>
        {
            _rxEvent = 0; _rxBusy = false;
            if (loop) ExternalCharactersIgnored++; else Receive((byte)(frame.Data & ((1 << (5 + (lcr & 3))) - 1)), frame.Errors);
            StartReceive();
        }, this, "event1", () => StateValues.Pack(frame, loop, lcr));
    }
    /// <summary>Start an idle transmitter at the reference board's next nominal bit boundary.</summary>
    private void KickTransmit()
    {
        if (_txBusy || _startEvent != 0 || _tx.Count == 0 || _divisor == 0) return;
        _startEvent = scheduler.ScheduleAfter(Interval(2), () => { _startEvent = 0; StartTransmit(); }, this, "event2");
    }
    /// <summary>Move one queued byte into the shift register; apply delayed single-byte FIFO-empty indication separately.</summary>
    private void StartTransmit()
    {
        if (_txBusy || _tx.Count == 0 || _divisor == 0) return;
        byte lcr = _lcr, value = (byte)(_tx.Dequeue() & ((1 << (5 + (_lcr & 3))) - 1));
        ushort divisor = _divisor; ulong start = scheduler.Now;
        bool loop = (_mcr & 16) != 0, breaking = (lcr & 64) != 0;
        _txBusy = true;
        if (_tx.Count == 0)
        {
            Cancel(ref _threEvent);
            if (!FifoEnabled || _burst) { _thre = _threPending = true; _burst = false; }
            else _threEvent = scheduler.ScheduleAfter(Interval(HalfBits(lcr) - StopHalfBits(lcr)), () =>
            { _threEvent = 0; if (_tx.Count == 0) _thre = _threPending = true; UpdateInterrupt(); }, this, "event3");
        }
        _txEvent = scheduler.ScheduleAfter(Delay((ulong)HalfBits(lcr) * 8 * _divisor, ref _txRemainder), () =>
        {
            _txEvent = 0; _txBusy = false; TransmittedCharacters++;
            if (loop) { if (!breaking) Receive(value, 0); }
            else Transmitted?.Invoke(new(start, scheduler.Now, breaking ? (byte)0 : value, breaking ? (byte)0x18 : (byte)0, lcr, divisor));
            StartTransmit(); UpdateInterrupt();
        }, this, "event4", () => StateValues.Pack(lcr, value, divisor, start, loop, breaking));
        UpdateInterrupt();
    }
    /// <summary>Model one break indication while the local loopback line remains continuously spacing.</summary>
    private void UpdateBreak()
    {
        Cancel(ref _breakEvent);
        if ((_lcr & 64) == 0 || (_mcr & 16) == 0) { _breakDelivered = false; return; }
        if (_breakDelivered || _divisor == 0) return;
        _breakEvent = scheduler.ScheduleAfter(Interval(HalfBits(_lcr)), () =>
        { _breakEvent = 0; _breakDelivered = true; Receive(0, 0x18); }, this, "event5");
    }
    /// <summary>Apply logical asserted CTS/DSR/RI/DCD pins without changing guest registers or bypassing local loopback.</summary>
    public void SetModemInputs(bool cts, bool dsr, bool ri, bool dcd)
    {
        _physicalModem = (byte)((cts ? 16 : 0) | (dsr ? 32 : 0) | (ri ? 64 : 0) | (dcd ? 128 : 0));
        UpdateModem();
    }
    /// <summary>Derive loopback modem pins and sticky delta bits; RI reports only the logical asserted-to-deasserted edge.</summary>
    private void UpdateModem()
    {
        byte next = (_mcr & 16) == 0 ? _physicalModem :
            (byte)(((_mcr & 2) << 3) | ((_mcr & 1) << 5) | ((_mcr & 4) << 4) | ((_mcr & 8) << 4));
        byte difference = (byte)(next ^ _effectiveModem);
        _modemDelta |= (byte)(((difference >> 4) & 0xb) | ((_effectiveModem & 64) != 0 && (next & 64) == 0 ? 4 : 0));
        _effectiveModem = next; UpdateInterrupt();
    }
    /// <summary>Validate the bus width; unimplemented wide accesses remain undriven and have no byte side effect.</summary>
    private static uint Mask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Read an actual UART register with its documented acknowledgement effects.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width); if (width != 1 || port < BasePort || port > BasePort + 7) return mask;
        int offset = port - BasePort; byte result;
        if ((_lcr & 128) != 0 && offset < 2) return offset == 0 ? (byte)_divisor : (byte)(_divisor >> 8);
        switch (offset)
        {
            case 0:
                result = _rxCount == 0 ? (byte)0 : _rx[_rxHead].Data;
                if (_rxCount != 0) { _rxHead = (_rxHead + 1) & 15; _rxCount--; }
                RestartTimeout(); break;
            case 1: return _ier;
            case 2: result = InterruptIdentification; if ((result & 15) == 2) _threPending = false; break;
            case 3: return _lcr;
            case 4: return _mcr;
            case 5: result = Status(); _overrun = false; if (_rxCount > 0) _rx[_rxHead].Errors = 0; break;
            case 6: result = (byte)(_effectiveModem | _modemDelta); _modemDelta = 0; break;
            default: return _scratch;
        }
        UpdateInterrupt(); return result;
    }
    /// <summary>Reset only FIFO/holding state requested by FCR, retaining in-flight transmitter and receiver shift frames.</summary>
    private void WriteFifo(byte value)
    {
        bool toggle = ((_fcr ^ value) & 1) != 0;
        _fcr = (byte)((value & 1) != 0 ? value & 0xc9 : 0);
        if (toggle || (FifoEnabled && (value & 2) != 0))
        { _rxCount = _rxHead = 0; _overrun = false; RestartTimeout(); }
        if (toggle || (FifoEnabled && (value & 4) != 0))
        {
            _tx.Clear(); Cancel(ref _startEvent); Cancel(ref _threEvent); _burst = false;
            _thre = _threPending = true;
        }
        UpdateInterrupt();
    }
    /// <summary>Write the selected UART register; IIR/FCR and DLAB aliases stay distinct and no CTS autoflow is invented.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Mask(width); if (width != 1 || port < BasePort || port > BasePort + 7) return;
        byte data = (byte)value; int offset = port - BasePort;
        if ((_lcr & 128) != 0 && offset < 2)
        {
            _divisor = offset == 0 ? (ushort)((_divisor & 0xff00) | data) : (ushort)((_divisor & 255) | data << 8);
            RestartTimeout(); KickTransmit(); StartReceive(); UpdateBreak(); UpdateInterrupt(); return;
        }
        switch (offset)
        {
            case 0:
                if (_tx.Count >= (FifoEnabled ? 16 : 1)) { RejectedTransmitWrites++; return; }
                _tx.Enqueue(data); Cancel(ref _threEvent); _thre = _threPending = false;
                if (_tx.Count >= 2) _burst = true;
                KickTransmit(); break;
            case 1:
                if ((_ier & 2) == 0 && (data & 2) != 0 && _thre) _threPending = true;
                _ier = (byte)(data & 15); break;
            case 2: WriteFifo(data); return;
            case 3: _lcr = data; RestartTimeout(); UpdateBreak(); break;
            case 4: _mcr = (byte)(data & 31); UpdateModem(); UpdateBreak(); break;
            case 7: _scratch = data; break;
        }
        UpdateInterrupt();
    }
}

/// <summary>A completed virtual output frame with its sampled format, nominal clock and actual scheduler boundaries.</summary>
public readonly record struct UartFrame(ulong Start, ulong End, byte Data, byte Errors, byte LineControl, ushort Divisor);
