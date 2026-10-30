// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Original RTL8029 PCI I/O controller around the NE2000 core, with a real relocatable 32-byte BAR.
/// This fixed no-boot-ROM board does not expose AS-only IDs, flow control or writable EEPROM programming.</summary>
public sealed partial class Rtl8029 : IPciConfigurationFunction, IIoPortWindowDecoder
{
    private readonly Ne2000 _nic;
    private readonly IoPortBus _ports;
    private readonly byte[] _config = new byte[256];
    private byte _eepromControl, _configuration2, _configuration3, _haltClock;
    private ushort _eepromShift, _eepromOutput;
    private int _eepromBits, _eepromOutputBits;
    private long _autoloadEvent;
    private ulong _autoloadDeadline;

    /// <summary>Binds one fixed core and port bus without creating a decoder mapping or enabling a backend.</summary>
    public Rtl8029(Ne2000 nic, IoPortBus ports)
    { ArgumentNullException.ThrowIfNull(nic); ArgumentNullException.ThrowIfNull(ports); _nic = nic; _ports = ports; Reset(); }
    /// <summary>Current complete 32-bit PCI I/O address, including high bits which cannot decode on this x86 bus.</summary>
    public uint IoBase => BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan(0x10, 4)) & 0xffffffe0;
    /// <summary>Actual PCI target I/O permission; remote-DMA state itself survives changes to IOSE.</summary>
    public bool IoSpaceEnabled => (_config[4] & 1) != 0;
    /// <summary>Actual masked source level retained independently from this wrapper's configured interrupt line byte.</summary>
    public bool InterruptAsserted => _nic.InterruptAsserted;
    /// <summary>Exact pending EEPROM auto-load token for scheduler state validation.</summary>
    public long AutoloadEventToken => _autoloadEvent;
    /// <summary>Exact future EEPROM auto-load deadline, or zero when no load is active.</summary>
    public ulong AutoloadDeadline => _autoloadDeadline;
    /// <summary>Checks immutable core/bus peers without probing configuration or invoking decoder side effects.</summary>
    public bool HasBindings(Ne2000 nic, IoPortBus ports) => ReferenceEquals(nic, _nic) && ReferenceEquals(ports, _ports);

    /// <summary>Restores real PCI/configuration defaults and cancels this wrapper's work, retaining the same NIC object.</summary>
    public void Reset()
    {
        if (_autoloadEvent != 0) _nic.Clock.Cancel(_autoloadEvent);
        _autoloadEvent = 0; _autoloadDeadline = 0;
        for (int i = 0; i < _config.Length; i++) _config[i] = ResetByte(i);
        _eepromControl = _configuration2 = _configuration3 = 0; _haltClock = 0x52;
        _eepromShift = _eepromOutput = 0; _eepromBits = _eepromOutputBits = 0;
        _nic.Reset();
    }
    /// <summary>Returns manufacturer-specified IDs/class/medium DEVSEL and a deliberately absent boot-ROM board.</summary>
    private static byte ResetByte(int offset) => offset switch
    { 0 or 0x2c => 0xec, 1 or 0x2d => 0x10, 2 or 0x2e => 0x29, 3 or 0x2f => 0x80,
        7 => 2, 0x0b => 2, 0x10 => 1, 0x3d => 1, _ => 0 };
    /// <summary>Validates an entire configuration transaction before a single lane can change.</summary>
    private static void ValidateTransaction(int offset, int width)
    { WidthMask(width); if (offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset)); }
    /// <summary>Rejects unsupported host bus widths before any selected target operation.</summary>
    private static uint WidthMask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
    /// <summary>Reads one complete little-endian PCI transaction, including the true BAR size probe.</summary>
    public uint ReadConfiguration(int offset, int width)
    { ValidateTransaction(offset, width); uint value = 0; for (int i = 0; i < width; i++) value |= (uint)_config[offset + i] << (8 * i); return value; }
    /// <summary>Commits real command/BAR/IRQ-line masks; no unimplemented master or boot-ROM capability can be enabled.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        ValidateTransaction(offset, width);
        for (int i = 0; i < width; i++)
        {
            int at = offset + i; byte incoming = (byte)(value >> (8 * i));
            byte mask = at switch { 4 => 3, 0x10 => 0xe0, 0x11 or 0x12 or 0x13 => 0xff, 0x3c => 0x0f, _ => 0 };
            _config[at] = (byte)((_config[at] & ~mask) | (incoming & mask));
        }
    }
    /// <summary>Advertises the programmed enabled window without calling another decoder or truncating a high BAR.</summary>
    public bool OverlapsWindow(ushort start, int length) => IoSpaceEnabled && IoBase <= 0xffe0 &&
        (uint)start < IoBase + 32 && IoBase < (uint)start + length;
    /// <summary>Decodes only a complete fixed- and dynamic-collision-free x86 window; high and probing BARs remain inert.</summary>
    public bool Decodes(ushort port, int width)
    {
        WidthMask(width); uint address = IoBase;
        return IoSpaceEnabled && address <= 0xffe0 && port >= address && (uint)port + width <= address + 32 &&
            _ports.IsFixedWindowAvailable((ushort)address, 32) && _ports.IsDynamicWindowAvailable((ushort)address, 32, this);
    }
    /// <summary>Reads real original Realtek page lanes or the unchanged NE2000 core at its current guest BAR.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = WidthMask(width); if (!Decodes(port, width)) return mask; int offset = port - (int)IoBase;
        if (_haltClock == 0x48 && offset != 9 && offset < 0x18) return mask;
        if (width == 1 && offset != 0 && offset < 0x10 && _nic.RegisterPage == 3) return ReadPageThree(offset);
        uint result = _nic.ReadOffset(offset, width, true);
        if (offset >= 0x18 && width == 1) ApplyExternalGate();
        return result;
    }
    /// <summary>Writes real original configuration lanes or one complete core transfer; BAR relocation never changes core identity.</summary>
    public void Write(ushort port, uint value, int width)
    {
        WidthMask(width); if (!Decodes(port, width)) return; int offset = port - (int)IoBase;
        if (_haltClock == 0x48 && offset != 9 && offset < 0x18) return;
        if (_haltClock == 0x48 && offset == 9 && width == 1) { WritePageThree(9, (byte)value); return; }
        if (width == 1 && offset != 0 && offset < 0x10 && _nic.RegisterPage == 3) WritePageThree(offset, (byte)value);
        else _nic.WriteOffset(offset, value, width, true);
        if (offset >= 0x18 && width == 1) ApplyExternalGate();
    }
    /// <summary>Returns current EEPROM pins and actual selected medium; AS-only register addresses are unimplemented open bus.</summary>
    private byte ReadPageThree(int offset) => offset switch
    {
        1 => (byte)((_eepromControl & 0xce) | (_eepromOutputBits == 17 ? 0 : _eepromOutputBits > 0 ? (_eepromOutput >> 15) & 1 : 1)),
        3 => (byte)(((_configuration2 & 0xc0) == 0xc0 || ((_configuration2 & 0xc0) == 0 && !_nic.CableConnected)) ? 4 : 0),
        5 => _configuration2, 6 => _configuration3, _ => 0xff
    };
    /// <summary>Implements gated original media/power controls and read-only 9346 serial access without claiming EEPROM writes.</summary>
    private void WritePageThree(int offset, byte value)
    {
        if (offset == 1) { WriteEepromControl(value); return; }
        if (offset == 9)
        {
            if ((_configuration3 & 2) != 0 && (value == 0x48 || value == 0x52)) _haltClock = value;
            return;
        }
        if ((_eepromControl & 0xc0) != 0xc0) return;
        if (offset == 5) _configuration2 = (byte)(value & 0xc0);
        if (offset == 6) { _configuration3 = (byte)(value & 6); if ((_configuration3 & 2) == 0) _haltClock = 0x52; }
        ApplyExternalGate();
    }
    /// <summary>Tracks real 93C46 read command bits on clock edges; writes and AS mode selection remain outside this board.</summary>
    private void WriteEepromControl(byte value)
    {
        if (_autoloadEvent != 0) return;
        byte before = _eepromControl; _eepromControl = (byte)(value & 0xce);
        if ((_eepromControl & 0xc0) == 0x40 && (before & 0xc0) != 0x40)
        {
            if (_autoloadEvent != 0) _nic.Clock.Cancel(_autoloadEvent);
            _autoloadDeadline = checked(_nic.Clock.Now + 2_000_000);
            _autoloadEvent = _nic.Clock.ScheduleAfter(2_000_000, CompleteAutoload, this, "autoload");
        }
        if ((_eepromControl & 8) == 0) { _eepromShift = 0; _eepromBits = _eepromOutputBits = 0; }
        else if ((_eepromControl & 0xc0) == 0x80 && (before & 4) == 0 && (_eepromControl & 4) != 0)
        {
            if (_eepromOutputBits > 0) { if (_eepromOutputBits != 17) _eepromOutput <<= 1; _eepromOutputBits--; }
            else
            {
                if (_eepromBits == 0 && (_eepromControl & 2) == 0) { ApplyExternalGate(); return; }
                _eepromShift = (ushort)((_eepromShift << 1) | ((_eepromControl >> 1) & 1)); _eepromBits++;
                if (_eepromBits == 9)
                {
                    if ((_eepromShift >> 6) == 6) { _eepromOutput = EepromWord(_eepromShift & 63); _eepromOutputBits = 17; }
                    _eepromBits = 0; _eepromShift = 0;
                }
            }
        }
        ApplyExternalGate();
    }
    /// <summary>Completes the documented nominal two-millisecond power-on data reload on the existing controller peer.</summary>
    private void CompleteAutoload()
    {
        _autoloadEvent = 0; _autoloadDeadline = 0; _eepromControl = _configuration2 = _configuration3 = 0;
        _haltClock = 0x52; _eepromShift = _eepromOutput = 0; _eepromBits = _eepromOutputBits = 0;
        _nic.Reset();
    }
    /// <summary>Returns the board's actual fixed EEPROM words: no ROM, immutable MAC, own zero product bytes and legacy mode.</summary>
    private ushort EepromWord(int address)
    {
        if (address is >= 1 and <= 3) { byte[] mac = _nic.MacAddress; int at = (address - 1) * 2; return (ushort)(mac[at] | mac[at + 1] << 8); }
        return 0;
    }
    /// <summary>Applies actual power-down or EEPROM-programming DMA gates while keeping cable input and PCI IOSE distinct.</summary>
    private void ApplyExternalGate() => _nic.SetExternalEnabled((_configuration3 & 2) == 0 && (_eepromControl & 0xc0) != 0x80 && _autoloadEvent == 0);
    /// <summary>Rejects impossible config masks, serial counters and event/deadline pairs without resetting the old machine.</summary>
    public void ValidateSnapshot()
    {
        if (_config.Length != 256 || (_configuration2 & 0x3f) != 0 || (_configuration3 & ~6) != 0 ||
            (_eepromControl & 0x31) != 0 || (_haltClock != 0x48 && _haltClock != 0x52) ||
            _eepromBits < 0 || _eepromBits > 8 || _eepromOutputBits < 0 || _eepromOutputBits > 17 ||
            _autoloadEvent < 0 || (_autoloadEvent == 0 ? _autoloadDeadline != 0 : _autoloadDeadline <= _nic.Clock.Now))
            throw new InvalidDataException("Invalid RTL8029 configuration or EEPROM state.");
        for (int i = 0; i < 256; i++)
        {
            byte mask = i switch { 4 => 3, 0x10 => 0xe0, 0x11 or 0x12 or 0x13 => 0xff, 0x3c => 0x0f, _ => 0 };
            if ((_config[i] & ~mask) != ResetByte(i)) throw new InvalidDataException("Invalid RTL8029 fixed configuration lane.");
        }
    }
}
