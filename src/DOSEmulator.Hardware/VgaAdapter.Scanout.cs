// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    /// <summary>Resolves a source pixel through the active VGA attribute/DAC palette mapping.</summary>
    private int PaletteIndex(int color)
    {
        int index = _attr[color & _attr[0x12] & 15] & 63;
        if ((_attr[0x10] & 128) != 0) index = (index & 15) | ((_attr[0x14] & 3) << 4);
        return (index | ((_attr[0x14] & 12) << 4)) & _pelMask;
    }
    /// <summary>Produces a diagnostic text approximation from guest VGA cells; it does not replace BIOS rendering.</summary>
    public string ReadText()
    {
        if (GraphicsMode) return "";
        int columns = _crtc[1] + 1, rows = Height / ((_crtc[9] & 31) + 1);
        var text = new StringBuilder();
        for (int row = 0; row < rows; row++)
        {
            var line = new StringBuilder(columns);
            for (int column = 0; column < columns; column++)
            {
                byte value = _planes[0][(Start + row * Stride + column) & PlaneAddressMask];
                line.Append(value is >= 32 and < 127 ? (char)value : ' ');
            }
            text.AppendLine(line.ToString().TrimEnd());
        }
        return text.ToString().TrimEnd();
    }
    /// <summary>Captures the component's current output into owned result storage.</summary>
    public VideoFrame Capture(ulong now) => Capture(now, new VgaFrameBuffer());
    /// <summary>Captures the component's current output into owned result storage.</summary>
    public VideoFrame Capture(ulong now, VgaFrameBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        int width = Width, height = Height;
        ulong phase = GraphicsMode ? 0 : (now / FramePeriod) & 48;
        if (!ReferenceEquals(buffer.Owner, this) || buffer.Revision != _pixelRevision || buffer.Phase != phase || buffer.Width != width || buffer.Height != height)
        {
            if (buffer.Pixels.Length != checked(width * height)) buffer.Pixels = new uint[checked(width * height)];
            Render(now, width, height, buffer.Pixels);
            buffer.Owner = this; buffer.Revision = _pixelRevision; buffer.Phase = phase;
            buffer.Width = width; buffer.Height = height; buffer.RenderedFrames++;
        }
        return buffer.Frame = new(++_frame, _generation, now, width, height, buffer.Pixels);
    }
    /// <summary>Reads original-Trio64 indexed or LUT-bypassed direct color from the same interleaved VRAM bytes.</summary>
    private uint ReadTrioScanoutColor(int address, int colorBits)
    {
        uint low = ReadTrioRaw(address);
        if (colorBits == 8) return _rgb[low & _pelMask];
        uint value = low | ((uint)ReadTrioRaw(address + 1) << 8);
        if (colorBits == 24) return 0xff000000u | ((uint)ReadTrioRaw(address + 2) << 16) | value;
        uint red = (value >> (colorBits == 15 ? 10 : 11)) & 31;
        uint green = (value >> 5) & (colorBits == 15 ? 31u : 63u);
        uint blue = value & 31;
        // Expand each source component to the host surface; DAC palette and PEL mask apply only to indexed modes.
        red = (red << 3) | (red >> 2); blue = (blue << 3) | (blue >> 2);
        green = colorBits == 15 ? (green << 3) | (green >> 2) : (green << 2) | (green >> 4);
        return 0xff000000u | (red << 16) | (green << 8) | blue;
    }
    /// <summary>Renders the current guest text/graphics state into a complete caller-owned pixel surface.</summary>
    private void Render(ulong now, int width, int height, uint[] pixels)
    {
        if (!_displayEnabled || (_seq[1] & 32) != 0 || !IsTrio64 && (_seq[0] & 3) != 3 || IsTrio64 && (_seq[0x18] & 0x20) != 0) Array.Fill(pixels, 0xff000000u);
        else if (TrioEnhanced)
        {
            // DB014-B table 11-2: enhanced DWORD scanout pitch is offset * 8 bytes; start units are DWORDs.
            int colorBits = TrioEnhancedColorBits;
            if (colorBits == 0 || (_crtc[0x42] & 0x20) != 0)
                Array.Fill(pixels, 0xff000000u);
            else
            {
                // Original Trio64 mode13 uses B,G,R,padding storage; the Trio32-only packed24 mode is not selected here.
                int bytesPerPixel = colorBits == 8 ? 1 : colorBits == 24 ? 4 : 2;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        pixels[y * width + x] = ReadTrioScanoutColor(TrioDisplayStart * 4 + y * TrioOffset * 8 + x * bytesPerPixel, colorBits);
                RenderTrioCursor(width,height,pixels,colorBits);
            }
        }
        else if (GraphicsMode)
        {
            int pan = _attr[0x13] & 15;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int pixel = x + pan, address = (Start + y * Stride + (Indexed256 ? pixel / 4 : pixel / 8)) & PlaneAddressMask;
                    int color = 0;
                    if (Indexed256) color = _planes[pixel & 3][address] & _pelMask;
                    else
                    {
                        for (int plane = 0; plane < 4; plane++) color |= ((_planes[plane][address] >> (7 - (pixel & 7))) & 1) << plane;
                        color = PaletteIndex(color);
                    }
                    pixels[y * width + x] = _rgb[color];
                }
        }
        else
        {
            int characterHeight = (_crtc[9] & 31) + 1;
            // VGA font blocks are interleaved 0,4,1,5,2,6,3,7 in plane 2.
            // Attribute bit 3 selects A/B when different maps are enabled; otherwise it is intensity.
            int fontA = ((_seq[3] & 12) << 12) | ((_seq[3] & 32) << 8);
            int fontB = ((_seq[3] & 3) << 14) | ((_seq[3] & 16) << 9);
            bool twoFonts = (_seq[4] & 2) != 0 && fontA != fontB;
            int cursor = ((_crtc[14] << 8 | _crtc[15] | (IsEt4000 ? (_crtc[0x33] & 12) << 14 : 0)) + ((_crtc[11] >> 5) & 3)) & PlaneAddressMask;
            ulong rasterFrame = now / FramePeriod;
            bool cursorOff = (rasterFrame & 16) != 0, blinkOff = (rasterFrame & 32) != 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int address = (Start + y / characterHeight * Stride + x / CharacterWidth) & PlaneAddressMask;
                    int character = _planes[0][address], attribute = _planes[1][address];
                    int row = y % characterHeight, column = x % CharacterWidth;
                    int font = twoFonts && (attribute & 8) != 0 ? fontA : fontB;
                    byte glyph = _planes[2][font + character * 32 + row];
                    bool foreground = column < 8 ? (glyph & (128 >> column)) != 0 : character is >= 0xc0 and <= 0xdf && (_attr[0x10] & 4) != 0 && (glyph & 1) != 0;
                    if ((_attr[0x10] & 8) != 0 && (attribute & 128) != 0 && blinkOff) foreground = false;
                    bool cursorPixel = address == cursor && (_crtc[10] & 32) == 0 && !cursorOff && row >= (_crtc[10] & 31) && row <= (_crtc[11] & 31);
                    int color = cursorPixel || foreground ? attribute & (twoFonts ? 7 : 15) : (attribute >> 4) & ((_attr[0x10] & 8) != 0 ? 7 : 15);
                    pixels[y * width + x] = _rgb[PaletteIndex(color)];
                }
        }
    }
}
