// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FSCALE while retaining ST1 and distinguishing exponent-response commits from input-preserving errors.</summary>
    private bool ExecuteFpuScaling(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm != 0xfd) return false;
        RecordFpuInstruction(opcode, modrm);
        if (EmptyFpu(FpuTop) || EmptyFpu((FpuTop + 1) & 7))
        {
            if (FpuStackFault(false)) FpuSet(FpuTop, Float80Value.Indefinite);
            return true;
        }
        var result = Float80Math.ScaleByPowerOfTwo(GetFpuRegister(0), GetFpuRegister(1), FpuControlWord);
        FpuNumericStatus(result.Exceptions, result.RoundedUp);
        if (result.Commit) FpuSet(FpuTop, result.Value);
        return true;
    }
}
