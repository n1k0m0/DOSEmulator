using System.Text;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

public sealed partial class VgaAdapter
{
    private int PaletteIndex(int color)
    {
        int index = _attr[color & _attr[0x12] & 15] & 63;
        if ((_attr[0x10] & 128) != 0) index = (index & 15) | ((_attr[0x14] & 3) << 4);
        return (index | ((_attr[0x14] & 12) << 4)) & _pelMask;
    }
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
                byte value = _planes[0][(Start + row * Stride + column) & 65535];
                line.Append(value is >= 32 and < 127 ? (char)value : ' ');
            }
            text.AppendLine(line.ToString().TrimEnd());
        }
        return text.ToString().TrimEnd();
    }
    public VideoFrame Capture(ulong now)
    {
        int width = Width, height = Height;
        var pixels = new uint[checked(width * height)];
        if (!_displayEnabled || (_seq[1] & 32) != 0 || (_seq[0] & 3) != 3) Array.Fill(pixels, 0xff000000u);
        else if (GraphicsMode)
        {
            int pan = _attr[0x13] & 15;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int pixel = x + pan, address = (Start + y * Stride + (Indexed256 ? pixel / 4 : pixel / 8)) & 65535;
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
            int cursor = ((_crtc[14] << 8 | _crtc[15]) + ((_crtc[11] >> 5) & 3)) & 65535;
            ulong rasterFrame = now / FramePeriod;
            bool cursorOff = (rasterFrame & 16) != 0, blinkOff = (rasterFrame & 32) != 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int address = (Start + y / characterHeight * Stride + x / CharacterWidth) & 65535;
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
        return new(++_frame, _generation, now, width, height, pixels);
    }
}
