// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>One current stack admission only; live RAM, stack pointers and return targets never enter the derived encoding cache.</summary>
    private readonly ref struct DecodedStackPlan
    {
        public readonly Span<byte> Data;
        public readonly int DataSlot, Width, Length, CodeTouches, Operation, Register;
        public readonly uint OldPointer, NextPointer, Target, ReturnAddress;

        /// <summary>Binds one completely checked current stack range and local control-transfer result.</summary>
        public DecodedStackPlan(Span<byte> data, int dataSlot, in DecodedElement element,
            uint oldPointer, uint nextPointer, uint target, uint returnAddress)
        {
            Data = data; DataSlot = dataSlot; Width = element.Width; Length = element.Length;
            CodeTouches = element.CodeTouches; Operation = element.Operation; Register = element.Operation & 7;
            OldPointer = oldPointer; NextPointer = nextPointer; Target = target; ReturnAddress = returnAddress;
        }
    }

    /// <summary>Proves one prefix-free register-stack or near-control instruction without TLB/bus/CPU effects or speculative device reads.</summary>
    private bool TryAdmitDecodedStack(in DecodedElement element, ReadOnlySpan<byte> wholeCode, out DecodedStackPlan plan)
    {
        plan = default;
        int opcode = element.Operation, width = element.Width;
        bool push = opcode is >= 0x50 and <= 0x57 or 0x60 or 0xe8;
        bool pop = opcode is >= 0x58 and <= 0x5f or 0x61 or 0xc2 or 0xc3;
        if ((!push && !pop) || width is not (2 or 4) || wholeCode.Length != 4096) return false;
        int length = opcode == 0xe8 ? 1 + width : opcode == 0xc2 ? 3 : 1;
        int codeTouches = opcode is 0xe8 or 0xc2 ? 2 : 1;
        if (element.Length != length || element.CodeTouches != codeTouches ||
            !WithinSegment(GetSegmentCache(Segment.Cs), Eip, (uint)length)) return false;

        int bytes = width * (opcode is 0x60 or 0x61 ? 8 : 1);
        uint pointer = StackPointer, mask = Mask(StackAddressWidth), next, beginning;
        if (push)
        {
            if (pointer < bytes) return false;
            next = beginning = pointer - (uint)bytes;
        }
        else
        {
            uint adjustment = opcode == 0xc2 ? element.Immediate & 65535u : 0;
            ulong advanced = (ulong)pointer + (uint)bytes + adjustment;
            if (advanced > mask) return false;
            next = (uint)advanced; beginning = pointer;
        }
        // RET's adjustment skips arguments: only the bytes actually read are checked or translated.
        if (!SafeRepSegment(Segment.Ss, beginning, bytes, push)) return false;
        uint linear = unchecked(GetSegmentBase(Segment.Ss) + beginning);
        if ((linear & 4095) + (uint)bytes > 4096 ||
            AlignmentCheckEnabled && (linear & (uint)(width - 1)) != 0 ||
            !CachedRamTranslation(linear, push, out uint physical, out int slot) ||
            !_memory.TryGetRamSpan(physical, bytes, out var data) ||
            push && ((ReadOnlySpan<byte>)data).Overlaps(wholeCode)) return false;

        uint returnAddress = unchecked(Eip + (uint)length), target = 0;
        if (opcode == 0xe8) target = unchecked(returnAddress + element.Immediate);
        else if (opcode is 0xc2 or 0xc3)
        {
            // This is an observational RAM peek only; its original code/data translation hits occur during execution.
            target = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
        }
        if (opcode is 0xe8 or 0xc2 or 0xc3)
        {
            if (width == 2) target &= 65535;
            if (!WithinSegment(GetSegmentCache(Segment.Cs), target, 1)) return false;
        }
        plan = new(data, slot, in element, pointer, next, target, returnAddress);
        return true;
    }

    /// <summary>Reads one actual-width value from an admitted current stack span.</summary>
    private static uint ReadDecodedStack(in DecodedStackPlan plan, int offset = 0) => plan.Width == 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(plan.Data[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(plan.Data[offset..]);

    /// <summary>Stores one actual-width value without touching surrounding stack RAM.</summary>
    private static void WriteDecodedStack(in DecodedStackPlan plan, int offset, uint value)
    {
        Span<byte> data = plan.Data[offset..];
        if (plan.Width == 2) BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    }

    /// <summary>Performs the original code/preflight/transfer TLB-hit sequence and precise stack-register commit order.</summary>
    private void ExecuteDecodedStack(in DecodedStackPlan plan, int codeSlot)
    {
        TouchIntegerMemoryTranslation(codeSlot, plan.CodeTouches);
        _instructionBytes = plan.Length; Eip = plan.ReturnAddress;
        int opcode = plan.Operation, width = plan.Width;
        if (opcode == 0x60)
        {
            // CheckPushes calls ProbeStackWrites once for EACH of eight words, before any write.
            TouchIntegerMemoryTranslation(plan.DataSlot, 8);
            uint original = ReadRegister(4, width);
            for (int i = 0; i < 8; i++)
            {
                TouchIntegerMemoryTranslation(plan.DataSlot);
                WriteDecodedStack(in plan, (7 - i) * width, i == 4 ? original : ReadRegister(i, width));
                SetStackPointer(plan.OldPointer - (uint)((i + 1) * width));
            }
        }
        else if (opcode == 0x61)
        {
            // POPA probes the whole one-page read range once, then really pops all eight slots including discarded SP.
            TouchIntegerMemoryTranslation(plan.DataSlot);
            for (int i = 0; i < 8; i++)
            {
                TouchIntegerMemoryTranslation(plan.DataSlot);
                uint value = ReadDecodedStack(in plan, i * width);
                SetStackPointer(plan.OldPointer + (uint)((i + 1) * width));
                int register = 7 - i;
                if (register != 4) WriteRegister(register, width, value);
            }
        }
        else if (opcode is >= 0x50 and <= 0x57 or 0xe8)
        {
            // Read PUSH ESP/SP before its decrement; CALL stores the already decoded return address.
            uint value = opcode == 0xe8 ? plan.ReturnAddress : ReadRegister(plan.Register, width);
            TouchIntegerMemoryTranslation(plan.DataSlot); WriteDecodedStack(in plan, 0, value);
            SetStackPointer(plan.NextPointer);
            if (opcode == 0xe8) Eip = plan.Target;
        }
        else
        {
            TouchIntegerMemoryTranslation(plan.DataSlot);
            uint value = ReadDecodedStack(in plan);
            SetStackPointer(plan.NextPointer);
            // POP ESP/SP replaces the post-pop pointer at the operand width, preserving its post-add high bits when narrow.
            if (opcode is >= 0x58 and <= 0x5f) WriteRegister(plan.Register, width, value);
            else Eip = plan.Target;
        }
    }
}
