// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Recognizes only the original MMX opcodes, excluding later SSE/3DNow extensions.</summary>
    private static bool IsMmxOpcode(byte opcode) => opcode is >= 0x60 and <= 0x6b or 0x6e or 0x6f or
        >= 0x71 and <= 0x77 or 0x7e or 0x7f or 0xd1 or 0xd2 or 0xd3 or 0xd5 or 0xd8 or 0xd9 or 0xdb or
        0xdc or 0xdd or 0xdf or 0xe1 or 0xe2 or 0xe5 or 0xe8 or 0xe9 or 0xeb or 0xec or 0xed or 0xef or
        0xf1 or 0xf2 or 0xf3 or 0xf5 or 0xf8 or 0xf9 or 0xfa or 0xfc or 0xfd or 0xfe;

    /// <summary>Reads a physical MM register, independently from x87 TOP and empty tags.</summary>
    public ulong GetMmxRegister(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 7);
        return _fpuRegisters[index].Significand;
    }

    /// <summary>Rejects unsupported models/EM and observes TS and the existing pending x87-error contract before data access.</summary>
    private void RequireMmx()
    {
        if (!HasMmx || (Cr0 & 4) != 0) throw new CpuFaultException(6);
        if ((Cr0 & 8) != 0) throw new CpuFaultException(7);
        CheckFpuPending();
    }

    /// <summary>Commits MMX occupancy only after successful operands; saved x87 tag classifications remain derived from raw values.</summary>
    private void CommitMmx(bool empty = false)
    {
        _fpuEmpty = empty ? (byte)0xff : (byte)0;
        FpuTopTo(0);
    }

    /// <summary>Writes the aliased mantissa and all-one sign/exponent without changing x87 environment pointers or status flags.</summary>
    private void WriteMmx(int index, ulong value) => _fpuRegisters[index] = new(value, 0xffff);

    /// <summary>Preflights a complete qword before either memory transaction or any MMX-register effect.</summary>
    private ulong ReadMmxOperand(Operand operand)
    {
        if (operand.IsRegister) return GetMmxRegister(operand.Register);
        CheckOperand(operand, 8); CheckAlignment(operand.Address, 8); ProbeMemory(operand.Address, 8);
        uint low = ReadMemory(operand.Address, 4);
        uint high = ReadMemory(unchecked(operand.Address + 4), 4);
        return low | (ulong)high << 32;
    }

    /// <summary>Preflights both pages and the complete segment span before storing either qword half.</summary>
    private void WriteMmxOperand(Operand operand, ulong value)
    {
        if (operand.IsRegister) { WriteMmx(operand.Register, value); return; }
        CheckOperand(operand, 8, true); CheckAlignment(operand.Address, 8); ProbeMemory(operand.Address, 8, true);
        WriteMemory(operand.Address, 4, (uint)value);
        WriteMemory(unchecked(operand.Address + 4), 4, (uint)(value >> 32));
    }

    /// <summary>Decodes original MMX forms using the current address size; the historical operand-size/REP prefixes are ignored.</summary>
    private void ExecuteMmx(byte opcode)
    {
        if (!HasMmx) throw new CpuFaultException(6);
        if (opcode == 0x77) { RequireMmx(); CommitMmx(true); return; }
        byte modrm = FetchByte(); int reg = modrm >> 3 & 7;
        if (opcode is 0x71 or 0x72 or 0x73)
        {
            if (modrm < 0xc0 || reg is not (2 or 4 or 6) || opcode == 0x73 && reg == 4) throw new CpuFaultException(6);
            byte count = FetchByte(); RequireMmx();
            int bits = opcode == 0x71 ? 16 : opcode == 0x72 ? 32 : 64;
            WriteMmx(modrm & 7, ShiftMmx(GetMmxRegister(modrm & 7), count, bits, reg == 6, reg == 4));
            CommitMmx(); return;
        }
        Operand operand = DecodeOperand(modrm);
        RequireMmx();
        if (opcode == 0x6e) WriteMmx(reg, Read(operand, 4));
        else if (opcode == 0x7e) Write(operand, 4, (uint)GetMmxRegister(reg));
        else if (opcode == 0x6f) WriteMmx(reg, ReadMmxOperand(operand));
        else if (opcode == 0x7f) WriteMmxOperand(operand, GetMmxRegister(reg));
        else
        {
            ulong destination = GetMmxRegister(reg), source = ReadMmxOperand(operand);
            ulong result = opcode switch
            {
                >= 0x60 and <= 0x62 => UnpackMmx(destination, source, 8 << (opcode - 0x60), false),
                >= 0x68 and <= 0x6a => UnpackMmx(destination, source, 8 << (opcode - 0x68), true),
                0x63 or 0x67 => PackMmx(destination, source, 16, opcode == 0x67),
                0x6b => PackMmx(destination, source, 32, false),
                0xdb => destination & source, 0xdf => ~destination & source,
                0xeb => destination | source, 0xef => destination ^ source,
                0xd1 or 0xd2 or 0xd3 => ShiftMmx(destination, source, 16 << (opcode - 0xd1), false, false),
                0xe1 or 0xe2 => ShiftMmx(destination, source, 16 << (opcode - 0xe1), false, true),
                0xf1 or 0xf2 or 0xf3 => ShiftMmx(destination, source, 16 << (opcode - 0xf1), true, false),
                0xf5 => MultiplyAddMmx(destination, source),
                _ => LanesMmx(opcode, destination, source)
            };
            WriteMmx(reg, result);
        }
        CommitMmx();
    }

    /// <summary>Extracts one unsigned packed lane without leaking adjacent bits.</summary>
    private static ulong MmxLane(ulong value, int lane, int bits) => bits == 64 ? value : value >> (lane * bits) & ((1UL << bits) - 1);

    /// <summary>Sign-extends one byte, word or dword for signed comparisons, saturation and multiplication.</summary>
    private static long MmxSigned(ulong value, int bits) => unchecked((long)(value << (64 - bits))) >> (64 - bits);

    /// <summary>Interleaves selected low/high source halves while preserving the destination/source ordering.</summary>
    private static ulong UnpackMmx(ulong a, ulong b, int bits, bool high)
    {
        ulong result = 0; int half = 32 / bits, first = high ? half : 0;
        for (int i = 0; i < half; i++)
            result |= MmxLane(a, first + i, bits) << (i * bits * 2) | MmxLane(b, first + i, bits) << ((i * 2 + 1) * bits);
        return result;
    }

    /// <summary>Packs destination then source with the original signed or unsigned saturation endpoints.</summary>
    private static ulong PackMmx(ulong a, ulong b, int sourceBits, bool unsigned)
    {
        ulong result = 0; int bits = sourceBits / 2, count = 64 / sourceBits;
        long low = unsigned ? 0 : -(1L << (bits - 1)), high = unsigned ? (1L << bits) - 1 : (1L << (bits - 1)) - 1;
        for (int i = 0; i < count * 2; i++)
        {
            long value = MmxSigned(MmxLane(i < count ? a : b, i % count, sourceBits), sourceBits);
            result |= ((ulong)Math.Clamp(value, low, high) & ((1UL << bits) - 1)) << (i * bits);
        }
        return result;
    }

    /// <summary>Uses the entire unsigned count operand and handles width/oversize counts before C# masks shift counts.</summary>
    private static ulong ShiftMmx(ulong value, ulong count, int bits, bool left, bool arithmetic)
    {
        if (!arithmetic && count >= (ulong)bits) return 0;
        int shift = (int)Math.Min(count, (ulong)(bits - 1));
        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1, result = 0;
        for (int i = 0; i < 64 / bits; i++)
        {
            ulong lane = MmxLane(value, i, bits);
            ulong shifted = arithmetic ? unchecked((ulong)(MmxSigned(lane, bits) >> shift)) : left ? lane << shift : lane >> shift;
            result |= (shifted & mask) << (i * bits);
        }
        return result;
    }

    /// <summary>Multiplies signed word pairs and wraps the adjacent sum to its architectural dword result.</summary>
    private static ulong MultiplyAddMmx(ulong a, ulong b)
    {
        ulong result = 0;
        for (int i = 0; i < 2; i++)
        {
            long first = MmxSigned(MmxLane(a, i * 2, 16), 16) * MmxSigned(MmxLane(b, i * 2, 16), 16);
            long second = MmxSigned(MmxLane(a, i * 2 + 1, 16), 16) * MmxSigned(MmxLane(b, i * 2 + 1, 16), 16);
            result |= (ulong)unchecked((uint)(first + second)) << (i * 32);
        }
        return result;
    }

    /// <summary>Executes independently packed add/subtract, comparison and signed word multiply lanes without flag effects.</summary>
    private static ulong LanesMmx(byte opcode, ulong a, ulong b)
    {
        int bits = opcode switch
        {
            0x64 or 0x74 or 0xdc or 0xd8 or 0xec or 0xe8 or 0xfc or 0xf8 => 8,
            0x66 or 0x76 or 0xfe or 0xfa => 32,
            _ => 16
        };
        ulong mask = (1UL << bits) - 1, result = 0;
        for (int i = 0; i < 64 / bits; i++)
        {
            ulong x = MmxLane(a, i, bits), y = MmxLane(b, i, bits), z;
            long sx = MmxSigned(x, bits), sy = MmxSigned(y, bits);
            if (opcode is >= 0x64 and <= 0x66) z = sx > sy ? mask : 0;
            else if (opcode is >= 0x74 and <= 0x76) z = x == y ? mask : 0;
            else if (opcode is 0xd5 or 0xe5) z = unchecked((ulong)(opcode == 0xd5 ? sx * sy : sx * sy >> 16));
            else if (opcode is 0xec or 0xed or 0xe8 or 0xe9)
                z = unchecked((ulong)Math.Clamp(opcode is 0xec or 0xed ? sx + sy : sx - sy, -(1L << (bits - 1)), (1L << (bits - 1)) - 1));
            else if (opcode is 0xdc or 0xdd) z = Math.Min(x + y, mask);
            else if (opcode is 0xd8 or 0xd9) z = x < y ? 0 : x - y;
            else z = opcode is >= 0xfc and <= 0xfe ? unchecked(x + y) : unchecked(x - y);
            result |= (z & mask) << (i * bits);
        }
        return result;
    }
}
