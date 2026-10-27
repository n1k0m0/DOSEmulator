// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Hardware;

// Reuse our existing YM3812 reference tables without changing the accepted OPL2 path.
public sealed partial class Opl3
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
}
