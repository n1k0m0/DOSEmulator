// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>
    /// Stateless prototype for owner-budgeted, prefix-free register instructions on one plain-RAM code page.
    /// The caller must check IRQs first and budget four clocks per returned element before every device deadline.
    /// No runtime caller is installed by this prototype. A zero result changes no CPU, paging or bus state.
    /// </summary>
    public int TryRunIntegerBatch(int maximum)
    {
        if (maximum < 2 || Halted || TripleFaulted || _waitingForNumericError ||
            InstructionStarting is not null || InterruptEntered is not null ||
            (Eflags & (Trap | Resume)) != 0 || EnabledDebugMask != 0 ||
            _interruptShadow != 0 || _debugShadow != 0 || _repValid || _repeat != 0 ||
            _debugDataActive || _debugStepShadowed || _debugInterruptTransfer ||
            _debugDataMatches != 0 || _debugDataTriggered || _debugDeferredStatus != 0 ||
            _debugDeferredTrigger || _debugDeferredData || _checkpointProtected) return 0;

        var codeSegment = GetSegmentCache(Segment.Cs);
        if (!codeSegment.Usable || !codeSegment.Code) return 0;
        uint linear = LinearInstructionAddress, codePage = linear >> 12;
        // CachedRamTranslation never fills a TLB entry, touches A/D bits or reports a fault.
        // The bus span applies A20, decoded DRAM and the CURRENT fixed mapping/hole classification.
        // Dynamic decoders have lower priority than installed RAM and cannot override this span.
        if (!CachedRamTranslation(linear, false, out uint physical, out _) ||
            !_memory.TryGetRamSpan(physical & ~4095u, 4096, out var codeBytes)) return 0;

        bool protectedStart = ProtectedMode, vmStart = Virtual8086, code32 = CodeDefault32;
        int completed = 0;
        uint lastIp = 0, lastFlags = 0, lastEsp = 0;
        while (completed < maximum)
        {
            linear = unchecked(codeSegment.Base + Eip);
            if (linear >> 12 != codePage) break;
            int at = (int)(linear & 4095);
            int length = IntegerBatchLength(codeBytes[at..], code32);
            if (length == 0 || !WithinSegment(codeSegment, Eip, (uint)length)) break;
            if (completed == 0)
            {
                // The rejected-candidate path above is entirely observational. These are the
                // invariant Step resets once at least one fully admitted element will execute.
                _controlCheckpointSaved = false;
                _checkpointProtected = protectedStart;
                _operand32 = _address32 = code32;
                _override = null; _repeat = 0;
                _lockPrefix = _lockedMemory = false;
            }
            lastIp = Eip; lastFlags = Eflags; lastEsp = GetRegister(Register.Sp);
            _instructionBytes = 0;
            // Keep real fetch/Immediate translation calls and their exact TLB hit/LRU-age order.
            // Admission proves that these operations cannot reach MMIO, a new page or a fault.
            byte opcode = FetchByte();
            Execute(opcode);
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

    /// <summary>Inspects only an already admitted RAM span; zero rejects prefixes, memory/control effects and boundaries.</summary>
    private static int IntegerBatchLength(ReadOnlySpan<byte> bytes, bool code32)
    {
        byte opcode = bytes[0];
        int width = code32 ? 4 : 2, length;
        if (opcode == 0x90 || opcode is >= 0x40 and <= 0x4f) return 1;
        if (opcode is >= 0xb0 and <= 0xb7) length = 2;
        else if (opcode is >= 0xb8 and <= 0xbf) length = 1 + width;
        else if (opcode is >= 0x88 and <= 0x8b)
        {
            if (bytes.Length < 2 || bytes[1] < 0xc0) return 0;
            length = 2;
        }
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
        else return 0;
        return length <= bytes.Length ? length : 0;
    }
}
