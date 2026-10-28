// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    /// <summary>Applies the own ISA-board register reset without clearing VRAM, reloading fonts, rewinding scanout time or invalidating existing reader leases.</summary>
    public void ApplyPiixHardReset()
    {
        Array.Clear(_latch); Array.Clear(_seq); Array.Clear(_gc); Array.Clear(_crtc); Array.Clear(_attr);
        new byte[] { 3, 1, 3, 0, 2 }.CopyTo(_seq, 0);
        new byte[] { 0, 0, 0, 0, 0, 0x10, 0x0e, 0, 255 }.CopyTo(_gc, 0);
        _crtc[1] = 79; _crtc[7] = 2; _crtc[9] = 15; _crtc[0x12] = 0x8f; _crtc[0x13] = 40; _crtc[0x0a] = 0x20;
        new byte[] { 0,1,2,3,4,5,0x14,7,0x38,0x39,0x3a,0x3b,0x3c,0x3d,0x3e,0x3f }.CopyTo(_attr, 0);
        _attr[0x10] = 0x0c; _attr[0x12] = 15;
        if (IsEt4000) { _seq[7] = 0xbc; _crtc[0x36] = 0x40; _crtc[0x37] = 0x0b; }
        _seqIndex = _gcIndex = _crtcIndex = _attrIndex = 0; _misc = 0x67; _pelMask = 255;
        _readIndex = _writeIndex = _readComponent = _writeComponent = _dacState = 0;
        _attributeData = false; _displayEnabled = true;
        _segmentSelect = _hercules = _modeControl = 0; _etKey = _etSegmentEnabled = false;
        if (IsTrio64) ResetTrioRegisters();
        VgaPalette.Planar().CopyTo(_dac, 0);
        for (int i = 0; i < 256; i++) UpdateColor(i);
        UpdateMode(); _pixelRevision++;
    }
}
