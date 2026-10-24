namespace DOSEmulator.Hardware;

/// <summary>Original reference-firmware palettes; components are six-bit VGA DAC values.</summary>
public static class VgaPalette
{
    public static readonly uint[] TextColors =
    [0xff000000,0xff0000aa,0xff00aa00,0xff00aaaa,0xffaa0000,0xffaa00aa,0xffaa5500,0xffaaaaaa,
     0xff555555,0xff5555ff,0xff55ff55,0xff55ffff,0xffff5555,0xffff55ff,0xffffff55,0xffffffff];
    public static byte[] Planar()
    {
        byte[] rgb = new byte[768];
        for (int i = 0; i < 64; i++)
        {
            rgb[i * 3] = (byte)(((i & 4) != 0 ? 42 : 0) + ((i & 32) != 0 ? 21 : 0));
            rgb[i * 3 + 1] = (byte)(((i & 2) != 0 ? 42 : 0) + ((i & 16) != 0 ? 21 : 0));
            rgb[i * 3 + 2] = (byte)(((i & 1) != 0 ? 42 : 0) + ((i & 8) != 0 ? 21 : 0));
        }
        return rgb;
    }
    public static byte[] Indexed()
    {
        byte[] rgb = new byte[768];
        for (int i = 0; i < 16; i++)
        {
            rgb[i * 3] = (byte)(((TextColors[i] >> 16) & 255) * 63 / 255);
            rgb[i * 3 + 1] = (byte)(((TextColors[i] >> 8) & 255) * 63 / 255);
            rgb[i * 3 + 2] = (byte)((TextColors[i] & 255) * 63 / 255);
        }
        for (int i = 16; i < 40; i++) rgb.AsSpan(i * 3, 3).Fill((byte)((i - 16) * 63 / 23));
        for (int i = 40; i < 256; i++)
        {
            int color = i - 40;
            rgb[i * 3] = (byte)(color / 36 * 63 / 5);
            rgb[i * 3 + 1] = (byte)(color / 6 % 6 * 63 / 5);
            rgb[i * 3 + 2] = (byte)(color % 6 * 63 / 5);
        }
        return rgb;
    }
}
