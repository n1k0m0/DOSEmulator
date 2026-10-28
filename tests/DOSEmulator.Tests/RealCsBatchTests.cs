// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Text.Json;
using DOSEmulator.Core;
using DOSEmulator.Cpu.X86;

/// <summary>Real-mode inherited CS data tags must not disable an otherwise scalar-equivalent batch; PE, limits and unusable caches stay guarded.</summary>
internal static class RealCsBatchTests
{
    /// <summary>Own CPU and complete physical RAM used for scalar-versus-batch comparison.</summary>
    private sealed record Rig(X86Cpu Cpu, MemoryBus Memory);
    /// <summary>Requires actual retirement or complete unchanged scalar CPU/RAM state.</summary>
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    /// <summary>Installs only controlled hidden-cache fixture values through the existing explicit state API.</summary>
    private static void Cs(Rig rig, byte access, byte flags, uint limit, bool usable = true, bool pe = false)
    {
        StateBlock state = rig.Cpu.SaveState();
        var caches = state.Fields["_segmentCaches"].Deserialize<SegmentDescriptorCache[]>(StateValues.Json)!;
        caches[(int)Segment.Cs] = caches[(int)Segment.Cs] with { Access = access, Flags = flags, Limit = limit, Usable = usable };
        state.Fields["_segmentCaches"] = JsonSerializer.SerializeToElement(caches, StateValues.Json);
        if (pe) state.Fields["Cr0"] = JsonSerializer.SerializeToElement(rig.Cpu.Cr0 | 1, StateValues.Json);
        rig.Cpu.LoadState(state);
    }
    /// <summary>Builds an own real-mode RAM program, with a distinct code/data page and an actual ordinary warmup instruction.</summary>
    private static Rig Make(X86CpuModel model, bool wide, byte[] code)
    {
        var memory = new MemoryBus(2 * 1024 * 1024); var clock = new DeviceScheduler();
        var cpu = model == X86CpuModel.Intel486Dx ? new X86Cpu(memory, new(), model) : new X86Cpu(memory, new(), model, clock, 100000000);
        cpu.SetSegment(Segment.Cs, 0); cpu.SetSegment(Segment.Ds, 0); cpu.SetSegment(Segment.Ss, 0);
        cpu.SetRegister(Register.Sp, 0x9000); cpu.SetRegister(Register.Ax, 0xa5d37219); cpu.SetRegister(Register.Bx, 0x12bc4488);
        cpu.Eip = 0x2000; memory.Ram[0x2000] = 0x90; code.CopyTo(memory.Ram, 0x2001); memory.Ram[0x2001 + code.Length] = 0xf4; memory.WriteDword(0x7000, 0x8fa5c127);
        cpu.Step(); var rig = new Rig(cpu, memory); Cs(rig, 0x93, wide ? (byte)4 : (byte)0, wide ? uint.MaxValue : 0xffff); return rig;
    }
    /// <summary>Encodes independent byte and word/dword 80/81/83 operations followed by a real read-only CMP and NOP.</summary>
    private static byte[] Program(bool wide) => wide
        ? [0x80,0x05,0,0x70,0,0,0x7f, 0x81,0x35,0,0x70,0,0,0x56,0xa5,0x83,0x9f,
           0x83,0x05,0,0x70,0,0,0x80, 0x83,0x3d,0,0x70,0,0,0, 0x90]
        : [0x80,0x06,0,0x70,0x7f, 0x81,0x36,0,0x70,0x56,0xa5, 0x83,0x06,0,0x70,0x80, 0x83,0x3e,0,0x70,0, 0x90];
    /// <summary>The compatibility batch's existing MOV/register-ALU memory subset, independent of the decoded-only immediate-memory extension.</summary>
    private static byte[] LegacyProgram(bool wide) => wide
        ? [0xb8,0x34,0x12,0x56,0x78, 0xa3,0,0x70,0,0, 0x01,0x05,0,0x70,0,0, 0x3b,0x05,0,0x70,0,0, 0x90]
        : [0xb8,0x34,0x12, 0xa3,0,0x70, 0x01,0x06,0,0x70, 0x3b,0x06,0,0x70, 0x90];
    /// <summary>Invokes the unchanged public API selected by this control.</summary>
    private static int Execute(Rig rig, bool decoded, int count = 64) => decoded ? rig.Cpu.TryRunDecodedBatch(count) : rig.Cpu.TryRunIntegerBatch(count);
    /// <summary>Compares every CPU field and complete RAM, including counters/checkpoints and retained hidden access tags.</summary>
    private static void Equal(Rig a, Rig b)
    {
        var left = a.Cpu.SaveState(); var right = b.Cpu.SaveState();
        Check(left.Fields.Keys.SequenceEqual(right.Fields.Keys), "RealCsBatch field set changed.");
        foreach (string field in left.Fields.Keys) Check(left.Fields[field].GetRawText() == right.Fields[field].GetRawText(), "RealCsBatch field differs: " + field);
        Check(a.Memory.Ram.AsSpan().SequenceEqual(b.Memory.Ram), "RealCsBatch whole RAM differs.");
    }
    /// <summary>Warms only derived plans and restores all authoritative bytes before comparing execution.</summary>
    private static void Prime(Rig rig, bool decoded)
    { StateBlock state = rig.Cpu.SaveState(); byte[] ram = rig.Memory.Ram.ToArray(); Execute(rig, decoded); ram.CopyTo(rig.Memory.Ram, 0); rig.Cpu.LoadState(state); }
    /// <summary>Checks actual returned instructions against the original Step implementation.</summary>
    private static void Compare(Rig a, Rig b, bool decoded, int expected)
    {
        int count = Execute(b, decoded); Check(count == expected, "RealCsBatch expected " + expected + " retired elements, got " + count);
        for (int i = 0; i < count; i++) Check(a.Cpu.Step() == 4, "RealCsBatch changed nominal clocks."); Equal(a, b);
        Check(b.Cpu.GetSegmentCache(Segment.Cs).Access == a.Cpu.GetSegmentCache(Segment.Cs).Access, "RealCsBatch rewrote inherited CS attributes.");
    }
    /// <summary>Registers both CPU models and batch APIs, retained real widths/tags, stack effects and unchanged refusal cases.</summary>
    public static void AddTo(Action<string, Action> test)
    {
        foreach (var modelValue in new[] { X86CpuModel.Intel486Dx, X86CpuModel.IntelP54cB5Subset })
        foreach (bool decodedValue in new[] { false, true })
        {
            var model = modelValue; bool decoded = decodedValue;
            foreach (bool wideValue in new[] { false, true })
            {
                bool wide = wideValue;
                foreach (bool warmValue in new[] { false, true })
                {
                    bool warm = warmValue;
                    test($"RealCsBatch actual inherited data CS memory model={model} decoded={decoded} wide={wide} warm={warm}", () =>
                    {
                        byte[] code = decoded ? Program(wide) : LegacyProgram(wide);
                        var a = Make(model, wide, code); var b = Make(model, wide, code); if (warm) Prime(b, decoded);
                        Compare(a, b, decoded, 5); Check(b.Cpu.GetSegmentCache(Segment.Cs).Access == 0x93, "Real-mode data tag was synthesized as code.");
                    });
                }
                test($"RealCsBatch short inherited limit refuses incomplete operand model={model} decoded={decoded} wide={wide}", () =>
                {
                    byte[] code = decoded ? Program(wide) : LegacyProgram(wide);
                    var a = Make(model, wide, code); var b = Make(model, wide, code); Prime(b, decoded);
                    foreach (var rig in new[] { a, b }) Cs(rig, 0x93, wide ? (byte)4 : (byte)0, 0x2002);
                    Compare(a, b, decoded, 0);
                });
            }
            test($"RealCsBatch unusable inherited CS remains refused model={model} decoded={decoded}", () =>
            {
                var a = Make(model, false, [0x90,0x40]); var b = Make(model, false, [0x90,0x40]);
                foreach (var rig in new[] { a, b }) Cs(rig, 0x93, 0, 0xffff, false); Compare(a, b, decoded, 0);
            });
            test($"RealCsBatch stack keeps real data-tagged CS and full SP model={model} decoded={decoded}", () =>
            { var a = Make(model, false, [0x50,0x5b,0x90]); var b = Make(model, false, [0x50,0x5b,0x90]); Compare(a, b, decoded, decoded ? 3 : 0); });
            test($"RealCsBatch inherited expand-down bounds retain scalar behavior model={model} decoded={decoded}", () =>
            {
                var a = Make(model, false, [0x90,0x40,0x90]); var b = Make(model, false, [0x90,0x40,0x90]);
                foreach (var rig in new[] { a, b }) Cs(rig, 0x97, 0, 0x1fff); Compare(a, b, decoded, 3);
            });
        }
        foreach (bool decodedValue in new[] { false, true })
        {
            bool decoded = decodedValue;
            test($"RealCsBatch protected data-tagged CS remains refused decoded={decoded}", () =>
            {
                var a = Make(X86CpuModel.IntelP54cB5Subset, true, [0x90,0x40]); var b = Make(X86CpuModel.IntelP54cB5Subset, true, [0x90,0x40]);
                foreach (var rig in new[] { a, b }) Cs(rig, 0x93, 4, uint.MaxValue, pe: true); Compare(a, b, decoded, 0);
            });
        }
    }
}
