// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Security.Cryptography;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>82437FX A2 host-controller subset with CPU DRAM top/hole decoding and PAM shadow storage.
/// Cache/timing attributes are readable latches; PCI masters receive only explicitly accepted DRAM, without SMM or cache timing.</summary>
public sealed partial class Intel82437Fx : IPciConfigurationFunction, IPciConfigurationAbortSink, IMemoryDevice
{
    private readonly MemoryBus _memory;
    private readonly byte[] _firmware;
    private readonly string _firmwareSha256;
    private readonly byte[] _config = new byte[256];

    /// <summary>Creates a cacheless A2 board binding with a private immutable 64-KiB F-segment ROM copy.</summary>
    public Intel82437Fx(MemoryBus memory, byte[] firmware)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(firmware);
        if (firmware.Length != 0x10000) throw new ArgumentException("The host requires exactly 64 KiB of board firmware.", nameof(firmware));
        _memory = memory;
        _firmware = firmware.ToArray();
        _firmwareSha256 = Convert.ToHexString(SHA256.HashData(_firmware));
        ResetConfiguration();
    }

    /// <summary>Identity of the immutable low BIOS target, used as a snapshot construction invariant.</summary>
    public string FirmwareIdentity => _firmwareSha256;
    /// <summary>Current CPU DRAM decoding ceiling, capped by both the 128-MiB chip limit and installed storage.</summary>
    public uint DecodedRamBytes => _memory.DecodedRamBytes;
    /// <summary>Current optional CPU DRAM hole base; the hole is not remapped above installed RAM.</summary>
    public uint RamHoleStart => _memory.RamHoleStart;
    /// <summary>Current optional CPU DRAM hole length; zero disables the hole.</summary>
    public uint RamHoleLength => _memory.RamHoleLength;
    /// <summary>MAE controls PCI-master DRAM acceptance; it does not gate the CPU's own RAM accesses.</summary>
    public bool MemoryAccessEnabled => (_config[4] & 2) != 0;

    /// <summary>Accepts one complete PCI physical DRAM grant, including every PAM bank; rejection never reads ROM or contacts MMIO.</summary>
    public bool TryGetPciMemorySpan(uint address, int length, bool write, out Span<byte> bytes)
    {
        bytes = default;
        if (!MemoryAccessEnabled || length <= 0) return false;
        ulong end = (ulong)address + (uint)length;
        if (end > 0x1_0000_0000UL) return false;
        uint pamStart = Math.Max(address, 0x000c0000u);
        ulong pamEnd = Math.Min(end, 0x00100000UL);
        byte required = write ? (byte)2 : (byte)1;
        for (ulong at = pamStart; at < pamEnd;)
        {
            if ((Attributes((uint)at - 0x000c0000u) & required) == 0) return false;
            at = at >= 0x000f0000UL ? 0x00100000UL : (at + 0x4000UL) & ~0x3fffUL;
        }
        return _memory.TryGetPhysicalRamSpan(address, length, this, out bytes);
    }

    /// <summary>Checks the fixed memory peer and the exact DRB4/DRAMC decoding derived from this controller.</summary>
    public bool HasMemoryBinding(MemoryBus memory)
    {
        GetExpectedDecode(out uint top, out uint holeStart, out uint holeLength);
        return ReferenceEquals(_memory, memory) && memory.DecodedRamBytes == top &&
            memory.RamHoleStart == holeStart && memory.RamHoleLength == holeLength;
    }

    /// <summary>Resets chip configuration and CPU memory decoding; a CPU-only hot reset must not call this method.</summary>
    public void ResetConfiguration()
    {
        for (int i = 0; i < _config.Length; i++) _config[i] = ResetValue(i);
        ApplyMemoryDecode();
    }

    /// <summary>Returns the defined power-on byte, including A2 identification and a board with no L2 cache populated.</summary>
    private static byte ResetValue(int offset) => offset switch
    {
        0x00 => 0x86, 0x01 => 0x80, 0x02 => 0x2d, 0x03 => 0x12,
        0x04 => 0x06, 0x07 => 0x02, 0x08 => 0x02, 0x0b => 0x06,
        0x52 => 0x02, 0x57 => 0x01, >= 0x60 and <= 0x64 => 0x02,
        0x72 => 0x02, _ => 0
    };

    /// <summary>Returns implemented writable bits; real SMRAM control remains explicitly outside this subset.</summary>
    private static byte WritableMask(int offset) => offset switch
    {
        0x04 => 0x02, 0x07 => 0x30, 0x0d => 0xf8,
        0x50 => 0xef, 0x52 => 0xfb, 0x57 => 0xcf, 0x58 => 0x7f,
        0x59 => 0x70, >= 0x5a and <= 0x5f => 0x77,
        >= 0x60 and <= 0x64 => 0x3f, 0x68 => 0x1f, _ => 0
    };

    /// <summary>Rejects an unsupported width or an incomplete transaction before reading or changing any byte.</summary>
    private static void ValidateTransaction(int offset, int width)
    {
        if (width is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(width));
        if (offset < 0 || offset > 256 - width) throw new ArgumentOutOfRangeException(nameof(offset));
    }

    /// <summary>Reads one byte/word/dword configuration transaction in little-endian byte-lane order.</summary>
    public uint ReadConfiguration(int offset, int width)
    {
        ValidateTransaction(offset, width);
        uint value = 0;
        for (int lane = 0; lane < width; lane++) value |= (uint)_config[offset + lane] << (lane * 8);
        return value;
    }

    /// <summary>Applies only requested writable lanes, with W1C status and one final CPU decode update.</summary>
    public void WriteConfiguration(int offset, uint value, int width)
    {
        ValidateTransaction(offset, width);
        bool decodeChanged = false;
        for (int lane = 0; lane < width; lane++)
        {
            int address = offset + lane;
            byte incoming = (byte)(value >> (lane * 8));
            byte mask = WritableMask(address);
            if (address == 0x07) _config[address] &= (byte)~(incoming & mask);
            else _config[address] = (byte)((_config[address] & ~mask) | (incoming & mask));
            decodeChanged |= address is 0x57 or 0x64;
        }
        if (decodeChanged) ApplyMemoryDecode();
    }

    /// <summary>Latches an unexpected host-initiated configuration master abort; guest status W1C clears it.</summary>
    public void RecordMasterAbort() => _config[0x07] |= 0x20;

    /// <summary>Derives the chip's DRB4 top and DRAMC hole; reserved HEN=11 uses the explicit no-hole fallback.</summary>
    private void GetExpectedDecode(out uint top, out uint holeStart, out uint holeLength)
    {
        top = Math.Min(Math.Min((uint)_config[0x64] * 0x400000u, 0x08000000u), (uint)_memory.Ram.Length);
        (holeStart, holeLength) = (_config[0x57] >> 6) switch
        {
            1 => (0x00080000u, 0x00020000u),
            2 => (0x00f00000u, 0x00100000u),
            _ => (0u, 0u)
        };
    }

    /// <summary>Publishes CPU decode settings without changing the hidden physical RAM bytes, time or event deadlines.</summary>
    private void ApplyMemoryDecode()
    {
        GetExpectedDecode(out uint top, out uint holeStart, out uint holeLength);
        _memory.SetDecodedRamBytes(top);
        _memory.SetRamHole(holeStart, holeLength);
    }

    /// <summary>Checks persisted byte contracts before media branching or any decoder mutation.</summary>
    public static void ValidateConfigurationBytes(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != 256) throw new InvalidDataException("Invalid 82437FX configuration size.");
        for (int i = 0; i < configuration.Length; i++)
        {
            int fixedMask = ~WritableMask(i) & 0xff;
            if ((configuration[i] & fixedMask) != (ResetValue(i) & fixedMask))
                throw new InvalidDataException($"Invalid 82437FX fixed configuration bits at {i:X2}.");
        }
    }

    /// <summary>Restores the decoder only from configuration that passed its complete fixed-bit contract.</summary>
    private void ValidateState()
    {
        ValidateConfigurationBytes(_config);
        ApplyMemoryDecode();
    }

    /// <summary>Selects one of twelve 16-KiB expansion segments or the single 64-KiB F-segment PAM field.</summary>
    private byte Attributes(uint offset)
    {
        if (offset >= 0x40000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset >= 0x30000) return (byte)(_config[0x59] >> 4);
        int segment = (int)(offset >> 14);
        return (byte)(_config[0x5a + (segment >> 1)] >> ((segment & 1) * 4));
    }

    /// <summary>Checks underlying DRAM availability after PAM selects it, preserving hidden bytes outside CPU decode.</summary>
    private bool HasDram(uint physical) => physical < _memory.DecodedRamBytes &&
        (_memory.RamHoleLength == 0 || physical < _memory.RamHoleStart ||
            (ulong)physical >= (ulong)_memory.RamHoleStart + _memory.RamHoleLength);

    /// <summary>Reads PAM-selected shadow DRAM or the immutable F ROM; absent C/E PCI targets float high.</summary>
    public byte ReadByte(uint offset)
    {
        byte attributes = Attributes(offset);
        uint physical = 0x000c0000 + offset;
        if ((attributes & 1) != 0) return HasDram(physical) ? _memory.Ram[physical] : (byte)0xff;
        return offset >= 0x30000 ? _firmware[offset - 0x30000] : (byte)0xff;
    }

    /// <summary>Writes only PAM-selected available shadow DRAM; forwarded ROM/absent-target writes leave it untouched.</summary>
    public void WriteByte(uint offset, byte value)
    {
        byte attributes = Attributes(offset);
        uint physical = 0x000c0000 + offset;
        if ((attributes & 2) != 0 && HasDram(physical))
        {
            _memory.Ram[physical] = value;
            _memory.NotifyPhysicalRamWrite(physical, 1);
        }
    }
}
