// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly Float80Value[] _fpuRegisters = new Float80Value[8];
    private byte _fpuEmpty = 0xff;
    private uint _fpuIp, _fpuData, _fpuRealIp, _fpuRealData;
    private ushort _fpuCs, _fpuDs, _fpuOpcode;
    /// <summary>Physical register index currently selected as logical ST0 by TOP.</summary>
    public int FpuTop => FpuStatusWord >> 11 & 7;
    /// <summary>Saved eleven-bit x87 opcode field for environment reporting.</summary>
    public ushort FpuOpcode => _fpuOpcode;
    /// <summary>Saved x87 instruction offset under the active environment-format contract.</summary>
    public uint FpuInstructionPointer => _fpuIp;
    /// <summary>Saved x87 operand offset under the active environment-format contract.</summary>
    public uint FpuDataPointer => _fpuData;
    /// <summary>Saved protected-mode selector associated with the x87 instruction pointer.</summary>
    public ushort FpuCodeSelector => _fpuCs;
    /// <summary>Saved protected-mode selector associated with the x87 data pointer.</summary>
    public ushort FpuDataSelector => _fpuDs;
    /// <summary>Physical-register tag word including empty slots and raw-value classifications.</summary>
    public ushort FpuTagWord
    {
        get
        {
            uint tags = 0;
            for (int i = 0; i < 8; i++) tags |= (uint)(EmptyFpu(i) ? 3 : _fpuRegisters[i].Tag) << (i * 2);
            return (ushort)tags;
        }
    }
    /// <summary>Returns a logical ST register after translating TOP to the physical eight-register file.</summary>
    public Float80Value GetFpuRegister(int logical)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logical);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(logical, 7);
        return _fpuRegisters[(FpuTop + logical) & 7];
    }
    /// <summary>Checks the empty tag of a logical ST register without changing its stable raw bits.</summary>
    public bool IsFpuRegisterEmpty(int logical)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logical);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(logical, 7);
        return EmptyFpu((FpuTop + logical) & 7);
    }
    /// <summary>Checks the physical slot's empty tag independently of its retained raw register value.</summary>
    private bool EmptyFpu(int physical) => (_fpuEmpty & (1 << physical)) != 0;
    /// <summary>Updates only the TOP field of the x87 status word.</summary>
    private void FpuTopTo(int value) => FpuStatusWord = (ushort)((FpuStatusWord & ~0x3800) | ((value & 7) << 11));
    /// <summary>Stores raw bits into a physical x87 slot and marks it nonempty without moving TOP.</summary>
    private void FpuSet(int physical, Float80Value value)
    { _fpuRegisters[physical] = value; _fpuEmpty &= (byte)~(1 << physical); }
    /// <summary>Initializes x87 control/status/tags and pointer state and republishes the resulting error level.</summary>
    private void InitializeFpu()
    {
        FpuControlWord = 0x037f; FpuStatusWord = 0; _fpuEmpty = 0xff;
        _fpuIp = _fpuData = _fpuRealIp = _fpuRealData = 0;
        _fpuCs = _fpuDs = _fpuOpcode = 0;
        PublishFpuError();
        // FNINIT/FNSAVE invalidate the tags, not the raw register bits.
    }
    /// <summary>Recomputes ES/B from pending unmasked numeric flags and updates the external FERR level.</summary>
    private void FpuSummary()
    {
        bool pending = (FpuStatusWord & ~FpuControlWord & 0x3f) != 0;
        FpuStatusWord = (ushort)(pending ? FpuStatusWord | 0x8080 : FpuStatusWord & ~0x8080);
        PublishFpuError();
    }
    /// <summary>Records stack underflow/overflow and reports whether masked execution may commit an indefinite result.</summary>
    private bool FpuStackFault(bool overflow)
    {
        FpuStatusWord = (ushort)((FpuStatusWord & ~0x200) | 0x41 | (overflow ? 0x200 : 0));
        FpuSummary();
        return (FpuControlWord & 1) != 0;
    }
    /// <summary>Reads a logical stack source or applies the masked/unmasked empty-source contract.</summary>
    private bool FpuReadTop(int logical, out Float80Value value)
    {
        int physical = (FpuTop + logical) & 7;
        value = _fpuRegisters[physical];
        if (!EmptyFpu(physical)) return true;
        value = Float80Value.Indefinite;
        return FpuStackFault(false);
    }
    /// <summary>Checks the next physical slot before moving TOP and installing a value or masked indefinite response.</summary>
    private void FpuPush(Float80Value value)
    {
        int physical = (FpuTop - 1) & 7;
        if (!EmptyFpu(physical))
        {
            if (!FpuStackFault(true)) return;
            value = Float80Value.Indefinite;
        }
        else FpuStatusWord &= 0xfdff;
        FpuTopTo(physical); FpuSet(physical, value);
    }
    /// <summary>Marks the old top slot empty and advances TOP while retaining its raw register bits.</summary>
    private void FpuPop()
    { _fpuEmpty |= (byte)(1 << FpuTop); FpuTopTo(FpuTop + 1); }
    /// <summary>Records the current x87 instruction/opcode and optional data address for later environment saves.</summary>
    private void RecordFpuInstruction(byte opcode, byte modrm, Operand? operand = null)
    {
        _fpuIp = _instructionStart; _fpuCs = GetSegment(Segment.Cs);
        _fpuRealIp = unchecked(GetSegmentBase(Segment.Cs) + _instructionStart);
        _fpuOpcode = (ushort)((opcode & 7) << 8 | modrm);
        if (operand is { } memory)
        {
            _fpuData = memory.Offset; _fpuDs = GetSegment(memory.Segment);
            _fpuRealData = memory.Address;
        }
    }
    /// <summary>Preflights segmentation, alignment and paging before collecting every physical byte address for an x87 operand.</summary>
    private uint[] FpuMemory(Operand operand, int size, bool write, int alignment)
    {
        var physical = new uint[size];
        FpuMemory(operand, write, alignment, physical);
        return physical;
    }
    /// <summary>Preflights an x87 operand into caller storage without allocating per instruction.</summary>
    private void FpuMemory(Operand operand, bool write, int alignment, Span<uint> physical)
    {
        int size = physical.Length;
        CheckOperand(operand, size, write); CheckAlignment(operand.Address, alignment);
        ProbeMemory(operand.Address, size, write);
        for (int i = 0; i < size; i++) { physical[i] = TranslateLinear(unchecked(operand.Address + (uint)i), write); EnsureDeviceCheckpoint(physical[i]); }
    }
    /// <summary>Reads prevalidated physical bytes and records one eligible debug-data observation.</summary>
    private byte[] FpuReadBytes(Operand operand, uint[] physical)
    {
        var bytes = new byte[physical.Length];
        FpuReadBytes(operand, physical, bytes);
        return bytes;
    }
    /// <summary>Reads prevalidated x87 bytes into caller storage with the original debug observation.</summary>
    private void FpuReadBytes(Operand operand, ReadOnlySpan<uint> physical, Span<byte> bytes)
    {
        ObserveDebugData(operand.Address, physical.Length, false, false);
        for (int i = 0; i < bytes.Length; i++) bytes[i] = _memory.ReadByte(physical[i]);
    }
    /// <summary>Stores prevalidated physical bytes and records one eligible debug-data observation.</summary>
    private void FpuWriteBytes(Operand operand, ReadOnlySpan<uint> physical, ReadOnlySpan<byte> bytes)
    {
        ObserveDebugData(operand.Address, physical.Length, true, false);
        for (int i = 0; i < bytes.Length; i++) _memory.WriteByte(physical[i], bytes[i]);
    }
    /// <summary>Decodes the explicit significand and sign/exponent fields of a ten-byte little-endian extended value.</summary>
    private static Float80Value DecodeFloat80(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
    /// <summary>Writes a raw extended value into its exact ten-byte little-endian representation.</summary>
    private static void EncodeFloat80(Span<byte> bytes, Float80Value value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Significand);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[8..], value.SignExponent);
    }
    /// <summary>Encodes the current 14/28-byte environment and optional eight raw registers without host floating-point conversion.</summary>
    private byte[] SaveFpuImage(bool registers)
    {
        int env = _operand32 ? 28 : 14;
        var bytes = new byte[env + (registers ? 80 : 0)];
        int word = _operand32 ? 4 : 2;
        // Writes a little-endian 16-bit field into the surrounding environment or firmware buffer.
        void W(int offset, uint value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)value);
        // Writes a little-endian 32-bit field into the surrounding environment or firmware buffer.
        void D(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        W(0, FpuControlWord); W(word, FpuStatusWord); W(word * 2, FpuTagWord);
        bool real = !ProtectedMode || Virtual8086;
        if (_operand32)
        {
            W(2, 0xffff); W(6, 0xffff); W(10, 0xffff);
            if (real)
            {
                D(12, _fpuRealIp & 0xffff); D(16, (_fpuRealIp << 12 & 0xf0000000) | _fpuOpcode);
                D(20, _fpuRealData & 0xffff); D(24, _fpuRealData << 12 & 0xf0000000);
            }
            else
            {
                D(12, _fpuIp); W(16, _fpuCs); W(18, _fpuOpcode); D(20, _fpuData); W(24, _fpuDs); W(26, 0xffff);
            }
        }
        else if (real)
        {
            W(6, _fpuRealIp); W(8, (_fpuRealIp >> 4 & 0xf000) | _fpuOpcode);
            W(10, _fpuRealData); W(12, _fpuRealData >> 4 & 0xf000);
        }
        else { W(6, _fpuIp); W(8, _fpuCs); W(10, _fpuData); W(12, _fpuDs); }
        if (registers)
            for (int i = 0; i < 8; i++) EncodeFloat80(bytes.AsSpan(env + i * 10, 10), GetFpuRegister(i));
        return bytes;
    }
    /// <summary>Restores the selected environment/register format and recalculates pending numeric-error state.</summary>
    private void RestoreFpuImage(ReadOnlySpan<byte> bytes, bool registers)
    {
        int env = _operand32 ? 28 : 14, word = _operand32 ? 4 : 2;
        FpuControlWord = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        FpuStatusWord = BinaryPrimitives.ReadUInt16LittleEndian(bytes[word..]);
        ushort tags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(word * 2)..]);
        _fpuEmpty = 0;
        for (int i = 0; i < 8; i++) if ((tags >> (i * 2) & 3) == 3) _fpuEmpty |= (byte)(1 << i);
        bool real = !ProtectedMode || Virtual8086;
        if (real)
        {
            int ip = word * 3, high = word * 4, dp = word * 5, dh = word * 6;
            uint highIp = _operand32 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[high..]) >> 12 & 0xf0000 :
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[high..]) & 0xf000) << 4;
            uint highDp = _operand32 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[dh..]) >> 12 & 0xf0000 :
                (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[dh..]) & 0xf000) << 4;
            _fpuIp = _fpuRealIp = BinaryPrimitives.ReadUInt16LittleEndian(bytes[ip..]) | highIp;
            _fpuData = _fpuRealData = BinaryPrimitives.ReadUInt16LittleEndian(bytes[dp..]) | highDp;
            _fpuOpcode = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[high..]) & 0x7ff);
            _fpuCs = _fpuDs = 0; // Real images contain linear 20-bit pointers, no selectors.
        }
        else
        {
            _fpuIp = _operand32 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
            _fpuData = _operand32 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
            _fpuCs = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(_operand32 ? 16 : 8)..]);
            _fpuDs = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(_operand32 ? 24 : 12)..]);
            _fpuOpcode = _operand32 ? (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]) & 0x7ff) : (ushort)0;
            _fpuRealIp = _fpuIp; _fpuRealData = _fpuData;
        }
        if (registers)
            for (int i = 0; i < 8; i++) _fpuRegisters[(FpuTop + i) & 7] = DecodeFloat80(bytes.Slice(env + i * 10, 10));
        FpuSummary();
    }
    /// <summary>Executes x87 register, environment and save/restore instructions through their width-specific memory contracts.</summary>
    private bool ExecuteFpuState(byte opcode, byte modrm)
    {
        if (modrm < 0xc0)
        {
            int op = modrm >> 3 & 7;
            if (opcode is 0xd9 or 0xdd && op is 4 or 6)
            {
                bool store = op == 6, registers = opcode == 0xdd;
                int size = (_operand32 ? 28 : 14) + (registers ? 80 : 0);
                Operand operand = DecodeOperand(modrm); uint[] physical = FpuMemory(operand, size, store, _operand32 ? 4 : 2);
                if (store)
                {
                    FpuWriteBytes(operand, physical, SaveFpuImage(registers));
                    if (registers) InitializeFpu(); else { FpuControlWord |= 0x3f; FpuSummary(); }
                }
                else RestoreFpuImage(FpuReadBytes(operand, physical), registers);
                return true;
            }
            if (opcode == 0xdb && op is 5 or 7)
            {
                bool store = op == 7;
                Operand operand = DecodeOperand(modrm); uint[] physical = FpuMemory(operand, 10, store, 8);
                if (store)
                {
                    RecordFpuInstruction(opcode, modrm, operand);
                    bool empty = EmptyFpu(FpuTop);
                    if (!FpuReadTop(0, out var value)) return true;
                    if (!empty) FpuStatusWord &= 0xfdff;
                    Span<byte> bytes = stackalloc byte[10]; EncodeFloat80(bytes, value);
                    FpuWriteBytes(operand, physical, bytes); FpuPop();
                }
                else
                {
                    var value = DecodeFloat80(FpuReadBytes(operand, physical));
                    RecordFpuInstruction(opcode, modrm, operand); FpuPush(value);
                }
                return true;
            }
            return false;
        }
        if (opcode == 0xd9 && (modrm is >= 0xc0 and <= 0xcf or >= 0xe8 and <= 0xee || modrm is 0xe0 or 0xe1 or 0xe5 or 0xf6 or 0xf7) ||
            opcode == 0xdd && modrm is >= 0xc0 and <= 0xc7 or >= 0xd0 and <= 0xdf)
        {
            RecordFpuInstruction(opcode, modrm);
            int logical = modrm & 7, physical = (FpuTop + logical) & 7;
            if (opcode == 0xd9 && modrm <= 0xc7)
            { if (FpuReadTop(logical, out var value)) FpuPush(value); }
            else if (opcode == 0xd9 && modrm <= 0xcf)
            {
                if (EmptyFpu(FpuTop) || EmptyFpu(physical))
                { if (FpuStackFault(false)) { FpuSet(FpuTop, Float80Value.Indefinite); FpuSet(physical, Float80Value.Indefinite); } }
                else
                {
                    (_fpuRegisters[FpuTop], _fpuRegisters[physical]) = (_fpuRegisters[physical], _fpuRegisters[FpuTop]);
                    FpuStatusWord &= 0xfdff;
                }
            }
            else if (opcode == 0xdd && modrm <= 0xc7) _fpuEmpty |= (byte)(1 << physical);
            else if (opcode == 0xdd)
            {
                bool empty = EmptyFpu(FpuTop);
                if (FpuReadTop(0, out var value))
                { if (!empty) FpuStatusWord &= 0xfdff; FpuSet(physical, value); if (modrm >= 0xd8) FpuPop(); }
            }
            else if (modrm is >= 0xe8 and <= 0xee) FpuPush(Float80Math.LoadConstant(modrm - 0xe8, FpuControlWord));
            else if (modrm is 0xf6 or 0xf7) { FpuStatusWord &= 0xfdff; FpuTopTo(FpuTop + (modrm == 0xf6 ? -1 : 1)); }
            else if (modrm == 0xe5)
            {
                var value = GetFpuRegister(0);
                int condition = EmptyFpu(FpuTop) ? 0x4100 : value.Tag == 1 ? 0x4000 : value.Exponent == 0 ? 0x4400 :
                    !value.IntegerBit ? 0 : value.Exponent != 0x7fff ? 0x400 : value.Significand == 0x8000000000000000 ? 0x500 : 0x100;
                FpuStatusWord = (ushort)((FpuStatusWord & ~0x4700) | condition | (value.Negative ? 0x200 : 0));
            }
            else
            {
                bool empty = EmptyFpu(FpuTop);
                if (FpuReadTop(0, out var value))
                {
                    if (!empty) { FpuStatusWord &= 0xfdff; value = value with { SignExponent = (ushort)(modrm == 0xe0 ? value.SignExponent ^ 0x8000 : value.SignExponent & 0x7fff) }; }
                    FpuSet(FpuTop, value);
                }
            }
            return true;
        }
        return false;
    }
}
