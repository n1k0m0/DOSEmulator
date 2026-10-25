namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    public uint Read(ushort port, int width)
    {
        uint result = 0;
        for (int b = 0; b < width; b++) result |= (uint)ReadPort((ushort)(port + b)) << (b * 8);
        return result;
    }
    private bool ActiveCrtc(ushort port) => (port & 0xfff0) == ((_misc & 1) != 0 ? 0x3d0 : 0x3b0);
    private byte ReadPort(ushort port)
    {
        if (port is 0x3b4 or 0x3d4) return ActiveCrtc(port) ? _crtcIndex : (byte)255;
        if (port is 0x3b5 or 0x3d5) return ActiveCrtc(port) && _crtcIndex < _crtc.Length ? _crtc[_crtcIndex] : (byte)255;
        if (port is 0x3ba or 0x3da)
        {
            if (!ActiveCrtc(port)) return 255;
            _attributeData = false;
            ulong period = FramePeriod;
            ulong position = _clock.Now % period, linePeriod = period / (ulong)(VerticalLines + 40);
            bool retrace = position >= period * 95 / 100;
            bool blank = retrace || position % linePeriod >= linePeriod * 4 / 5;
            return (byte)((retrace ? 8 : 0) | (blank ? 1 : 0));
        }
        switch (port)
        {
            case 0x3c0: return (byte)(_attrIndex | (_displayEnabled ? 0x20 : 0));
            case 0x3c1: return _attrIndex < _attr.Length ? _attr[_attrIndex] : (byte)255;
            case 0x3c2: return 0x10;
            case 0x3c3: return 1;
            case 0x3c4: return _seqIndex;
            case 0x3c5: return _seqIndex < _seq.Length ? _seq[_seqIndex] : (byte)255;
            case 0x3c6: return _pelMask;
            case 0x3c7: return _dacState;
            case 0x3c8: return _writeIndex;
            case 0x3c9:
                byte value = _dac[_readIndex * 3 + _readComponent];
                if (++_readComponent == 3) { _readComponent = 0; _readIndex++; }
                return value;
            case 0x3cc: return _misc;
            case 0x3ce: return _gcIndex;
            case 0x3cf: return _gcIndex < _gc.Length ? _gc[_gcIndex] : (byte)255;
            default: return 255;
        }
    }
    public void Write(ushort port, uint value, int width)
    {
        for (int b = 0; b < width; b++) WritePort((ushort)(port + b), (byte)(value >> (b * 8)));
    }
    private void WritePort(ushort port, byte value)
    {
        if (port is 0x3b4 or 0x3d4) { if (ActiveCrtc(port)) _crtcIndex = (byte)(value & 31); return; }
        if (port is 0x3b5 or 0x3d5)
        {
            if (!ActiveCrtc(port) || _crtcIndex >= _crtc.Length) return;
            if ((_crtc[0x11] & 128) != 0 && _crtcIndex < 8)
            { if (_crtcIndex == 7) _crtc[7] = (byte)((_crtc[7] & ~16) | (value & 16)); }
            else _crtc[_crtcIndex] = value;
            UpdateMode(); return;
        }
        switch (port)
        {
            case 0x3c0:
                if (!_attributeData) { _attrIndex = (byte)(value & 31); _displayEnabled = (value & 32) != 0; }
                else if (_attrIndex < _attr.Length) _attr[_attrIndex] = value;
                _attributeData = !_attributeData; break;
            case 0x3c2: _misc = value; break;
            case 0x3c4: _seqIndex = (byte)(value & 7); break;
            case 0x3c5: if (_seqIndex < _seq.Length) _seq[_seqIndex] = value; UpdateMode(); break;
            case 0x3c6: _pelMask = value; break;
            case 0x3c7: _readIndex = value; _readComponent = 0; _dacState = 3; break;
            case 0x3c8: _writeIndex = value; _writeComponent = 0; _dacState = 0; break;
            case 0x3c9:
                _dac[_writeIndex * 3 + _writeComponent] = (byte)(value & 63); UpdateColor(_writeIndex);
                if (++_writeComponent == 3) { _writeComponent = 0; _writeIndex++; }
                break;
            case 0x3ce: _gcIndex = (byte)(value & 15); break;
            case 0x3cf: if (_gcIndex < _gc.Length) _gc[_gcIndex] = value; UpdateMode(); break;
        }
    }
}
