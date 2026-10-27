// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>An ISA IDE channel with independent master/slave task files and a combined IRQ line.</summary>
public sealed partial class AtaPioController : IIoPortDevice
{
    private readonly AtaPioDrive[] _drives;
    private readonly ushort _basePort, _controlPort;
    private int _selected;
    private bool _interrupt;
    public event Action<bool>? InterruptChanged;
    /// <summary>Current device status byte; destructive acknowledgement occurs only through its documented port read.</summary>
    public byte Status => _drives[_selected].Status;
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
    {
        if ((basePort, controlPort) is not ((0x1f0, 0x3f6) or (0x170, 0x376))) throw new ArgumentException("Unsupported ISA IDE channel.");
        _basePort = basePort; _controlPort = controlPort;
        _drives = [new(scheduler, master), new(scheduler, slave)];
        foreach (var drive in _drives) drive.InterruptChanged += UpdateInterrupt;
    }

    /// <summary>Resets task files and scheduled transfers while leaving attached media intact.</summary>
    public void Reset()
    {
        foreach (var drive in _drives) drive.Reset();
        _selected = 0; UpdateInterrupt();
    }

    /// <summary>Reads the selected device; regular status acknowledges its IRQ, alternate status does not.</summary>
    public uint Read(ushort port, int width)
    {
        ushort normalized = Normalize(port);
        return normalized == 0 ? (width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue) : _drives[_selected].Read(normalized, width);
    }

    /// <summary>Broadcasts task/control writes and routes commands/data only to the selected device.</summary>
    public void Write(ushort port, uint value, int width)
    {
        ushort normalized = Normalize(port);
        if (normalized == 0) return;
        if (width == 1 && normalized == 0x1f6) _selected = ((int)value >> 4) & 1;
        if (width == 1 && (normalized == 0x3f6 || normalized is >= 0x1f1 and <= 0x1f6))
            foreach (var drive in _drives) drive.Write(normalized, value, width);
        else _drives[_selected].Write(normalized, value, width);
        UpdateInterrupt();
    }

    /// <summary>Maps physical channel ports onto the shared task-engine implementation.</summary>
    private ushort Normalize(ushort port) => port == _controlPort ? (ushort)0x3f6 :
        port >= _basePort && port < _basePort + 8 ? (ushort)(port - _basePort + 0x1f0) : (ushort)0;

    /// <summary>Publishes only IRQ edges, including asynchronous sector completion and nIEN changes.</summary>
    private void UpdateInterrupt()
    {
        bool high = _drives[0].InterruptAsserted || _drives[1].InterruptAsserted;
        if (_interrupt == high) return;
        _interrupt = high; InterruptChanged?.Invoke(high);
    }
}