// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes one actual FPREM/FPREM1 reduction step and publishes C2/quotient bits without hiding iterations.</summary>
    private bool ExecuteFpuRemainder(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm is not (0xf8 or 0xf5)) return false;
        RecordFpuInstruction(opcode, modrm);
        if (EmptyFpu(FpuTop) || EmptyFpu((FpuTop + 1) & 7))
        {
            FpuStatusWord &= 0xfbff;
            if (FpuStackFault(false)) FpuSet(FpuTop, Float80Value.Indefinite);
            return true;
        }
        var result = Float80Math.Remainder(GetFpuRegister(0), GetFpuRegister(1), modrm == 0xf5, FpuControlWord);
        FpuNumericStatus(result.Exceptions, false);
        FpuStatusWord = (ushort)((FpuStatusWord & ~result.ConditionMask) | result.Conditions);
        if (result.Commit) FpuSet(FpuTop, result.Value);
        return true;
    }
}
