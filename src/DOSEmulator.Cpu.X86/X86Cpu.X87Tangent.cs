// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FPTAN with range/C2 checks and one coordinated tangent/one stack push.</summary>
    private bool ExecuteFpuTangent(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm != 0xf2) return false;
        RecordFpuInstruction(opcode, modrm);
        FpuStatusWord &= unchecked((ushort)~0x400);
        int oldTop = FpuTop, newTop = (oldTop-1)&7;
        bool underflow = EmptyFpu(oldTop);
        // Source/stack faults precede range testing. Masked faults produce
        // two indefinites; even a quiet NaN is pushed twice, rather than 1.
        if (underflow || !EmptyFpu(newTop))
        {
            if (FpuStackFault(!underflow))
            {
                FpuSet(oldTop, Float80Value.Indefinite);
                FpuSet(newTop, Float80Value.Indefinite);
                FpuTopTo(newTop);
            }
            return true;
        }
        var result = Float80Math.Tangent(_fpuRegisters[oldTop], FpuControlWord);
        FpuNumericStatus(result.Number.Exceptions, result.Number.RoundedUp);
        if (result.OutOfRange) FpuStatusWord |= 0x400;
        if (result.Number.Commit)
        {
            FpuSet(oldTop, result.Number.Value);
            FpuSet(newTop, result.Pushed);
            FpuTopTo(newTop);
        }
        return true;
    }
}
