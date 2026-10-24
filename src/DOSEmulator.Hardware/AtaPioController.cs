using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Development primary IDE master: IDENTIFY, LBA28 READ/WRITE SECTORS and FLUSH CACHE. Writes require a private overlay.</summary>
public sealed class AtaPioController : IIoPortDevice
{
    private readonly DeviceScheduler _scheduler;
    private readonly IReadOnlyBlockDevice? _disk;
    private readonly byte[] _data = new byte[512];
    private readonly byte[] _task = new byte[8];
    private int _offset, _remaining;
    private long _lba;
    private bool _identify, _resetActive, _writing;
    private long _event;
    private byte _control;
    private bool _pendingInterrupt;
    public byte Status { get; private set; }
    public bool InterruptAsserted => _pendingInterrupt && (_control & 2) == 0;
    public long ReadSectorCount { get; private set; }
    public long WrittenSectorCount { get; private set; }

    public AtaPioController(DeviceScheduler scheduler, IReadOnlyBlockDevice? disk)
    {
        _scheduler = scheduler; _disk = disk; Reset();
    }
    public void Reset()
    {
        if (_event != 0) _scheduler.Cancel(_event);
        _event = 0; Array.Clear(_task); Array.Clear(_data);
        _task[1] = 1; _task[2] = 1; _task[3] = 1; _task[6] = 0xa0;
        _offset = _remaining = 0; _pendingInterrupt = false; _identify = _writing = false;
        Status = _disk is null ? (byte)0 : (byte)0x50;
    }

    public uint Read(ushort port, int width)
    {
        if (port == 0x3f6) return SelectedExists ? Status : 0u;
        if (port == 0x1f0)
        {
            if (width != 2 || !SelectedExists || _writing || (Status & 8) == 0) return width == 1 ? 0xffu : 0xffffu;
            uint word = (uint)(_data[_offset] | _data[_offset + 1] << 8);
            _offset += 2;
            if (_offset == 512)
            {
                if (!_identify) { ReadSectorCount++; _lba++; _remaining--; UpdateTaskAddress(); }
                if (!_identify && _remaining > 0) BeginRead();
                else Status = 0x50;
            }
            return word;
        }
        if (width != 1) return 0xffff;
        int index = port - 0x1f0;
        if (index == 7) { _pendingInterrupt = false; return SelectedExists ? Status : 0u; }
        return index is >= 1 and <= 6 ? _task[index] : 0xffu;
    }

    public void Write(ushort port, uint value, int width)
    {
        if (port == 0x1f0 && width == 2)
        {
            if (!_writing || !SelectedExists || (Status & 8) == 0) return;
            _data[_offset++] = (byte)value; _data[_offset++] = (byte)(value >> 8);
            if (_offset == 512)
            {
                Status = 0x80;
                _event = _scheduler.ScheduleAfter(5_000, () =>
                {
                    _event = 0;
                    try { ((IWritableBlockDevice)_disk!).WriteSector(_lba, _data); }
                    catch (IOException) { Abort(0x40); return; }
                    WrittenSectorCount++; _lba++; _remaining--; UpdateTaskAddress(); _offset = 0;
                    Status = _remaining > 0 ? (byte)0x58 : (byte)0x50; _pendingInterrupt = true;
                });
            }
            return;
        }
        if (width != 1) return;
        if (port == 0x3f6)
        {
            bool reset = (value & 4) != 0;
            _control = (byte)value;
            if (reset && !_resetActive) { Reset(); Status = 0x80; }
            else if (!reset && _resetActive) Reset();
            _resetActive = reset;
            return;
        }
        int index = port - 0x1f0;
        if (index is >= 1 and <= 6)
        {
            if (index == 6 || (Status & 0x80) == 0) _task[index] = (byte)value;
            return;
        }
        if (index != 7 || _resetActive || !SelectedExists || (Status & 0x88) != 0) return;
        _pendingInterrupt = false; _task[1] = 0;
        if (value == 0xec)
        {
            _identify = true; _writing = false; _offset = 0; Status = 0x80;
            _event = _scheduler.ScheduleAfter(5_000, () => { _event = 0; Identify(); Status = 0x58; _pendingInterrupt = true; });
        }
        else if (value is 0x20 or 0x21 && (_task[6] & 0x40) != 0)
        {
            _identify = _writing = false;
            _lba = _task[3] | (long)_task[4] << 8 | (long)_task[5] << 16 | (long)(_task[6] & 15) << 24;
            _remaining = _task[2] == 0 ? 256 : _task[2];
            BeginRead();
        }
        else if (value is 0x30 or 0x31 && (_task[6] & 0x40) != 0 && _disk is IWritableBlockDevice)
        {
            _identify = false; _writing = true;
            _lba = _task[3] | (long)_task[4] << 8 | (long)_task[5] << 16 | (long)(_task[6] & 15) << 24;
            _remaining = _task[2] == 0 ? 256 : _task[2]; _offset = 0;
            if (_lba < 0 || _lba + _remaining > _disk.SectorCount) { Abort(0x10); return; }
            Status = 0x80;
            _event = _scheduler.ScheduleAfter(5_000, () => { _event = 0; Status = 0x58; });
        }
        else if (value == 0xe7)
        {
            try { if (_disk is IWritableBlockDevice writable) writable.Flush(); Status = 0x50; _pendingInterrupt = true; }
            catch (IOException) { Abort(0x40); }
        }
        else Abort(4);
    }

    private bool SelectedExists => _disk is not null && (_task[6] & 0x10) == 0;
    private void Abort(byte error) { _writing = false; _task[1] = error; Status = 0x51; _pendingInterrupt = true; }
    private void BeginRead()
    {
        Status = 0x80; _offset = 0;
        _event = _scheduler.ScheduleAfter(5_000, () =>
        {
            _event = 0;
            if (_disk is null || _lba < 0 || _lba >= _disk.SectorCount) { Abort(0x10); return; }
            try { _disk.ReadSector(_lba, _data); }
            catch (IOException) { Abort(0x40); return; }
            Status = 0x58; _pendingInterrupt = true;
        });
    }
    private void UpdateTaskAddress()
    {
        _task[2] = (byte)_remaining; _task[3] = (byte)_lba; _task[4] = (byte)(_lba >> 8);
        _task[5] = (byte)(_lba >> 16); _task[6] = (byte)((uint)(_task[6] & 0xf0) | ((uint)(_lba >> 24) & 15));
    }
    private void Identify()
    {
        Array.Clear(_data);
        void Word(int n, ushort v) { _data[n * 2] = (byte)v; _data[n * 2 + 1] = (byte)(v >> 8); }
        Word(0, 0x0040); Word(1, 1024); Word(3, 16); Word(6, 63); Word(49, 0x0200);
        uint sectors = (uint)Math.Min(_disk?.SectorCount ?? 0, 0x0fffffff);
        Word(60, (ushort)sectors); Word(61, (ushort)(sectors >> 16));
        string model = (_disk is IWritableBlockDevice ? "DOSEMULATOR OVERLAY DEVELOPMENT DISK" : "DOSEMULATOR READ ONLY DEVELOPMENT DISK").PadRight(40);
        for (int i = 0; i < 40; i += 2) { _data[54 + i] = (byte)model[i + 1]; _data[55 + i] = (byte)model[i]; }
    }
}
