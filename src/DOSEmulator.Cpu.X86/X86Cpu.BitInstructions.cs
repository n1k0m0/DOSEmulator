// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Implements BT/BTS/BTR/BTC and validates writable locked forms before data effects.</summary>
    private void BitTest(byte opcode)
    {
        byte modrm = FetchByte(); int operation = opcode == 0xba ? (modrm >> 3) & 7 : 4 + ((opcode >> 3) & 3);
        if (operation < 4) throw new CpuFaultException(6);
        ValidateLockOperand(modrm, operation != 4);
        Operand operand = DecodeOperand(modrm); int width = Width, bits = width * 8;
        uint index = opcode == 0xba ? FetchByte() : ReadRegister((modrm >> 3) & 7, width);
        if (!operand.IsRegister && opcode != 0xba)
        {
            long displacement = Signed(index, width) >> (width == 2 ? 4 : 5);
            uint offset = unchecked(operand.Offset + (uint)(displacement * width)) & Mask(_address32 ? 4 : 2);
            operand = operand with { Offset = offset, Address = unchecked(GetSegmentBase(operand.Segment) + offset) };
        }
        uint bit = 1u << (int)(index & (uint)(bits - 1));
        if (!operand.IsRegister) { CheckOperand(operand, width, operation != 4); CheckAlignment(operand.Address, 4); }
        uint value = Read(operand, width, operation != 4);
        SetFlag(Carry, (value & bit) != 0);
        if (operation != 4) Write(operand, width, operation switch { 5 => value | bit, 6 => value & ~bit, _ => value ^ bit });
    }
    /// <summary>Executes SHLD/SHRD using masked counts and the reference flag contract.</summary>
    private void DoubleShift(byte opcode)
    {
        byte modrm = FetchByte(); Operand operand = DecodeOperand(modrm); int width = Width, bits = width * 8;
        int count = (opcode & 1) == 0 ? FetchByte() & 31 : (int)ReadRegister(1, 1) & 31;
        uint original = Read(operand, width, true), source = ReadRegister((modrm >> 3) & 7, width);
        if (count == 0) return;
        bool right = (opcode & 8) != 0;
        ulong pair = right ? ((ulong)source << bits) | original : ((ulong)original << bits) | source;
        uint value = right ? (uint)(pair >> count) : (uint)((pair << count) >> bits);
        bool carry = right ? ((pair >> (count - 1)) & 1) != 0 : ((pair >> (bits * 2 - count)) & 1) != 0;
        Write(operand, width, value); SetFlag(Carry, carry); SetSzp(value, width);
        if (count == 1) SetFlag(Overflow, ((original ^ value) & (1u << (bits - 1))) != 0);
        // 16-bit counts above 16 and OF/AF for the other counts are undefined;
        // this interpreter provides deterministic results without a stepping claim.
    }
    /// <summary>Finds the first or last set bit and preserves the documented zero-source behavior.</summary>
    private void BitScan(bool reverse)
    {
        byte modrm = FetchByte(); Operand operand = DecodeOperand(modrm); uint value = Read(operand, Width);
        SetFlag(Zero, value == 0);
        if (value != 0) WriteRegister((modrm >> 3) & 7, Width, (uint)(reverse ? 31 - System.Numerics.BitOperations.LeadingZeroCount(value) : System.Numerics.BitOperations.TrailingZeroCount(value)));
    }
}
