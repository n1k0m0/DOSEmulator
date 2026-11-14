// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly byte[] _repCode = new byte[15];
    private bool _repValid, _repAddress32, _repCode32;
    private byte _repOpcode;
    private int _repLength, _repSize;
    private uint _repStart, _repEnd, _repCsBase;
    private ushort _repCs;
    private Segment _repSource;
    private long _repTask;
    /// <summary>Diagnostic count of rep batched elements.</summary>
    public long RepBatchedElements { get; private set; }
    /// <summary>Whether a decoded REP element can be resumed at the next architectural boundary.</summary>
    public bool HasRepContinuation => _repValid;

    /// <summary>Whether observers/debug conditions require the conservative per-element REP path.</summary>
    private bool RepObservable => InstructionStarting is not null || (Eflags & Trap) != 0 || EnabledDebugMask != 0 ||
        _interruptShadow != 0 || _debugShadow != 0 || _debugDeferredTrigger || _debugDeferredStatus != 0;

    /// <summary>Records the continuation needed to resume a REP string operation at an element boundary.</summary>
    private void RememberRep(byte opcode)
    {
        if (RepObservable || _instructionBytes > 15 || (LinearInstructionAddress & 4095) + _instructionBytes > 4096 ||
            !_memory.TryGetCodeSpan(_firstInstructionPhysical, _instructionBytes, out var code)) return;
        // The first element may itself have changed the instruction. Reparse
        // the tiny prefix-only encoding before trusting a continuation.
        bool operand32 = CodeDefault32, address32 = CodeDefault32;
        Segment? segment = null; byte repeat = 0;
        if (code[^1] != opcode) return;
        foreach (byte prefix in code[..^1])
        {
            if (prefix == 0x66) operand32 = !CodeDefault32;
            else if (prefix == 0x67) address32 = !CodeDefault32;
            else if (prefix is 0xf2 or 0xf3) repeat = prefix;
            else if (prefix is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65)
                segment = prefix switch { 0x26 => Segment.Es, 0x2e => Segment.Cs, 0x36 => Segment.Ss, 0x3e => Segment.Ds, 0x64 => Segment.Fs, _ => Segment.Gs };
            else return;
        }
        if (operand32 != _operand32 || address32 != _address32 || segment != _override || repeat != _repeat) return;
        code.CopyTo(_repCode); _repLength = _instructionBytes;
        _repStart = Eip; _repEnd = unchecked(Eip + (uint)_repLength); _repOpcode = opcode;
        _repSize = (opcode & 1) == 0 ? 1 : Width; _repAddress32 = _address32;
        _repSource = _override ?? Segment.Ds; _repCs = GetSegment(Segment.Cs); _repCsBase = GetSegmentBase(Segment.Cs);
        _repCode32 = CodeDefault32; _repTask = _taskSwitchEpoch; _repValid = true;
    }

    /// <summary>Reuses a validated TLB entry for a plain-RAM path without bypassing required access rights.</summary>
    private bool CachedRamTranslation(uint linear, bool write, out uint physical, out int slot)
    {
        physical = linear; slot = -1;
        if (!PagingEnabled) return true;
        int first = (int)((linear >> 12) & 7) * 4;
        for (int i = first; i < first + 4; i++)
        {
            ref var entry = ref _tlb[i];
            if (!entry.Valid || entry.Page != linear >> 12) continue;
            if (CurrentPrivilegeLevel == 3 && !entry.User || write && (!entry.Dirty || !entry.Writable && (CurrentPrivilegeLevel == 3 || (Cr0 & 0x10000) != 0))) return false;
            physical = entry.Frame | (linear & 4095); slot = i; return true;
        }
        return false; // The ordinary Step warms new pages and delivers all faults.
    }

    /// <summary>Checks whether an entire proposed REP span can use the conservative segment fast path.</summary>
    private bool SafeRepSegment(Segment segment, uint offset, int bytes, bool write)
    {
        var cache = GetSegmentCache(segment);
        return cache.Usable && (!ProtectedMode || Virtual8086 ||
            (write ? !cache.Code && (cache.Access & 2) != 0 : !cache.Code || (cache.Access & 2) != 0)) && WithinSegment(cache, offset, (uint)bytes);
    }

    /// <summary>Owner thread only. Caller budgets four clocks per element and stops at device deadlines.</summary>
    public int TryRunRepBatch(int maximum)
    {
        if (maximum < 2 || !_repValid || Halted || WaitingForNumericError || RepObservable ||
            Eip != _repStart || _taskSwitchEpoch != _repTask || GetSegment(Segment.Cs) != _repCs ||
            GetSegmentBase(Segment.Cs) != _repCsBase || CodeDefault32 != _repCode32 ||
            !WithinSegment(GetSegmentCache(Segment.Cs), Eip, (uint)_repLength)) return 0;
        if (!CachedRamTranslation(LinearInstructionAddress, false, out uint codePhysical, out int codeSlot) ||
            !_memory.TryGetCodeSpan(codePhysical, _repLength, out var code) || !code.SequenceEqual(_repCode.AsSpan(0, _repLength))) return 0;
        bool move = _repOpcode is 0xa4 or 0xa5, reverse = (Eflags & Direction) != 0;
        int registerWidth = _repAddress32 ? 4 : 2, size = _repSize;
        uint count = ReadRegister(1, registerWidth), si = ReadRegister(6, registerWidth), di = ReadRegister(7, registerWidth);
        uint source = unchecked(GetSegmentBase(_repSource) + si), destination = unchecked(GetSegmentBase(Segment.Es) + di);
        int amount = (int)Math.Min(count, (uint)Math.Min(maximum, 4096 / size));
        // Limits a REP batch to the already validated portion of a page.
        int PageBudget(uint address) => reverse ? (int)(address & 4095) / size + 1 : (4096 - (int)(address & 4095)) / size;
        // Limits REP batching before an index/count register would cross its wrapping boundary.
        int RegisterBudget(uint offset) => (int)Math.Min(4096UL, reverse ? (ulong)offset / (uint)size + 1 : ((ulong)Mask(registerWidth) + 1 - offset) / (uint)size);
        amount = Math.Min(amount, Math.Min(PageBudget(destination), RegisterBudget(di)));
        if (move) amount = Math.Min(amount, Math.Min(PageBudget(source), RegisterBudget(si)));
        if (amount < 2 || (destination & 4095) + size > 4096 || move && (source & 4095) + size > 4096) return 0;
        if (CurrentPrivilegeLevel == 3 && (Cr0 & 0x40000) != 0 && (Eflags & 0x40000) != 0 &&
            ((destination & (uint)(size - 1)) != 0 || move && (source & (uint)(size - 1)) != 0)) return 0;
        int bytes = amount * size; uint back = reverse ? (uint)(bytes - size) : 0;
        if (!SafeRepSegment(Segment.Es, di - back, bytes, true) || move && !SafeRepSegment(_repSource, si - back, bytes, false)) return 0;
        if (!CachedRamTranslation(destination, true, out uint destinationPhysical, out int destinationSlot) ||
            !_memory.TryGetRamSpan(destinationPhysical - back, bytes, out var target) || target.Overlaps(code)) return 0;
        int sourceSlot = -1;
        if (move)
        {
            if (!CachedRamTranslation(source, false, out uint sourcePhysical, out sourceSlot) ||
                !_memory.TryGetReadOnlyRamSpan(sourcePhysical - back, bytes, out var input)) return 0;
            if (!input.Overlaps(target)) input.CopyTo(target);
            else for (int e = 0; e < amount; e++)
            {
                int at = reverse ? bytes - size - e * size : e * size;
                uint value = size == 1 ? input[at] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(input[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(input[at..]);
                if (size == 1) target[at] = (byte)value;
                else if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(target[at..], (ushort)value);
                else BinaryPrimitives.WriteUInt32LittleEndian(target[at..], value);
            }
        }
        else
        {
            uint value = ReadRegister(0, size);
            if (size == 1) target.Fill((byte)value);
            else for (int at = 0; at < bytes; at += size)
                if (size == 2) BinaryPrimitives.WriteUInt16LittleEndian(target[at..], (ushort)value);
                else BinaryPrimitives.WriteUInt32LittleEndian(target[at..], value);
        }
        int advance = reverse ? -bytes : bytes;
        WriteRegister(7, registerWidth, unchecked(di + (uint)advance));
        if (move) WriteRegister(6, registerWidth, unchecked(si + (uint)advance));
        WriteRegister(1, registerWidth, count - (uint)amount);
        if (count == amount) { Eip = _repEnd; Eflags &= ~Resume; _repValid = false; }
        Eflags = (Eflags | 2) & ImplementedFlags;
        Instructions += amount; if (ProtectedMode) ProtectedInstructions += amount; if (Virtual8086) Virtual8086Instructions += amount;
        RepBatchedElements += amount;
        if (PagingEnabled)
        {
            long hits = (long)amount * (_repLength + (move ? 4 : 2)); TlbHits += hits; _translationAge += hits;
            _tlb[codeSlot].Age = _translationAge - (move ? 4 : 2);
            if (move) _tlb[sourceSlot].Age = _translationAge - 1;
            _tlb[destinationSlot].Age = _translationAge;
        }
        return amount;
    }
}
