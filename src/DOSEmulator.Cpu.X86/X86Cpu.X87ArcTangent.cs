// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FPATAN with the checked ST1 target and exception-dependent pop.</summary>
    private bool ExecuteFpuArcTangent(byte opcode,byte modrm)
    {
        if (opcode!=0xd9 || modrm!=0xf3) return false;
        RecordFpuInstruction(opcode,modrm);
        int target=(FpuTop+1)&7;
        if (EmptyFpu(FpuTop) || EmptyFpu(target))
        {
            if (FpuStackFault(false)) { FpuSet(target,Float80Value.Indefinite);FpuPop(); }
            return true;
        }
        var result=Float80Math.ArcTangent(GetFpuRegister(0),GetFpuRegister(1),FpuControlWord);
        FpuNumericStatus(result.Exceptions,result.RoundedUp);
        if (result.Commit) { FpuSet(target,result.Value);FpuPop(); }
        return true;
    }
}
