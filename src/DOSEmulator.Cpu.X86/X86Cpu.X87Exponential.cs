// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes F2XM1 using the independent integer interval kernel and its documented input/exception contract.</summary>
    private bool ExecuteFpuExponential(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm != 0xf0) return false;
        RecordFpuInstruction(opcode, modrm);
        if (EmptyFpu(FpuTop))
        {
            if (FpuStackFault(false)) FpuSet(FpuTop, Float80Value.Indefinite);
            return true;
        }
        var result = Float80Math.Exp2MinusOne(GetFpuRegister(0), FpuControlWord);
        FpuNumericStatus(result.Exceptions, result.RoundedUp);
        if (result.Commit) FpuSet(FpuTop, result.Value);
        return true;
    }
}
