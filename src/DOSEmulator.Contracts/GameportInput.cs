// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Contracts;

/// <summary>Four external resistances and four closed switches; null means an open axis circuit, not a centered joystick.</summary>
public sealed record GameportInput
{
    /// <summary>Joystick A horizontal resistance in ohms, 0..100000, or an unplugged input.</summary>
    public int? AxOhms { get; init; }
    /// <summary>Joystick A vertical resistance in ohms, 0..100000, or an unplugged input.</summary>
    public int? AyOhms { get; init; }
    /// <summary>Joystick B horizontal resistance in ohms, 0..100000, or an unplugged input.</summary>
    public int? BxOhms { get; init; }
    /// <summary>Joystick B vertical resistance in ohms, 0..100000, or an unplugged input.</summary>
    public int? ByOhms { get; init; }
    /// <summary>Pressed switches in A1/A2/B1/B2 order; device reads invert these four bits.</summary>
    public int Buttons { get; init; }

    /// <summary>Read one immutable axis in the hardware's low-nibble ordering.</summary>
    public int? Axis(int axis) => axis switch
    { 0 => AxOhms, 1 => AyOhms, 2 => BxOhms, 3 => ByOhms, _ => throw new ArgumentOutOfRangeException(nameof(axis)) };

    /// <summary>Reject the entire external input before replacing any live or sampled device state.</summary>
    public void Validate()
    {
        if ((uint)Buttons > 15) throw new ArgumentOutOfRangeException(nameof(Buttons));
        for (int axis = 0; axis < 4; axis++)
            if (Axis(axis) is < 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(axis), "Gameport resistance must be 0..100000 ohms or null.");
    }
}
