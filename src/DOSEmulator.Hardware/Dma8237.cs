// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public enum DmaReadResult { Transferred, Blocked, InvalidMode }

/// <summary>AT DMA registers and byte/word grants. Devices schedule requests; electrical bus arbitration is not modeled.</summary>
public sealed partial class Dma8237(MemoryBus memory) : IIoPortDevice
{
    private sealed partial class Channel
    {
        public ushort Address, BaseAddress, Count, BaseCount;
        public byte Page, Mode;
        public bool Masked = true, Requested, TerminalCount;
    }
    private readonly Channel[] _channels = Enumerable.Range(0, 8).Select(_ => new Channel()).ToArray();
    private readonly bool[] _highByte = new bool[2];
    private readonly byte[] _status = new byte[2], _command = new byte[2];
    /// <summary>Resolves an AT DMA page-register port to its byte or word channel.</summary>
    private static int PageChannel(ushort port) => port switch { 0x87 => 0, 0x83 => 1, 0x81 => 2, 0x82 => 3, 0x8f => 4, 0x8b => 5, 0x89 => 6, 0x8a => 7, _ => -1 };
    /// <summary>Resolves a controller port into its channel/register selection without applying a transfer.</summary>
    private static (int Chip, int Register) Decode(ushort port) => port < 0x10 ? (0, port) : (1, (port - 0xc0) / 2);
    /// <summary>Reports the transfer's terminal-count condition without inventing an additional data grant.</summary>
    public bool IsTerminalCount(int channel)
    {
        if ((uint)channel >= 8) throw new ArgumentOutOfRangeException(nameof(channel));
        return _channels[channel].TerminalCount;
    }
    /// <summary>One device-paced 8-bit memory read, without interpreting verify/write mode as DAC data.</summary>
    public DmaReadResult TryReadByte(int channel, out byte value)
    {
        if ((uint)channel >= 4) throw new ArgumentOutOfRangeException(nameof(channel));
        value = 0;
        Channel c = _channels[channel];
        if (c.Masked || (_command[0] & 4) != 0) return DmaReadResult.Blocked;
        if (((c.Mode >> 2) & 3) != 2 || (c.Mode & 0xc0) == 0xc0) return DmaReadResult.InvalidMode;
        Span<byte> data = stackalloc byte[1];
        if (Transfer(channel, data, false) != 1) return DmaReadResult.Blocked;
        value = data[0]; return DmaReadResult.Transferred;
    }
    /// <summary>One device-paced byte into RAM. Verify/read/cascade never become a device write.</summary>
    public DmaReadResult TryWriteByte(int channel, byte value)
    {
        if ((uint)channel >= 4) throw new ArgumentOutOfRangeException(nameof(channel));
        Channel c = _channels[channel];
        if (c.Masked || (_command[0] & 4) != 0) return DmaReadResult.Blocked;
        if (((c.Mode >> 2) & 3) != 1 || (c.Mode & 0xc0) == 0xc0) return DmaReadResult.InvalidMode;
        Span<byte> data = stackalloc byte[1]; data[0] = value;
        return Transfer(channel, data, true) == 1 ? DmaReadResult.Transferred : DmaReadResult.Blocked;
    }
    /// <summary>One device-paced 16-bit memory read on AT channel 5, 6 or 7. Address/count advance in words.</summary>
    public DmaReadResult TryReadWord(int channel, out ushort value)
    {
        if (channel is < 5 or > 7) throw new ArgumentOutOfRangeException(nameof(channel));
        value = 0;
        Channel c = _channels[channel];
        if (c.Masked || (_command[1] & 4) != 0) return DmaReadResult.Blocked;
        if (((c.Mode >> 2) & 3) != 2 || (c.Mode & 0xc0) == 0xc0) return DmaReadResult.InvalidMode;
        Span<byte> data = stackalloc byte[2];
        if (Transfer(channel, data, false) != 2) return DmaReadResult.Blocked;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return DmaReadResult.Transferred;
    }
    /// <summary>One device-paced word into memory. Verify/read/cascade never consume device data.</summary>
    public DmaReadResult TryWriteWord(int channel, ushort value)
    {
        if (channel is < 5 or > 7) throw new ArgumentOutOfRangeException(nameof(channel));
        Channel c = _channels[channel];
        if (c.Masked || (_command[1] & 4) != 0) return DmaReadResult.Blocked;
        if (((c.Mode >> 2) & 3) != 1 || (c.Mode & 0xc0) == 0xc0) return DmaReadResult.InvalidMode;
        Span<byte> data = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        return Transfer(channel, data, true) == 2 ? DmaReadResult.Transferred : DmaReadResult.Blocked;
    }
    /// <summary>Reads the selected DMA register under the byte-pointer and terminal-count status rules.</summary>
    public uint Read(ushort port, int width)
    {
        if (width != 1) return 0xff;
        if (port >= 0xc0 && (port & 1) != 0) return 0xff;
        int page = PageChannel(port); if (page >= 0) return _channels[page].Page;
        var (chip, register) = Decode(port);
        if (register < 8)
        {
            Channel c = _channels[chip * 4 + register / 2]; ushort value = (register & 1) == 0 ? c.Address : c.Count;
            byte result = _highByte[chip] ? (byte)(value >> 8) : (byte)value; _highByte[chip] = !_highByte[chip]; return result;
        }
        if (register == 8)
        {
            byte result = _status[chip]; _status[chip] = 0;
            for (int i = 0; i < 4; i++) if (_channels[chip * 4 + i].Requested) result |= (byte)(0x10 << i);
            return result;
        }
        if (register == 15)
        {
            uint result = 0; for (int i = 0; i < 4; i++) if (_channels[chip * 4 + i].Masked) result |= 1u << i; return result;
        }
        return 0xff;
    }
    /// <summary>Programs the selected byte/word DMA controller register, mask, mode or page latch.</summary>
    public void Write(ushort port, uint value, int width)
    {
        if (width != 1) return;
        if (port >= 0xc0 && (port & 1) != 0) return;
        int page = PageChannel(port); if (page >= 0) { _channels[page].Page = (byte)value; return; }
        var (chip, register) = Decode(port);
        if (register < 8)
        {
            Channel c = _channels[chip * 4 + register / 2];
            ushort previous = (register & 1) == 0 ? c.Address : c.Count;
            ushort updated = _highByte[chip] ? (ushort)(((uint)previous & 255) | ((value & 255) << 8)) : (ushort)(((uint)previous & 0xff00) | (value & 255));
            if ((register & 1) == 0) c.Address = c.BaseAddress = updated; else c.Count = c.BaseCount = updated;
            _highByte[chip] = !_highByte[chip]; return;
        }
        int selected = chip * 4 + (int)(value & 3);
        switch (register)
        {
            case 8: _command[chip] = (byte)value; break;
            case 9: _channels[selected].Requested = (value & 4) != 0; break;
            case 10: _channels[selected].Masked = (value & 4) != 0; break;
            case 11: _channels[selected].Mode = (byte)value; break;
            case 12: _highByte[chip] = false; break;
            case 13:
                _highByte[chip] = false; _status[chip] = _command[chip] = 0;
                for (int i = chip * 4; i < chip * 4 + 4; i++) { _channels[i].Masked = true; _channels[i].Requested = false; _channels[i].TerminalCount = false; }
                break;
            case 14: for (int i = chip * 4; i < chip * 4 + 4; i++) _channels[i].Masked = false; break;
            case 15: for (int i = 0; i < 4; i++) _channels[chip * 4 + i].Masked = (value & (1u << i)) != 0; break;
        }
    }
    /// <summary>Performs the requested bounded controller transfer while preserving count, address and terminal-count semantics.</summary>
    public int Transfer(int channel, Span<byte> deviceBytes, bool deviceToMemory)
    {
        if ((uint)channel >= 8 || channel == 4) throw new ArgumentOutOfRangeException(nameof(channel));
        Channel c = _channels[channel]; int chip = channel / 4, unit = channel < 4 ? 1 : 2; c.TerminalCount = false;
        if (deviceBytes.Length % unit != 0) throw new ArgumentException("DMA buffer must contain whole transfer units.");
        int operation = (c.Mode >> 2) & 3;
        if (c.Masked || (_command[chip] & 4) != 0 || operation == 3 || ((c.Mode >> 6) & 3) == 3) return 0;
        if (operation != 0 && operation != (deviceToMemory ? 1 : 2)) throw new InvalidOperationException("DMA direction does not match the programmed channel mode.");
        int transferred = 0; c.Requested = true;
        try
        {
            for (int offset = 0; offset < deviceBytes.Length; offset += unit)
            {
                uint physical = unit == 1 ? ((uint)c.Page << 16) | c.Address : ((uint)(c.Page & 0xfe) << 16) | ((uint)c.Address << 1);
                if (operation != 0)
                    for (int i = 0; i < unit; i++)
                        if (deviceToMemory) memory.WriteByte(physical + (uint)i, deviceBytes[offset + i]); else deviceBytes[offset + i] = memory.ReadByte(physical + (uint)i);
                transferred += unit;
                c.Address = unchecked((ushort)(c.Address + ((c.Mode & 0x20) != 0 ? -1 : 1)));
                bool terminal = c.Count == 0; c.Count--;
                if (terminal)
                {
                    c.TerminalCount = true;
                    _status[chip] |= (byte)(1 << (channel & 3));
                    if ((c.Mode & 0x10) != 0) { c.Address = c.BaseAddress; c.Count = c.BaseCount; }
                    else c.Masked = true;
                    break;
                }
            }
        }
        finally { c.Requested = false; }
        return transferred;
    }
}
