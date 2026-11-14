// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using DOSEmulator.Core;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>
    /// Stateless execution for owner-budgeted, prefix-free register and admitted ordinary RAM instructions.
    /// The caller must check IRQs first and budget four clocks per returned element before every device deadline.
    /// A zero result changes no CPU, paging or bus state; unsuitable instructions retain ordinary Step execution.
    /// </summary>
    public int TryRunIntegerBatch(int maximum) => TryRunIntegerBatchCore(maximum, null, 0, null);

    /// <summary>Computes the device/caller deadline budget only after one complete nonfaulting instruction is eligible.</summary>
    public int TryRunIntegerBatch(int maximum, DeviceScheduler scheduler, uint frequency, ulong? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        return TryRunIntegerBatchCore(maximum, scheduler, frequency, deadline);
    }

    /// <summary>Shares CPU admission for explicit prebudgeted callers and deadline-aware owners, with no persistent cache.</summary>
    private int TryRunIntegerBatchCore(int maximum, DeviceScheduler? scheduler, uint frequency, ulong? deadline)
    {
        if (maximum < 2 || Halted || TripleFaulted || _waitingForNumericError ||
            InstructionStarting is not null || InterruptEntered is not null ||
            (Eflags & (Trap | Resume)) != 0 || EnabledDebugMask != 0 ||
            _interruptShadow != 0 || _debugShadow != 0 || _repValid || _repeat != 0 ||
            _debugDataActive || _debugStepShadowed || _debugInterruptTransfer ||
            _debugDataMatches != 0 || _debugDataTriggered || _debugDeferredStatus != 0 ||
            _debugDeferredTrigger || _debugDeferredData || _checkpointProtected) return 0;

        var codeSegment = GetSegmentCache(Segment.Cs);
        // A real-mode CS may retain data attributes after a mode switch. Preserve
        // its exact cache and scalar bounds while retaining the protected-mode code-type gate.
        if (!codeSegment.Usable || ProtectedMode && !codeSegment.Code) return 0;
        uint linear = LinearInstructionAddress, codePage = linear >> 12;
        // CachedRamTranslation never fills a TLB entry, touches A/D bits or reports a fault.
        // The bus span applies A20, decoded DRAM and the CURRENT fixed mapping/hole classification.
        // Dynamic decoders have lower priority than installed RAM and cannot override this span.
        if (!CachedRamTranslation(linear, false, out uint physical, out int codeSlot) ||
            !_memory.TryGetReadOnlyRamSpan(physical & ~4095u, 4096, out var codeBytes)) return 0;

        bool protectedStart = ProtectedMode, vmStart = Virtual8086, code32 = CodeDefault32;
        int completed = 0;
        uint lastIp = 0, lastFlags = 0, lastEsp = 0;
        while (completed < maximum)
        {
            linear = unchecked(codeSegment.Base + Eip);
            if (linear >> 12 != codePage) break;
            int at = (int)(linear & 4095);
            int length = IntegerBatchLength(codeBytes[at..], code32, codeSegment);
            scoped IntegerMemoryPlan memoryPlan = default;
            bool memory = length == 0 && TryPlanIntegerMemory(codeBytes[at..], code32, codeBytes, out memoryPlan);
            if (memory) length = memoryPlan.Length;
            if (length == 0 || !WithinSegment(codeSegment, Eip, (uint)length)) break;
            if (completed == 0)
            {
                if (scheduler is not null)
                {
                    maximum = scheduler.StepBudget(4, frequency, maximum, deadline ?? ulong.MaxValue);
                    if (maximum < 2) return 0;
                }
                // The rejected-candidate path above is entirely observational. These are the
                // invariant Step resets once at least one fully admitted element will execute.
                _controlCheckpointSaved = false;
                _checkpointProtected = protectedStart;
                _operand32 = _address32 = code32;
                _override = null; _repeat = 0;
                _lockPrefix = _lockedMemory = false;
            }
            lastIp = Eip; lastFlags = Eflags; lastEsp = GetRegister(Register.Sp);
            if (memory)
            {
                _firstInstructionPhysical = (physical & ~4095u) | (linear & 4095);
                ExecutePlannedIntegerMemory(in memoryPlan, codeSlot);
            }
            else if (TryExecuteIntegerDirect(codeBytes.Slice(at, length), code32 ? 4 : 2, out int codeTouches))
            {
                // Admission has proved one cached permitted code page and no other memory,
                // observable callbacks or faults. Preserve FetchByte's UNFOLDED diagnostic
                // address; only the already granted span applies physical A20 folding.
                _firstInstructionPhysical = (physical & ~4095u) | (linear & 4095);
                _instructionBytes = length;
                Eip = unchecked(Eip + (uint)length);
                if (codeSlot >= 0)
                {
                    // Immediate(2/4) already shares one translation in ordinary Step.
                    // Update each instruction here, so interleaved ordinary branch/LEA
                    // fetches see the same age/hit order, not a delayed batch-end total.
                    TlbHits = unchecked(TlbHits + codeTouches);
                    _translationAge = unchecked(_translationAge + codeTouches);
                    _tlb[codeSlot].Age = _translationAge;
                }
            }
            else
            {
                _instructionBytes = 0;
                // Less common admitted forms retain their exact existing decoder/fetch path.
                byte opcode = FetchByte();
                Execute(opcode);
            }
            Eflags = (Eflags | 2) & ImplementedFlags;
            completed++;
        }
        if (completed == 0) return 0;

        // No admitted operation can inspect these checkpoint values or change task/segment/debug state.
        // Preserve the COMPLETE last ordinary Step record, including scratch fields and diagnostics.
        _checkpointIp = lastIp; _checkpointFlags = lastFlags; _checkpointEsp = lastEsp;
        _instructionStart = lastIp;
        _checkpointProtected = false;
        Instructions += completed;
        if (protectedStart) ProtectedInstructions += completed;
        if (vmStart) Virtual8086Instructions += completed;
        return completed;
    }

    /// <summary>Executes only admitted pure register forms, returning their exact ordinary code-translation count.</summary>
    private bool TryExecuteIntegerDirect(ReadOnlySpan<byte> bytes, int width, out int codeTouches)
    {
        byte opcode = bytes[0];
        codeTouches = 0;
        if (opcode is >= 0x90 and <= 0x97)
        {
            if (opcode != 0x90)
            {
                int register = opcode & 7;
                uint old = ReadRegister(0, width);
                WriteRegister(0, width, ReadRegister(register, width)); WriteRegister(register, width, old);
            }
            codeTouches = 1; return true;
        }
        if (opcode is >= 0x40 and <= 0x4f)
        {
            uint carry = Eflags & Carry; int register = opcode & 7;
            WriteRegister(register, width, Alu(opcode < 0x48 ? 0 : 5, ReadRegister(register, width), 1, width));
            Eflags = (Eflags & ~Carry) | carry;
            codeTouches = 1; return true;
        }
        if (opcode is >= 0xb0 and <= 0xbf)
        {
            int size = opcode < 0xb8 ? 1 : width;
            uint value = size == 1 ? bytes[1] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..])
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
            WriteRegister(opcode & 7, size, value);
            codeTouches = 2; return true;
        }
        if (opcode <= 0x3d && (opcode & 7) <= 5)
        {
            int operation = opcode >> 3, form = opcode & 7, size = (form & 1) == 0 ? 1 : width;
            if (form < 4)
            {
                byte modrm = bytes[1]; if (modrm < 0xc0) return false;
                int rm = modrm & 7, register = (modrm >> 3) & 7;
                int destination = form < 2 ? rm : register, source = form < 2 ? register : rm;
                uint result = Alu(operation, ReadRegister(destination, size), ReadRegister(source, size), size);
                if (operation != 7) WriteRegister(destination, size, result);
            }
            else
            {
                uint value = size == 1 ? bytes[1] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..])
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
                uint result = Alu(operation, ReadRegister(0, size), value, size);
                if (operation != 7) WriteRegister(0, size, result);
            }
            codeTouches = 2; return true;
        }
        if (opcode is 0x80 or 0x81 or 0x83)
        {
            byte modrm = bytes[1]; if (modrm < 0xc0) return false;
            int operation = (modrm >> 3) & 7, register = modrm & 7, size = opcode == 0x80 ? 1 : width;
            uint value = opcode == 0x83 ? unchecked((uint)(sbyte)bytes[2]) : size == 1 ? bytes[2]
                : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
            uint result = Alu(operation, ReadRegister(register, size), value, size);
            if (operation != 7) WriteRegister(register, size, result);
            codeTouches = 3; return true;
        }
        if (opcode is >= 0x84 and <= 0x8b)
        {
            byte modrm = bytes[1]; if (modrm < 0xc0) return false;
            int rm = modrm & 7, register = (modrm >> 3) & 7, size = (opcode & 1) == 0 ? 1 : width;
            if (opcode < 0x86) Alu(4, ReadRegister(rm, size), ReadRegister(register, size), size);
            else if (opcode < 0x88)
            {
                uint old = ReadRegister(rm, size);
                WriteRegister(rm, size, ReadRegister(register, size)); WriteRegister(register, size, old);
            }
            else if (opcode < 0x8a) WriteRegister(rm, size, ReadRegister(register, size));
            else WriteRegister(register, size, ReadRegister(rm, size));
            codeTouches = 2; return true;
        }
        if (opcode is 0xa8 or 0xa9)
        {
            int size = opcode == 0xa8 ? 1 : width;
            uint value = size == 1 ? bytes[1] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..])
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]);
            Alu(4, ReadRegister(0, size), value, size);
            codeTouches = 2; return true;
        }
        return false;
    }

    /// <summary>Inspects only admitted RAM; zero rejects prefixes, data effects and any potentially faulting branch target.</summary>
    private int IntegerBatchLength(ReadOnlySpan<byte> bytes, bool code32, in SegmentDescriptorCache code)
    {
        byte opcode = bytes[0];
        int width = code32 ? 4 : 2, length;
        if (opcode is >= 0x90 and <= 0x99 or 0x9e or 0x9f or 0xf5 or 0xf8 or 0xf9 or 0xfc or 0xfd ||
            opcode is >= 0x40 and <= 0x4f) return 1;
        if (opcode is >= 0xb0 and <= 0xb7) length = 2;
        else if (opcode is >= 0xb8 and <= 0xbf) length = 1 + width;
        else if (opcode is >= 0x84 and <= 0x8b)
        {
            if (bytes.Length < 2 || bytes[1] < 0xc0) return 0;
            length = 2;
        }
        else if (opcode == 0x8d) length = IntegerBatchLeaLength(bytes, code32);
        else if (opcode is 0xa8 or 0xa9) length = 1 + (opcode == 0xa8 ? 1 : width);
        else if (opcode <= 0x3d && (opcode & 7) <= 5)
        {
            int form = opcode & 7;
            if (form < 4)
            {
                if (bytes.Length < 2 || bytes[1] < 0xc0) return 0;
                length = 2;
            }
            else length = 1 + ((opcode & 1) == 0 ? 1 : width);
        }
        else if (opcode is 0x80 or 0x81 or 0x83)
        {
            if (bytes.Length < 2 || bytes[1] < 0xc0) return 0;
            length = 2 + (opcode == 0x81 ? width : 1);
        }
        else if (opcode is 0xc6 or 0xc7)
        {
            if (bytes.Length < 2 || bytes[1] is < 0xc0 or > 0xc7) return 0;
            length = 2 + (opcode == 0xc6 ? 1 : width);
        }
        else if (opcode is >= 0x70 and <= 0x7f or 0xeb)
            return IntegerBatchBranchLength(bytes, 2, 1, code32, opcode == 0xeb || Condition(opcode & 15), code);
        else if (opcode == 0xe9)
            return IntegerBatchBranchLength(bytes, 1 + width, width, code32, true, code);
        else if (opcode == 0x0f)
        {
            if (bytes.Length < 2) return 0;
            byte second = bytes[1];
            if (second is >= 0x80 and <= 0x8f)
                return IntegerBatchBranchLength(bytes, 2 + width, width, code32, Condition(second & 15), code);
            if (second is >= 0xc8 and <= 0xcf) length = 2;
            else if (second is >= 0x90 and <= 0x9f or 0xb6 or 0xb7 or 0xbe or 0xbf)
            {
                if (bytes.Length < 3 || bytes[2] < 0xc0) return 0;
                length = 3;
            }
            else return 0;
        }
        else return 0;
        return length <= bytes.Length ? length : 0;
    }

    /// <summary>Counts complete LEA code bytes without resolving or checking its deliberately unaccessed data address.</summary>
    private static int IntegerBatchLeaLength(ReadOnlySpan<byte> bytes, bool address32)
    {
        if (bytes.Length < 2 || bytes[1] >= 0xc0) return 0;
        int mod = bytes[1] >> 6, rm = bytes[1] & 7, length = 2;
        if (!address32) return length + (mod == 0 ? rm == 6 ? 2 : 0 : mod == 1 ? 1 : 2);
        if (rm == 4)
        {
            if (bytes.Length < 3) return 0;
            rm = bytes[2] & 7;
            length++;
        }
        return length + (mod == 0 ? rm == 5 ? 4 : 0 : mod == 1 ? 1 : 4);
    }

    /// <summary>Admits a branch only when its actual taken target cannot fault; target paging remains a future fetch.</summary>
    private int IntegerBatchBranchLength(ReadOnlySpan<byte> bytes, int length, int width, bool code32, bool take,
        in SegmentDescriptorCache code)
    {
        if (bytes.Length < length) return 0;
        if (!take) return length;
        ReadOnlySpan<byte> displacement = bytes.Slice(length - width, width);
        int delta = width == 1 ? (sbyte)displacement[0] : width == 2
            ? BinaryPrimitives.ReadInt16LittleEndian(displacement) : BinaryPrimitives.ReadInt32LittleEndian(displacement);
        uint target = unchecked(Eip + (uint)length + (uint)delta);
        if (!code32) target &= 0xffff;
        return WithinSegment(code, target, 1) ? length : 0;
    }
}
