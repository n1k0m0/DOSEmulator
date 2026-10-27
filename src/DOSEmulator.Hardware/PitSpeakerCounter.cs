// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>8254 counter 2: binary modes 0/2/3, gate, clock-boundary loads and count/status latches.</summary>
public sealed partial class PitSpeakerCounter(DeviceScheduler clock)
{
    public const uint Frequency = 1_193_182;
    private const ulong TickUnits = 1_000_000_000;
    private byte _control = 0x36, _low;
    private bool _writeHigh, _readHigh, _latchHigh, _gate, _running, _loaded, _fixedOutput = true, _startsHigh = true;
    private uint _reload = 65536, _programmed = 65536;
    private ulong _originTick, _pausedElapsed;
    private ushort _frozenCount;
    private ushort? _latchedCount;
    private byte? _latchedStatus;
    private long _loadEvent;
    public event Action? BeforeChange;
    /// <summary>Diagnostic indication that the guest selected programming outside the implemented reference contract.</summary>
    public bool UnsupportedProgramming { get; private set; }
    /// <summary>Programmed reference counter/transfer mode.</summary>
    public int Mode => (_control >> 1) & 7;
    /// <summary>Programmed low/high-byte access sequence for the PIT register.</summary>
    private int Access => (_control >> 4) & 3;
    /// <summary>Current counter time expressed in its native integer clock ticks.</summary>
    private ulong NowTick => (ulong)((UInt128)clock.Now * Frequency / TickUnits);
    /// <summary>Current external gate input to the reference PIT counter.</summary>
    public bool Gate => _gate;
    /// <summary>Whether the newly programmed PIT count has not yet become an active loaded count.</summary>
    public bool NullCount => !_loaded || _loadEvent != 0;
    /// <summary>Current programmed reload count under the counter's zero-means-maximum rule.</summary>
    public uint Reload => _programmed;
    /// <summary>Current reference output signal of PitSpeakerCounter, in that component's native representation.</summary>
    public bool Output => !_running || NowTick < _originTick ? _fixedOutput : Mode == 0
        ? NowTick - _originTick >= _reload : Phase(NowTick - _originTick) < HighTicks;
    /// <summary>Accumulated high-output duration in the counter's native tick unit.</summary>
    private uint HighTicks => Mode == 2 ? _reload - 1 : (_reload + 1) / 2;
    /// <summary>Determines the current reference counter phase from elapsed integer PIT ticks.</summary>
    private ulong Phase(ulong elapsed) => (elapsed + (_startsHigh ? 0 : HighTicks)) % _reload;

    /// <summary>Programs the speaker counter's mode and low/high-byte access state.</summary>
    public void Control(byte control)
    {
        if (((control >> 4) & 3) == 0) { Latch(true, false); return; }
        BeforeChange?.Invoke(); CancelLoad();
        int mode = (control >> 1) & 7; if (mode >= 6) mode -= 4;
        _control = (byte)((control & 0x31) | mode << 1);
        UnsupportedProgramming = (control & 1) != 0 || mode is not (0 or 2 or 3);
        _writeHigh = _readHigh = _latchHigh = _running = _loaded = false;
        _latchedCount = null; _latchedStatus = null; _pausedElapsed = 0;
        _fixedOutput = mode != 0;
    }
    /// <summary>Accepts a programmed count byte and applies the reference delayed-load contract.</summary>
    public void Write(byte value)
    {
        if (Access == 3 && !_writeHigh)
        {
            _low = value; _writeHigh = true;
            if (Mode == 0) { BeforeChange?.Invoke(); Freeze(); CancelLoad(); _fixedOutput = false; }
            return;
        }
        ushort raw = Access == 3 ? (ushort)(_low | value << 8) : Access == 1 ? value : (ushort)(value << 8);
        _writeHigh = false; BeforeChange?.Invoke();
        _programmed = raw == 0 ? 65536u : raw;
        if (UnsupportedProgramming || (Mode is 2 or 3 && _programmed < 2))
        { UnsupportedProgramming = true; CancelLoad(); _running = _loaded = false; return; }
        ulong at = NowTick + 1; bool startsHigh = true;
        if (_running && _loaded && Mode is 2 or 3)
        {
            ulong phase = Phase(NowTick - _originTick);
            if (Mode == 2) at = NowTick + _reload - phase;
            else
            {
                bool currentlyHigh = phase < HighTicks;
                at = NowTick + (currentlyHigh ? HighTicks - phase : _reload - phase);
                startsHigh = !currentlyHigh;
            }
        }
        ScheduleLoad(at, startsHigh, false);
    }
    /// <summary>Changes the counter gate after integrating/fixing the old state at the actual virtual tick.</summary>
    public void SetGate(bool gate)
    {
        if (_gate == gate) return;
        BeforeChange?.Invoke(); _gate = gate;
        if (!gate)
        {
            Freeze();
            if (Mode is 2 or 3) _fixedOutput = true;
        }
        else if (_loaded && !UnsupportedProgramming)
        {
            if (Mode is 2 or 3) _fixedOutput = true;
            ScheduleLoad(NowTick + 1, true, Mode == 0);
        }
    }
    /// <summary>Captures the current counter state at a gate/control boundary before future timing changes.</summary>
    private void Freeze()
    {
        if (_running)
        {
            _frozenCount = Current(); _fixedOutput = Output;
            _pausedElapsed = NowTick >= _originTick ? NowTick - _originTick : 0;
        }
        _running = false;
    }
    /// <summary>Cancels a pending programmed-count load without inventing a counter transition.</summary>
    private void CancelLoad() { if (_loadEvent != 0) clock.Cancel(_loadEvent); _loadEvent = 0; }
    /// <summary>Schedules the new PIT count at its reference clock boundary.</summary>
    private void ScheduleLoad(ulong tick, bool startsHigh, bool resume)
    {
        CancelLoad();
        ulong nanoseconds = (ulong)(((UInt128)tick * TickUnits + Frequency - 1) / Frequency);
        _loadEvent = clock.ScheduleAfter(nanoseconds - clock.Now, () =>
        {
            BeforeChange?.Invoke(); _loadEvent = 0;
            _reload = _programmed; _originTick = resume ? tick - _pausedElapsed : tick;
            _startsHigh = startsHigh; _loaded = true; _running = _gate;
            _frozenCount = (ushort)_reload;
            if (!resume) { _pausedElapsed = 0; _fixedOutput = Mode != 0; }
        }, this, "event0", () => StateValues.Pack(tick, startsHigh, resume));
    }
    /// <summary>Returns the current reference speaker-counter value under its active mode.</summary>
    private ushort Current()
    {
        if (!_loaded || !_running || NowTick < _originTick) return _frozenCount;
        ulong elapsed = NowTick - _originTick;
        if (Mode == 0) return unchecked((ushort)(_reload - elapsed));
        ulong phase = Phase(elapsed);
        if (Mode == 2) return (ushort)(_reload - phase);
        ulong withinHalf = phase < HighTicks ? phase : phase - HighTicks;
        return (ushort)((_reload & ~1u) - 2 * withinHalf);
    }
    /// <summary>Captures the current counter value for a subsequent guest low/high-byte read sequence.</summary>
    public void Latch(bool count, bool status)
    {
        if (count && _latchedCount is null) { _latchedCount = Current(); _latchHigh = false; }
        if (status) _latchedStatus ??= (byte)((Output ? 128 : 0) | (NullCount ? 64 : 0) | _control);
    }
    /// <summary>Returns a latched or current count byte under the programmed access sequence.</summary>
    public byte Read()
    {
        if (_latchedStatus is { } status) { _latchedStatus = null; return status; }
        ushort count = _latchedCount ?? Current();
        if (Access != 3) { _latchedCount = null; return Access == 1 ? (byte)count : (byte)(count >> 8); }
        bool high = _latchedCount is not null ? _latchHigh : _readHigh;
        byte result = high ? (byte)(count >> 8) : (byte)count;
        if (_latchedCount is not null) { _latchHigh = !high; if (high) _latchedCount = null; }
        else _readHigh = !high;
        return result;
    }
    // Integral in ns * input-Hz units. Exact integer area, including sub-tick portions of a PCM sample.
    public UInt128 HighArea(ulong start, ulong end)
    {
        if (end < start || end > clock.Now) throw new ArgumentOutOfRangeException(nameof(end));
        UInt128 from = (UInt128)start * Frequency, to = (UInt128)end * Frequency;
        if (!_running) return _fixedOutput ? to - from : 0;
        UInt128 origin = (UInt128)_originTick * TickUnits, result = 0;
        if (from < origin)
        {
            UInt128 before = UInt128.Min(to, origin);
            if (_fixedOutput) result += before - from;
            from = before;
        }
        if (to <= from) return result;
        // Computes the signed high/low tick area for the requested reference counter interval.
        UInt128 Integral(UInt128 units)
        {
            if (Mode == 0) return units > (UInt128)_reload * TickUnits ? units - (UInt128)_reload * TickUnits : 0;
            UInt128 period = (UInt128)_reload * TickUnits, high = (UInt128)HighTicks * TickUnits;
            units += _startsHigh ? 0 : high;
            return units / period * high + UInt128.Min(units % period, high);
        }
        return result + Integral(to - origin) - Integral(from - origin);
    }
}
