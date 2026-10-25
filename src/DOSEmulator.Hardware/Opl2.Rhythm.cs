namespace DOSEmulator.Hardware;

public sealed partial class Opl2
{
    private void UpdateRhythmKeys()
    {
        byte controls = _registers[0xbd];
        for (int channel = 6; channel < 9; channel++)
            SetKey(channel, !RhythmEnabled && (_registers[0xb0 + channel] & 0x20) != 0);
        // BD owns both operators of channel 6; HH/SD and TOM/CY are independent.
        int[] masks = [16,16,1,8,4,2];
        for (int slot = 12; slot < 18; slot++)
            SetKeySource(_operators[slot], 4, RhythmEnabled && (controls & masks[slot - 12]) != 0);
    }
    private double RhythmSample(double am, double vibrato)
    {
        Operator bassMod = _operators[12], bass = _operators[13];
        Channel bd = _channels[6];
        double feedback = (bassMod.Previous + bassMod.Older) * 0.5 * FeedbackCycles[bd.Feedback];
        double mod = OperatorSample(bassMod, feedback, am, vibrato);
        bassMod.Older = bassMod.Previous; bassMod.Previous = mod;
        double sum = OperatorSample(bass, bd.Additive ? 0 : mod * 2, am, vibrato);

        int hh = (int)(_operators[14].Phase >> 11), cymbal = (int)(_operators[17].Phase >> 11);
        int selection = (((hh >> 2) ^ (hh >> 7)) | (hh >> 3) | ((cymbal >> 5) ^ (cymbal >> 3))) & 1;
        int noise = (int)(_noise & 1);
        int hatPhase = (selection << 9) | (0xd0 >> (2 * (noise ^ selection)));
        int snarePhase = (0x100 << ((hh >> 8) & 1)) ^ (noise << 8);
        int cymbalPhase = 0x100 | (selection << 9);
        sum += OperatorSample(_operators[14], 0, am, vibrato, hatPhase);
        sum += OperatorSample(_operators[15], 0, am, vibrato, snarePhase);
        sum += OperatorSample(_operators[16], 0, am, vibrato);
        sum += OperatorSample(_operators[17], 0, am, vibrato, cymbalPhase);
        return sum * 2;
    }
}
