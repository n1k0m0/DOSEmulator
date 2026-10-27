// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>One ATA PIO task file and sector engine; its channel owns selection and shared register writes.</summary>
internal sealed partial class AtaPioDrive : IIoPortDevice
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
    public event Action? InterruptChanged;
    /// <summary>Internal ATA interrupt latch, retained independently of the channel's nIEN output gating.</summary>
    private bool PendingInterrupt
    {
        get => _pendingInterrupt;
        set { if (_pendingInterrupt == value) return; _pendingInterrupt = value; InterruptChanged?.Invoke(); }
    }
    /// <summary>Current device status byte; destructive acknowledgement occurs only through its documented port read.</summary>
    public byte Status { get; private set; }
    /// <summary>Externally observable interrupt line after the component's enable/mask policy.</summary>
    public bool InterruptAsserted => PendingInterrupt && (_control & 2) == 0;
    /// <summary>Diagnostic count of read sector count.</summary>
    public long ReadSectorCount { get; private set; }
    /// <summary>Diagnostic count of written sector count.</summary>
    public long WrittenSectorCount { get; private set; }

    /// <summary>Initializes AtaPioDrive and connects the supplied dependencies before the component is used.</summary>
    public AtaPioDrive(DeviceScheduler scheduler, IReadOnlyBlockDevice? disk)
    {
        _scheduler = scheduler; _disk = disk; Reset();
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset()
    {
        if (_event != 0) _scheduler.Cancel(_event);
        _event = 0; Array.Clear(_task); Array.Clear(_data);
        _task[1] = 1; _task[2] = 1; _task[3] = 1; _task[6] = 0xa0;
        _offset = _remaining = 0; PendingInterrupt = false; _identify = _writing = false;
        Status = _disk is null ? (byte)0 : (byte)0x50;
    }

    /// <summary>Consumes a selected ATA data word or task-file register; regular status acknowledges this drive's IRQ.</summary>
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
        if (index == 7) { PendingInterrupt = false; return SelectedExists ? Status : 0u; }
        return index is >= 1 and <= 6 ? _task[index] : 0xffu;
    }

    /// <summary>Accepts task/data/control writes and schedules sector completion only after a valid command/data phase.</summary>
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
                    Status = _remaining > 0 ? (byte)0x58 : (byte)0x50; PendingInterrupt = true;
                }, this, "event0");
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
            _resetActive = reset; InterruptChanged?.Invoke();
            return;
        }
        int index = port - 0x1f0;
        if (index is >= 1 and <= 6)
        {
            if (index == 6 || (Status & 0x80) == 0) _task[index] = (byte)value;
            return;
        }
        if (index != 7 || _resetActive || !SelectedExists || (Status & 0x88) != 0) return;
        PendingInterrupt = false; _task[1] = 0;
        if (value == 0xec)
        {
            _identify = true; _writing = false; _offset = 0; Status = 0x80;
            _event = _scheduler.ScheduleAfter(5_000, () => { _event = 0; Identify(); Status = 0x58; PendingInterrupt = true; }, this, "event1");
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
            _event = _scheduler.ScheduleAfter(5_000, () => { _event = 0; Status = 0x58; }, this, "event2");
        }
        else if (value == 0xe7)
        {
            try { if (_disk is IWritableBlockDevice writable) writable.Flush(); Status = 0x50; PendingInterrupt = true; }
            catch (IOException) { Abort(0x40); }
        }
        else Abort(4);
    }

    /// <summary>Whether this task engine has a real attached medium rather than an absent-device status.</summary>
    private bool SelectedExists => _disk is not null;
    /// <summary>Terminates the current device operation and publishes its error/result interrupt state.</summary>
    private void Abort(byte error) { _writing = false; _task[1] = error; Status = 0x51; PendingInterrupt = true; }
    /// <summary>Schedules an ATA sector load and publishes DRQ/IRQ only when the device deadline completes.</summary>
    private void BeginRead()
    {
        Status = 0x80; _offset = 0;
        _event = _scheduler.ScheduleAfter(5_000, () =>
        {
            _event = 0;
            if (_disk is null || _lba < 0 || _lba >= _disk.SectorCount) { Abort(0x10); return; }
            try { _disk.ReadSector(_lba, _data); }
            catch (IOException) { Abort(0x40); return; }
            Status = 0x58; PendingInterrupt = true;
        }, this, "event3");
    }
    /// <summary>Advances the ATA task file's sector/count fields after an actual completed transfer.</summary>
    private void UpdateTaskAddress()
    {
        _task[2] = (byte)_remaining; _task[3] = (byte)_lba; _task[4] = (byte)(_lba >> 8);
        _task[5] = (byte)(_lba >> 16); _task[6] = (byte)((uint)(_task[6] & 0xf0) | ((uint)(_lba >> 24) & 15));
    }
    /// <summary>Builds the selected ATA device's IDENTIFY words, including LBA28 capacity and media mode.</summary>
    private void Identify()
    {
        Array.Clear(_data);
        // Writes one explicitly numbered little-endian IDENTIFY word into the private response sector.
        void Word(int n, ushort v) { _data[n * 2] = (byte)v; _data[n * 2 + 1] = (byte)(v >> 8); }
        Word(0, 0x0040); Word(1, 1024); Word(3, 16); Word(6, 63); Word(49, 0x0200);
        uint sectors = (uint)Math.Min(_disk?.SectorCount ?? 0, 0x10000000);
        Word(60, (ushort)sectors); Word(61, (ushort)(sectors >> 16));
        string model = (_disk is IWritableBlockDevice ? "DOSEMULATOR OVERLAY DEVELOPMENT DISK" : "DOSEMULATOR READ ONLY DEVELOPMENT DISK").PadRight(40);
        for (int i = 0; i < 40; i += 2) { _data[54 + i] = (byte)model[i + 1]; _data[55 + i] = (byte)model[i]; }
    }
}
