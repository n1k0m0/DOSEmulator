// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private enum IntegerMemoryKind { MoveRead, MoveWrite, AluMemory, AluRegister, Test, AluImmediate }

    /// <summary>One stack-scoped admission result; RAM spans and translation slots are never retained in CPU or snapshot state.</summary>
    private readonly ref struct IntegerMemoryPlan
    {
        public readonly int Length, Width, CodeTouches, DataSlot, Register, Operation;
        public readonly IntegerMemoryKind Kind;
        public readonly uint Immediate;
        public readonly Span<byte> Data;

        /// <summary>Binds exactly one fully checked current RAM operand and its original instruction-fetch accounting.</summary>
        public IntegerMemoryPlan(int length, int width, int codeTouches, int dataSlot, int register, int operation,
            IntegerMemoryKind kind, Span<byte> data, uint immediate = 0)
        {
            Length = length; Width = width; CodeTouches = codeTouches; DataSlot = dataSlot;
            Register = register; Operation = operation; Kind = kind; Immediate = immediate; Data = data;
        }
    }

    /// <summary>Admits one prefix-free MOV/ALU/TEST RAM operand without fetching, filling a TLB entry, touching A/D, or changing CPU state.</summary>
    private bool TryPlanIntegerMemory(ReadOnlySpan<byte> instruction, bool code32, ReadOnlySpan<byte> wholeCodePage,
        out IntegerMemoryPlan plan)
    {
        plan = default;
        if (instruction.IsEmpty || wholeCodePage.Length != 4096) return false;
        byte opcode = instruction[0];
        int width = (opcode & 1) == 0 ? 1 : code32 ? 4 : 2;
        int length, touches, register, operation = 0;
        uint offset; Segment segment;
        IntegerMemoryKind kind;
        bool writes;
        if (opcode is >= 0xa0 and <= 0xa3)
        {
            int addressBytes = code32 ? 4 : 2;
            length = 1 + addressBytes;
            if (instruction.Length < length) return false;
            offset = addressBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(instruction[1..])
                : BinaryPrimitives.ReadUInt32LittleEndian(instruction[1..]);
            segment = Segment.Ds; register = 0; touches = 2;
            writes = opcode >= 0xa2; kind = writes ? IntegerMemoryKind.MoveWrite : IntegerMemoryKind.MoveRead;
        }
        else
        {
            bool alu = opcode <= 0x3b && (opcode & 7) <= 3;
            bool move = opcode is >= 0x88 and <= 0x8b, test = opcode is 0x84 or 0x85;
            if (!alu && !move && !test ||
                !TryPlanIntegerAddress(instruction, code32, out offset, out segment, out length, out touches, out register)) return false;
            if (move)
            {
                writes = opcode < 0x8a;
                kind = writes ? IntegerMemoryKind.MoveWrite : IntegerMemoryKind.MoveRead;
            }
            else if (test) { writes = false; kind = IntegerMemoryKind.Test; operation = 4; }
            else
            {
                operation = opcode >> 3;
                bool memoryDestination = (opcode & 2) == 0;
                writes = memoryDestination && operation != 7;
                kind = memoryDestination ? IntegerMemoryKind.AluMemory : IntegerMemoryKind.AluRegister;
            }
        }
        // Both instruction range and data permissions are proven before the outer batch changes any scratch state.
        if (!WithinSegment(GetSegmentCache(Segment.Cs), Eip, (uint)length) ||
            !SafeRepSegment(segment, offset, width, writes)) return false;
        uint linear = unchecked(GetSegmentBase(segment) + offset);
        if ((linear & 4095) + (uint)width > 4096 ||
            AlignmentCheckEnabled && (linear & (uint)(width - 1)) != 0 ||
            !CachedRamTranslation(linear, writes, out uint physical, out int slot) ||
            !_memory.TryGetRamSpan(physical, width, out var data)) return false;
        // Reject every alias of the current code page, including different linear PTEs and physical A20 folds.
        if (writes && ((ReadOnlySpan<byte>)data).Overlaps(wholeCodePage)) return false;
        plan = new(length, width, touches, slot, register, operation, kind, data);
        return true;
    }

    /// <summary>Decodes only complete code bytes and current register inputs; data translation and reads remain separate admission steps.</summary>
    private bool TryPlanIntegerAddress(ReadOnlySpan<byte> instruction, bool address32, out uint offset, out Segment segment,
        out int length, out int touches, out int register)
    {
        offset = 0; segment = Segment.Ds; length = touches = register = 0;
        if (instruction.Length < 2 || instruction[1] >= 0xc0) return false;
        byte modrm = instruction[1];
        int mod = modrm >> 6, rm = modrm & 7;
        register = (modrm >> 3) & 7; length = touches = 2;
        bool stack = false;
        if (!address32)
        {
            uint bx = ReadRegister(3, 2), bp = ReadRegister(5, 2), si = ReadRegister(6, 2), di = ReadRegister(7, 2);
            offset = rm switch { 0 => bx + si, 1 => bx + di, 2 => bp + si, 3 => bp + di, 4 => si, 5 => di, 6 => bp, _ => bx };
            stack = rm is 2 or 3 or 6;
            if (mod == 0 && rm == 6)
            {
                if (instruction.Length < length + 2) return false;
                offset = BinaryPrimitives.ReadUInt16LittleEndian(instruction[length..]); length += 2; touches++; stack = false;
            }
            else if (mod == 1)
            {
                if (instruction.Length <= length) return false;
                offset = unchecked(offset + (uint)(sbyte)instruction[length++]); touches++;
            }
            else if (mod == 2)
            {
                if (instruction.Length < length + 2) return false;
                offset += BinaryPrimitives.ReadUInt16LittleEndian(instruction[length..]); length += 2; touches++;
            }
            offset &= 0xffff;
        }
        else
        {
            int basis = rm; uint index = 0;
            if (rm == 4)
            {
                if (instruction.Length <= length) return false;
                byte sib = instruction[length++]; touches++;
                basis = sib & 7; int source = (sib >> 3) & 7;
                if (source != 4) index = GetRegister((Register)source) << (sib >> 6);
            }
            if (mod == 0 && basis == 5)
            {
                if (instruction.Length < length + 4) return false;
                offset = BinaryPrimitives.ReadUInt32LittleEndian(instruction[length..]); length += 4; touches++;
            }
            else { offset = GetRegister((Register)basis); stack = basis is 4 or 5; }
            offset = unchecked(offset + index);
            if (mod == 1)
            {
                if (instruction.Length <= length) return false;
                offset = unchecked(offset + (uint)(sbyte)instruction[length++]); touches++;
            }
            else if (mod == 2)
            {
                if (instruction.Length < length + 4) return false;
                offset = unchecked(offset + BinaryPrimitives.ReadUInt32LittleEndian(instruction[length..])); length += 4; touches++;
            }
        }
        segment = stack ? Segment.Ss : Segment.Ds;
        return true;
    }

    /// <summary>Reproduces only an already proved cached translation hit, retaining exact hit counts and deterministic LRU age.</summary>
    private void TouchIntegerMemoryTranslation(int slot, int count = 1)
    {
        if (slot < 0) return;
        TlbHits = unchecked(TlbHits + count);
        _translationAge = unchecked(_translationAge + count);
        _tlb[slot].Age = _translationAge;
    }

    /// <summary>Reads the exact width from an admitted ordinary RAM span, without any bus or additional paging operation.</summary>
    private static uint ReadIntegerMemory(in IntegerMemoryPlan plan) => plan.Width == 1 ? plan.Data[0]
        : plan.Width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(plan.Data) : BinaryPrimitives.ReadUInt32LittleEndian(plan.Data);

    /// <summary>Writes the exact width after admission, retaining all neighboring RAM bytes.</summary>
    private static void WriteIntegerMemory(in IntegerMemoryPlan plan, uint value)
    {
        Span<byte> data = plan.Data;
        if (plan.Width == 1) data[0] = (byte)value;
        else if (plan.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    }

    /// <summary>Executes a proved memory element; the outer batch retains first-physical, checkpoint, flag-normalization and retirement ownership.</summary>
    private void ExecutePlannedIntegerMemory(in IntegerMemoryPlan plan, int codeSlot)
    {
        TouchIntegerMemoryTranslation(codeSlot, plan.CodeTouches);
        _instructionBytes = plan.Length; Eip = unchecked(Eip + (uint)plan.Length);
        TouchIntegerMemoryTranslation(plan.DataSlot);
        if (plan.Kind == IntegerMemoryKind.MoveRead) WriteRegister(plan.Register, plan.Width, ReadIntegerMemory(plan));
        else if (plan.Kind == IntegerMemoryKind.MoveWrite) WriteIntegerMemory(plan, ReadRegister(plan.Register, plan.Width));
        else if (plan.Kind == IntegerMemoryKind.AluImmediate)
        {
            uint result = Alu(plan.Operation, ReadIntegerMemory(plan), plan.Immediate, plan.Width);
            if (plan.Operation != 7) { TouchIntegerMemoryTranslation(plan.DataSlot); WriteIntegerMemory(plan, result); }
        }
        else
        {
            uint memory = ReadIntegerMemory(plan), register = ReadRegister(plan.Register, plan.Width);
            bool memoryDestination = plan.Kind is IntegerMemoryKind.AluMemory or IntegerMemoryKind.Test;
            uint result = Alu(plan.Operation, memoryDestination ? memory : register, memoryDestination ? register : memory, plan.Width);
            if (plan.Operation != 7 && plan.Kind != IntegerMemoryKind.Test)
            {
                if (memoryDestination) { TouchIntegerMemoryTranslation(plan.DataSlot); WriteIntegerMemory(plan, result); }
                else WriteRegister(plan.Register, plan.Width, result);
            }
        }
    }
}
