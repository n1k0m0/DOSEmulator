// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private sealed class CpuFaultException(byte vector, uint? errorCode = null, bool trap = false) : Exception
    {
        /// <summary>Architectural exception/interrupt vector carried by this delivery request.</summary>
        public byte Vector { get; } = vector;
        /// <summary>Optional architectural error code to be placed in the exception frame.</summary>
        public uint? ErrorCode { get; } = errorCode;
        /// <summary>Whether delivery uses post-instruction trap semantics rather than an instruction-restart fault.</summary>
        public bool Trap { get; } = trap;
    }
    /// <summary>Sign-extends a width-selected integer operand into the host's wider signed representation.</summary>
    private static long Signed(uint value, int width) => width switch { 1 => (sbyte)value, 2 => (short)value, _ => (int)value };

    /// <summary>Handles additional primary opcode groups and reports whether the opcode was recognized.</summary>
    private bool ExecuteExtended(byte opcode)
    {
        int w = Width;
        switch (opcode)
        {
            case 0x63: if (!AdjustRpl()) throw new CpuFaultException(6); return true;
            case 0x0f: ExecuteTwoByte(); return true;
            case >= 0xd8 and <= 0xdf: ExecuteX87(opcode); X87Instructions++; return true;
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
                bool overflow = result != Signed(unchecked((uint)result), w); SetCarryOverflow(overflow); return true;
            }
            case 0x9a:
            {
                uint target = Immediate(w); ushort segment = (ushort)Immediate(2);
                FarTransfer(segment, target, true, w); return true;
            }
            case 0x9b:
                if ((Cr0 & 0x0a) == 0x0a) throw new CpuFaultException(7);
                CheckFpuPending(); return true;
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
                Push(ReadRegister(5, w), w); uint frame = StackPointer;
                if (nesting > 0)
                {
                    uint bp = ReadRegister(5, w);
                    for (int i = 1; i < nesting; i++) { bp = unchecked(bp - (uint)w) & Mask(w); CheckSegment(Segment.Ss, bp, w); Push(ReadMemory(unchecked(GetSegmentBase(Segment.Ss) + bp), w), w); }
                    Push(frame, w);
                }
                uint finalPointer = unchecked(StackPointer - allocation) & Mask(StackAddressWidth);
                // ENTER also checks its allocation endpoint, without reading or
                // writing unaccessed locals or undoing earlier sequential stores.
                CheckSegment(Segment.Ss, finalPointer, 1, true);
                WriteRegister(5, w, frame); SetStackPointer(finalPointer); return true;
            }
            case 0xc9: SetStackPointer(ReadRegister(5, StackAddressWidth)); WriteRegister(5, w, Pop(w)); return true;
            case 0xce: if ((Eflags & Overflow) != 0) DispatchInterrupt(4, true); return true;
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
                CheckSegment(_override ?? Segment.Ds, offset, 1); WriteRegister(0, 1, ReadMemory(unchecked(GetSegmentBase(_override ?? Segment.Ds) + offset), 1)); return true;
            }
            case 0xf6: case 0xf7: MultiplyDivide(opcode == 0xf6 ? 1 : w); return true;
            default: return false;
        }
    }

    /// <summary>Checks and loads a memory far pointer before committing its register and segment destinations.</summary>
    private void LoadFarPointer(Segment segment, int width)
    {
        byte m = FetchByte(); Operand rm = DecodeOperand(m);
        if (rm.IsRegister) throw Unsupported("Far pointer requires a memory operand.");
        CheckOperand(rm, width + 2); CheckAlignment(rm.Address, width); ProbeMemory(rm.Address, width + 2);
        uint offset = Read(rm, width); ushort selector = (ushort)ReadMemory(unchecked(rm.Address + (uint)width), 2);
        SetSegment(segment, selector); WriteRegister((m >> 3) & 7, width, offset);
        // LSS commits both destinations in this instruction. Unlike MOV/POP SS,
        // it does not inhibit interrupts after completion (Intel486 PRM 3.10).
    }

    /// <summary>Evaluates width/count-specific shifts and rotates with the reference model's defined and stable undefined flags.</summary>
    private void Shift(Operand rm, int operation, int width, int rawCount)
    {
        int count = rawCount & 31;
        if (operation is 2 or 3 && width < 4) count %= width * 8 + 1;
        uint mask = Mask(width), sign = 1u << (width * 8 - 1), original = Read(rm, width, true), value = original;
        if (count == 0) return;
        bool carry = (Eflags & Carry) != 0;
        int bits = width * 8;
        switch (operation)
        {
            case 0: // ROL: retain a nonzero full rotation's CF and old undefined OF.
            {
                int rotation = count & (bits - 1);
                value = ((original << rotation) | (original >> (bits - rotation))) & mask;
                carry = (value & 1) != 0; break;
            }
            case 1:
            {
                int rotation = count & (bits - 1);
                value = ((original >> rotation) | (original << (bits - rotation))) & mask;
                carry = (value & sign) != 0; break;
            }
            case 2: case 3:
            {
                int ringBits = bits + 1;
                ulong ringMask = (1UL << ringBits) - 1;
                ulong ring = ((ulong)original << 1) | (carry ? 1UL : 0);
                ring = operation == 2
                    ? ((ring << count) | (ring >> (ringBits - count))) & ringMask
                    : ((ring >> count) | (ring << (ringBits - count))) & ringMask;
                value = (uint)(ring >> 1); carry = (ring & 1) != 0; break;
            }
            case 4: case 6:
                value = count < bits ? (original << count) & mask : 0;
                carry = count <= bits && ((original >> (bits - count)) & 1) != 0; break;
            case 5:
                value = count < bits ? original >> count : 0;
                carry = count <= bits && ((original >> (count - 1)) & 1) != 0; break;
            case 7:
                value = (uint)(Signed(original, width) >> Math.Min(count, bits - 1)) & mask;
                carry = ((original >> (Math.Min(count, bits) - 1)) & 1) != 0; break;
            default: throw new InvalidOperationException();
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

    /// <summary>Executes grouped multiply/divide operations, including divide-error checks and operand-width result placement.</summary>
    private void MultiplyDivide(int width)
    {
        byte m = FetchByte(); int operation = (m >> 3) & 7; ValidateLockOperand(m, operation is 2 or 3);
        Operand rm = DecodeOperand(m); uint value = Read(rm, width, operation is 2 or 3);
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
            SetCarryOverflow(overflow); return;
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

    /// <summary>Decodes and dispatches the 0F opcode map with model-specific validity checks.</summary>
    private void ExecuteTwoByte()
    {
        byte opcode = FetchByte(); int w = Width;
        if (_checkpointProtected && NeedsTwoByteControlCheckpoint(opcode)) EnsureControlCheckpoint();
        ValidateLockOpcode(opcode, true);
        if (IsMmxOpcode(opcode)) { ExecuteMmx(opcode); return; }
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
            case 0xa1: PopSegment(Segment.Fs, w); return;
            case 0xa8: Push(GetSegment(Segment.Gs), w); return;
            case 0xa9: PopSegment(Segment.Gs, w); return;
            case 0xaf:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                long result = Signed(ReadRegister(reg, w), w) * Signed(Read(rm, w), w); WriteRegister(reg, w, (uint)result);
                bool overflow = result != Signed((uint)result, w); SetCarryOverflow(overflow); return;
            }
            case 0xb2: LoadFarPointer(Segment.Ss, w); return;
            case 0xb4: LoadFarPointer(Segment.Fs, w); return;
            case 0xb5: LoadFarPointer(Segment.Gs, w); return;
            case 0xb6: case 0xb7: case 0xbe: case 0xbf:
            {
                int size = (opcode & 1) == 0 ? 1 : 2; byte m = FetchByte(); Operand rm = DecodeOperand(m); uint value = Read(rm, size);
                WriteRegister((m >> 3) & 7, w, opcode < 0xbe ? value : unchecked((uint)Signed(value, size))); return;
            }
            case 0x00: ExecuteSystemSegmentGroup(); return;
            case 0x01: ExecuteSystemGroup(); return;
            case 0x02: QueryDescriptor(false); return;
            case 0x03: QueryDescriptor(true); return;
            case 0xa3: case 0xab: case 0xb3: case 0xbb: case 0xba: BitTest(opcode); return;
            case 0xb0: case 0xb1: case 0xc0: case 0xc1: ExchangeAddCompare(opcode); return;
            case 0xa4: case 0xa5: case 0xac: case 0xad: DoubleShift(opcode); return;
            case 0xbc: case 0xbd: BitScan(opcode == 0xbd); return;
            case 0x06: RequirePrivilegeZero(); Cr0 &= ~0x08u; return;
            case 0x20: case 0x22: MoveControlRegister(opcode); return;
            case 0x21: case 0x23: MoveDebugRegister(opcode); return;
            case 0xa2: IdentifyCpu(); return;
            case 0x30: TransferTimestampMsr(true); return;
            case 0x31: ReadTimeStampCounter(); return;
            case 0x32: TransferTimestampMsr(false); return;
            case 0xc7: CompareExchangeEightBytes(); return;
            // Reserved on the 486/P54C: fault after both opcode bytes, without a later-generation UD0 operand fetch.
            case 0xff: throw new CpuFaultException(6);
            default: throw Unsupported($"Two-byte opcode 0F {opcode:X2} is not implemented.");
        }
    }
}
