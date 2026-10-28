// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>One ATA PIO task file and sector engine; its channel owns selection and shared register writes.</summary>
internal sealed partial class AtaPioDrive : IIoPortDevice
{
    private readonly DeviceScheduler _scheduler;
    private readonly IReadOnlyBlockDevice? _disk;
    private readonly bool _dmaAvailable;
    private AtaDmaDirection _dmaDirection;
    private bool _dmaSelected;
    private readonly byte[] _data = new byte[512];
    private readonly byte[] _task = new byte[8];
    private int _offset, _remaining;
    private long _lba;
    private bool _identify, _resetActive, _writing;
    private long _event;
    private byte _control;
    private bool _pendingInterrupt;
    public event Action? InterruptChanged;
    /// <summary>Notifies the channel only after complete DMA readiness, progress, terminal or reset state has been published.</summary>
    public event Action? DmaStateChanged;
    /// <summary>Constructor-fixed availability; this flag does not advertise additional ATA timing modes.</summary>
    public bool DmaAvailable => _dmaAvailable;
    /// <summary>Active command direction, retained while the device loads or commits a complete sector.</summary>
    public AtaDmaDirection DmaDirection => _dmaDirection;
    /// <summary>Whether the active DMA command can exchange one or more complete words now.</summary>
    public bool DmaReady => _dmaDirection != AtaDmaDirection.None && !_resetActive && (Status & 0x88) == 8;
    /// <summary>Current DMA sector-buffer offset; no PIO data-port access may advance it.</summary>
    public int DmaOffset => _offset;
    /// <summary>Remaining command sectors, including a write whose full data is waiting for its media commit.</summary>
    public int DmaRemainingSectors => _remaining;
    /// <summary>Current command LBA, independent of later channel drive selection.</summary>
    public long DmaLba => _lba;
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
        : this(scheduler, disk, false) { }
    /// <summary>Enables only the explicit board's functional DMA handshake, retaining the original constructor ABI.</summary>
    public AtaPioDrive(DeviceScheduler scheduler, IReadOnlyBlockDevice? disk, bool dmaAvailable)
    {
        _scheduler = scheduler; _disk = disk; _dmaAvailable = dmaAvailable; Reset();
    }
    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset()
    {
        if (_event != 0) _scheduler.Cancel(_event);
        _event = 0; Array.Clear(_task); Array.Clear(_data);
        _task[1] = 1; _task[2] = 1; _task[3] = 1; _task[6] = 0xa0;
        _offset = _remaining = 0; PendingInterrupt = false; _identify = _writing = false;
        _dmaDirection = AtaDmaDirection.None; _dmaSelected = false;
        Status = _disk is null ? (byte)0 : (byte)0x50;
        if (_dmaAvailable) DmaStateChanged?.Invoke();
    }

    /// <summary>Consumes a selected ATA data word or task-file register; regular status acknowledges this drive's IRQ.</summary>
    public uint Read(ushort port, int width)
    {
        if (port == 0x3f6) return SelectedExists ? Status : 0u;
        if (port == 0x1f0)
        {
            if (width != 2 || !SelectedExists || _dmaDirection != AtaDmaDirection.None || _writing || (Status & 8) == 0) return width == 1 ? 0xffu : 0xffffu;
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
            if (_dmaDirection != AtaDmaDirection.None || !_writing || !SelectedExists || (Status & 8) == 0) return;
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
        byte features = _task[1];
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
        else if (_dmaAvailable && (value is 0xc8 or 0xc9 or 0xca or 0xcb) && (_task[6] & 0x40) != 0)
        {
            bool write = value is 0xca or 0xcb;
            if (write && _disk is not IWritableBlockDevice) { Abort(4); return; }
            _identify = false; _writing = write; _offset = 0;
            _lba = _task[3] | (long)_task[4] << 8 | (long)_task[5] << 16 | (long)(_task[6] & 15) << 24;
            _remaining = _task[2] == 0 ? 256 : _task[2];
            if (write && (_lba < 0 || _lba + _remaining > _disk!.SectorCount)) { Abort(0x10); return; }
            _dmaDirection = write ? AtaDmaDirection.MemoryToDevice : AtaDmaDirection.DeviceToMemory;
            if (write) BeginDmaWrite(); else BeginDmaRead();
        }
        else if (_dmaAvailable && value == 0xef && features == 3 && (_task[2] is 0 or 8 or 0x20))
        {
            // Own narrow transfer-selection policy: MDMA0 supported, selected only after EF/03/20h.
            // PIO default/PIO0 clear that selected bit; command admission does not require prior SET FEATURES.
            _dmaSelected = _task[2] == 0x20; Status = 0x50; PendingInterrupt = true;
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
    private void Abort(byte error)
    {
        bool dma = _dmaDirection != AtaDmaDirection.None;
        _dmaDirection = AtaDmaDirection.None;
        _writing = false; _task[1] = error; Status = 0x51; PendingInterrupt = true;
        if (dma) DmaStateChanged?.Invoke();
    }
    /// <summary>Copies only available DMA words into an already checked PCI target; completion follows actual visible bytes.</summary>
    public int ReadDma(Span<byte> destination)
    {
        if (_dmaDirection != AtaDmaDirection.DeviceToMemory || !DmaReady) return 0;
        int count = Math.Min(destination.Length & ~1, 512 - _offset);
        if (count == 0) return 0;
        _data.AsSpan(_offset, count).CopyTo(destination); _offset += count;
        if (_offset == 512)
        {
            ReadSectorCount++; _lba++; _remaining--; UpdateTaskAddress();
            if (_remaining > 0) BeginDmaRead(); else CompleteDma();
        }
        return count;
    }
    /// <summary>Stages only complete DMA words; no incomplete sector can reach the media or advance its task address.</summary>
    public int WriteDma(ReadOnlySpan<byte> source)
    {
        if (_dmaDirection != AtaDmaDirection.MemoryToDevice || !DmaReady) return 0;
        int count = Math.Min(source.Length & ~1, 512 - _offset);
        if (count == 0) return 0;
        source[..count].CopyTo(_data.AsSpan(_offset)); _offset += count;
        if (_offset == 512)
        {
            Status = 0x80;
            _event = _scheduler.ScheduleAfter(5_000, () =>
            {
                _event = 0;
                try { ((IWritableBlockDevice)_disk!).WriteSector(_lba, _data); }
                catch (IOException) { Abort(0x40); return; }
                WrittenSectorCount++; _lba++; _remaining--; UpdateTaskAddress(); _offset = 0;
                if (_remaining > 0) { Status = 0x58; DmaStateChanged?.Invoke(); }
                else CompleteDma();
            }, this, "dma-write-commit");
            DmaStateChanged?.Invoke();
        }
        return count;
    }
    /// <summary>Publishes the one real terminal device interrupt, independently from bus-master EOT, Stop or status latches.</summary>
    private void CompleteDma()
    {
        _dmaDirection = AtaDmaDirection.None; _writing = false; Status = 0x50;
        PendingInterrupt = true; DmaStateChanged?.Invoke();
    }
    /// <summary>Loads one DMA read sector at its own nominal deadline without a PIO data-ready interrupt.</summary>
    private void BeginDmaRead()
    {
        Status = 0x80; _offset = 0;
        _event = _scheduler.ScheduleAfter(5_000, () =>
        {
            _event = 0;
            if (_disk is null || _lba < 0 || _lba >= _disk.SectorCount) { Abort(0x10); return; }
            try { _disk.ReadSector(_lba, _data); }
            catch (IOException) { Abort(0x40); return; }
            Status = 0x58; DmaStateChanged?.Invoke();
        }, this, "dma-read-ready");
        DmaStateChanged?.Invoke();
    }
    /// <summary>Requests the first DMA write sector after its nominal preparation deadline, without a device interrupt.</summary>
    private void BeginDmaWrite()
    {
        Status = 0x80;
        _event = _scheduler.ScheduleAfter(5_000, () =>
        {
            _event = 0; Status = 0x58; DmaStateChanged?.Invoke();
        }, this, "dma-write-ready");
        DmaStateChanged?.Invoke();
    }
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
        Word(0, 0x0040); Word(1, 1024); Word(3, 16); Word(6, 63); Word(49, _dmaAvailable ? (ushort)0x0300 : (ushort)0x0200);
        if (_dmaAvailable) Word(63, _dmaSelected ? (ushort)0x0101 : (ushort)0x0001);
        uint sectors = (uint)Math.Min(_disk?.SectorCount ?? 0, 0x10000000);
        Word(60, (ushort)sectors); Word(61, (ushort)(sectors >> 16));
        string model = (_disk is IWritableBlockDevice ? "DOSEMULATOR OVERLAY DEVELOPMENT DISK" : "DOSEMULATOR READ ONLY DEVELOPMENT DISK").PadRight(40);
        for (int i = 0; i < 40; i += 2) { _data[54 + i] = (byte)model[i + 1]; _data[55 + i] = (byte)model[i]; }
    }
}
