// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>DOSE VGA, ET4000AX ISA with one MiB, or the two-MiB 86C764 VGA/8bpp subset.
/// Four planes/latches and a six-bit DAC share owned storage. Raster timing is approximate; accelerated/high-color S3 functions remain absent.</summary>
public sealed partial class VgaAdapter : IMemoryDevice, IIoPortDevice
{
    private readonly byte[][] _planes;
    private readonly byte[] _latch = new byte[4], _seq, _gc = new byte[9], _crtc, _attr;
    private readonly byte[] _dac = VgaPalette.Planar();
    private readonly uint[] _rgb = new uint[256];
    private readonly DeviceScheduler _clock;
    private byte _seqIndex, _gcIndex, _crtcIndex, _attrIndex, _misc = 0x67, _pelMask = 255;
    private byte _readIndex, _writeIndex, _readComponent, _writeComponent, _dacState;
    private bool _attributeData, _displayEnabled = true;
    private long _frame;
    private int _generation = 1;
    private long _pixelRevision = 1;
    private (bool Graphics, bool Indexed, int Width, int Height) _modeKey;
    /// <summary>Whether current guest VGA registers select a graphics scanout instead of text cells.</summary>
    public bool GraphicsMode => (_gc[6] & 1) != 0 || TrioEnhanced;
    /// <summary>Whether the current VGA graphics path interprets indexed 256-color pixels.</summary>
    public bool Indexed256 => TrioEnhanced ? TrioEnhanced8 : GraphicsMode && (_gc[5] & 0x40) != 0;
    /// <summary>Current guest text-cell width in output pixels.</summary>
    private int CharacterWidth => (_seq[1] & 1) != 0 ? 8 : 9;
    /// <summary>Number of output scanlines repeating each source scanline under the active VGA registers.</summary>
    private int ScanlineRepeat => ((_crtc[9] & 31) + 1) * ((_crtc[9] & 0x80) != 0 ? 2 : 1);
    /// <summary>Visible vertical source/display extent derived from the active CRTC overflow bits.</summary>
    private int VerticalLines => _crtc[0x12] + ((_crtc[7] & 2) << 7) + ((_crtc[7] & 0x40) << 3) + (IsEt4000 ? (_crtc[0x35] & 4) << 8 : IsTrio64 ? (_crtc[0x5e] & 2) << 9 : 0) + 1;
    /// <summary>Current visible output width in pixels.</summary>
    public int Width => IsTrio64 ? TrioWidth : (_crtc[1] + 1) * (GraphicsMode ? 8 : CharacterWidth) / (Indexed256 && (!IsEt4000 || (_attr[0x10] & 0x40) != 0) ? 2 : 1);
    /// <summary>Current visible output height in pixels.</summary>
    public int Height => Math.Max(1, GraphicsMode ? VerticalLines / ScanlineRepeat : VerticalLines / ((_crtc[9] & 0x80) != 0 ? 2 : 1));
    /// <summary>Monotonic generation incremented when derived guest video-mode state changes.</summary>
    public int ModeGeneration => _generation;
    /// <summary>Current scanout start address, independent from the CPU's selected ET4000 memory bank.</summary>
    private int Start => IsTrio64 ? TrioDisplayStart : _crtc[12] << 8 | _crtc[13] | (IsEt4000 ? (_crtc[0x33] & 3) << 16 : 0);
    /// <summary>Current guest scanout row stride derived from VGA CRTC addressing.</summary>
    private int Stride => (IsTrio64 ? TrioOffset : _crtc[0x13]) * 2;
    // Same approximate raster family as status-1 polling, independent of host captures.
    private ulong FramePeriod => VerticalLines >= 480 ? 16_666_667UL : 14_285_714UL;

    /// <summary>Initializes VgaAdapter and connects the supplied dependencies before the component is used.</summary>
    public VgaAdapter(DeviceScheduler? clock = null, bool et4000 = false) : this(clock, et4000, false) { }

    /// <summary>Constructs the named video model; ET4000 and Trio64 are mutually exclusive storage/register contracts.</summary>
    public VgaAdapter(DeviceScheduler? clock, bool et4000, bool trio64)
    {
        if (et4000 && trio64) throw new ArgumentException("ET4000 and Trio64 cannot share one video adapter.");
        IsEt4000 = et4000; IsTrio64 = trio64;
        _planes = Enumerable.Range(0, 4).Select(_ => new byte[trio64 ? 524288 : et4000 ? 262144 : 65536]).ToArray();
        _seq = new byte[trio64 ? 32 : et4000 ? 8 : 5]; _crtc = new byte[trio64 ? 128 : et4000 ? 64 : 25]; _attr = new byte[et4000 ? 23 : 21];
        if (et4000) { _seq[7] = 0xbc; _crtc[0x36] = 0x40; _crtc[0x37] = 0x0b; }
        _clock = clock ?? new();
        new byte[] { 3, 1, 3, 0, 2 }.CopyTo(_seq, 0);
        new byte[] { 0, 0, 0, 0, 0, 0x10, 0x0e, 0, 255 }.CopyTo(_gc, 0);
        _crtc[1] = 79; _crtc[7] = 2; _crtc[9] = 15; _crtc[0x12] = 0x8f; _crtc[0x13] = 40; _crtc[0x0a] = 0x20;
        new byte[] { 0,1,2,3,4,5,0x14,7,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f }.CopyTo(_attr, 0);
        _attr[0x10] = 0x0c; _attr[0x12] = 15;
        if (trio64) ResetTrioRegisters();
        ReadOnlySpan<byte> font = Cp437Font.Table(16);
        for (int character = 0; character < 256; character++)
            font.Slice(character * 16, 16).CopyTo(_planes[2].AsSpan(character * 32, 16));
        for (int i = 0; i < 256; i++) UpdateColor(i);
        _modeKey = (GraphicsMode, Indexed256, Width, Height);
    }
    /// <summary>Derives the visible text/graphics layout from guest registers and invalidates changed presentation state.</summary>
    private void UpdateMode()
    {
        var next = (GraphicsMode, Indexed256, Width, Height);
        if (next != _modeKey) { _modeKey = next; _generation++; }
    }
    /// <summary>Recomputes a palette entry after a guest DAC component change.</summary>
    private void UpdateColor(int index)
    {
        // Expands one six-bit DAC component into the renderer's eight-bit color channel.
        uint Component(int channel) { uint value = _dac[index * 3 + channel]; return (value << 2) | (value >> 4); }
        _rgb[index] = 0xff000000u | Component(0) << 16 | Component(1) << 8 | Component(2);
    }
    /// <summary>Decodes a CPU aperture offset under the active VGA memory-map register and rejects unmapped bytes.</summary>
    private bool Aperture(uint offset, out int address)
    {
        if (IsTrio64) return TrioAperture(offset, out address);
        address = 0;
        if ((_misc & 2) == 0 || offset >= 0x20000) return false;
        uint start = ((_gc[6] >> 2) & 3) switch { 2 => 0x10000u, 3 => 0x18000u, _ => 0 };
        uint length = ((_gc[6] >> 2) & 3) switch { 0 => 0x20000u, 1 => 0x10000u, _ => 0x8000u };
        if (offset < start || offset - start >= length) return false;
        address = (int)(offset - start); return true;
    }
    /// <summary>Reads the decoded VGA/ET4000 aperture using the active read mode, bank and plane/latch selection.</summary>
    public byte ReadByte(uint offset)
    {
        if (!Aperture(offset, out int address)) return 255;
        if (IsEt4000 && (_crtc[0x36] & 0x10) != 0) return 255; // This ISA board does not wire a high linear aperture.
        return ReadVgaMemory(address, true);
    }
    /// <summary>Shares the existing plane/latch read path with an explicitly unbanked S3 linear grant.</summary>
    private byte ReadVgaMemory(int address, bool banked)
    {
        int plane = _gc[4] & 3;
        if ((_seq[4] & 8) != 0) { plane = address & 3; address >>= 2; }
        else if ((_gc[5] & 0x10) != 0) { plane = (plane & 2) | (address & 1); address >>= 1; }
        address = banked ? BankAddress(address, false) : address;
        if (IsTrio64 && (uint)address >= (uint)_planes[0].Length) return 255;
        for (int p = 0; p < 4; p++) _latch[p] = _planes[p][address];
        if ((_gc[5] & 8) == 0) return _latch[plane];
        int result = 255;
        for (int p = 0; p < 4; p++)
            if ((_gc[7] & (1 << p)) != 0) result &= ~(_latch[p] ^ ((_gc[2] & (1 << p)) != 0 ? 255 : 0));
        return (byte)result;
    }
    /// <summary>Writes the decoded VGA/ET4000 aperture under current bank, plane-mask and write-mode rules.</summary>
    public void WriteByte(uint offset, byte value)
    {
        if (!Aperture(offset, out int address)) return;
        if (IsEt4000 && (_crtc[0x36] & 0x10) != 0) return;
        WriteVgaMemory(address, value, true);
    }
    /// <summary>Shares VGA masks, rotation, raster operations and latches with an explicitly unbanked S3 linear grant.</summary>
    private void WriteVgaMemory(int address, byte value, bool banked)
    {
        int planes = _seq[2] & 15;
        if ((_seq[4] & 8) != 0) { planes &= 1 << (address & 3); address >>= 2; }
        else if ((_seq[4] & 4) == 0) { planes &= (address & 1) == 0 ? 5 : 10; address >>= 1; }
        address = banked ? BankAddress(address, true) : address;
        if (IsTrio64 && (uint)address >= (uint)_planes[0].Length) return;
        int mode = _gc[5] & 3, rotation = _gc[3] & 7;
        if ((_seq[4] & 8) != 0 && mode == 0 && _gc[1] == 0 && _gc[3] == 0 && _gc[8] == 255)
        {
            if (planes != 0) StorePlane(System.Numerics.BitOperations.TrailingZeroCount((uint)planes), address, value);
            return;
        }
        byte rotated = (byte)((value >> rotation) | (value << (8 - rotation)));
        int mask = mode == 3 ? _gc[8] & rotated : _gc[8];
        for (int plane = 0; plane < 4; plane++)
        {
            if ((planes & (1 << plane)) == 0) continue;
            if (mode == 1) { StorePlane(plane, address, _latch[plane]); continue; }
            int bits = mode switch
            {
                2 => (value & (1 << plane)) != 0 ? 255 : 0,
                3 => (_gc[0] & (1 << plane)) != 0 ? 255 : 0,
                _ => (_gc[1] & (1 << plane)) != 0 ? (_gc[0] & (1 << plane)) != 0 ? 255 : 0 : rotated
            };
            bits = ((_gc[3] >> 3) & 3) switch { 1 => bits & _latch[plane], 2 => bits | _latch[plane], 3 => bits ^ _latch[plane], _ => bits };
            StorePlane(plane, address, (byte)((bits & mask) | (_latch[plane] & ~mask)));
        }
    }
    /// <summary>Writes one VGA plane byte and marks the affected scanout storage as changed.</summary>
    private void StorePlane(int plane, int address, byte value)
    {
        if (_planes[plane][address] == value) return;
        _planes[plane][address] = value; _pixelRevision++;
    }
}
