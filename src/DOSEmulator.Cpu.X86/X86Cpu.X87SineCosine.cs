// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FSIN/FCOS with range/C2 checks and exception-dependent preservation of the original source.</summary>
    private bool ExecuteFpuSineCosine(byte opcode,byte modrm)
    {
        if (opcode!=0xd9 || modrm is not (0xfe or 0xff)) return false;
        RecordFpuInstruction(opcode,modrm);
        FpuStatusWord&=unchecked((ushort)~0x400);
        int top=FpuTop;
        if (EmptyFpu(top))
        {
            if (FpuStackFault(false)) FpuSet(top,Float80Value.Indefinite);
            return true;
        }
        var result=Float80Math.SineCosine(_fpuRegisters[top],modrm==0xff,FpuControlWord);
        FpuNumericStatus(result.Number.Exceptions,result.Number.RoundedUp);
        if (result.OutOfRange) FpuStatusWord|=0x400;
        if (result.Number.Commit) FpuSet(top,result.Number.Value);
        return true;
    }
}
