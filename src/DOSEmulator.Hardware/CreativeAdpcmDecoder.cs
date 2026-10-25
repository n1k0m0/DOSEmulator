namespace DOSEmulator.Hardware;

/// <summary>Unsigned 8-bit Creative ADPCM predictor. Bit depths 2, 3 (3/3/2 packing),
/// and 4 are distinct from IMA/MS ADPCM. Arithmetic derived from the documented
/// format and DOSLIB's original-hardware observations; no foreign decoder is embedded.</summary>
public sealed class CreativeAdpcmDecoder
{
    public byte Predictor { get; private set; } = 128;
    public int Step { get; private set; }

    public void Reference(byte value) { Predictor = value; Step = 0; }
    // Creative's "accumulated reference" retains the predictor, but the measured
    // block boundary restarts adaptation at its largest step, including auto-init.
    public void ContinueBlock(int bits) => Step = MaximumStep(bits);
    public static int SamplesPerByte(int bits) => bits switch
    {
        2 => 4, 3 => 3, 4 => 2, _ => throw new ArgumentOutOfRangeException(nameof(bits))
    };
    private static int MaximumStep(int bits) => bits switch
    {
        2 => 5, 3 => 4, 4 => 3, _ => throw new ArgumentOutOfRangeException(nameof(bits))
    };
    public byte Decode(byte packed, int bits, int sample)
    {
        int count = SamplesPerByte(bits);
        if ((uint)sample >= count) throw new ArgumentOutOfRangeException(nameof(sample));
        // The last 2.6-bit sample is a 3-bit code with an implicit low zero bit.
        int code = bits == 3
            ? sample == 2 ? (packed & 3) << 1 : (packed >> (5 - 3 * sample)) & 7
            : (packed >> ((count - 1 - sample) * bits)) & ((1 << bits) - 1);
        int sign = 1 << (bits - 1), magnitude = code & (sign - 1);
        int delta = Step == 0 ? magnitude
            : (2 * magnitude + 1) * (bits == 3 && Step == 4 ? 5 : 1 << (Step - 1));
        Predictor = (byte)Math.Clamp(Predictor + ((code & sign) == 0 ? delta : -delta), 0, 255);
        if (magnitude >= (bits == 4 ? 5 : sign - 1)) Step = Math.Min(Step + 1, MaximumStep(bits));
        else if (magnitude == 0) Step = Math.Max(0, Step - 1);
        return Predictor;
    }
}
