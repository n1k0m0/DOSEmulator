namespace DOSEmulator.Hardware;

public sealed partial class Opl2
{
    private const uint PhaseMask = (1u << 21) - 1;
    private static readonly int[] OperatorOffsets = [0, 3, 1, 4, 2, 5, 8, 11, 9, 12, 10, 13, 16, 19, 17, 20, 18, 21];
    private static readonly int[] TwiceMultiples = [1, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 20, 24, 24, 30, 30];
    private static readonly double[] FeedbackCycles = [0, 1.0 / 32, 1.0 / 16, 1.0 / 8, 1.0 / 4, 0.5, 1, 2];
    private static readonly double[] KslAtBlock7 = [0, 9, 12, 13.875, 15, 16.125, 16.875, 17.625, 18, 18.75, 19.125, 19.5, 19.875, 20.25, 20.625, 21];
    // Yamaha application manual table 3-6, full 0..96-dB envelope durations at the 3.6-MHz reference.
    // Continuous per-chip-sample interpolation is our approximation, not the die's microstep sequencer.
    private static readonly double[] AttackMs = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
        2826.24,2252.8,1884.16,1597.44, 1413.12,1126.4,942.08,798.72, 706.56,563.2,471.04,399.36,
        353.28,281.6,235.52,199.68, 176.64,140.8,117.76,99.84, 88.32,70.4,58.88,49.92,
        44.16,35.2,29.44,24.96, 22.08,17.6,14.72,12.48, 11.04,8.8,7.36,6.24, 5.52,4.4,3.68,3.12,
        2.76,2.2,1.84,1.56, 1.4,1.12,0.92,0.8, 0.7,0.56,0.46,0.42, 0.38,0.3,0.24,0.2, 0,0,0,0];
    private static readonly double[] DecayMs = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
        39280.64,31416.32,26173.44,22446.08, 19640.32,15708.16,13086.72,11223.04, 9820.16,7854.08,6543.36,5611.52,
        4910.08,3927.04,3271.68,2805.76, 2455.04,1963.52,1635.84,1402.88, 1227.52,981.76,817.92,701.44,
        613.76,490.88,408.96,350.72, 306.88,245.44,204.48,175.36, 153.44,122.72,102.24,87.68,
        76.72,61.36,51.12,43.84, 38.36,30.68,25.56,21.92, 19.2,15.36,12.8,10.96, 9.6,7.68,6.4,5.48,
        4.8,3.84,3.2,2.74, 2.4,2.4,2.4,2.4];
    private static readonly double[] AttackDecay = AttackMs.Select(ms => ms == 0 ? 0 : Math.Exp(-Math.Log(512) / (ms * 50))).ToArray();
    private static readonly double[] DecayStep = DecayMs.Select(ms => 96 / (ms * 50)).ToArray();
    private static readonly double[] AttenuationGain = Enumerable.Range(0, 1024).Select(i => Math.Pow(10, -i * 0.1875 / 20)).ToArray();
    private static readonly double[][] Waves = Enumerable.Range(0, 4).Select(wave => Enumerable.Range(0, 1024).Select(i =>
    {
        double sine = Math.Sin(2 * Math.PI * i / 1024);
        return wave switch { 1 => i < 512 ? sine : 0, 2 => Math.Abs(sine), 3 => (i & 256) == 0 ? Math.Abs(sine) : 0, _ => sine };
    }).ToArray()).ToArray();
    private sealed class Operator
    {
        public uint Phase, Increment;
        public int KeySources;
        public bool Keyed => KeySources != 0;
        public bool Sustain, Am, Vibrato;
        public int Multiple = 1, AttackRate, DecayRate, ReleaseRate, Wave;
        public double Attenuation = 96, TotalLevel, SustainLevel, KeyScaleLevel, AttackFactor = 1, DecayAmount, ReleaseAmount;
        public OplEnvelopeStage Stage;
        public double Previous, Older;
    }
    private sealed class Channel { public int Fnum, Block, Feedback; public bool Additive; }
    private void UpdateOperator(int slot)
    {
        Operator op = _operators[slot]; int offset = OperatorOffsets[slot];
        byte control = _registers[0x20 + offset], level = _registers[0x40 + offset];
        op.Multiple = TwiceMultiples[control & 15]; op.Am = (control & 0x80) != 0;
        op.Vibrato = (control & 0x40) != 0; op.Sustain = (control & 0x20) != 0;
        op.TotalLevel = (level & 63) * 0.75; op.SustainLevel = (_registers[0x80 + offset] >> 4) == 15 ? 93 : (_registers[0x80 + offset] >> 4) * 3;
        op.AttackRate = _registers[0x60 + offset] >> 4; op.DecayRate = _registers[0x60 + offset] & 15;
        op.ReleaseRate = _registers[0x80 + offset] & 15; op.Wave = _registers[0xe0 + offset] & 3;
    }
    private void UpdateChannel(int channel)
    {
        Channel ch = _channels[channel]; byte high = _registers[0xb0 + channel];
        ch.Fnum = _registers[0xa0 + channel] | (high & 3) << 8; ch.Block = (high >> 2) & 7;
        int note = ch.Block * 2 + ((ch.Fnum >> ((_registers[8] & 0x40) != 0 ? 9 : 8)) & 1);
        for (int slot = channel * 2; slot <= channel * 2 + 1; slot++)
        {
            Operator op = _operators[slot]; int offset = OperatorOffsets[slot];
            int keyScale = (_registers[0x20 + offset] & 0x10) != 0 ? note : note >> 2;
            int Rate(int rate) => rate == 0 ? 0 : Math.Min(63, rate * 4 + keyScale);
            op.Increment = (uint)((ch.Fnum << ch.Block) * op.Multiple);
            op.AttackFactor = AttackDecay[Rate(op.AttackRate)]; op.DecayAmount = DecayStep[Rate(op.DecayRate)]; op.ReleaseAmount = DecayStep[Rate(op.ReleaseRate)];
            int ksl = _registers[0x40 + offset] >> 6;
            double kslBase = Math.Max(0, KslAtBlock7[ch.Fnum >> 6] - 3 * (7 - ch.Block));
            op.KeyScaleLevel = ksl switch { 1 => kslBase, 2 => kslBase / 2, 3 => kslBase * 2, _ => 0 };
        }
    }
    private void SetKey(int channel, bool keyed)
    {
        for (int slot = channel * 2; slot <= channel * 2 + 1; slot++)
            SetKeySource(_operators[slot], 1, keyed);
    }
    private void SetKeySource(Operator op, int source, bool keyed)
    {
        bool wasKeyed = op.Keyed;
        op.KeySources = keyed ? op.KeySources | source : op.KeySources & ~source;
        if (wasKeyed == op.Keyed) return;
        if (op.Keyed) { op.Stage = OplEnvelopeStage.Attack; op.Phase = 0; op.Previous = op.Older = 0; KeyOnCount++; }
        else if (op.Stage != OplEnvelopeStage.Off) op.Stage = OplEnvelopeStage.Release;
    }
    private void GenerateSample()
    {
        if (ActiveOperators == 0) { _output = 0; return; }
        // Manual LFO rates, referenced to the chip clock. The precise die LFO sequence is pending.
        static double Triangle(ulong phase, ulong period) => 1 - 4 * Math.Abs(phase / (double)period - 0.5);
        ulong sample = (ulong)GeneratedSamples;
        double am = (Triangle(sample * 37 % 500000, 500000) + 1) / 2 * ((_registers[0xbd] & 0x80) != 0 ? 4.8 : 1);
        double vibrato = Math.Pow(2, Triangle(sample * 64 % 500000, 500000) * ((_registers[0xbd] & 0x40) != 0 ? 14 : 7) / 1200);
        double sum = 0;
        for (int channel = 0; channel < 9; channel++)
        {
            if (UnsupportedRhythm && channel >= 6) continue;
            Channel ch = _channels[channel]; Operator modulator = _operators[channel * 2], carrier = _operators[channel * 2 + 1];
            double feedback = (modulator.Previous + modulator.Older) * 0.5 * FeedbackCycles[ch.Feedback];
            double modulation = OperatorSample(modulator, feedback, am, vibrato);
            modulator.Older = modulator.Previous; modulator.Previous = modulation;
            // FM modulation depth is a model approximation pending comparison with recorded silicon.
            double output = OperatorSample(carrier, ch.Additive ? 0 : modulation * 2, am, vibrato);
            sum += output + (ch.Additive ? modulation : 0);
        }
        _output = (short)Math.Round(Math.Clamp(sum / 4, -1, 1) * 32767);
        if (_csmReleaseSample is { } release && GeneratedSamples + 1 >= release) ReleaseCsmKey();
    }
    private double OperatorSample(Operator op, double phaseOffset, double am, double vibrato)
    {
        switch (op.Stage)
        {
            case OplEnvelopeStage.Attack:
                op.Attenuation *= op.AttackFactor;
                if (op.Attenuation < 0.1875) { op.Attenuation = 0; op.Stage = OplEnvelopeStage.Decay; }
                break;
            case OplEnvelopeStage.Decay:
                op.Attenuation += op.DecayAmount;
                if (op.Attenuation >= op.SustainLevel) { op.Attenuation = op.SustainLevel; op.Stage = OplEnvelopeStage.Sustain; }
                break;
            case OplEnvelopeStage.Sustain:
                if (!op.Sustain) op.Attenuation += op.ReleaseAmount;
                break;
            case OplEnvelopeStage.Release: op.Attenuation += op.ReleaseAmount; break;
        }
        if (op.Attenuation >= 96 && op.Stage is OplEnvelopeStage.Release or OplEnvelopeStage.Sustain)
        { op.Attenuation = 96; op.Stage = OplEnvelopeStage.Off; }
        op.Phase = (op.Phase + (uint)Math.Round(op.Increment * (op.Vibrato ? vibrato : 1))) & PhaseMask;
        if (op.Stage == OplEnvelopeStage.Off || op.Attenuation >= 96) return 0;
        int phase = ((int)(op.Phase >> 11) + (int)Math.Round(phaseOffset * 1024)) & 1023;
        int wave = (_registers[1] & 0x20) != 0 ? op.Wave : 0;
        int attenuation = Math.Clamp((int)Math.Round((op.Attenuation + op.TotalLevel + op.KeyScaleLevel + (op.Am ? am : 0)) / 0.1875), 0, 1023);
        return Waves[wave][phase] * AttenuationGain[attenuation];
    }
}
