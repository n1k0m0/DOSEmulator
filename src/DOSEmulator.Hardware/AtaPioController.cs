// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Functional DMA command direction, separate from the bus-master command register's read/write wording.</summary>
public enum AtaDmaDirection { None, DeviceToMemory, MemoryToDevice }

/// <summary>An ISA IDE channel with independent master/slave task files and an optional explicitly bound DMA handshake.</summary>
public sealed partial class AtaPioController : IIoPortDevice
{
    private readonly AtaPioDrive[] _drives;
    private readonly ushort _basePort, _controlPort;
    private readonly bool _dmaAvailable;
    private readonly AtapiCdRom? _atapi;
    private readonly int _packetIndex;
    private readonly bool _shadowAtapiSlave;
    private int _dmaDriveIndex = -1;
    private int _selected;
    private bool _interrupt;
    public event Action<bool>? InterruptChanged;
    /// <summary>Signals a complete DMA readiness, terminal or reset transition to the fixed bus-master peer.</summary>
    public event Action? DmaStateChanged;
    /// <summary>Whether this constructor explicitly enabled the DMA handshake; old channels remain PIO-only.</summary>
    public bool DmaAvailable => _dmaAvailable;
    /// <summary>Currently selected task-file view, separate from the DMA command's latched drive.</summary>
    public int SelectedDriveIndex => _selected;
    /// <summary>Active DMA drive, latched at command admission and independent from the selected task-file view.</summary>
    public int DmaDriveIndex => _dmaDriveIndex;
    /// <summary>Active DMA direction, including device-ready and media-commit wait phases.</summary>
    public AtaDmaDirection DmaDirection => _dmaDriveIndex < 0 ? AtaDmaDirection.None : _drives[_dmaDriveIndex].DmaDirection;
    /// <summary>Whether the latched DMA drive can exchange complete words at this owner boundary.</summary>
    public bool DmaReady => _dmaDriveIndex >= 0 && _drives[_dmaDriveIndex].DmaReady;
    /// <summary>Status of the latched DMA drive; zero when no DMA command is active, independent of task-file selection.</summary>
    public byte DmaStatus => _dmaDriveIndex < 0 ? (byte)0 : _drives[_dmaDriveIndex].Status;
    /// <summary>Current offset in the latched DMA sector; zero when no DMA command is active.</summary>
    public int DmaOffset => _dmaDriveIndex < 0 ? 0 : _drives[_dmaDriveIndex].DmaOffset;
    /// <summary>Remaining DMA command sectors, including an uncommitted complete write sector.</summary>
    public int DmaRemainingSectors => _dmaDriveIndex < 0 ? 0 : _drives[_dmaDriveIndex].DmaRemainingSectors;
    /// <summary>Current latched DMA command LBA; zero when no DMA command is active.</summary>
    public long DmaLba => _dmaDriveIndex < 0 ? 0 : _drives[_dmaDriveIndex].DmaLba;
    /// <summary>Transfers actual available device words only into the caller's already checked PCI write target.</summary>
    public int ReadDma(Span<byte> destination) => _dmaDriveIndex < 0 ? 0 : _drives[_dmaDriveIndex].ReadDma(destination);
    /// <summary>Copies actual available words from the caller's already checked PCI read source into device staging.</summary>
    public int WriteDma(ReadOnlySpan<byte> source) => _dmaDriveIndex < 0 ? 0 : _drives[_dmaDriveIndex].WriteDma(source);
    /// <summary>Current device status byte; destructive acknowledgement occurs only through its documented port read.</summary>
    public byte Status => _selected == _packetIndex ? _atapi!.Status : _drives[_selected].Status;
    /// <summary>Externally observable interrupt line after the component's enable/mask policy.</summary>
    public bool InterruptAsserted => _interrupt;
    /// <summary>Diagnostic count of read sector count.</summary>
    public long ReadSectorCount => _drives[0].ReadSectorCount + _drives[1].ReadSectorCount;
    /// <summary>Diagnostic count of written sector count.</summary>
    public long WrittenSectorCount => _drives[0].WrittenSectorCount + _drives[1].WrittenSectorCount;

    /// <summary>Retains the original primary-master API for existing callers and reference benchmarks.</summary>
    public AtaPioController(DeviceScheduler scheduler, IReadOnlyBlockDevice? disk)
        : this(scheduler, disk, null, 0x1f0, 0x3f6) { }

    /// <summary>Wires both task engines to a primary or secondary ISA IDE register window.</summary>
    public AtaPioController(DeviceScheduler scheduler, IReadOnlyBlockDevice? master, IReadOnlyBlockDevice? slave, ushort basePort, ushort controlPort)
        : this(scheduler, master, slave, basePort, controlPort, false) { }
    /// <summary>Enables DMA only for a board whose factory also supplies a fixed bus-master peer.</summary>
    public AtaPioController(DeviceScheduler scheduler, IReadOnlyBlockDevice? master, IReadOnlyBlockDevice? slave, ushort basePort, ushort controlPort, bool dmaAvailable)
        : this(scheduler, master, slave, basePort, controlPort, dmaAvailable, null, -1) { }

    /// <summary>Adds one explicitly fixed ATAPI peer without converting cooked optical blocks into ATA sectors.</summary>
    public AtaPioController(DeviceScheduler scheduler, IReadOnlyBlockDevice? master, IReadOnlyBlockDevice? slave, ushort basePort, ushort controlPort,
        bool dmaAvailable, AtapiCdRom? packet, int packetIndex)
    {
        if ((basePort, controlPort) is not ((0x1f0, 0x3f6) or (0x170, 0x376))) throw new ArgumentException("Unsupported ISA IDE channel.");
        if (packet is null ? packetIndex != -1 : packetIndex is not (0 or 1) || packet.DeviceNumber != packetIndex ||
            (packetIndex == 0 ? master : slave) is not null)
            throw new ArgumentException("The fixed ATAPI peer must own one otherwise empty physical device position.");
        _basePort = basePort; _controlPort = controlPort; _dmaAvailable = dmaAvailable;
        _atapi = packet; _packetIndex = packetIndex; _shadowAtapiSlave = packet is not null && packetIndex == 0 && slave is null;
        if (packet is not null) { packet.Select(packetIndex == 0); packet.InterruptChanged += UpdateInterrupt; }
        _drives = [new(scheduler, master, dmaAvailable), new(scheduler, slave, dmaAvailable)];
        foreach (var drive in _drives) drive.InterruptChanged += UpdateInterrupt;
        for (int i = 0; i < _drives.Length; i++)
        {
            int index = i; _drives[index].DmaStateChanged += () => UpdateDma(index);
        }
    }

    /// <summary>Resets task files and scheduled transfers while leaving attached media intact.</summary>
    public void Reset()
    {
        foreach (var drive in _drives) drive.Reset();
        if (_atapi is not null) { _atapi.ApplyPiixHardReset(); _atapi.ReleasePiixHardReset(); }
        _selected = 0; _atapi?.Select(_packetIndex == 0); UpdateInterrupt();
    }

    /// <summary>Checks the sole constructor-bound packet peer and the selected-source gate, including earlier no-packet channels.</summary>
    public bool HasPacketBinding(AtapiCdRom? packet, int packetIndex) => ReferenceEquals(_atapi, packet) && _packetIndex == packetIndex &&
        (_atapi is null || _atapi.Selected == (_selected == _packetIndex));

    /// <summary>Reads the selected device; regular status acknowledges its IRQ, alternate status does not.</summary>
    public uint Read(ushort port, int width)
    {
        ushort normalized = Normalize(port);
        if (normalized == 0) return width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
        if (_atapi is not null)
        {
            if (_selected == _packetIndex) return _atapi.Read(normalized, width);
            if (_shadowAtapiSlave && _selected == 1) return _atapi.ReadShadow(normalized, width);
        }
        return _drives[_selected].Read(normalized, width);
    }

    /// <summary>Broadcasts task/control writes and routes commands/data only to the selected device.</summary>
    public void Write(ushort port, uint value, int width)
    {
        ushort normalized = Normalize(port);
        if (normalized == 0) return;
        if (width == 1 && normalized == 0x1f6)
        { _selected = ((int)value >> 4) & 1; _atapi?.Select(_selected == _packetIndex); }
        if (width == 1 && (normalized == 0x3f6 || normalized is >= 0x1f1 and <= 0x1f6))
        {
            bool wasPacketSrst = _atapi?.SrstActive ?? false;
            foreach (var drive in _drives) drive.Write(normalized, value, width);
            _atapi?.Write(normalized, value, width);
            // Only a falling packet-channel SRST selects device0; a plain nIEN write retains selection.
            if (_atapi is not null && normalized == 0x3f6 && wasPacketSrst && (value & 4) == 0)
            { _selected = 0; _atapi.Select(_packetIndex == 0); }
        }
        else
        {
            // An active channel DMA handshake cannot be stolen by a second drive command.
            if (width == 1 && normalized == 0x1f7 && (value is 0xc8 or 0xc9 or 0xca or 0xcb) &&
                _dmaDriveIndex >= 0 && _dmaDriveIndex != _selected) return;
            if (_atapi is not null)
            {
                // A PACKET cannot steal a live disk-DMA channel, and non-overlapped CD work cannot be stolen by a second disk command.
                if (width == 1 && normalized == 0x1f7 && _selected == _packetIndex && _dmaDriveIndex >= 0 && value != 8) return;
                if (width == 1 && normalized == 0x1f7 && _selected != _packetIndex && _atapi.OwnsTaskFile) return;
                if (_selected == _packetIndex) { _atapi.Write(normalized, value, width); UpdateInterrupt(); return; }
                if (_shadowAtapiSlave && _selected == 1)
                { if (width == 1 && normalized == 0x1f7) _atapi.WriteShadowCommand(); return; }
            }
            _drives[_selected].Write(normalized, value, width);
        }
        UpdateInterrupt();
    }

    /// <summary>Retains only the drive that actually admitted a DMA command; no loading or rebinding publishes a synthetic transfer.</summary>
    private void UpdateDma(int drive)
    {
        if (_drives[drive].DmaDirection != AtaDmaDirection.None) _dmaDriveIndex = drive;
        else if (_dmaDriveIndex == drive) _dmaDriveIndex = -1;
        DmaStateChanged?.Invoke();
    }

    /// <summary>Maps physical channel ports onto the shared task-engine implementation.</summary>
    private ushort Normalize(ushort port) => port == _controlPort ? (ushort)0x3f6 :
        port >= _basePort && port < _basePort + 8 ? (ushort)(port - _basePort + 0x1f0) : (ushort)0;

    /// <summary>Publishes only IRQ edges, including asynchronous sector completion and nIEN changes.</summary>
    private void UpdateInterrupt()
    {
        bool high = _drives[0].InterruptAsserted || _drives[1].InterruptAsserted || (_atapi?.InterruptAsserted ?? false);
        if (_interrupt == high) return;
        _interrupt = high; InterruptChanged?.Invoke(high);
    }
}