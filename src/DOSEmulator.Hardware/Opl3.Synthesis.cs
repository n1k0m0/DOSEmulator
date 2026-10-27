// SPDX-License-Identifier: Apache-2.0 AND BSD-3-Clause
// Copyright 2026 Nils Kopal
// Noise/percussion formulas derived from YMFM, Copyright (c) 2021 Aaron Giles.
// The applicable BSD notice is in LICENSES/BSD-3-Clause.txt and docs/THIRD-PARTY-NOTICES.md.
namespace DOSEmulator.Hardware;

public sealed partial class Opl3
{
    /// <summary>Immutable static-data percussion key masks, avoiding one temporary array per topology operator.</summary>
    private static ReadOnlySpan<int> RhythmKeyMasks => [16, 16, 1, 8, 4, 2];
    private static readonly double[][] Waves = Enumerable.Range(0, 8).Select(wave => Enumerable.Range(0, 1024).Select(i =>
    {
        double sine = Math.Sin(2 * Math.PI * i / 1024);
        return wave switch
        {
            1 => i < 512 ? sine : 0, 2 => Math.Abs(sine), 3 => (i & 256) == 0 ? Math.Abs(sine) : 0,
            4 => i < 512 ? Math.Sin(4 * Math.PI * i / 1024) : 0,
            5 => i < 512 ? Math.Abs(Math.Sin(4 * Math.PI * i / 1024)) : 0,
            6 => i < 512 ? 1 : -1,
            7 => i < 512 ? Math.Pow(2, -i / 32.0) : -Math.Pow(2, -(1023 - i) / 32.0),
            _ => sine
        };
    }).ToArray()).ToArray();
    private sealed partial class Operator
    {
        public uint Phase, Increment;
        public bool Keyed, Sustain, Am, Vibrato;
        public int Multiple = 1, AttackRate, DecayRate, ReleaseRate, Wave;
        public double Attenuation = 96, TotalLevel, SustainLevel, KeyScaleLevel, AttackFactor = 1, DecayAmount, ReleaseAmount;
        public OplEnvelopeStage Stage;
        public double Previous, Older;
    }
    private sealed partial class Channel { public int Fnum, Block, Feedback, Routing = 3; public bool Additive; }
    /// <summary>Resolves an OPL3 four-operator member to the pair's authoritative first channel.</summary>
    private int PairPrimary(int channel)
    {
        int local = channel % 9;
        if (local >= 6) return -1;
        int primary = local < 3 ? channel : channel - 3;
        int bit = primary % 9 + (primary >= 9 ? 3 : 0);
        return (_pairMask & (1 << bit)) != 0 ? primary : -1;
    }
    /// <summary>Decodes the selected operator registers into the reference synthesis parameters.</summary>
    private void UpdateOperator(int slot)
    {
        Operator op = _operators[slot]; int address = (slot >= 18 ? 0x100 : 0) + OperatorOffsets[slot % 18];
        byte control = _registers[0x20 + address], level = _registers[0x40 + address];
        op.Multiple = TwiceMultiples[control & 15]; op.Am = (control & 0x80) != 0;
        op.Vibrato = (control & 0x40) != 0; op.Sustain = (control & 0x20) != 0;
        op.TotalLevel = (level & 63) * 0.75;
        op.SustainLevel = (_registers[0x80 + address] >> 4) == 15 ? 93 : (_registers[0x80 + address] >> 4) * 3;
        op.AttackRate = _registers[0x60 + address] >> 4; op.DecayRate = _registers[0x60 + address] & 15;
        op.ReleaseRate = _registers[0x80 + address] & 15;
    }
    /// <summary>Rebuilds the reference OPL3 channel/operator routing after a pairing or algorithm change.</summary>
    private void RefreshTopology()
    {
        for (int channel = 0; channel < 18; channel++)
        {
            int owner = PairPrimary(channel); if (owner < 0) owner = channel;
            int address = (owner >= 9 ? 0x100 : 0) + owner % 9;
            Channel ch = _channels[channel]; byte high = _registers[0xb0 + address];
            ch.Fnum = _registers[0xa0 + address] | (high & 3) << 8; ch.Block = (high >> 2) & 7;
            int note = ch.Block * 2 + ((ch.Fnum >> ((_registers[8] & 0x40) != 0 ? 9 : 8)) & 1);
            for (int slot = channel * 2; slot <= channel * 2 + 1; slot++)
            {
                Operator op = _operators[slot]; int opAddress = (slot >= 18 ? 0x100 : 0) + OperatorOffsets[slot % 18];
                int keyScale = (_registers[0x20 + opAddress] & 0x10) != 0 ? note : note >> 2;
                // Converts the selected reference envelope-rate bits into the effective synthesis step.
                int Rate(int rate) => rate == 0 ? 0 : Math.Min(63, rate * 4 + keyScale);
                op.Increment = (uint)((ch.Fnum << ch.Block) * op.Multiple);
                op.AttackFactor = AttackDecay[Rate(op.AttackRate)];
                op.DecayAmount = DecayStep[Rate(op.DecayRate)]; op.ReleaseAmount = DecayStep[Rate(op.ReleaseRate)];
                int ksl = _registers[0x40 + opAddress] >> 6;
                double kslBase = Math.Max(0, KslAtBlock7[ch.Fnum >> 6] - 3 * (7 - ch.Block));
                op.KeyScaleLevel = ksl switch { 1 => kslBase, 2 => kslBase / 2, 3 => kslBase * 2, _ => 0 };
                bool keyed = (high & 0x20) != 0;
                if (RhythmEnabled && channel is >= 6 and <= 8)
                {
                    keyed = (_registers[0xbd] & RhythmKeyMasks[slot - 12]) != 0;
                }
                SetKey(op, keyed);
            }
        }
    }
    /// <summary>Updates the operator's effective key state and performs an actual envelope key transition when required.</summary>
    private void SetKey(Operator op, bool keyed)
    {
        if (op.Keyed == keyed) return;
        op.Keyed = keyed;
        if (keyed) { op.Stage = OplEnvelopeStage.Attack; op.Phase = 0; op.Previous = op.Older = 0; KeyOnCount++; }
        else if (op.Stage != OplEnvelopeStage.Off) op.Stage = OplEnvelopeStage.Release;
    }
    /// <summary>Updates the percussion operators' independent key sources from the rhythm-control register.</summary>
    private void UpdateRhythmKeys() => RefreshTopology();
    /// <summary>Advances one native reference FM frame and publishes its current output buses.</summary>
    private void GenerateSample()
    {
        // Same attributed phase/noise research as our OPL2 rhythm path; see THIRD-PARTY-NOTICES.
        uint shifted = _noise << 1;
        _noise = (shifted | (((shifted >> 23) ^ (shifted >> 9) ^ (shifted >> 8) ^ (shifted >> 1)) & 1)) & 0x7fffff;
        if (!HasActiveOperators) { Array.Clear(_outputs); return; }
        // Builds the bounded reference triangular modulation value used by the surrounding synthesis calculation.
        static double Triangle(ulong phase, ulong period) => 1 - 4 * Math.Abs(phase / (double)period - 0.5);
        ulong sample = (ulong)GeneratedSamples;
        double am = (Triangle(sample * 37 % 500000, 500000) + 1) / 2 * ((_registers[0xbd] & 0x80) != 0 ? 4.8 : 1);
        double vibrato = Math.Pow(2, Triangle(sample * 64 % 500000, 500000) * ((_registers[0xbd] & 0x40) != 0 ? 14 : 7) / 1200);
        foreach (Operator op in _operators)
            op.Phase = (op.Phase + (uint)Math.Round(op.Increment * (op.Vibrato ? vibrato : 1))) & PhaseMask;
        Span<double> sums = stackalloc double[4]; sums.Clear();
        for (int channel = 0; channel < 18; channel++)
        {
            if (RhythmEnabled && channel is >= 6 and <= 8) continue;
            int primary = PairPrimary(channel);
            if (primary >= 0 && primary != channel) continue;
            Channel ch = _channels[channel]; Operator one = _operators[channel * 2], two = _operators[channel * 2 + 1];
            double feedback = (one.Previous + one.Older) * 0.5 * FeedbackCycles[ch.Feedback];
            double a = OperatorSample(one, feedback, am);
            one.Older = one.Previous; one.Previous = a;
            double b = OperatorSample(two, ch.Additive ? 0 : a * 2, am), value;
            if (primary < 0) value = b + (ch.Additive ? a : 0);
            else
            {
                Channel second = _channels[channel + 3];
                Operator three = _operators[(channel + 3) * 2], four = _operators[(channel + 3) * 2 + 1];
                double c = OperatorSample(three, !ch.Additive && second.Additive ? 0 : b * 2, am);
                double d = OperatorSample(four, second.Additive && ch.Additive ? 0 : c * 2, am);
                value = d + (ch.Additive ? a : second.Additive ? b : 0) + (ch.Additive && second.Additive ? c : 0);
                // All four-operator carriers use the primary channel output mask.
            }
            RouteTo(sums, ch.Routing, value);
        }
        if (RhythmEnabled) RhythmSample(sums, am);
        for (int i = 0; i < 4; i++) _outputs[i] = (short)Math.Round(Math.Clamp(sums[i] / 4, -1, 1) * 32767);
    }
    /// <summary>Adds a carrier only to buses enabled by the authoritative channel's output mask.</summary>
    private static void RouteTo(Span<double> sums, int routing, double value)
    { for (int i = 0; i < 4; i++) if ((routing & (1 << i)) != 0) sums[i] += value; }
    /// <summary>Advances a reference operator's phase/envelope and evaluates its selected waveform/modulation input.</summary>
    private double OperatorSample(Operator op, double phaseOffset, double am, int? rhythmPhase = null)
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
            case OplEnvelopeStage.Sustain: if (!op.Sustain) op.Attenuation += op.ReleaseAmount; break;
            case OplEnvelopeStage.Release: op.Attenuation += op.ReleaseAmount; break;
        }
        if (op.Attenuation >= 96 && op.Stage is OplEnvelopeStage.Release or OplEnvelopeStage.Sustain)
        { op.Attenuation = 96; op.Stage = OplEnvelopeStage.Off; }
        if (op.Stage == OplEnvelopeStage.Off || op.Attenuation >= 96) return 0;
        int phase = rhythmPhase ?? (((int)(op.Phase >> 11) + (int)Math.Round(phaseOffset * 1024)) & 1023);
        int attenuation = Math.Clamp((int)Math.Round((op.Attenuation + op.TotalLevel + op.KeyScaleLevel + (op.Am ? am : 0)) / 0.1875), 0, 1023);
        return Waves[op.Wave][phase] * AttenuationGain[attenuation];
    }
    /// <summary>Computes the reference percussion contribution from the current phases and deterministic noise state.</summary>
    private void RhythmSample(Span<double> sums, double am)
    {
        Operator mod = _operators[12], bass = _operators[13]; Channel ch = _channels[6];
        double m = OperatorSample(mod, (mod.Previous + mod.Older) * 0.5 * FeedbackCycles[ch.Feedback], am);
        mod.Older = mod.Previous; mod.Previous = m;
        RouteTo(sums, ch.Routing, OperatorSample(bass, ch.Additive ? 0 : m * 2, am) * 2);
        int hh = (int)(_operators[14].Phase >> 11), cymbal = (int)(_operators[17].Phase >> 11);
        int selection = (((hh >> 2) ^ (hh >> 7)) | (hh >> 3) | ((cymbal >> 5) ^ (cymbal >> 3))) & 1;
        int noise = (int)(_noise & 1), hatPhase = (selection << 9) | (0xd0 >> (2 * (noise ^ selection)));
        int snarePhase = (0x100 << ((hh >> 8) & 1)) ^ (noise << 8), cymbalPhase = 0x100 | (selection << 9);
        RouteTo(sums, _channels[7].Routing, (OperatorSample(_operators[14], 0, am, hatPhase) + OperatorSample(_operators[15], 0, am, snarePhase)) * 2);
        RouteTo(sums, _channels[8].Routing, (OperatorSample(_operators[16], 0, am) + OperatorSample(_operators[17], 0, am, cymbalPhase)) * 2);
    }
}
