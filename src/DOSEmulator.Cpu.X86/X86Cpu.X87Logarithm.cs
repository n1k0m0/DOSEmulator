// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FYL2X/FYL2XP1 and coordinates the logarithmic product result with its conditional pop.</summary>
    private bool ExecuteFpuLogarithm(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm is not (0xf1 or 0xf9)) return false;
        RecordFpuInstruction(opcode, modrm);
        int target = (FpuTop + 1) & 7;
        if (EmptyFpu(FpuTop) || EmptyFpu(target))
        {
            if (FpuStackFault(false)) { FpuSet(target, Float80Value.Indefinite); FpuPop(); }
            return true;
        }
        var result = Float80Math.Log2Multiply(GetFpuRegister(0), GetFpuRegister(1), modrm == 0xf9, FpuControlWord);
        FpuNumericStatus(result.Exceptions, result.RoundedUp);
        if (result.Commit) { FpuSet(target, result.Value); FpuPop(); }
        return true;
    }
}
