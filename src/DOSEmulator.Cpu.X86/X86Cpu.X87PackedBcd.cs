// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Executes packed BCD load/store with checked memory and separate conversion/pop commit decisions.</summary>
    private bool ExecuteFpuPackedBcd(byte opcode, byte modrm)
    {
        int group = modrm >> 3 & 7;
        if (opcode != 0xdf || modrm >= 0xc0 || group is not (4 or 6)) return false;
        bool store = group == 6;
        Operand operand = DecodeOperand(modrm);
        uint[] physical = FpuMemory(operand, 10, store, 8);
        if (!store)
        {
            byte[] bytes = FpuReadBytes(operand, physical);
            RecordFpuInstruction(opcode, modrm, operand);
            FpuPush(Float80Math.LoadPackedBcd(bytes));
        }
        else
        {
            RecordFpuInstruction(opcode, modrm, operand);
            if (!FpuReadTop(0, out var value)) return true;
            var result = Float80Math.StorePackedBcd(value, FpuControlWord);
            FpuNumericStatus(result.Exceptions, result.RoundedUp);
            if (result.Commit)
            {
                Span<byte> bytes = stackalloc byte[10];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, result.Low);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], result.High);
                FpuWriteBytes(operand, physical, bytes);
                FpuPop();
            }
        }
        return true;
    }
}
