using DOSEmulator.Hardware;

internal static partial class Opl2Tests
{
    private static void RhythmSetup(Rig r)
    {
        foreach (byte offset in new byte[] {16,19,17,20,18,21})
        {
            r.Write((byte)(0x20+offset),0x21); r.Write((byte)(0x40+offset),8);
            r.Write((byte)(0x60+offset),0xf0); r.Write((byte)(0x80+offset),0x0f);
        }
        for (int ch = 6; ch < 9; ch++) { r.Write((byte)(0xa0+ch),0x44); r.Write((byte)(0xb0+ch),0x12); }
        r.Write(0xbd,0x20);
    }
    public static void AddRhythmTo(Action<string,Action> test)
    {
        foreach ((byte bit, int[] slots) in new (byte,int[])[] { (16,[12,13]),(1,[14]),(8,[15]),(4,[16]),(2,[17]) })
            test($"OPL rhythm bit {bit} keys only its assigned percussion operators and produces sound", () =>
            {
                var r = new Rig(); RhythmSetup(r); r.Write(0xbd,(byte)(0x20|bit));
                for (int i = 0; i < 18; i++) Check(r.Opl.InspectOperator(i/2,(i&1)!=0).Keyed == slots.Contains(i),$"key slot {i}");
                Check(r.Opl.KeyOnCount == slots.Length,"key count"); r.Advance(100000000); Check(r.RmsTail(1000) > 0.001,$"audible RMS {r.RmsTail(1000)}");
                r.Write(0xbd,0x20); r.Advance(300000000); Check(r.RmsTail(1000) < 1e-8 && r.Opl.ActiveOperators == 0,$"release RMS {r.RmsTail(1000)} active {r.Opl.ActiveOperators}");
            });
        test("OPL held percussion bits do not retrigger; a new edge restarts their envelopes", () =>
        {
            var r = new Rig(); RhythmSetup(r); r.Write(0xbd,0x3f); long keys = r.Opl.KeyOnCount;
            r.Write(0xbd,0xff); Check(r.Opl.KeyOnCount == keys); r.Write(0xbd,0x20); r.Write(0xbd,0x3f);
            Check(r.Opl.KeyOnCount == keys+6);
        });
        test("OPL rhythm lower bits are inert without rhythm enable and normal channel keys return on exit", () =>
        {
            var r = new Rig(); RhythmSetup(r); r.Write(0xbd,0x1f); Check(r.Opl.KeyOnCount == 0);
            r.Write(0xb7,0x32); Check(r.Opl.InspectOperator(7,true).Keyed);
            r.Write(0xbd,0x20); Check(!r.Opl.InspectOperator(7,true).Keyed);
            r.Write(0xb7,0x32); Check(!r.Opl.InspectOperator(7,true).Keyed);
            r.Write(0xbd,0); Check(r.Opl.InspectOperator(7,true).Keyed);
        });
        test("OPL six melodic channels remain keyed during percussion mode", () =>
        {
            var r = new Rig(); r.Tone(0); RhythmSetup(r); r.Write(0xbd,0x3f); r.Advance(100000000);
            Check(r.Opl.InspectOperator(0,true).Keyed && r.Opl.ActiveOperators >= 8 && !r.Opl.UnsupportedProgramming);
        });
        test("OPL 23-bit percussion noise continues while silent and is independent of host chunking", () =>
        {
            var r = new Rig(); uint expected = 1;
            for (int i = 0; i < 1000; i++)
            {
                // Equivalent Fibonacci polynomial, evaluated from the old state.
                uint feedback = ((expected >> 22) ^ (expected >> 8) ^ (expected >> 7) ^ expected) & 1;
                expected = ((expected << 1)|feedback) & 0x7fffff;
            }
            r.Advance(NativeSampleTime(1000)); Check(r.Opl.GeneratedSamples == 1000 && r.Opl.NoiseState == expected && r.Opl.NoiseState != 0);
            r.Opl.Reset(); Check(r.Opl.NoiseState == 1);
        });
        test("OPL percussion waveform and noise are independent of mixer source gain and update chunks", () =>
        {
            var a = new Rig(); var b = new Rig(); RhythmSetup(a); RhythmSetup(b);
            a.Write(0xbd,0x3f); b.Write(0xbd,0x3f); a.Advance(100000000);
            for (int i = 0; i < 1000; i++) b.Advance(100000);
            Check(a.Samples().AsSpan().SequenceEqual(b.Samples()) && a.Opl.NoiseState == b.Opl.NoiseState);
            a.Opl.Volume=0; a.Advance(100000000); Check(a.RmsTail(1000)<1e-8 && a.Opl.ActiveOperators==6);
        });
        test("OPL bass drum additive connection mutes modulator contribution instead of adding it", () =>
        {
            var a = new Rig(); var b = new Rig(); RhythmSetup(a); RhythmSetup(b);
            a.Write(0xc6,1); b.Write(0xc6,1); a.Write(0x50,8); b.Write(0x50,63); a.Write(0xbd,0x30); b.Write(0xbd,0x30);
            a.Advance(100000000); b.Advance(100000000);
            Check(a.Samples().AsSpan().SequenceEqual(b.Samples()));
        });
    }
}
