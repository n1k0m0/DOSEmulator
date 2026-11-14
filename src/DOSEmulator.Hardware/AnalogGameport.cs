// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Contracts;
using DOSEmulator.Core;

namespace DOSEmulator.Hardware;

/// <summary>Functional IBM 6361493 game-control adapter: four virtual RC pulses and live active-low switches.
/// Nominal timing follows IBM's 24.2 us + .011 us/ohm. No electrical tolerances, debounce, MIDI or host controller access.</summary>
public sealed partial class AnalogGameport(DeviceScheduler scheduler) : IIoPortDevice
{
    public const string ProfileId = "ibm-game-control-development";
    private GameportInput _sampled = new();
    /// <summary>Current external resistance/switch state, applied only by the emulation owner.</summary>
    public GameportInput Input { get; private set; } = new();
    /// <summary>Whether a byte OUT has started a conversion since this adapter's cold creation.</summary>
    public bool ConversionStarted { get; private set; }
    /// <summary>Virtual instant of the last simultaneous trigger; CPU hot reset does not reset this powered card.</summary>
    public ulong TriggerTime { get; private set; }
    /// <summary>Actual decoded byte IN count, including reads while a conversion is active.</summary>
    public long ReadCount { get; private set; }
    /// <summary>Actual decoded byte OUT count; written data bits do not select individual axes.</summary>
    public long TriggerCount { get; private set; }
    /// <summary>Validated external state replacements, separate from guest port transfers.</summary>
    public long InputUpdates { get; private set; }

    /// <summary>Apply immutable external inputs atomically; running conversions retain their trigger-time resistances.</summary>
    public void SetInput(GameportInput input)
    {
        ArgumentNullException.ThrowIfNull(input); input.Validate();
        Input = input; InputUpdates++;
    }

    /// <summary>Decode only A9..A0, as on the original IBM card; upper ISA address bits do not select a new adapter.</summary>
    public static bool Decodes(ushort port) => (port & 0x3ff) == 0x201;

    /// <summary>Validate bus width and return undriven bits for accesses outside the supported byte contract.</summary>
    private static uint Mask(int width) => width switch
    { 1 => 255, 2 => 65535, 4 => uint.MaxValue, _ => throw new ArgumentOutOfRangeException(nameof(width)) };

    /// <summary>Read four live switches and independently expired pulses without advancing time or retriggering.</summary>
    public uint Read(ushort port, int width)
    {
        uint mask = Mask(width);
        if (width != 1 || !Decodes(port)) return mask;
        ReadCount++;
        uint value = (uint)((~Input.Buttons & 15) << 4);
        if (ConversionStarted)
        {
            // A restored or directly supplied component state must not turn a
            // future trigger into a huge expired interval through ulong wrap.
            ulong elapsed = scheduler.Now >= TriggerTime ? scheduler.Now - TriggerTime : 0;
            for (int axis = 0; axis < 4; axis++)
                if (_sampled.Axis(axis) is not { } resistance || elapsed < 24200UL + (ulong)resistance * 11)
                    value |= 1u << axis;
        }
        return value;
    }

    /// <summary>Any decoded byte OUT simultaneously restarts four nominal pulses, sampling all resistances once.</summary>
    public void Write(ushort port, uint value, int width)
    {
        _ = Mask(width);
        if (width != 1 || !Decodes(port)) return;
        _sampled = Input; TriggerTime = scheduler.Now; ConversionStarted = true; TriggerCount++;
    }
}
