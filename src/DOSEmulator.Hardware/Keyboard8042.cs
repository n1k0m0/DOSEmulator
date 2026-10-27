// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Development KBC byte FIFO with tagged keyboard/Aux sources and translated set-1 injection.</summary>
public sealed partial class Keyboard8042 : IIoPortDevice
{
    private readonly MemoryBus memory;
    private readonly Action<bool> irq;
    private readonly Action<bool>? _auxIrq;
    private readonly Ps2Mouse? _mouse;
    private readonly Queue<(byte Value, bool Aux)> _output = new();
    private byte _commandByte = 0x45, _outputPort = 3;
    private byte _expectedController, _expectedKeyboard;
    private bool _enabled = true, _scanning = true;
    /// <summary>Initializes Keyboard8042 and connects the supplied dependencies before the component is used.</summary>
    public Keyboard8042(MemoryBus memory, Action<bool> irq, Ps2Mouse? mouse = null, Action<bool>? auxiliaryIrq = null)
    {
        this.memory = memory; this.irq = irq; _mouse = mouse; _auxIrq = auxiliaryIrq;
        if (mouse is not null) { _commandByte |= 0x20; mouse.ConnectOutput(EnqueueAux); }
    }
    private bool _resetPulse;
    /// <summary>Current sustained keyboard-controller CPU reset output level.</summary>
    public bool ResetLineAsserted => (_outputPort & 1) == 0;
    /// <summary>Whether an emulated reset request is pending consumption by the platform owner.</summary>
    public bool ResetRequested => _resetPulse || ResetLineAsserted;
    /// <summary>Returns and clears a pending emulated reset pulse, leaving the sustained reset line separate.</summary>
    public bool ConsumeResetPulse()
    {
        bool pulse = _resetPulse; _resetPulse = false; return pulse;
    }
    /// <summary>Most recent diagnostic record for an unimplemented controller command.</summary>
    public bool UnsupportedCommand { get; private set; }
    /// <summary>Number of bytes pending in the controller's bounded output path.</summary>
    public int OutputByteCount => _output.Count;
    /// <summary>Queues guest-visible translated set-1 keyboard bytes through the controller's normal output path.</summary>
    public void InjectTranslatedSet1(byte scanCode)
    {
        TryInjectTranslatedSet1(scanCode);
    }
    /// <summary>Accepts a guest scan byte only when the controller's bounded input/output conditions permit it.</summary>
    public bool TryInjectTranslatedSet1(byte scanCode)
    {
        if (!_enabled || !_scanning || (_commandByte & 0x40) == 0 || _output.Count >= 64) return false;
        Enqueue(scanCode); return true;
    }
    /// <summary>Atomically accepts a complete translated scan packet, preserving prefixes/chords on rejection.</summary>
    public bool TryInjectTranslatedSet1(ReadOnlySpan<byte> scanCodes)
    {
        if (scanCodes.IsEmpty || !_enabled || !_scanning || (_commandByte & 0x40) == 0 || scanCodes.Length > 64 - _output.Count) return false;
        foreach (byte scanCode in scanCodes) _output.Enqueue((scanCode, false));
        UpdateIrq(); return true;
    }
    /// <summary>Adds a bounded controller response and updates its eligible interrupt line.</summary>
    private void Enqueue(byte value)
    {
        if (_output.Count < 64) _output.Enqueue((value, false));
        UpdateIrq();
    }
    /// <summary>Queues auxiliary-device output and its source tag for the shared 8042 output buffer.</summary>
    private bool EnqueueAux(byte[] values)
    {
        if (_output.Count + values.Length > 64) return false;
        foreach (byte value in values) _output.Enqueue((value, true));
        UpdateIrq(); return true;
    }
    /// <summary>Recomputes the component's externally observable interrupt level after state changes.</summary>
    private void UpdateIrq()
    {
        bool full = _output.TryPeek(out var head);
        irq(full && !head.Aux && (_commandByte & 1) != 0 && _enabled);
        _auxIrq?.Invoke(full && head.Aux && (_commandByte & 2) != 0);
        _mouse?.SetClockEnabled((_commandByte & 0x20) == 0);
    }
    /// <summary>Consumes controller output or reads status, preserving keyboard/auxiliary source and IRQ distinctions.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1) return 0xff;
        if (port == 0x64) return (uint)((_commandByte & 4) | (_output.TryPeek(out var head) ? 1 | (head.Aux ? 0x20 : 0) : 0));
        if (port != 0x60 || !_output.TryDequeue(out var value)) return 0xff;
        irq(false); _auxIrq?.Invoke(false); UpdateIrq(); return value.Value;
    }
    /// <summary>Processes host-to-controller/device commands, output-port state and bounded keyboard responses.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        byte b = (byte)value;
        if (port == 0x64)
        {
            switch (b)
            {
                case 0x20: Enqueue(_commandByte); break;
                case 0x60: case 0xd1: case 0xd4: case 0xd3: _expectedController = b; break;
                case 0xaa: Enqueue(0x55); break;
                case 0xab: Enqueue(0); break;
                case 0xad: _enabled = false; _commandByte |= 0x10; UpdateIrq(); break;
                case 0xae: _enabled = true; _commandByte &= 0xef; UpdateIrq(); break;
                case 0xa7: _commandByte |= 0x20; UpdateIrq(); break;
                case 0xa8: _commandByte &= 0xdf; UpdateIrq(); break;
                case 0xa9: Enqueue(_mouse is null ? (byte)1 : (byte)0); break;
                case 0xd0: Enqueue(_outputPort); break;
                case >= 0xf0: if ((b & 1) == 0) _resetPulse = true; break;
                default: UnsupportedCommand = true; break;
            }
            return;
        }
        if (port != 0x60) return;
        if (_expectedController != 0)
        {
            if (_expectedController == 0x60) { _commandByte = b; _enabled = (b & 0x10) == 0; UpdateIrq(); }
            else if (_expectedController == 0xd4) _mouse?.Write(b);
            else if (_expectedController == 0xd3) EnqueueAux([b]);
            else { _outputPort = b; memory.A20Enabled = (b & 2) != 0; }
            _expectedController = 0; return;
        }
        if (_expectedKeyboard != 0)
        {
            if (b == 0xff) { _expectedKeyboard = 0; _scanning = true; Enqueue(0xfa); Enqueue(0xaa); return; }
            if (_expectedKeyboard == 0xf0 && b == 0) { Enqueue(0xfa); Enqueue(2); }
            else Enqueue(0xfa);
            _expectedKeyboard = 0; return;
        }
        switch (b)
        {
            case 0xff: Enqueue(0xfa); Enqueue(0xaa); _scanning = true; break;
            case 0xf4: case 0xf6: _scanning = true; Enqueue(0xfa); break;
            case 0xf5: _scanning = false; Enqueue(0xfa); break;
            case 0xed: case 0xf0: case 0xf3: _expectedKeyboard = b; Enqueue(0xfa); break;
            case 0xee: Enqueue(0xee); break;
            default: UnsupportedCommand = true; Enqueue(0xfe); break;
        }
    }
}
