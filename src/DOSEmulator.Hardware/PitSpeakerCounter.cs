using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>8254 counter 2: binary modes 0/2/3, gate, clock-boundary loads and count/status latches.</summary>
public sealed class PitSpeakerCounter(DeviceScheduler clock)
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
    public bool UnsupportedProgramming { get; private set; }
    public int Mode => (_control >> 1) & 7;
    private int Access => (_control >> 4) & 3;
    private ulong NowTick => (ulong)((UInt128)clock.Now * Frequency / TickUnits);
    public bool Gate => _gate;
    public bool NullCount => !_loaded || _loadEvent != 0;
    public uint Reload => _programmed;
    public bool Output => !_running || NowTick < _originTick ? _fixedOutput : Mode == 0
        ? NowTick - _originTick >= _reload : Phase(NowTick - _originTick) < HighTicks;
    private uint HighTicks => Mode == 2 ? _reload - 1 : (_reload + 1) / 2;
    private ulong Phase(ulong elapsed) => (elapsed + (_startsHigh ? 0 : HighTicks)) % _reload;

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
    private void Freeze()
    {
        if (_running)
        {
            _frozenCount = Current(); _fixedOutput = Output;
            _pausedElapsed = NowTick >= _originTick ? NowTick - _originTick : 0;
        }
        _running = false;
    }
    private void CancelLoad() { if (_loadEvent != 0) clock.Cancel(_loadEvent); _loadEvent = 0; }
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
        });
    }
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
    public void Latch(bool count, bool status)
    {
        if (count && _latchedCount is null) { _latchedCount = Current(); _latchHigh = false; }
        if (status) _latchedStatus ??= (byte)((Output ? 128 : 0) | (NullCount ? 64 : 0) | _control);
    }
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
