// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Reflection;
using System.Text.Json;
using DOSEmulator.Contracts;
using DOSEmulator.Core;
using DOSEmulator.Hardware;
using DOSEmulator.Runtime;

/// <summary>Rejects a future saved analog trigger and keeps a directly loaded component from unsigned time wrap.</summary>
internal static class AnalogGameportClockTests
{
    /// <summary>Registers the malformed-state and direct-device behavior as one independent control.</summary>
    public static void AddTo(Action<string, Action> test) => test("Gameport future trigger cannot expire through unsigned time wrap", () =>
    {
        var scheduler = new DeviceScheduler();
        var port = new AnalogGameport(scheduler);
        port.SetInput(new GameportInput { AxOhms = 0, AyOhms = 0, BxOhms = 0, ByOhms = 0 });
        port.Write(0x201, 0, 1);
        var state = port.SaveState();
        state.Fields["TriggerTime"] = JsonSerializer.SerializeToElement(1_000_000UL);
        port.LoadState(state);
        if ((port.Read(0x201, 1) & 15) != 15)
            throw new Exception("Future trigger underflowed to an expired analog pulse.");

        using var machine = PcMachine.CreateDemo(new() { Gameport = AnalogGameport.ProfileId });
        var graph = machine.CaptureHardwareState();
        graph.Components["gameport"].Fields["TriggerTime"] = JsonSerializer.SerializeToElement(1_000_000UL);
        var validation = typeof(PcMachine).Assembly.GetType("DOSEmulator.Runtime.SnapshotValidation")!
            .GetMethod("Check", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { validation.Invoke(null, [graph]); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { return; }
        throw new Exception("A future gameport trigger passed snapshot validation.");
    });
}
