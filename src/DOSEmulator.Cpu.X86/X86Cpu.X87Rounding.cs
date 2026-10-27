// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FRNDINT/FSQRT with their control-word and masked/unmasked commit rules.</summary>
    private bool ExecuteFpuRounding(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm is not (0xfa or 0xfc)) return false;
        RecordFpuInstruction(opcode, modrm);
        if (!FpuReadTop(0, out var value)) return true;
        var result = modrm == 0xfa ? Float80Math.SquareRoot(value, FpuControlWord) : Float80Math.RoundToInteger(value, FpuControlWord);
        FpuNumericStatus(result.Exceptions, result.RoundedUp);
        if (result.Commit) FpuSet(FpuTop, result.Value);
        return true;
    }
}
