namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private sealed class CpuFaultException(byte vector) : Exception { public byte Vector { get; } = vector; }
    private static long Signed(uint value, int width) => width switch { 1 => (sbyte)value, 2 => (short)value, _ => (int)value };

    private bool ExecuteExtended(byte opcode)
    {
        int w = Width;
        switch (opcode)
        {
            case 0x0f: ExecuteTwoByte(); return true;
            case 0x27: case 0x2f: // DAA / DAS
            {
                byte old = (byte)ReadRegister(0, 1); bool oldCarry = (Eflags & Carry) != 0;
                int a = old; bool adjustLow = (a & 15) > 9 || (Eflags & Auxiliary) != 0;
                if (adjustLow) a += opcode == 0x27 ? 6 : -6;
                bool adjustHigh = old > 0x99 || oldCarry;
                if (adjustHigh) a += opcode == 0x27 ? 0x60 : -0x60;
                WriteRegister(0, 1, (uint)a); SetFlag(Auxiliary, adjustLow); SetFlag(Carry, adjustHigh); SetSzp((uint)a, 1); return true;
            }
            case 0x37: case 0x3f: // AAA / AAS
            {
                bool adjust = (ReadRegister(0, 1) & 15) > 9 || (Eflags & Auxiliary) != 0;
                if (adjust) WriteRegister(0, 2, unchecked(ReadRegister(0, 2) + (opcode == 0x37 ? 0x106u : unchecked((uint)-0x106))));
                WriteRegister(0, 1, ReadRegister(0, 1) & 15); SetFlag(Auxiliary, adjust); SetFlag(Carry, adjust); return true;
            }
            case 0x69: case 0x6b:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); long a = Signed(Read(rm, w), w);
                long b = opcode == 0x6b ? (sbyte)FetchByte() : Signed(Immediate(w), w);
                long result = a * b; WriteRegister((m >> 3) & 7, w, unchecked((uint)result));
                bool overflow = result != Signed(unchecked((uint)result), w); SetFlag(Carry, overflow); SetFlag(Overflow, overflow); return true;
            }
            case 0x9a:
            {
                uint target = Immediate(w); ushort segment = (ushort)Immediate(2); CheckLimit(target, 1);
                Push(GetSegment(Segment.Cs), w); Push(Eip, w); SetSegment(Segment.Cs, segment); Eip = target; return true;
            }
            case 0x9b: return true; // WAIT with an idle, not-yet-executing FPU has no pending exception.
            case 0xc0: case 0xc1: case 0xd0: case 0xd1: case 0xd2: case 0xd3:
            {
                int size = (opcode & 1) == 0 ? 1 : w; byte m = FetchByte(); Operand rm = DecodeOperand(m);
                int count = opcode is 0xc0 or 0xc1 ? FetchByte() : opcode is 0xd0 or 0xd1 ? 1 : (int)ReadRegister(1, 1);
                Shift(rm, (m >> 3) & 7, size, count); return true;
            }
            case 0xc4: case 0xc5: LoadFarPointer(opcode == 0xc4 ? Segment.Es : Segment.Ds, w); return true;
            case 0xc8:
            {
                uint allocation = Immediate(2); int nesting = FetchByte() & 31;
                Push(ReadRegister(5, w), w); uint frame = ReadRegister(4, 2);
                if (nesting > 0)
                {
                    uint bp = ReadRegister(5, 2);
                    for (int i = 1; i < nesting; i++) { bp = (bp - (uint)w) & 0xffff; CheckLimit(bp, w); Push(ReadMemory(GetSegmentBase(Segment.Ss) + bp, w), w); }
                    Push(frame, w);
                }
                WriteRegister(5, w, frame); WriteRegister(4, 2, ReadRegister(4, 2) - allocation); return true;
            }
            case 0xc9: WriteRegister(4, 2, ReadRegister(5, 2)); WriteRegister(5, w, Pop(w)); return true;
            case 0xce: if ((Eflags & Overflow) != 0) EnterRealModeInterrupt(4); return true;
            case 0xd4:
            {
                uint radix = FetchByte(); if (radix == 0) throw new CpuFaultException(0);
                uint value = ReadRegister(0, 1); WriteRegister(4, 1, value / radix); WriteRegister(0, 1, value % radix); SetSzp(value % radix, 1); return true;
            }
            case 0xd5:
            {
                uint radix = FetchByte(), value = (ReadRegister(4, 1) * radix + ReadRegister(0, 1)) & 255;
                WriteRegister(0, 2, value); SetSzp(value, 1); return true;
            }
            case 0xd7:
            {
                uint offset = unchecked(ReadRegister(3, _address32 ? 4 : 2) + ReadRegister(0, 1)); if (!_address32) offset &= 0xffff;
                CheckLimit(offset, 1); WriteRegister(0, 1, ReadMemory(GetSegmentBase(_override ?? Segment.Ds) + offset, 1)); return true;
            }
            case 0xf6: case 0xf7: MultiplyDivide(opcode == 0xf6 ? 1 : w); return true;
            default: return false;
        }
    }

    private void LoadFarPointer(Segment segment, int width)
    {
        byte m = FetchByte(); Operand rm = DecodeOperand(m);
        if (rm.IsRegister) throw Unsupported("Far pointer requires a memory operand.");
        CheckLimit(rm.Offset, width + 2); uint offset = Read(rm, width); ushort selector = _memory.ReadWord(rm.Address + (uint)width);
        WriteRegister((m >> 3) & 7, width, offset); SetSegment(segment, selector);
        if (segment == Segment.Ss) _interruptShadow = 1;
    }

    private void Shift(Operand rm, int operation, int width, int rawCount)
    {
        int count = rawCount & 31;
        if (operation is 2 or 3 && width < 4) count %= width * 8 + 1;
        if (count == 0) return;
        uint mask = Mask(width), sign = 1u << (width * 8 - 1), original = Read(rm, width), value = original;
        bool carry = (Eflags & Carry) != 0;
        for (int i = 0; i < count; i++)
        {
            bool high = (value & sign) != 0, low = (value & 1) != 0;
            value = operation switch
            {
                0 => ((value << 1) | (high ? 1u : 0)) & mask,
                1 => (value >> 1) | (low ? sign : 0),
                2 => ((value << 1) | (carry ? 1u : 0)) & mask,
                3 => (value >> 1) | (carry ? sign : 0),
                4 or 6 => (value << 1) & mask,
                5 => value >> 1,
                7 => (value >> 1) | (value & sign),
                _ => throw new InvalidOperationException()
            };
            carry = operation is 0 or 2 or 4 or 6 ? high : low;
        }
        Write(rm, width, value); SetFlag(Carry, carry);
        if (operation >= 4) SetSzp(value, width);
        if (count == 1)
        {
            bool overflow = operation switch
            {
                0 or 2 or 4 or 6 => ((value & sign) != 0) ^ carry,
                1 or 3 => ((value & sign) != 0) ^ ((value & (sign >> 1)) != 0),
                5 => (original & sign) != 0,
                _ => false
            };
            SetFlag(Overflow, overflow);
        }
    }

    private void MultiplyDivide(int width)
    {
        byte m = FetchByte(); int operation = (m >> 3) & 7; Operand rm = DecodeOperand(m); uint value = Read(rm, width);
        if (operation == 0) { Alu(4, value, Immediate(width), width); return; }
        if (operation == 2) { Write(rm, width, ~value); return; }
        if (operation == 3) { Write(rm, width, Alu(5, 0, value, width)); return; }
        if (operation is 4 or 5)
        {
            ulong product; bool overflow;
            if (operation == 4)
            {
                product = (ulong)ReadRegister(0, width) * value;
                overflow = (product >> (width * 8)) != 0;
            }
            else
            {
                long signedProduct = Signed(ReadRegister(0, width), width) * Signed(value, width);
                product = unchecked((ulong)signedProduct); overflow = signedProduct != Signed((uint)product, width);
            }
            if (width == 1) WriteRegister(0, 2, (uint)product);
            else { WriteRegister(0, width, (uint)product); WriteRegister(2, width, (uint)(product >> (width * 8))); }
            SetFlag(Carry, overflow); SetFlag(Overflow, overflow); return;
        }
        if (operation is 6 or 7)
        {
            if (value == 0) throw new CpuFaultException(0);
            ulong dividend = width == 1 ? ReadRegister(0, 2) : (ulong)ReadRegister(2, width) << (width * 8) | ReadRegister(0, width);
            ulong quotient, remainder;
            if (operation == 6)
            {
                quotient = dividend / value; remainder = dividend % value;
                if (quotient > Mask(width)) throw new CpuFaultException(0);
            }
            else
            {
                long signedDividend = width switch { 1 => (short)dividend, 2 => (int)dividend, _ => unchecked((long)dividend) };
                long divisor = Signed(value, width);
                if (signedDividend == long.MinValue && divisor == -1) throw new CpuFaultException(0);
                long q = signedDividend / divisor, r = signedDividend % divisor;
                if (q != Signed(unchecked((uint)q), width)) throw new CpuFaultException(0);
                quotient = unchecked((ulong)q); remainder = unchecked((ulong)r);
            }
            WriteRegister(0, width, (uint)quotient);
            WriteRegister(width == 1 ? 4 : 2, width, (uint)remainder); return;
        }
        throw Unsupported("Reserved group-3 operation.");
    }

    private void ExecuteTwoByte()
    {
        byte opcode = FetchByte(); int w = Width;
        if (opcode is >= 0x80 and <= 0x8f)
        {
            int displacement = w == 2 ? (short)Immediate(2) : unchecked((int)Immediate(4));
            if (Condition(opcode & 15)) Eip = NearTarget(unchecked(Eip + (uint)displacement)); return;
        }
        if (opcode is >= 0x90 and <= 0x9f) { byte m = FetchByte(); Operand rm = DecodeOperand(m); Write(rm, 1, Condition(opcode & 15) ? 1u : 0); return; }
        if (opcode is >= 0xc8 and <= 0xcf)
        {
            uint value = ReadRegister(opcode & 7, 4); WriteRegister(opcode & 7, 4, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value)); return;
        }
        switch (opcode)
        {
            case 0xa0: Push(GetSegment(Segment.Fs), w); return;
            case 0xa1: SetSegment(Segment.Fs, (ushort)Pop(w)); return;
            case 0xa8: Push(GetSegment(Segment.Gs), w); return;
            case 0xa9: SetSegment(Segment.Gs, (ushort)Pop(w)); return;
            case 0xaf:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                long result = Signed(ReadRegister(reg, w), w) * Signed(Read(rm, w), w); WriteRegister(reg, w, (uint)result);
                bool overflow = result != Signed((uint)result, w); SetFlag(Carry, overflow); SetFlag(Overflow, overflow); return;
            }
            case 0xb2: LoadFarPointer(Segment.Ss, w); return;
            case 0xb4: LoadFarPointer(Segment.Fs, w); return;
            case 0xb5: LoadFarPointer(Segment.Gs, w); return;
            case 0xb6: case 0xb7: case 0xbe: case 0xbf:
            {
                int size = (opcode & 1) == 0 ? 1 : 2; byte m = FetchByte(); Operand rm = DecodeOperand(m); uint value = Read(rm, size);
                WriteRegister((m >> 3) & 7, w, opcode < 0xbe ? value : unchecked((uint)Signed(value, size))); return;
            }
            case 0x01:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int op = (m >> 3) & 7;
                if (op == 4) { Write(rm, rm.IsRegister ? w : 2, Cr0 & 0xffff); return; }
                throw Unsupported("This system descriptor/control operation is not implemented yet.");
            }
            case 0x20: case 0x22:
            {
                byte m = FetchByte(); if (m < 0xc0 || ((m >> 3) & 7) != 0) throw Unsupported("Only real-mode CR0 access is implemented.");
                if (opcode == 0x20) WriteRegister(m & 7, 4, Cr0);
                else { uint value = ReadRegister(m & 7, 4); if ((value & 0x80000001) != 0) throw Unsupported("Protected mode and paging are not implemented yet."); Cr0 = value; }
                return;
            }
            default: throw Unsupported($"Two-byte opcode 0F {opcode:X2} is not implemented.");
        }
    }
}
