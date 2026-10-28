// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    /// <summary>Reads an implemented VGA port register while preserving attribute flip-flop and status-read effects.</summary>
    public uint Read(ushort port, int width)
    {
        uint result = 0;
        for (int b = 0; b < width; b++) result |= (uint)ReadPort((ushort)(port + b)) << (b * 8);
        return result;
    }
    /// <summary>Checks whether the port belongs to the mono/color CRTC decoder selected by Misc-Out.</summary>
    private bool ActiveCrtc(ushort port) => (port & 0xfff0) == ((_misc & 1) != 0 ? 0x3d0 : 0x3b0);
    /// <summary>Reads the selected VGA register port, including active decoder and attribute/status side effects.</summary>
    private byte ReadPort(ushort port)
    {
        if (port is 0x3b4 or 0x3d4) return ActiveCrtc(port) ? _crtcIndex : (byte)255;
        if (port is 0x3b5 or 0x3d5)
        {
            if (!ActiveCrtc(port) || !ReadableCrtc(_crtcIndex)) return 255;
            return IsTrio64 ? ReadTrioCrtcValue(_crtcIndex) : _crtc[_crtcIndex];
        }
        if (IsEt4000 && port is 0x3b8 or 0x3d8) return ActiveCrtc(port) ? (byte)((_modeControl & 0x3f) | ((_hercules & 2) << 5)) : (byte)255;
        if (IsEt4000 && port == 0x3cd) return _etSegmentEnabled ? _segmentSelect : (byte)255;
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
            case 0x3c3: return IsTrio64 ? (byte)255 : (byte)1;
            case 0x3c4: return _seqIndex;
            case 0x3c5: return IsTrio64 ? ReadTrioSequencer() : _seqIndex < _seq.Length ? _seq[_seqIndex] : (byte)255;
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
    /// <summary>Routes a guest VGA register write through the active mono/color decoder and derived-state invalidation.</summary>
    public void Write(ushort port, uint value, int width)
    {
        for (int b = 0; b < width; b++) WritePort((ushort)(port + b), (byte)(value >> (b * 8)));
    }
    /// <summary>Processes a VGA register-port write before updating affected derived scanout state.</summary>
    private void WritePort(ushort port, byte value)
    {
        if (IsEt4000 && port is 0x3bf or 0x3b8 or 0x3d8) { WriteEtKey(port, value); return; }
        if (IsEt4000 && port == 0x3cd)
        {
            if (_etSegmentEnabled) { _segmentSelect = value; BankSwitchWrites++; }
            return;
        }
        if (port is 0x3b4 or 0x3d4) { if (ActiveCrtc(port)) _crtcIndex = (byte)(value & (IsTrio64 ? 255 : IsEt4000 ? 63 : 31)); return; }
        if (port is 0x3b5 or 0x3d5)
        {
            if (!ActiveCrtc(port)) return;
            if (IsTrio64) { WriteTrioCrtc(_crtcIndex, value); return; }
            if (!WritableCrtc(_crtcIndex)) return;
            byte previous = _crtc[_crtcIndex];
            if ((_crtc[0x11] & 128) != 0 && _crtcIndex < 8)
            { if (_crtcIndex == 7) _crtc[7] = (byte)((_crtc[7] & ~16) | (value & 16)); }
            else if (IsEt4000 && _crtcIndex == 0x35 && (_crtc[0x11] & 128) != 0)
                _crtc[0x35] = (byte)((_crtc[0x35] & ~0x90) | (value & 0x90));
            else _crtc[_crtcIndex] = value;
            if (_crtc[_crtcIndex] != previous) { _pixelRevision++; UpdateMode(); }
            return;
        }
        if (IsTrio64 && (port is >= 0x3c6 and <= 0x3c9) && (_crtc[0x33] & 0x10) != 0) return;
        switch (port)
        {
            case 0x3c0:
                if (!_attributeData)
                {
                    bool enabled = (value & 32) != 0;
                    if (_displayEnabled != enabled) _pixelRevision++;
                    _attrIndex = (byte)(value & 31); _displayEnabled = enabled;
                }
                else if (_attrIndex < _attr.Length && (_attrIndex != 0x16 || _etKey) &&
                    !(IsTrio64 && (_crtc[0x33] & 0x40) != 0 && (_attrIndex < 16 || _attrIndex == 0x11)) && _attr[_attrIndex] != value)
                { _attr[_attrIndex] = value; _pixelRevision++; UpdateMode(); }
                _attributeData = !_attributeData; break;
            case 0x3c2:
                if (IsTrio64) value &= 0xef;
                if (_misc != value) _pixelRevision++; _misc = value; break;
            case 0x3c4: _seqIndex = (byte)(value & (IsTrio64 ? 31 : 7)); break;
            case 0x3c5:
                if (IsTrio64) { WriteTrioSequencer(value); break; }
                if (_seqIndex < _seq.Length && (_seqIndex < 5 || _etKey && _seqIndex is 6 or 7))
                {
                    if (_seq[_seqIndex] != value) _pixelRevision++;
                    _seq[_seqIndex] = value;
                    if (IsEt4000 && _seqIndex == 0 && (value & 2) == 0) { _etSegmentEnabled = false; _etKey = false; }
                }
                UpdateMode(); break;
            case 0x3c6: if (_pelMask != value) _pixelRevision++; _pelMask = value; break;
            case 0x3c7: _readIndex = value; _readComponent = 0; _dacState = 3; break;
            case 0x3c8: _writeIndex = value; _writeComponent = 0; _dacState = 0; break;
            case 0x3c9:
                if (_dac[_writeIndex * 3 + _writeComponent] != (value & 63)) _pixelRevision++;
                _dac[_writeIndex * 3 + _writeComponent] = (byte)(value & 63); UpdateColor(_writeIndex);
                if (++_writeComponent == 3) { _writeComponent = 0; _writeIndex++; }
                break;
            case 0x3ce: _gcIndex = (byte)(value & 15); break;
            case 0x3cf:
                if (_gcIndex < _gc.Length)
                {
                    int changed = _gc[_gcIndex] ^ value;
                    _gc[_gcIndex] = value;
                    // Only these implemented GC bits affect scanout. CPU read/write controls still latch normally.
                    int displayMask = _gcIndex == 5 ? 0x40 : _gcIndex == 6 ? 1 : 0;
                    if ((changed & displayMask) != 0) { _pixelRevision++; UpdateMode(); }
                }
                break;
        }
    }
}
