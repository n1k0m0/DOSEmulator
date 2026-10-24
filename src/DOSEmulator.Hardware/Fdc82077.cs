using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>PC/AT two-drive sector-level FDC subset. DMA2, IRQ6 and deterministic seek/motor/sector delays.
/// Raw images cannot represent physical flux, deleted marks or CRC errors. Non-DMA data mode is not implemented.</summary>
public sealed class Fdc82077 : IIoPortDevice, IDisposable
{
    private readonly DeviceScheduler _clock;
    private readonly Dma8237 _dma;
    private readonly Action<bool> _irq;
    private readonly FloppyGeometry?[] _geometry;
    private readonly IReadOnlyBlockDevice?[] _media;
    private readonly byte[] _cylinder = new byte[4], _command = new byte[16];
    private readonly bool[] _changed = [true, true];
    private readonly ulong[] _motorReadyAt = new ulong[2];
    private readonly long[] _seekEvents = new long[4];
    private readonly Queue<byte> _result = new();
    private readonly Queue<(byte Status, byte Cylinder)> _sense = new();
    private byte _dor = 0x0c, _rate, _tape, _specify1, _specify2, _configure, _precomp;
    private bool _executing, _interrupt, _locked;
    private int _received, _expected, _drive, _c, _h, _r, _eot;
    private bool _write, _multiTrack;
    private long _event;
    private readonly byte[] _sector = new byte[512];
    public long ReadSectorCount { get; private set; }
    public long WrittenSectorCount { get; private set; }
    public bool Busy => _executing || _received != 0 || _result.Count != 0 || _seekEvents.Any(e => e != 0);
    public bool InterruptAsserted => _interrupt && (_dor & 0x0c) == 0x0c;

    public Fdc82077(DeviceScheduler clock, Dma8237 dma, Action<bool> irq,
        FloppyGeometry? driveA, FloppyGeometry? driveB, IReadOnlyBlockDevice? mediumA = null, IReadOnlyBlockDevice? mediumB = null)
    {
        _clock = clock; _dma = dma; _irq = irq; _geometry = [driveA, driveB]; _media = [mediumA, mediumB];
        ValidateMedium(0, mediumA); ValidateMedium(1, mediumB);
    }
    private void ValidateMedium(int drive, IReadOnlyBlockDevice? medium)
    {
        if ((uint)drive >= 2) throw new ArgumentOutOfRangeException(nameof(drive));
        if (medium is not null && (_geometry[drive] is null || _geometry[drive]!.SectorCount != medium.SectorCount))
            throw new ArgumentException("Floppy image must match the configured drive geometry. Change drive type with a power cycle.");
    }
    public string MediaName(int drive) => _media[drive]?.Name ?? "Empty";
    public IReadOnlyBlockDevice? ReopenMedia(int drive) => _media[drive]?.Reopen();
    public void ReplaceMedia(int drive, IReadOnlyBlockDevice? medium)
    {
        ValidateMedium(drive, medium);
        if (Busy) throw new InvalidOperationException("Floppy media can only change between controller operations.");
        if (ReferenceEquals(_media[drive], medium)) return;
        _media[drive]?.Dispose(); _media[drive] = medium; _changed[drive] = true;
    }
    private void SetInterrupt(bool value) { _interrupt = value; _irq(InterruptAsserted); }
    private bool Ready(int drive) => drive < 2 && (_dor & 3) == drive && (_dor & (0x10 << drive)) != 0 && _media[drive] is not null;
    public uint Read(ushort port, int width)
    {
        if (width != 1) return 0xff;
        return port switch
        {
            0x3f2 => _dor,
            0x3f3 => (uint)(_tape | 0xfc),
            0x3f4 => MainStatus(),
            0x3f5 => ReadResult(),
            0x3f7 => (_dor & 3) < 2 && _changed[_dor & 3] ? 0x80u : 0u,
            _ => 0xff
        };
    }
    private uint MainStatus()
    {
        if ((_dor & 4) == 0) return 0;
        uint result = _executing ? 0x10u : _result.Count > 0 ? 0xd0u : _received > 0 ? 0x90u : 0x80u;
        for (int i = 0; i < 4; i++) if (_seekEvents[i] != 0) result |= 1u << i;
        return result;
    }
    private byte ReadResult()
    {
        if (_result.Count == 0) return 0xff;
        SetInterrupt(false); return _result.Dequeue();
    }
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        byte data = (byte)value;
        switch (port)
        {
            case 0x3f2:
            {
                byte previous = _dor; _dor = data;
                for (int i = 0; i < 2; i++) if ((data & (0x10 << i)) != 0 && (previous & (0x10 << i)) == 0) _motorReadyAt[i] = _clock.Now + 300_000_000;
                if ((data & 4) == 0) Reset(false);
                else if ((previous & 4) == 0) Reset(true);
                _irq(InterruptAsserted); break;
            }
            case 0x3f3: _tape = (byte)(data & 3); break;
            case 0x3f4: _rate = (byte)(data & 3); if ((data & 0x80) != 0) Reset(true); break;
            case 0x3f7: _rate = (byte)(data & 3); break;
            case 0x3f5:
                if ((_dor & 4) == 0 || _executing || _result.Count > 0) return;
                if (_received == 0)
                {
                    _expected = (data & 0x1f) switch { 3 => 3, 4 or 7 or 10 or 18 => 2, 5 or 6 => 9, 8 or 14 or 16 or 20 => 1, 13 => 6, 15 => 3, 19 => 4, _ => 1 };
                }
                _command[_received++] = data;
                if (_received == _expected) { _received = 0; Execute(); }
                break;
        }
    }
    private void Reset(bool release)
    {
        if (_event != 0) _clock.Cancel(_event); _event = 0;
        for (int i = 0; i < 4; i++) { if (_seekEvents[i] != 0) _clock.Cancel(_seekEvents[i]); _seekEvents[i] = 0; }
        _received = 0; _executing = false; _result.Clear(); _sense.Clear(); SetInterrupt(false);
        if (!_locked) _configure = _precomp = 0;
        if (release) { _executing = true; _event = _clock.ScheduleAfter(1_000_000, () =>
        {
            _event = 0; _executing = false;
            for (int i = 0; i < 4; i++) _sense.Enqueue(((byte)(0xc0 | i), _cylinder[i])); SetInterrupt(true);
        }); }
    }
    private void Result(bool interrupt, params byte[] bytes)
    {
        _executing = false; foreach (byte b in bytes) _result.Enqueue(b);
        if (interrupt) SetInterrupt(true);
    }
    private void Execute()
    {
        int opcode = _command[0] & 0x1f, unit = _command[1] & 3;
        switch (opcode)
        {
            case 3: _specify1 = _command[1]; _specify2 = _command[2]; break;
            case 4:
                byte status = (byte)(_command[1] & 7);
                if (unit < 2 && _geometry[unit] is not null)
                {
                    status |= 8; if (_cylinder[unit] == 0) status |= 0x10;
                    if (Ready(unit)) status |= 0x20;
                    if (_media[unit] is not IWritableBlockDevice) status |= 0x40;
                }
                Result(false, status); break;
            case 7: Seek(unit, 0, true); break;
            case 15: Seek(unit, _command[2], false); break;
            case 8:
                SetInterrupt(false);
                if (_sense.TryDequeue(out var sense)) Result(false, sense.Status, sense.Cylinder); else Result(false, 0x80);
                break;
            case 16: Result(false, 0x90); break;
            case 19: _configure = _command[2]; _precomp = _command[3]; break;
            case 20: _locked = (_command[0] & 0x80) != 0; Result(false, _locked ? (byte)0x10 : (byte)0); break;
            case 14: Result(false, _cylinder[0], _cylinder[1], _cylinder[2], _cylinder[3], _specify1, _specify2, 0, (byte)(_locked ? 0x80 : 0), _configure, _precomp); break;
            case 5 or 6 or 10 or 13:
                _drive = unit; _write = opcode is 5 or 13; _multiTrack = (_command[0] & 0x80) != 0;
                _c = opcode == 10 || opcode == 13 ? _cylinder[unit] : _command[2];
                _h = opcode == 10 || opcode == 13 ? (_command[1] >> 2 & 1) : _command[3];
                _r = opcode == 10 || opcode == 13 ? 1 : _command[4]; _eot = _command[6];
                _executing = true; SetInterrupt(false);
                ulong wait = unit < 2 && _motorReadyAt[unit] > _clock.Now ? _motorReadyAt[unit] - _clock.Now : 0;
                _event = _clock.ScheduleAfter(wait + SectorDelay(), () =>
                {
                    _event = 0;
                    if (opcode == 10) { if (ValidateTransfer(false)) Finish(0, 0); }
                    else if (opcode == 13) FormatTrack();
                    else TransferSector();
                });
                break;
            default: Result(false, 0x80); break;
        }
    }
    private ulong SectorDelay() => (ulong)(200_000_000 / (_drive < 2 ? _geometry[_drive]?.SectorsPerTrack ?? 18 : 18));
    private void Seek(int drive, byte target, bool recalibrate)
    {
        if (_seekEvents[drive] != 0) { Result(false, 0x80); return; }
        ulong delay = (ulong)Math.Max(1, Math.Abs(target - _cylinder[drive])) * 2_000_000;
        _seekEvents[drive] = _clock.ScheduleAfter(delay, () =>
        {
            _seekEvents[drive] = 0; byte st0 = (byte)(0x20 | drive);
            if (drive >= 2 || _geometry[drive] is null) st0 |= 0x50;
            else
            {
                if (_media[drive] is not null && (recalibrate || target != _cylinder[drive])) _changed[drive] = false;
                _cylinder[drive] = target;
            }
            _sense.Enqueue((st0, _cylinder[drive])); SetInterrupt(true);
        });
    }
    private bool ValidateTransfer(bool data)
    {
        if (!Ready(_drive)) { Finish(4, 0, notReady: true); return false; }
        FloppyGeometry geometry = _geometry[_drive]!;
        if (_rate != geometry.DataRate || (_command[0] & 0x40) == 0) { Finish(4, 0); return false; }
        if (_c != _cylinder[_drive])
        {
            if ((_configure & 0x40) != 0) { _cylinder[_drive] = (byte)_c; _changed[_drive] = false; }
            else { Finish(4, 0x10); return false; }
        }
        if (_c >= geometry.Cylinders || _h >= geometry.Heads || _h != (_command[1] >> 2 & 1) || _r < 1 || _r > geometry.SectorsPerTrack)
        { Finish(4, 0); return false; }
        if (data && (_command[5] != 2 || _eot < _r || _eot > geometry.SectorsPerTrack)) { Finish(4, 0); return false; }
        if (_write && _media[_drive] is not IWritableBlockDevice) { Finish(2, 0); return false; }
        if (data && ((_specify2 & 1) != 0 || (_dor & 8) == 0)) { Finish(0x10, 0); return false; }
        return true;
    }
    private void TransferSector()
    {
        if (!ValidateTransfer(true)) return;
        FloppyGeometry geometry = _geometry[_drive]!;
        long lba = ((_c * geometry.Heads + _h) * geometry.SectorsPerTrack) + _r - 1;
        try
        {
            if (!_write) _media[_drive]!.ReadSector(lba, _sector);
            int bytes = _dma.Transfer(2, _sector, !_write);
            if (bytes != 512) { Finish(0x10, 0); return; }
            if (_write) { ((IWritableBlockDevice)_media[_drive]!).WriteSector(lba, _sector); WrittenSectorCount++; }
            else ReadSectorCount++;
            if (_dma.IsTerminalCount(2)) { AdvanceSector(); Finish(0, 0); return; }
            if (_r == _eot)
            {
                if (_multiTrack && _h == 0) { _h = 1; _r = 1; _command[1] |= 4; }
                else { AdvanceSector(); Finish(0x80, 0); return; }
            }
            else _r++;
            _event = _clock.ScheduleAfter(SectorDelay(), () => { _event = 0; TransferSector(); });
        }
        catch (IOException) { Finish(0x20, 0); }
        catch (InvalidOperationException) { Finish(0x10, 0); }
    }
    private void AdvanceSector()
    {
        if (_r < _eot) _r++;
        else { _r = 1; if (_multiTrack && _h == 0) _h = 1; else { _h = 0; _c++; } }
    }
    private void FormatTrack()
    {
        if (!ValidateTransfer(false)) return;
        int count = _command[3]; FloppyGeometry geometry = _geometry[_drive]!;
        if (_command[2] != 2 || count < 1 || count > geometry.SectorsPerTrack || (_specify2 & 1) != 0 || (_dor & 8) == 0) { Finish(4, 0); return; }
        byte[] descriptors = new byte[count * 4];
        try
        {
            if (_dma.Transfer(2, descriptors, false) != descriptors.Length) { Finish(0x10, 0); return; }
            for (int i = 0; i < count; i++)
            {
                int offset = i * 4;
                if (descriptors[offset] != _c || descriptors[offset + 1] != _h || descriptors[offset + 2] < 1 || descriptors[offset + 2] > geometry.SectorsPerTrack || descriptors[offset + 3] != 2)
                { Finish(4, 0); return; }
            }
            _sector.AsSpan().Fill(_command[5]);
            for (int i = 0; i < count; i++)
            {
                _r = descriptors[i * 4 + 2]; long lba = (_c * geometry.Heads + _h) * geometry.SectorsPerTrack + _r - 1;
                ((IWritableBlockDevice)_media[_drive]!).WriteSector(lba, _sector); WrittenSectorCount++;
            }
            Finish(0, 0);
        }
        catch (IOException) { Finish(0x20, 0); }
        catch (InvalidOperationException) { Finish(0x10, 0); }
    }
    private void Finish(byte st1, byte st2, bool notReady = false) => Result(true,
        (byte)((st1 != 0 || st2 != 0 ? 0x40 : 0) | (notReady ? 8 : 0) | (_h & 1) << 2 | _drive),
        st1, st2, (byte)_c, (byte)_h, (byte)_r, 2);
    public void Dispose()
    {
        Reset(false); foreach (var medium in _media) medium?.Dispose();
    }
}
