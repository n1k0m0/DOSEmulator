// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Commits both FSINCOS results and its push together, or preserves both sources on noncommitting errors.</summary>
    private bool ExecuteFpuSinCosPair(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm != 0xfb) return false;
        RecordFpuInstruction(opcode, modrm);
        FpuStatusWord &= unchecked((ushort)~0x400);
        int oldTop = FpuTop, newTop = (oldTop - 1) & 7;
        bool underflow = EmptyFpu(oldTop);
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
        var result = Float80Math.SinCosPair(_fpuRegisters[oldTop], FpuControlWord);
        FpuNumericStatus(result.Exceptions, result.RoundedUp);
        if (result.OutOfRange) FpuStatusWord |= 0x400;
        if (result.Commit)
        {
            FpuSet(oldTop, result.Sine);
            FpuSet(newTop, result.Cosine);
            FpuTopTo(newTop);
        }
        return true;
    }
}
