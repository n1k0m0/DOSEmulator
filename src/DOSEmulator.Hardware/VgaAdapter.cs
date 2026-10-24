using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>256-KiB reference VGA subset. Four planes/latches, indexed registers and six-bit DAC.
/// Scanout timing is approximate; this is not cycle-accurate VGA.</summary>
public sealed partial class VgaAdapter : IMemoryDevice, IIoPortDevice
{
    private readonly byte[][] _planes = Enumerable.Range(0, 4).Select(_ => new byte[65536]).ToArray();
    private readonly byte[] _latch = new byte[4], _seq = new byte[5], _gc = new byte[9], _crtc = new byte[25], _attr = new byte[21];
    private readonly byte[] _dac = VgaPalette.Planar();
    private readonly uint[] _rgb = new uint[256];
    private readonly DeviceScheduler _clock;
    private byte _seqIndex, _gcIndex, _crtcIndex, _attrIndex, _misc = 0x67, _pelMask = 255;
    private byte _readIndex, _writeIndex, _readComponent, _writeComponent, _dacState;
    private bool _attributeData, _displayEnabled = true;
    private long _frame;
    private int _generation = 1;
    private (bool Graphics, bool Indexed, int Width, int Height) _modeKey;
    public bool GraphicsMode => (_gc[6] & 1) != 0;
    public bool Indexed256 => GraphicsMode && (_gc[5] & 0x40) != 0;
    private int CharacterWidth => (_seq[1] & 1) != 0 ? 8 : 9;
    private int ScanlineRepeat => ((_crtc[9] & 31) + 1) * ((_crtc[9] & 0x80) != 0 ? 2 : 1);
    private int VerticalLines => _crtc[0x12] + ((_crtc[7] & 2) << 7) + ((_crtc[7] & 0x40) << 3) + 1;
    public int Width => (_crtc[1] + 1) * (GraphicsMode ? 8 : CharacterWidth) / (Indexed256 ? 2 : 1);
    public int Height => GraphicsMode ? Math.Max(1, VerticalLines / ScanlineRepeat) : VerticalLines;
    public int ModeGeneration => _generation;
    private int Start => _crtc[12] << 8 | _crtc[13];
    private int Stride => _crtc[0x13] * 2;

    public VgaAdapter(DeviceScheduler? clock = null)
    {
        _clock = clock ?? new();
        new byte[] { 3, 1, 3, 0, 2 }.CopyTo(_seq, 0);
        new byte[] { 0, 0, 0, 0, 0, 0x10, 0x0e, 0, 255 }.CopyTo(_gc, 0);
        _crtc[1] = 79; _crtc[7] = 2; _crtc[9] = 15; _crtc[0x12] = 0x8f; _crtc[0x13] = 40; _crtc[0x0a] = 0x20;
        new byte[] { 0,1,2,3,4,5,0x14,7,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f }.CopyTo(_attr, 0);
        _attr[0x10] = 0x0c; _attr[0x12] = 15;
        for (int character = 0; character < 256; character++)
            for (int row = 1; row < 15; row++) _planes[2][character * 32 + row] = (byte)(BitmapFont.Row((char)character, (row - 1) / 2) << 2);
        for (int i = 0; i < 256; i++) UpdateColor(i);
        _modeKey = (GraphicsMode, Indexed256, Width, Height);
    }
    private void UpdateMode()
    {
        var next = (GraphicsMode, Indexed256, Width, Height);
        if (next != _modeKey) { _modeKey = next; _generation++; }
    }
    private void UpdateColor(int index)
    {
        uint Component(int channel) { uint value = _dac[index * 3 + channel]; return (value << 2) | (value >> 4); }
        _rgb[index] = 0xff000000u | Component(0) << 16 | Component(1) << 8 | Component(2);
    }
    private bool Aperture(uint offset, out int address)
    {
        address = 0;
        if ((_misc & 2) == 0 || offset >= 0x20000) return false;
        uint start = ((_gc[6] >> 2) & 3) switch { 2 => 0x10000u, 3 => 0x18000u, _ => 0 };
        uint length = ((_gc[6] >> 2) & 3) switch { 0 => 0x20000u, 1 => 0x10000u, _ => 0x8000u };
        if (offset < start || offset - start >= length) return false;
        address = (int)(offset - start); return true;
    }
    public byte ReadByte(uint offset)
    {
        if (!Aperture(offset, out int address)) return 255;
        int plane = _gc[4] & 3;
        if ((_seq[4] & 8) != 0) { plane = address & 3; address >>= 2; }
        else if ((_gc[5] & 0x10) != 0) { plane = (plane & 2) | (address & 1); address >>= 1; }
        for (int p = 0; p < 4; p++) _latch[p] = _planes[p][address & 65535];
        if ((_gc[5] & 8) == 0) return _latch[plane];
        int result = 255;
        for (int p = 0; p < 4; p++)
            if ((_gc[7] & (1 << p)) != 0) result &= ~(_latch[p] ^ ((_gc[2] & (1 << p)) != 0 ? 255 : 0));
        return (byte)result;
    }
    public void WriteByte(uint offset, byte value)
    {
        if (!Aperture(offset, out int address)) return;
        int planes = _seq[2] & 15;
        if ((_seq[4] & 8) != 0) { planes &= 1 << (address & 3); address >>= 2; }
        else if ((_seq[4] & 4) == 0) { planes &= (address & 1) == 0 ? 5 : 10; address >>= 1; }
        int mode = _gc[5] & 3, rotation = _gc[3] & 7;
        byte rotated = (byte)((value >> rotation) | (value << (8 - rotation)));
        int mask = mode == 3 ? _gc[8] & rotated : _gc[8];
        for (int plane = 0; plane < 4; plane++)
        {
            if ((planes & (1 << plane)) == 0) continue;
            if (mode == 1) { _planes[plane][address & 65535] = _latch[plane]; continue; }
            int bits = mode switch
            {
                2 => (value & (1 << plane)) != 0 ? 255 : 0,
                3 => (_gc[0] & (1 << plane)) != 0 ? 255 : 0,
                _ => (_gc[1] & (1 << plane)) != 0 ? (_gc[0] & (1 << plane)) != 0 ? 255 : 0 : rotated
            };
            bits = ((_gc[3] >> 3) & 3) switch { 1 => bits & _latch[plane], 2 => bits | _latch[plane], 3 => bits ^ _latch[plane], _ => bits };
            _planes[plane][address & 65535] = (byte)((bits & mask) | (_latch[plane] & ~mask));
        }
    }
}
