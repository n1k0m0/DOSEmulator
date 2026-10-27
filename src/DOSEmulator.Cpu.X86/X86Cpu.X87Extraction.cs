// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes FXTRACT with a checked push slot and two coordinated raw 80-bit results.</summary>
    private bool ExecuteFpuExtraction(byte opcode, byte modrm)
    {
        if (opcode != 0xd9 || modrm != 0xf4) return false;
        RecordFpuInstruction(opcode, modrm);
        int oldTop = FpuTop, newTop = (oldTop - 1) & 7;
        // The missing source wins over a simultaneously occupied push slot.
        // Check both destinations before replacing the old source: unmasked
        // stack/operand exceptions must preserve TOP, tags and raw registers.
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
        var result = Float80Math.Extract(_fpuRegisters[oldTop], FpuControlWord);
        FpuNumericStatus(result.Exceptions, false);
        if (result.Commit)
        {
            FpuSet(oldTop, result.Exponent);
            FpuSet(newTop, result.Significand);
            FpuTopTo(newTop);
        }
        return true;
    }
}
