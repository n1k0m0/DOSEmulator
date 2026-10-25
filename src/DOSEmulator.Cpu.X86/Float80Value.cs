namespace DOSEmulator.Cpu.X86;

/// <summary>Raw x87 extended format: explicit 64-bit significand and sign/exponent.</summary>
public readonly record struct Float80Value(ulong Significand, ushort SignExponent)
{
    public int Exponent => SignExponent & 0x7fff;
    public bool Negative => (SignExponent & 0x8000) != 0;
    public bool IntegerBit => (Significand & 0x8000000000000000) != 0;
    public int Tag => Exponent == 0 && Significand == 0 ? 1 :
        Exponent == 0 || Exponent == 0x7fff || !IntegerBit ? 2 : 0;
    public static Float80Value One => new(0x8000000000000000, 0x3fff);
    public static Float80Value Zero => default;
    public static Float80Value Indefinite => new(0xc000000000000000, 0xffff);
}
