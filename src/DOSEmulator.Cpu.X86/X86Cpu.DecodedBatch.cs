// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DOSEmulator.Core;

namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private enum DecodedIntegerKind
    {
        Nop, MoveImmediate, MoveRegister, AluRegister, AluImmediate, TestRegister, TestImmediate,
        UnaryRegister, ShiftRegister, DoubleShiftRegister, MultiplyImmediate, MultiplyRegister, IncDec, Exchange, EffectiveAddress, SignExtend, Flag, Bswap, MoveExtend, SetCondition, Branch, Memory, Stack
    }

    private readonly record struct DecodedAddress(int Basis, int Index, int Shift, uint Displacement, Segment Segment, bool Narrow);
    private readonly record struct DecodedElement(DecodedIntegerKind Kind, int Length, int Width, int CodeTouches,
        int Destination = 0, int Source = 0, int Operation = 0, uint Immediate = 0,
        DecodedAddress Address = default, IntegerMemoryKind MemoryKind = default, byte PrefixState = 0);
    private readonly record struct DecodedBlockKey(uint Ip, uint Physical, bool Code32, bool Protected, bool Vm,
        ushort Selector, SegmentDescriptorCache Code);

    /// <summary>Immutable encoding data only; no spans, live translations, CPU references or authoritative state.</summary>
    private sealed record DecodedBlock(byte[] Bytes, DecodedElement[] Elements);
    /// <summary>Retains the full context guard beside a cheap physical/IP dictionary index; equality is never replaced by hashing.</summary>
    private sealed record DecodedBlockEntry(DecodedBlockKey Key, DecodedBlock Block);

    /// <summary>Mixes both index halves without cancelling identity-mapped physical/IP pairs into the same bucket.</summary>
    private sealed class DecodedIndexComparer : IEqualityComparer<ulong>
    {
        /// <summary>Retains exact packed-index equality; hash collisions never authorize cache reuse.</summary>
        public bool Equals(ulong left, ulong right) => left == right;
        /// <summary>Uses a cheap multiply/rotate mix rather than XORing equal physical and IP halves.</summary>
        public int GetHashCode(ulong value) => unchecked((int)((uint)value * 0x9e3779b1u +
            System.Numerics.BitOperations.RotateLeft((uint)(value >> 32), 13)));
    }

    /// <summary>Owner-only bounded derived cache. Replaced bytes replace plans; eviction order cannot affect guest execution.</summary>
    private sealed class DecodedBlockCache
    {
        private readonly Dictionary<ulong, DecodedBlockEntry> _blocks = new(new DecodedIndexComparer());
        private readonly Queue<ulong> _order = [];
        // Private host diagnostics only. No caller uses these to change guest execution,
        // and neither the cache nor these counters are CPU instance/snapshot members.
        private long _hits, _misses, _contextReplacements, _byteReplacements, _evictions;

        /// <summary>Checks every retained source byte before using a plan, including after CPU/DMA/host writes or LoadState.</summary>
        public DecodedBlock Get(in DecodedBlockKey key, ReadOnlySpan<byte> source)
        {
            ulong index = ((ulong)key.Physical << 32) | key.Ip;
            bool exists = _blocks.TryGetValue(index, out var entry);
            if (exists && entry is not null && entry.Key == key && source.Length >= entry.Block.Bytes.Length &&
                source[..entry.Block.Bytes.Length].SequenceEqual(entry.Block.Bytes))
            {
                _hits++; return entry.Block;
            }
            _misses++;
            if (exists && entry is not null)
            {
                // Context and byte replacements are mutually exclusive causes. The full
                // byte guard is mandatory even when the physical/IP and full context match.
                if (entry.Key != key) _contextReplacements++;
                else _byteReplacements++;
            }
            var block = DecodeIntegerBlock(source, key.Ip, key.Code32, key.Code);
            if (!exists)
            {
                if (_blocks.Count == 4096) { _blocks.Remove(_order.Dequeue()); _evictions++; }
                _order.Enqueue(index);
            }
            _blocks[index] = new(key, block);
            return block;
        }
    }

    // This weak, static association is disposable derived acceleration data. It is deliberately
    // absent from CPU instance members and snapshots, and never retains its CPU key as a value.
    private static readonly ConditionalWeakTable<X86Cpu, DecodedBlockCache> DecodedBlocks = new();

    /// <summary>Runs a bounded, byte-guarded decoded block; the owner must prebudget clocks and interrupt boundaries.</summary>
    public int TryRunDecodedBatch(int maximum) => TryRunDecodedBatchCore(maximum, null, 0, null);

    /// <summary>Checks current code/data guards before obtaining a budget ending at the next device/caller deadline.</summary>
    public int TryRunDecodedBatch(int maximum, DeviceScheduler scheduler, uint frequency, ulong? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        if (frequency == 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        return TryRunDecodedBatchCore(maximum, scheduler, frequency, deadline);
    }

    /// <summary>Executes immutable encoding plans against current registers/segments/permissions, never cached data addresses.</summary>
    private int TryRunDecodedBatchCore(int maximum, DeviceScheduler? scheduler, uint frequency, ulong? deadline)
    {
        if(maximum<2 || Halted || TripleFaulted || _waitingForNumericError ||
            InstructionStarting is not null || InterruptEntered is not null)return 0;
        var code=GetSegmentCache(Segment.Cs);
        uint linear=unchecked(code.Base+Eip);
        bool vmStart=Virtual8086,protectedStart=ProtectedMode,code32=!vmStart && code.Default32;
        if(!CachedRamTranslation(linear,false,out uint physical,out int codeSlot) ||
            !_memory.TryGetSinglePageRamSpan(physical&~4095u,4096,out var wholeCode))return 0;
        ReadOnlySpan<byte> source=wholeCode[(int)(linear&4095)..];
        if(!IsDecodedOpcodeCandidate(source,code32))return 0;
        // These are all pure field/property reads. The single rejection preserves every
        // prior guard while avoiding a long chain of taken/not-taken host branches.
        if(((Eflags&(Trap|Resume))!=0) | (EnabledDebugMask!=0) |
            (_interruptShadow!=0) | (_debugShadow!=0) | _repValid | (_repeat!=0) |
            _debugDataActive | _debugStepShadowed | _debugInterruptTransfer |
            (_debugDataMatches!=0) | _debugDataTriggered | (_debugDeferredStatus!=0) |
            _debugDeferredTrigger | _debugDeferredData | _checkpointProtected)return 0;
        if(!code.Usable || protectedStart && !code.Code)return 0;
        var key = new DecodedBlockKey(Eip, physical, code32, protectedStart, vmStart, GetSegment(Segment.Cs), code);
        var block = DecodedBlocks.GetValue(this, static _ => new()).Get(key, source);
        int completed = 0;
        uint lastIp = 0, lastFlags = 0, lastEsp = 0;
        byte livePrefixes = 0;
        do
        {
        int passStart = completed;
        foreach (ref readonly DecodedElement element in block.Elements.AsSpan())
        {
            if (completed == maximum) break;
            scoped IntegerMemoryPlan memoryPlan = default;
            if (element.Kind == DecodedIntegerKind.Memory && !AdmitDecodedMemory(element, wholeCode, out memoryPlan)) break;
            scoped DecodedStackPlan stackPlan = default;
            if(element.Kind==DecodedIntegerKind.Stack && element.Operation==0xff && element.Source<0 &&
                !AdmitDecodedMemory(element,wholeCode,out memoryPlan))break;
            if (element.Kind == DecodedIntegerKind.Stack && !TryAdmitDecodedStack(element, wholeCode, out stackPlan)) break;
            uint branchTarget = unchecked(Eip + (uint)element.Length);
            if (element.Kind == DecodedIntegerKind.Branch && (element.Operation == 16 || Condition(element.Operation)))
            {
                branchTarget = unchecked(branchTarget + element.Immediate);
                if (element.Width == 2) branchTarget &= 0xffff;
                if (!WithinSegment(code, branchTarget, 1)) break;
            }
            if (completed == 0)
            {
                if (scheduler is not null)
                {
                    maximum = scheduler.StepBudget(4, frequency, maximum, deadline ?? ulong.MaxValue);
                    if (maximum < 2) return 0;
                }
                _controlCheckpointSaved = false; _checkpointProtected = protectedStart;
                _operand32 = _address32 = code32; _override = null; _repeat = 0;
                _lockPrefix = _lockedMemory = false;
            }
            if (element.PrefixState != livePrefixes)
            {
                livePrefixes = element.PrefixState;
                _operand32 = code32 ^ (livePrefixes & 1) != 0;
                _address32 = code32 ^ (livePrefixes & 2) != 0;
                _override = livePrefixes >> 2 is int segment && segment != 0 ? (Segment)(segment - 1) : null;
            }
            lastIp = Eip; lastFlags = Eflags; lastEsp = GetRegister(Register.Sp);
            _controlCheckpointSaved=false;
            if(memoryPlan.DeviceWrite || element.Kind==DecodedIntegerKind.Stack && element.Operation==0xff)
            {_checkpointIp=lastIp;_checkpointFlags=lastFlags;_checkpointEsp=lastEsp;}
            if(element.Kind==DecodedIntegerKind.Stack && element.Operation==0xff)EnsureControlCheckpoint();
            _firstInstructionPhysical = (physical & ~4095u) | (unchecked(code.Base + Eip) & 4095);
            if (element.Kind == DecodedIntegerKind.Memory) ExecutePlannedIntegerMemory(in memoryPlan, codeSlot);
            else if (element.Kind == DecodedIntegerKind.Stack) ExecuteDecodedStack(in stackPlan, codeSlot,in memoryPlan);
            else
            {
                TouchIntegerMemoryTranslation(codeSlot, element.CodeTouches);
                _instructionBytes = element.Length; Eip = unchecked(Eip + (uint)element.Length);
                ExecuteDecodedInteger(element, branchTarget);
            }
            Eflags = (Eflags | 2) & ImplementedFlags;
            completed++;
        }
        // Only an actual backedge to the exact retained entry may replay the same plan.
        // Current bytes are checked again; no admitted memory/stack write can alias this page.
        if (completed == passStart || Eip != key.Ip || completed >= maximum ||
            !source[..block.Bytes.Length].SequenceEqual(block.Bytes)) break;
        } while (true);
        if (completed == 0) return 0;
        _checkpointIp = lastIp; _checkpointFlags = lastFlags; _checkpointEsp = lastEsp;
        _instructionStart = lastIp; _checkpointProtected = false;
        Instructions += completed;
        if (protectedStart) ProtectedInstructions += completed;
        if (vmStart) Virtual8086Instructions += completed;
        return completed;
    }

    /// <summary>Rejects unsupported entry encodings from current plain code bytes, without a cache lookup or architectural fetch.</summary>
    private static bool IsDecodedOpcodeCandidate(ReadOnlySpan<byte> bytes, bool code32)
    {
        int prefixes = DecodeIntegerPrefixes(bytes, out byte state);
        return prefixes < bytes.Length && IsDecodedOpcodeCandidateCore(bytes[prefixes..],
            code32 ^ (state & 1) != 0, code32 ^ (state & 2) != 0);
    }

    /// <summary>Classifies only current complete opcode facts after harmless size/segment prefixes.</summary>
    private static bool IsDecodedOpcodeCandidateCore(ReadOnlySpan<byte> bytes, bool operand32, bool address32)
    {
        if (bytes.IsEmpty) return false;
        byte opcode = bytes[0]; int width = operand32 ? 4 : 2;
        if (opcode is >= 0x40 and <= 0x4f or >= 0x90 and <= 0x99 or 0x9e or 0x9f or 0xf5 or 0xf8 or 0xf9 or 0xfc or 0xfd) return true;
        if (opcode is >= 0x50 and <= 0x5f or 0x60 or 0x61 or 0xc3 or 0xc9) return true;
        if (opcode is 0x68 or 0x6a) return bytes.Length >= 1 + (opcode == 0x6a ? 1 : width);
        if (opcode == 0xc2) return bytes.Length >= 3;
        if (opcode == 0xe8) return bytes.Length >= 1 + width;
        if (opcode is >= 0xb0 and <= 0xbf)
            return bytes.Length >= 1 + (opcode < 0xb8 ? 1 : width);
        if (opcode <= 0x3d && (opcode & 7) <= 5)
            return bytes.Length >= ((opcode & 7) < 4 ? 2 : 1 + ((opcode & 1) == 0 ? 1 : width));
        if (opcode is >= 0x70 and <= 0x7f or 0xeb) return bytes.Length >= 2;
        if (opcode == 0xe9) return bytes.Length >= 1 + width;
        if (opcode is >= 0xa0 and <= 0xa3) return bytes.Length >= 1 + (address32 ? 4 : 2);
        if (opcode is 0xa8 or 0xa9) return bytes.Length >= 1 + (opcode == 0xa8 ? 1 : width);
        if (opcode == 0x0f)
        {
            if (bytes.Length < 2) return false;
            byte second = bytes[1];
            if (second is >= 0xc8 and <= 0xcf) return true; // Retain the existing 32-bit effect in every admitted code-default mode.
            if (second is >= 0x80 and <= 0x8f) return bytes.Length >= 2 + width;
            if(second is 0xa4 or 0xa5 or 0xac or 0xad)
                return bytes.Length>=((second&1)==0?4:3) && bytes[2]>=0xc0;
            if (second is 0xb6 or 0xb7 or 0xbe or 0xbf) return bytes.Length >= 3;
            return (second is >= 0x90 and <= 0x9f or 0xaf) && bytes.Length >= 3 && bytes[2] >= 0xc0;
        }
        if (bytes.Length < 2) return false;
        byte modrm = bytes[1];
        if (opcode is 0x69 or 0x6b)
            return modrm >= 0xc0 && bytes.Length >= 2 + (opcode == 0x6b ? 1 : width);
        if (opcode is 0xc0 or 0xc1 or 0xd0 or 0xd1 or 0xd2 or 0xd3)
            return modrm >= 0xc0 && bytes.Length >= (opcode is 0xc0 or 0xc1 ? 3 : 2);
        if (opcode is >= 0x88 and <= 0x8b or 0x84 or 0x85) return true;
        if (opcode is 0xf6 or 0xf7) return (modrm & 0x38) is 0 or 16 or 24;
        if (opcode is 0xfe or 0xff) return (modrm & 0x38) <= 8 || opcode==0xff && (modrm & 0x38)==48;
        if (opcode is 0x86 or 0x87) return modrm >= 0xc0;
        if (opcode == 0x8d) return modrm < 0xc0;
        if (opcode is 0xc6 or 0xc7)
            return (modrm & 0x38) == 0 && bytes.Length >= 2 + (opcode == 0xc6 ? 1 : width);
        if (opcode is 0x80 or 0x81 or 0x83)
            return bytes.Length >= 2 + (opcode == 0x81 ? width : 1);
        return false;
    }

    /// <summary>Evaluates the current register expression; source encoding and displacement alone are cacheable.</summary>
    private uint DecodedOffset(in DecodedAddress address)
    {
        uint result = address.Displacement;
        if (address.Basis >= 0) result = unchecked(result + ReadRegister(address.Basis, address.Narrow ? 2 : 4));
        if (address.Index >= 0) result = unchecked(result + (ReadRegister(address.Index, address.Narrow ? 2 : 4) << address.Shift));
        return address.Narrow ? result & 65535 : result;
    }

    /// <summary>Revalidates live data segments, access rights, dirty state, alignment, RAM decode and code-write aliases.</summary>
    private bool AdmitDecodedMemory(in DecodedElement element, ReadOnlySpan<byte> wholeCode, out IntegerMemoryPlan plan)
    {
        plan = default;
        bool write = element.MemoryKind is IntegerMemoryKind.MoveWrite or IntegerMemoryKind.MoveImmediate or IntegerMemoryKind.IncDec or IntegerMemoryKind.Unary ||
            element.MemoryKind is IntegerMemoryKind.AluMemory or IntegerMemoryKind.AluImmediate && element.Operation != 7;
        uint offset = DecodedOffset(element.Address);
        Segment segment = element.Address.Segment;
        if (!SafeRepSegment(segment, offset, element.Width, write)) return false;
        uint linear = unchecked(GetSegmentBase(segment) + offset);
        if ((linear & 4095) + (uint)element.Width > 4096 ||
            AlignmentCheckEnabled && (linear & (uint)(element.Width - 1)) != 0 ||
            !CachedRamTranslation(linear, write, out uint physical, out int slot)) return false;
        if(_memory.TryGetSinglePageRamSpan(physical, element.Width, out var data))
        {
            if(write && ((ReadOnlySpan<byte>)data).Overlaps(wholeCode))return false;
            plan=new(element.Length,element.Width,element.CodeTouches,slot,element.Source,element.Operation,element.MemoryKind,data,element.Immediate);
            return true;
        }
        if(element.Width!=1 || element.MemoryKind is not (IntegerMemoryKind.MoveWrite or IntegerMemoryKind.MoveImmediate) ||
            !_memory.TryPlanSimpleByteWrite(physical,out var device) || !device.Valid || device.Aliases(wholeCode))return false;
        plan=new(element.Length,element.Width,element.CodeTouches,slot,element.Source,element.Operation,element.MemoryKind,default,element.Immediate,device);
        return true;
    }

    /// <summary>Dispatches one previously decoded nonfaulting register, address-expression or near-branch operation.</summary>
    private void ExecuteDecodedInteger(in DecodedElement element, uint branchTarget)
    {
        int width = element.Width, destination = element.Destination, source = element.Source;
        uint value;
        switch (element.Kind)
        {
            case DecodedIntegerKind.Nop: break;
            case DecodedIntegerKind.MoveImmediate: WriteRegister(destination, width, element.Immediate); break;
            case DecodedIntegerKind.MoveRegister: WriteRegister(destination, width, ReadRegister(source, width)); break;
            case DecodedIntegerKind.AluRegister:
                value = Alu(element.Operation, ReadRegister(destination, width), ReadRegister(source, width), width);
                if (element.Operation != 7) WriteRegister(destination, width, value);
                break;
            case DecodedIntegerKind.AluImmediate:
                value = Alu(element.Operation, ReadRegister(destination, width), element.Immediate, width);
                if (element.Operation != 7) WriteRegister(destination, width, value);
                break;
            case DecodedIntegerKind.TestRegister: Alu(4, ReadRegister(destination, width), ReadRegister(source, width), width); break;
            case DecodedIntegerKind.TestImmediate: Alu(4, ReadRegister(destination, width), element.Immediate, width); break;
            case DecodedIntegerKind.UnaryRegister:
                value = ReadRegister(destination, width);
                WriteRegister(destination, width, element.Operation == 2 ? ~value : Alu(5, 0, value, width));
                break;
            case DecodedIntegerKind.ShiftRegister:
                Shift(new(true, destination, 0, 0), element.Operation, width,
                    element.Source != 0 ? (int)ReadRegister(1, 1) : (int)element.Immediate);
                break;
            case DecodedIntegerKind.DoubleShiftRegister:
            {
                int count=(element.Operation&1)==0?(int)element.Immediate&31:(int)ReadRegister(1,1)&31;
                uint original=ReadRegister(destination,width),other=ReadRegister(source,width);
                if(count==0)break;
                int bits=width*8;bool shiftRight=(element.Operation&8)!=0;
                ulong pair=shiftRight?((ulong)other<<bits)|original:((ulong)original<<bits)|other;
                uint shifted=shiftRight?(uint)(pair>>count):(uint)((pair<<count)>>bits);
                bool shiftedCarry=shiftRight?((pair>>(count-1))&1)!=0:((pair>>(bits*2-count))&1)!=0;
                WriteRegister(destination,width,shifted);SetFlag(Carry,shiftedCarry);SetSzp(shifted,width);
                if(count==1)SetFlag(Overflow,((original^shifted)&(1u<<(bits-1)))!=0);
                break;
            }
            case DecodedIntegerKind.MultiplyImmediate:
            case DecodedIntegerKind.MultiplyRegister:
                long left = Signed(ReadRegister(source, width), width);
                long right = element.Kind == DecodedIntegerKind.MultiplyRegister ? Signed(ReadRegister(destination, width), width)
                    : element.Operation == 0x6b ? unchecked((sbyte)element.Immediate) : Signed(element.Immediate, width);
                long product = left * right;
                WriteRegister(destination, width, unchecked((uint)product));
                bool overflow = product != Signed(unchecked((uint)product), width);
                SetFlag(Carry, overflow); SetFlag(Overflow, overflow);
                break;
            case DecodedIntegerKind.IncDec:
                uint carry = Eflags & Carry;
                WriteRegister(destination, width, Alu(element.Operation, ReadRegister(destination, width), 1, width));
                Eflags = (Eflags & ~Carry) | carry;
                break;
            case DecodedIntegerKind.Exchange:
                value = ReadRegister(destination, width);
                WriteRegister(destination, width, ReadRegister(source, width)); WriteRegister(source, width, value);
                break;
            case DecodedIntegerKind.EffectiveAddress: WriteRegister(destination, width, DecodedOffset(element.Address)); break;
            case DecodedIntegerKind.SignExtend:
                if (element.Operation == 0x98)
                    WriteRegister(0, width, width == 2 ? unchecked((uint)(short)(sbyte)ReadRegister(0, 1)) : unchecked((uint)(int)(short)ReadRegister(0, 2)));
                else WriteRegister(2, width, (ReadRegister(0, width) & (1u << (width * 8 - 1))) != 0 ? Mask(width) : 0);
                break;
            case DecodedIntegerKind.Flag:
                switch (element.Operation)
                {
                    case 0x9e: Eflags = (Eflags & ~0xd5u) | (ReadRegister(4, 1) & 0xd5); break;
                    case 0x9f: WriteRegister(4, 1, Eflags & 255); break;
                    case 0xf5: Eflags ^= Carry; break;
                    case 0xf8: Eflags &= ~Carry; break;
                    case 0xf9: Eflags |= Carry; break;
                    case 0xfc: Eflags &= ~Direction; break;
                    case 0xfd: Eflags |= Direction; break;
                }
                break;
            case DecodedIntegerKind.Bswap: WriteRegister(destination, 4, BinaryPrimitives.ReverseEndianness(ReadRegister(destination, 4))); break;
            case DecodedIntegerKind.MoveExtend:
                value = ReadRegister(source, element.Operation & 3);
                if ((element.Operation & 4) != 0) value = unchecked((uint)Signed(value, element.Operation & 3));
                WriteRegister(destination, width, value); break;
            case DecodedIntegerKind.SetCondition: WriteRegister(destination, 1, Condition(element.Operation) ? 1u : 0); break;
            case DecodedIntegerKind.Branch: Eip = branchTarget; break;
        }
    }

    /// <summary>Copies at most 64 complete same-page instructions and ends before unsupported bytes or after one near branch.</summary>
    private static DecodedBlock DecodeIntegerBlock(ReadOnlySpan<byte> source, uint ip, bool code32, in SegmentDescriptorCache code)
    {
        var elements = new List<DecodedElement>(64); int used = 0;
        while (elements.Count < 64 && used < source.Length && TryDecodeInteger(source[used..], code32, out var element) &&
            WithinSegment(code, unchecked(ip + (uint)used), (uint)element.Length))
        {
            elements.Add(element); used += element.Length;
            if (element.Kind == DecodedIntegerKind.Branch ||
                element.Kind == DecodedIntegerKind.Stack && element.Operation is 0xe8 or 0xc2 or 0xc3) break;
        }
        // Empty/unsupported plans also guard every potentially inspected x86 instruction byte.
        int guarded = used == 0 ? Math.Min(15, source.Length) : used;
        return new(source[..guarded].ToArray(), elements.ToArray());
    }

    /// <summary>Reads a complete immediate from known code storage without executing a fetch or mutating any CPU state.</summary>
    private static uint DecodeImmediate(ReadOnlySpan<byte> bytes, int width) => width == 1 ? bytes[0]
        : width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    /// <summary>Parses encoding facts only; taken branch conditions and all data-address effects remain runtime decisions.</summary>
    private static bool TryDecodeInteger(ReadOnlySpan<byte> bytes, bool code32, out DecodedElement element)
    {
        element = default;
        bytes = bytes[..Math.Min(15, bytes.Length)];
        int prefixes = DecodeIntegerPrefixes(bytes, out byte state);
        if (prefixes >= bytes.Length || !TryDecodeIntegerCore(bytes[prefixes..],
            code32 ^ (state & 1) != 0, code32 ^ (state & 2) != 0, out element)) return false;
        var address = element.Address;
        if (state >> 2 is int segment && segment != 0) address = address with { Segment = (Segment)(segment - 1) };
        element = element with { Length = element.Length + prefixes, CodeTouches = element.CodeTouches + prefixes,
            PrefixState = state, Address = address };
        return true;
    }

    /// <summary>Repeated size prefixes retain the scalar default-relative rule; LOCK and REP remain scalar.</summary>
    private static int DecodeIntegerPrefixes(ReadOnlySpan<byte> bytes, out byte state)
    {
        state = 0; int used = 0;
        while (used < Math.Min(15, bytes.Length))
        {
            byte prefix = bytes[used];
            if (prefix == 0x66) state |= 1;
            else if (prefix == 0x67) state |= 2;
            else if (prefix is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65)
            {
                int segment = prefix switch { 0x26 => 0, 0x2e => 1, 0x36 => 2, 0x3e => 3, 0x64 => 4, _ => 5 };
                state = (byte)((state & 3) | (segment + 1) << 2);
            }
            else break;
            used++;
        }
        return used;
    }

    /// <summary>Decodes independent operand/address widths while retaining the scalar opcode semantics.</summary>
    private static bool TryDecodeIntegerCore(ReadOnlySpan<byte> bytes, bool operand32, bool address32, out DecodedElement element)
    {
        element = default;
        if (bytes.IsEmpty) return false;
        byte opcode = bytes[0]; int width = operand32 ? 4 : 2;
        if (opcode == 0x90) { element = new(DecodedIntegerKind.Nop, 1, width, 1); return true; }
        if (opcode is 0xc0 or 0xc1 or 0xd0 or 0xd1 or 0xd2 or 0xd3)
        {
            bool immediate = opcode is 0xc0 or 0xc1;
            if (bytes.Length < (immediate ? 3 : 2) || bytes[1] < 0xc0) return false;
            element = new(DecodedIntegerKind.ShiftRegister, immediate ? 3 : 2, (opcode & 1) == 0 ? 1 : width,
                immediate ? 3 : 2, bytes[1] & 7, opcode is 0xd2 or 0xd3 ? 1 : 0,
                (bytes[1] >> 3) & 7, immediate ? bytes[2] : 1u);
            return true;
        }
        if (opcode is 0x69 or 0x6b)
        {
            int count = opcode == 0x6b ? 1 : width;
            if (bytes.Length < 2 + count || bytes[1] < 0xc0) return false;
            element = new(DecodedIntegerKind.MultiplyImmediate, 2 + count, width, 3,
                (bytes[1] >> 3) & 7, bytes[1] & 7, opcode, DecodeImmediate(bytes[2..], count));
            return true;
        }
        if(opcode==0xff && bytes.Length>=2 && (bytes[1]&0x38)==48)
        {
            if(bytes[1]>=0xc0)element=new(DecodedIntegerKind.Stack,2,width,2,Source:bytes[1]&7,Operation:0xff);
            else
            {
                if(!DecodeAddress(bytes,address32,out var pushAddress,out int pushLength,out int pushTouches))return false;
                // Group-5 saves its full checkpoint before DecodeOperand. Immediate's
                // sequential fallback therefore fetches every displacement byte separately.
                element=new(DecodedIntegerKind.Stack,pushLength,width,pushLength,Source:-1,Operation:0xff,
                    Address:pushAddress,MemoryKind:IntegerMemoryKind.MoveRead);
            }
            return true;
        }
        if (opcode is 0xfe or 0xff)
        {
            if (bytes.Length < 2 || (bytes[1] & 0x38) > 8) return false;
            int size = opcode == 0xfe ? 1 : width, operation = (bytes[1] & 8) == 0 ? 0 : 5;
            if (bytes[1] >= 0xc0)
                element = new(DecodedIntegerKind.IncDec, 2, size, 2, bytes[1] & 7, Operation: operation);
            else
            {
                if (!DecodeAddress(bytes, address32, out var counterAddress, out int counterLength, out int counterTouches)) return false;
                element = new(DecodedIntegerKind.Memory, counterLength, size, counterTouches, Operation: operation,
                    Address: counterAddress, MemoryKind: IntegerMemoryKind.IncDec);
            }
            return true;
        }
        if (opcode is 0xf6 or 0xf7)
        {
            if (bytes.Length < 2) return false;
            int groupOperation = (bytes[1] >> 3) & 7, groupWidth = opcode == 0xf6 ? 1 : width;
            if (groupOperation == 0 && bytes[1] >= 0xc0)
            {
                if (bytes.Length < 2 + groupWidth) return false;
                element = new(DecodedIntegerKind.TestImmediate, 2 + groupWidth, groupWidth, 3,
                    bytes[1] & 7, Immediate: DecodeImmediate(bytes[2..], groupWidth));
                return true;
            }
            if(groupOperation==0)
            {
                if(!DecodeAddress(bytes,address32,out var testAddress,out int testLength,out int testTouches) ||
                    bytes.Length<testLength+groupWidth)return false;
                element=new(DecodedIntegerKind.Memory,testLength+groupWidth,groupWidth,testTouches+1,
                    Immediate:DecodeImmediate(bytes[testLength..],groupWidth),Address:testAddress,MemoryKind:IntegerMemoryKind.TestImmediate);
                return true;
            }
            if (groupOperation is not (2 or 3)) return false;
            if (bytes[1] >= 0xc0)
                element = new(DecodedIntegerKind.UnaryRegister, 2, groupWidth, 2, bytes[1] & 7, Operation: groupOperation);
            else
            {
                if (!DecodeAddress(bytes, address32, out var unaryAddress, out int unaryLength, out int unaryTouches)) return false;
                element = new(DecodedIntegerKind.Memory, unaryLength, groupWidth, unaryTouches,
                    Operation: groupOperation, Address: unaryAddress, MemoryKind: IntegerMemoryKind.Unary);
            }
            return true;
        }
        if (opcode is >= 0x50 and <= 0x5f or 0x60 or 0x61 or 0xc3 or 0xc9)
        { element = new(DecodedIntegerKind.Stack, 1, width, 1, Operation: opcode); return true; }
        if (opcode is 0x68 or 0x6a)
        {
            int count = opcode == 0x6a ? 1 : width;
            if (bytes.Length < 1 + count) return false;
            uint immediate = DecodeImmediate(bytes[1..], count);
            if (opcode == 0x6a) immediate = unchecked((uint)(sbyte)immediate);
            element = new(DecodedIntegerKind.Stack, 1 + count, width, 2, Operation: opcode, Immediate: immediate);
            return true;
        }
        if (opcode is 0xe8 or 0xc2)
        {
            int size = opcode == 0xe8 ? width : 2;
            if (bytes.Length < 1 + size) return false;
            uint immediate = DecodeImmediate(bytes[1..], size);
            if (opcode == 0xe8 && width == 2) immediate = unchecked((uint)(short)immediate);
            element = new(DecodedIntegerKind.Stack, 1 + size, width, 2, Operation: opcode, Immediate: immediate);
            return true;
        }
        if (opcode is >= 0x40 and <= 0x4f)
        { element = new(DecodedIntegerKind.IncDec, 1, width, 1, opcode & 7, Operation: opcode < 0x48 ? 0 : 5); return true; }
        if (opcode is >= 0x91 and <= 0x97)
        { element = new(DecodedIntegerKind.Exchange, 1, width, 1, 0, opcode & 7); return true; }
        if (opcode is 0x98 or 0x99)
        { element = new(DecodedIntegerKind.SignExtend, 1, width, 1, Operation: opcode); return true; }
        if (opcode is 0x9e or 0x9f or 0xf5 or 0xf8 or 0xf9 or 0xfc or 0xfd)
        { element = new(DecodedIntegerKind.Flag, 1, width, 1, Operation: opcode); return true; }
        if (opcode is >= 0xb0 and <= 0xbf or 0xa8 or 0xa9 || opcode <= 0x3d && (opcode & 7) is 4 or 5)
        {
            bool move = opcode is >= 0xb0 and <= 0xbf, test = opcode is 0xa8 or 0xa9;
            int size = move ? opcode < 0xb8 ? 1 : width : (opcode & 1) == 0 ? 1 : width;
            if (bytes.Length < 1 + size) return false;
            element = new(move ? DecodedIntegerKind.MoveImmediate : test ? DecodedIntegerKind.TestImmediate : DecodedIntegerKind.AluImmediate,
                1 + size, size, 2, move ? opcode & 7 : 0, Operation: test ? 4 : opcode >> 3, Immediate: DecodeImmediate(bytes[1..], size));
            return true;
        }
        if (opcode is >= 0x70 and <= 0x7f or 0xeb or 0xe9)
        {
            int size = opcode == 0xe9 ? width : 1;
            if (bytes.Length < 1 + size) return false;
            uint delta = DecodeImmediate(bytes[1..], size);
            if (size == 1) delta = unchecked((uint)(sbyte)delta); else if (size == 2) delta = unchecked((uint)(short)delta);
            element = new(DecodedIntegerKind.Branch, 1 + size, width, 2, Operation: opcode is 0xe9 or 0xeb ? 16 : opcode & 15, Immediate: delta);
            return true;
        }
        if (opcode is >= 0xa0 and <= 0xa3)
        {
            int size = (opcode & 1) == 0 ? 1 : width, count = address32 ? 4 : 2;
            if (bytes.Length < 1 + count) return false;
            element = new(DecodedIntegerKind.Memory, 1 + count, size, 2, Address:
                new(-1, -1, 0, DecodeImmediate(bytes[1..], count), Segment.Ds, !address32),
                MemoryKind: opcode >= 0xa2 ? IntegerMemoryKind.MoveWrite : IntegerMemoryKind.MoveRead);
            return true;
        }
        if (opcode == 0x0f)
        {
            if (bytes.Length < 2) return false;
            byte second = bytes[1];
            if (second is >= 0xc8 and <= 0xcf)
            { element = new(DecodedIntegerKind.Bswap, 2, 4, 2, second & 7); return true; }
            if (second is >= 0x80 and <= 0x8f)
            {
                if (bytes.Length < 2 + width) return false;
                uint delta = DecodeImmediate(bytes[2..], width); if (width == 2) delta = unchecked((uint)(short)delta);
                element = new(DecodedIntegerKind.Branch, 2 + width, width, 3, Operation: second & 15, Immediate: delta); return true;
            }
            if (bytes.Length < 3) return false;
            int register = (bytes[2] >> 3) & 7, rm = bytes[2] & 7;
            if(second is 0xa4 or 0xa5 or 0xac or 0xad)
            {
                int size=(second&1)==0?4:3;
                if(bytes.Length<size || bytes[2]<0xc0)return false;
                element=new(DecodedIntegerKind.DoubleShiftRegister,size,width,size,rm,register,second,
                    Immediate:size==4?bytes[3]:0u);
                return true;
            }
            if (bytes[2] < 0xc0)
            {
                if (second is not (0xb6 or 0xb7 or 0xbe or 0xbf) ||
                    !DecodeAddress(bytes[1..], address32, out var extendAddress, out int extendLength, out int extendTouches)) return false;
                int sourceWidth = (second & 1) == 0 ? 1 : 2;
                element = new(DecodedIntegerKind.Memory, 1 + extendLength, sourceWidth, 1 + extendTouches,
                    Source: register, Operation: width | (second >= 0xbe ? 16 : 0), Address: extendAddress,
                    MemoryKind: IntegerMemoryKind.ExtendRead);
                return true;
            }
            if (second == 0xaf)
            { element = new(DecodedIntegerKind.MultiplyRegister, 3, width, 3, register, rm); return true; }
            if (second is >= 0x90 and <= 0x9f)
            { element = new(DecodedIntegerKind.SetCondition, 3, 1, 3, rm, Operation: second & 15); return true; }
            if (second is 0xb6 or 0xb7 or 0xbe or 0xbf)
            {
                int encodedWidthAndSign = (second & 1) == 0 ? 1 : 2;
                if (second >= 0xbe) encodedWidthAndSign |= 4;
                element = new(DecodedIntegerKind.MoveExtend, 3, width, 3, register, rm, encodedWidthAndSign); return true;
            }
            return false;
        }
        bool alu = opcode <= 0x3b && (opcode & 7) <= 3;
        bool moveRegister = opcode is >= 0x88 and <= 0x8b, testRegister = opcode is 0x84 or 0x85;
        bool exchange = opcode is 0x86 or 0x87, group = opcode is 0x80 or 0x81 or 0x83, moveGroup = opcode is 0xc6 or 0xc7;
        if (!alu && !moveRegister && !testRegister && !exchange && !group && !moveGroup && opcode != 0x8d || bytes.Length < 2) return false;
        byte modrm = bytes[1]; int reg = (modrm >> 3) & 7, operand = modrm & 7;
        int operandWidth = opcode == 0x80 || opcode == 0xc6 ? 1 : (opcode & 1) == 0 && opcode != 0x8d ? 1 : width;
        if (modrm >= 0xc0)
        {
            if (opcode == 0x8d) return false;
            if (group || moveGroup)
            {
                if (moveGroup && reg != 0) return false;
                int count = opcode == 0x83 ? 1 : operandWidth;
                if (bytes.Length < 2 + count) return false;
                uint immediate = DecodeImmediate(bytes[2..], count);
                if (opcode == 0x83) immediate = unchecked((uint)(sbyte)immediate);
                element = new(moveGroup ? DecodedIntegerKind.MoveImmediate : DecodedIntegerKind.AluImmediate,
                    2 + count, operandWidth, 3, operand, Operation: reg, Immediate: immediate); return true;
            }
            bool destinationRm = (opcode & 2) == 0;
            element = new(alu ? DecodedIntegerKind.AluRegister : moveRegister ? DecodedIntegerKind.MoveRegister :
                testRegister ? DecodedIntegerKind.TestRegister : DecodedIntegerKind.Exchange, 2, operandWidth, 2,
                destinationRm || testRegister || exchange ? operand : reg,
                destinationRm || testRegister || exchange ? reg : operand, alu ? opcode >> 3 : 4);
            return true;
        }
        if (exchange || moveGroup && reg != 0 || !DecodeAddress(bytes, address32, out var address, out int length, out int touches)) return false;
        if (moveGroup)
        {
            if (bytes.Length < length + operandWidth) return false;
            element = new(DecodedIntegerKind.Memory, length + operandWidth, operandWidth, touches + 1,
                Immediate: DecodeImmediate(bytes[length..], operandWidth), Address: address, MemoryKind: IntegerMemoryKind.MoveImmediate);
            return true;
        }
        if (group)
        {
            int count = opcode == 0x83 ? 1 : operandWidth;
            if (bytes.Length < length + count) return false;
            uint immediate = DecodeImmediate(bytes[length..], count);
            if (opcode == 0x83) immediate = unchecked((uint)(sbyte)immediate);
            element = new(DecodedIntegerKind.Memory, length + count, operandWidth, touches + 1,
                Operation: reg, Immediate: immediate, Address: address, MemoryKind: IntegerMemoryKind.AluImmediate);
            return true;
        }
        if (opcode == 0x8d)
        { element = new(DecodedIntegerKind.EffectiveAddress, length, width, touches, reg, Address: address); return true; }
        IntegerMemoryKind kind = moveRegister ? opcode < 0x8a ? IntegerMemoryKind.MoveWrite : IntegerMemoryKind.MoveRead
            : testRegister ? IntegerMemoryKind.Test : (opcode & 2) == 0 ? IntegerMemoryKind.AluMemory : IntegerMemoryKind.AluRegister;
        element = new(DecodedIntegerKind.Memory, length, operandWidth, touches, Source: reg,
            Operation: testRegister ? 4 : opcode >> 3, Address: address, MemoryKind: kind);
        return true;
    }

    /// <summary>Decodes only static ModRM/SIB/base/index/scale/displacement facts, including BP/direct and no-base distinctions.</summary>
    private static bool DecodeAddress(ReadOnlySpan<byte> bytes, bool wide, out DecodedAddress address, out int length, out int touches)
    {
        address = default; length = touches = 2;
        int mod = bytes[1] >> 6, rm = bytes[1] & 7, basis, index = -1, shift = 0, displacementBytes = 0;
        Segment segment = Segment.Ds;
        if (!wide)
        {
            (basis, index) = rm switch { 0 => (3, 6), 1 => (3, 7), 2 => (5, 6), 3 => (5, 7),
                4 => (6, -1), 5 => (7, -1), 6 => (5, -1), _ => (3, -1) };
            if (rm is 2 or 3 or 6) segment = Segment.Ss;
            if (mod == 0 && rm == 6) { basis = -1; segment = Segment.Ds; displacementBytes = 2; }
            else if (mod != 0) displacementBytes = mod == 1 ? 1 : 2;
        }
        else
        {
            basis = rm;
            if (rm == 4)
            {
                if (bytes.Length < 3) return false;
                byte sib = bytes[length++]; touches++; basis = sib & 7; index = (sib >> 3) & 7;
                if (index == 4) index = -1;
                shift = sib >> 6;
            }
            if (mod == 0 && basis == 5) { basis = -1; displacementBytes = 4; }
            else { if (basis is 4 or 5) segment = Segment.Ss; if (mod != 0) displacementBytes = mod == 1 ? 1 : 4; }
        }
        if (bytes.Length < length + displacementBytes) return false;
        uint displacement = displacementBytes == 0 ? 0 : DecodeImmediate(bytes[length..], displacementBytes);
        if (displacementBytes == 1) displacement = unchecked((uint)(sbyte)displacement);
        if (displacementBytes != 0) { length += displacementBytes; touches++; }
        address = new(basis, index, shift, displacement, segment, !wide);
        return true;
    }
}
