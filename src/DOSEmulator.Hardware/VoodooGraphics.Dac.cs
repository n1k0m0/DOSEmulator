// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VoodooGraphics
{
    private readonly byte[] _dacPll = new byte[32];
    private readonly byte[] _dacPalette = new byte[768];
    private byte _dacReadIndex, _dacWriteIndex, _dacReadPhase, _dacWritePhase;
    private byte _dacPaletteReadIndex, _dacPaletteWriteIndex, _dacPaletteReadPhase, _dacPaletteWritePhase;
    private byte _dacReadLatch, _dacCommand, _dacMask;

    /// <summary>Initializes the chosen Voodoo ICS clock assignment and GENDAC serial state only at power-on, independently from PCI/FBI reset.</summary>
    private void PowerOnDac()
    {
        // Original video defaults and the board's A/B memory-clock indices. These are writable clock registers, not detection responses.
        ReadOnlySpan<byte> video = [0x7d,0x50,0x55,0x49,0x2a,0x43,0x77,0x4a,0x79,0x49,0x6f,0x47,0x74,0x2b,0x71,0x29];
        video.CopyTo(_dacPll); _dacPll[20] = 0x4f; _dacPll[21] = 0x2b; _dacPll[22] = 0x79; _dacPll[23] = 0x2e;
        _dacMask = 255;
    }

    /// <summary>Returns the programmed nominal clock from its full M/N pair, without treating a PLL write as elapsed guest time.</summary>
    public ulong NominalVideoClockHz
    {
        get
        {
            int selected = (_dacPll[28] & 32) != 0 ? _dacPll[28] & 7 : 0;
            int m = _dacPll[selected * 2] & 127, n = _dacPll[selected * 2 + 1];
            return 14_318_000UL * (ulong)(m + 2) / ((ulong)((n & 31) + 2) << (n >> 5 & 3));
        }
    }

    /// <summary>Initiates one actual external DAC byte transaction; only a read updates FBI's separate two-step readback latch.</summary>
    private void TransferDac(uint value)
    {
        int address = (int)(value >> 8 & 7);
        if ((value & 0x800) != 0) _dacReadLatch = ReadDac(address);
        else WriteDac(address, (byte)value);
    }

    /// <summary>Advances one independent PLL transfer cursor after two bytes, or after the single control-register byte.</summary>
    private static void AdvancePll(ref byte index, ref byte phase)
    {
        if (index == 14 || phase == 1) { index = (byte)((index + 1) & 15); phase = 0; }
        else phase = 1;
    }

    /// <summary>Reads indexed clock, palette, mask and command bytes through the selected GENDAC register address.</summary>
    private byte ReadDac(int address)
    {
        switch (address)
        {
            case 0: return _dacPaletteWriteIndex;
            case 1:
            {
                byte value = _dacPalette[_dacPaletteReadIndex * 3 + _dacPaletteReadPhase];
                if (++_dacPaletteReadPhase == 3) { _dacPaletteReadPhase = 0; _dacPaletteReadIndex++; }
                return value;
            }
            case 2: return _dacMask;
            case 3: return _dacPaletteReadIndex;
            case 4: return _dacWriteIndex;
            case 5:
            {
                byte value = _dacPll[_dacReadIndex * 2 + _dacReadPhase];
                AdvancePll(ref _dacReadIndex, ref _dacReadPhase); return value;
            }
            case 6: return _dacCommand;
            default: return _dacReadIndex;
        }
    }

    /// <summary>Writes the selected serial register and retains independent read/write cursors, including palette and PLL auto-increment.</summary>
    private void WriteDac(int address, byte value)
    {
        switch (address)
        {
            case 0: _dacPaletteWriteIndex = value; _dacPaletteWritePhase = 0; break;
            case 1:
                _dacPalette[_dacPaletteWriteIndex * 3 + _dacPaletteWritePhase] = (byte)(value & 63);
                if (++_dacPaletteWritePhase == 3) { _dacPaletteWritePhase = 0; _dacPaletteWriteIndex++; } break;
            case 2: _dacMask = value; break;
            case 3: _dacPaletteReadIndex = value; _dacPaletteReadPhase = 0; break;
            case 4: _dacWriteIndex = (byte)(value & 15); _dacWritePhase = 0; break;
            case 5:
                if (_dacWriteIndex is <= 7 or 10 or 11 or 14)
                    _dacPll[_dacWriteIndex * 2 + _dacWritePhase] = (byte)(value & (_dacWriteIndex == 14 ? 0x37 : 0x7f));
                AdvancePll(ref _dacWriteIndex, ref _dacWritePhase); break;
            case 6: _dacCommand = value; break;
            case 7: _dacReadIndex = (byte)(value & 15); _dacReadPhase = 0; break;
        }
    }

    /// <summary>Rejects malformed serial cursors, impossible half-control transfers and changed physical array sizes before adoption.</summary>
    private static void ValidateDac(byte[] pll, byte[] palette, byte readIndex, byte writeIndex, byte readPhase, byte writePhase, byte paletteReadPhase, byte paletteWritePhase)
    {
        if (pll is not { Length: 32 } || palette is not { Length: 768 } || palette.Any(v => v > 63) ||
            readIndex > 15 || writeIndex > 15 || readPhase > 1 || writePhase > 1 ||
            readIndex == 14 && readPhase != 0 || writeIndex == 14 && writePhase != 0 || paletteReadPhase > 2 || paletteWritePhase > 2 ||
            (pll[28] & ~0x37) != 0 || pll[29] != 0 || pll.Any(v => v > 127) ||
            new[] { 8, 9, 12, 13, 15 }.Any(i => pll[i * 2] != 0 || pll[i * 2 + 1] != 0))
            throw new InvalidDataException("Invalid SST-1 external DAC/PLL serial state.");
    }
}
