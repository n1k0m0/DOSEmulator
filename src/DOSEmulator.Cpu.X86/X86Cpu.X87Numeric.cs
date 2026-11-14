// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Merges exception flags and the selected rounding direction into x87 status and its error summary.</summary>
    private void FpuNumericStatus(ushort exceptions, bool roundedUp)
    {
        FpuStatusWord = (ushort)((FpuStatusWord & ~0x200) | exceptions | (roundedUp ? 0x200 : 0));
        FpuSummary();
    }
    /// <summary>Reads a validated 16-, 32- or 64-bit little-endian integer operand buffer.</summary>
    private static ulong FpuBits(ReadOnlySpan<byte> bytes) => bytes.Length switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes)
    };
    /// <summary>Converts a signed width-selected integer operand into an exact raw extended value.</summary>
    private static Float80Value FpuInteger(ulong bits, int size) => Float80Math.FromInteger(size switch
    { 2 => (short)bits, 4 => (int)bits, _ => (long)bits });
    /// <summary>Executes supported numeric transfers/arithmetic and separates precommit exceptions from committed stack changes.</summary>
    private bool ExecuteFpuNumeric(byte opcode, byte modrm)
    {
        int group = modrm >> 3 & 7, logical = modrm & 7;
        if (modrm < 0xc0 && (opcode is 0xd9 or 0xdd && group is 0 or 2 or 3 ||
            opcode == 0xdb && group is 0 or 2 or 3 || opcode == 0xdf && group is 0 or 2 or 3 or 5 or 7))
        {
            bool integer = opcode is 0xdb or 0xdf;
            bool load = group == 0 || opcode == 0xdf && group == 5;
            bool pop = group is 3 or 7;
            int size = opcode switch { 0xd9 => 4, 0xdd => 8, 0xdb => 4, _ => group is 5 or 7 ? 8 : 2 };
            Operand operand = DecodeOperand(modrm);
            Span<uint> physicalStorage = stackalloc uint[8];
            Span<uint> physical = physicalStorage[..size];
            FpuMemory(operand, !load, size, physical);
            if (load)
            {
                Span<byte> inputStorage = stackalloc byte[8];
                Span<byte> input = inputStorage[..size];
                FpuReadBytes(operand, physical, input);
                ulong bits = FpuBits(input);
                RecordFpuInstruction(opcode, modrm, operand);
                int target = (FpuTop - 1) & 7;
                if (!EmptyFpu(target)) { FpuPush(Float80Value.Indefinite); return true; }
                var result = integer ? new Float80Result(FpuInteger(bits, size)) : Float80Math.LoadBinary(bits, size * 8, FpuControlWord);
                FpuNumericStatus(result.Exceptions, false);
                if (result.Commit) FpuPush(result.Value);
            }
            else
            {
                RecordFpuInstruction(opcode, modrm, operand);
                if (!FpuReadTop(0, out var value)) return true;
                var result = integer ? Float80Math.StoreInteger(value, size * 8, FpuControlWord) : Float80Math.StoreBinary(value, size * 8, FpuControlWord);
                FpuNumericStatus(result.Exceptions, result.RoundedUp);
                if (result.Commit)
                {
                    Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, result.Bits);
                    FpuWriteBytes(operand, physical, bytes[..size]); if (pop) FpuPop();
                }
            }
            return true;
        }
        bool operationGroup = group is 0 or 1 or 4 or 5 or 6 or 7;
        bool memory = modrm < 0xc0;
        if (!operationGroup || (memory ? opcode is not (0xd8 or 0xda or 0xdc or 0xde) : opcode is not (0xd8 or 0xdc or 0xde))) return false;
        var operation = group switch { 0 => Float80Operation.Add, 1 => Float80Operation.Multiply, 4 or 5 => Float80Operation.Subtract, _ => Float80Operation.Divide };
        bool reverse = memory || opcode == 0xd8 ? group is 5 or 7 : group is 4 or 6;
        bool popResult = !memory && opcode == 0xde;
        int destination = (FpuTop + (!memory && opcode != 0xd8 ? logical : 0)) & 7;
        Float80Value left = _fpuRegisters[destination], right;
        Float80Result calculated;
        if (memory)
        {
            bool integer = opcode is 0xda or 0xde;
            int size = opcode == 0xdc ? 8 : opcode == 0xde ? 2 : 4;
            Operand operand = DecodeOperand(modrm);
            Span<uint> physicalStorage = stackalloc uint[8];
            Span<uint> physical = physicalStorage[..size];
            FpuMemory(operand, false, size, physical);
            Span<byte> inputStorage = stackalloc byte[8];
            Span<byte> input = inputStorage[..size];
            FpuReadBytes(operand, physical, input);
            ulong bits = FpuBits(input);
            RecordFpuInstruction(opcode, modrm, operand);
            if (EmptyFpu(destination))
            { if (FpuStackFault(false)) FpuSet(destination, Float80Value.Indefinite); return true; }
            if (integer)
            {
                right = FpuInteger(bits, size);
                calculated = reverse ? Float80Math.Calculate(right, left, operation, FpuControlWord) : Float80Math.Calculate(left, right, operation, FpuControlWord);
            }
            else calculated = Float80Math.CalculateBinaryMemory(left, bits, size * 8, operation, FpuControlWord, reverse);
        }
        else
        {
            int source = (FpuTop + (opcode == 0xd8 ? logical : 0)) & 7;
            RecordFpuInstruction(opcode, modrm);
            if (EmptyFpu(destination) || EmptyFpu(source))
            {
                if (FpuStackFault(false)) { FpuSet(destination, Float80Value.Indefinite); if (popResult) FpuPop(); }
                return true;
            }
            right = _fpuRegisters[source];
            calculated = reverse ? Float80Math.Calculate(right, left, operation, FpuControlWord) : Float80Math.Calculate(left, right, operation, FpuControlWord);
        }
        FpuNumericStatus(calculated.Exceptions, calculated.RoundedUp);
        if (calculated.Commit) { FpuSet(destination, calculated.Value); if (popResult) FpuPop(); }
        return true;
    }
}
