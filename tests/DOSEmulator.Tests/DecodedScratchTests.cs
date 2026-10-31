// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Collections;
using System.Reflection;
using DOSEmulator.Cpu.X86;

/// <summary>Checks retained-plan futures across long, short and empty decoder scratch-buffer reuse.</summary>
internal static class DecodedScratchTests
{
    /// <summary>Requires an independent retirement count, ownership bound or complete scalar-equivalent future.</summary>
    private static void Need(bool value, string why)
    {
        if (!value) throw new Exception(why);
    }

    /// <summary>Retires the public decoded entry's count and compares every CPU and RAM field.</summary>
    private static void Compare(DecodedBatchTests.Rig scalar, DecodedBatchTests.Rig decoded, int expected)
    {
        int actual = decoded.Cpu.TryRunDecodedBatch(64);
        Need(actual == expected, $"Expected {expected} elements, got {actual}.");
        for (int i = 0; i < actual; i++) Need(scalar.Cpu.Step() == 4, "Changed clocks.");
        DecodedBatchTests.Equal(scalar, decoded);
    }

    /// <summary>Checks bounded host-derived ownership; a retained plan must never alias mutable scratch storage.</summary>
    private static void Ownership(X86Cpu cpu)
    {
        object table = typeof(X86Cpu).GetField("DecodedBlocks", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [cpu, null];
        Need((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!, "Missing derived cache.");
        object cache = args[1]!;
        FieldInfo? field = cache.GetType().GetField("_decodeScratch", BindingFlags.Instance | BindingFlags.NonPublic);
        Need(field is not null, "Decoder has no reusable scratch storage.");
        var scratch = (Array)field!.GetValue(cache)!;
        Need(scratch.Length == 64, "Scratch storage exceeded its fixed bound.");
        var blocks = (IDictionary)cache.GetType().GetField("_blocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        foreach (object entry in blocks.Values)
        {
            object block = entry.GetType().GetProperty("Block")!.GetValue(entry)!;
            var retained = (Array)block.GetType().GetProperty("Elements")!.GetValue(block)!;
            Need(retained.Length <= 64, "Retained plan exceeded its element bound.");
            Need(!ReferenceEquals(scratch, retained), "Retained plan aliases mutable scratch storage.");
        }
    }

    /// <summary>Exercises old long plans after shorter and zero-element decodes overwrite temporary storage.</summary>
    private static void Case(int mode, X86CpuModel model)
    {
        byte[] program = Enumerable.Repeat((byte)0x40, 64).Append((byte)0xf4).ToArray();
        var scalar = DecodedBatchTests.Make(mode, program, model);
        var decoded = DecodedBatchTests.Make(mode, program, model);
        uint shortIp = scalar.Start + 256;
        uint linearPage = (scalar.Cpu.GetSegmentBase(Segment.Cs) + scalar.Start) & ~4095u;
        uint emptyIp = linearPage + 4094 - scalar.Cpu.GetSegmentBase(Segment.Cs);
        bool address32 = !scalar.Cpu.Virtual8086 && scalar.Cpu.GetSegmentCache(Segment.Cs).Default32;
        foreach (var rig in new[] { scalar, decoded })
        {
            uint basis = rig.Cpu.GetSegmentBase(Segment.Cs);
            new byte[] { 0x4b, 0xf4 }.CopyTo(rig.Memory.Ram, (int)(basis + shortIp));
            new byte[] { 0xf6, address32 ? (byte)0x05 : (byte)0x06 }.CopyTo(rig.Memory.Ram, (int)(basis + emptyIp));
        }
        Compare(scalar, decoded, 64);
        foreach (var rig in new[] { scalar, decoded }) rig.Cpu.Eip = shortIp;
        Compare(scalar, decoded, 1);
        foreach (var rig in new[] { scalar, decoded }) rig.Cpu.Eip = rig.Start;
        Compare(scalar, decoded, 64);
        foreach (var rig in new[] { scalar, decoded }) rig.Cpu.Eip = emptyIp;
        Compare(scalar, decoded, 0);
        foreach (var rig in new[] { scalar, decoded }) rig.Cpu.Eip = rig.Start;
        Compare(scalar, decoded, 64);
        Ownership(decoded.Cpu);
    }

    /// <summary>Registers actual real, protected, paged and V86 contexts on both CPU models.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var modelValue in new[] { X86CpuModel.Intel486Dx, X86CpuModel.IntelP54cB5Subset })
        for (int modeValue = 0; modeValue < 7; modeValue++)
        {
            var model = modelValue;
            int mode = modeValue;
            test($"DecodedScratch immutable futures model{model} mode{mode}", () => Case(mode, model));
        }
    }
}
