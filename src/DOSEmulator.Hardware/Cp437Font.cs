using System.Globalization;

namespace DOSEmulator.Hardware;

/// <summary>Original reference-platform artwork. No host font or ROM-image dependency.</summary>
public static class Cp437Font
{
    private static readonly byte[] Eight = ReadArtwork();
    private static readonly byte[] Fourteen = Resize(14);
    private static readonly byte[] Sixteen = Resize(16);

    /// <summary>Packed, immutable 256-glyph tables; bit 7 is the left pixel.</summary>
    public static ReadOnlySpan<byte> Table(int height) => height switch
    {
        8 => Eight, 14 => Fourteen, 16 => Sixteen,
        _ => throw new ArgumentOutOfRangeException(nameof(height), "Reference fonts have 8, 14 or 16 rows.")
    };

    private static byte[] ReadArtwork()
    {
        using var stream = typeof(Cp437Font).Assembly.GetManifestResourceStream("DOSEmulator.Cp437Artwork")
            ?? throw new InvalidOperationException("Missing original CP437 artwork.");
        using var reader = new StreamReader(stream);
        byte[] result = new byte[256 * 8];
        int next = 0;
        while (reader.ReadLine() is { } line)
        {
            string[] fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields.Length != 9 || next >= 256 || byte.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) != next)
                throw new InvalidOperationException("CP437 artwork must define each byte exactly once, in order.");
            for (int row = 0; row < 8; row++) result[next * 8 + row] = byte.Parse(fields[row + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            next++;
        }
        if (next != 256) throw new InvalidOperationException("Incomplete CP437 artwork.");
        return result;
    }

    private static byte[] Resize(int height)
    {
        byte[] result = new byte[256 * height];
        for (int code = 0; code < 256; code++)
            for (int y = 0; y < height; y++)
            {
                int sourceY;
                if (code is >= 0xb0 and <= 0xb2) sourceY = y & 7; // Stable shade density, no vertical stretching.
                else if (code is >= 0xb3 and <= 0xda)
                {
                    // Preserve one-pixel single strokes and separated double rails.
                    // Extend only the vertical arms above/below their central joints.
                    int middle = height / 2;
                    sourceY = y < middle - 1 ? Math.Min(y, 2) : Math.Min(7, y - middle + 4);
                }
                else if (code is >= 0xdb and <= 0xdf) sourceY = y * 8 / height;
                else
                {
                    // Existing seven-row Latin drawings retain their original 14-row
                    // body (and 16-row side bearings). Eight-row accents/descenders
                    // use all eight source rows without losing the bottom stroke.
                    int margin = height == 16 ? 1 : 0;
                    if (y < margin || y >= height - margin) continue;
                    int sourceRows = Eight[code * 8 + 7] == 0 ? 7 : 8;
                    sourceY = (y - margin) * sourceRows / (height - 2 * margin);
                }
                result[code * height + y] = Eight[code * 8 + sourceY];
            }
        return result;
    }
}
