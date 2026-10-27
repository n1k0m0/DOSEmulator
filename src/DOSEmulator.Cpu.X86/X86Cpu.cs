// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
using DOSEmulator.Core;
using System.Buffers.Binary;

namespace DOSEmulator.Cpu.X86;

public enum Register { Ax, Cx, Dx, Bx, Sp, Bp, Si, Di }
public enum Segment { Es, Cs, Ss, Ds, Fs, Gs }

public sealed class UnsupportedInstructionException(string message) : Exception(message);

/// <summary>
/// Development interpreter with real mode and a growing protected-mode subset, not a complete 486.
/// Unimplemented instructions/modes stop with an explicit diagnostic, never act as NOPs.
/// </summary>
public sealed partial class X86Cpu
{
    public const uint Carry = 1, Parity = 4, Auxiliary = 0x10, Zero = 0x40, Sign = 0x80;
    public const uint Trap = 0x100, InterruptEnable = 0x200, Direction = 0x400, Overflow = 0x800;
    // Early 486DX (reset signature 0400h): AC exists, CPUID/ID/VIF/VIP do not.
    private uint ImplementedFlags => Model == X86CpuModel.Intel486Dx ? 0x00077fd7u : 0x00277fd7u;
    private readonly MemoryBus _memory;
    private readonly IoPortBus _io;
    private readonly uint[] _registers = new uint[8];
    private readonly ushort[] _segments = new ushort[6];
    private int _instructionBytes;
    private bool _operand32, _address32;
    private Segment? _override;
    private byte _repeat;
    private int _interruptShadow;
    private uint _instructionStart;

    /// <summary>Current architectural instruction pointer; its effective width follows the active code/operand contract.</summary>
    public uint Eip { get; set; }
    /// <summary>Implemented architectural flags, including the model's stable reserved-bit policy.</summary>
    public uint Eflags { get; set; } = 2;
    /// <summary>Implemented control register zero, including PE, paging, alignment and numeric-error control bits.</summary>
    public uint Cr0 { get; private set; }
    /// <summary>Whether the guest CPU is halted awaiting an eligible event rather than actively fetching instructions.</summary>
    public bool Halted { get; private set; }
    /// <summary>Whether IF and the current instruction shadow permit accepting a maskable interrupt.</summary>
    public bool CanAcceptMaskableInterrupt => (Eflags & InterruptEnable) != 0 && _interruptShadow == 0;
    /// <summary>Count of executed/interpreter-accounted guest instruction elements under the reference timing model.</summary>
    public long Instructions { get; private set; }
    /// <summary>Current linear instruction address before paging translation.</summary>
    public uint LinearInstructionAddress => unchecked(GetSegmentBase(Segment.Cs) + Eip);
    /// <summary>Translated physical instruction address used by the diagnostic fetch view.</summary>
    public uint PhysicalInstructionAddress => MappedInstructionAddress ?? LinearInstructionAddress;
    /// <summary>Human-readable selector/instruction-pointer position for diagnostics.</summary>
    public string Position => $"{GetSegment(Segment.Cs):X4}:{Eip:X8}";
    public event Action<uint, byte>? InstructionStarting;
    public event Action<byte>? InterruptEntered;

    /// <summary>Initializes X86Cpu and connects the supplied dependencies before the component is used.</summary>
    public X86Cpu(MemoryBus memory, IoPortBus io) : this(memory, io, X86CpuModel.Intel486Dx) { }

    /// <summary>Constructs an explicitly selected core subset without changing the default 486 contract.</summary>
    public X86Cpu(MemoryBus memory, IoPortBus io, X86CpuModel model) : this(memory, io, model, null, 0) { }

    /// <summary>Binds only an explicit virtual scheduler/nominal frequency; the existing constructors retain their contracts.</summary>
    public X86Cpu(MemoryBus memory, IoPortBus io, X86CpuModel model, DeviceScheduler? timestampSource, uint timestampFrequency)
    {
        if (model is not (X86CpuModel.Intel486Dx or X86CpuModel.IntelP54cB5Subset)) throw new ArgumentOutOfRangeException(nameof(model));
        if ((timestampSource is null) != (timestampFrequency == 0) || model == X86CpuModel.Intel486Dx && timestampSource is not null)
            throw new ArgumentException("The P54C timestamp subset requires an explicit virtual clock/frequency pair.");
        Model = model; _timestampSource = timestampSource; TimestampFrequency = timestampFrequency;
        _memory = memory;
        _io = io;
        Reset();
    }

    /// <summary>Restarts the component using its documented reset state and the supplied reset parameters.</summary>
    public void Reset()
    {
        ulong timestampEpoch = _timestampSource?.ClockTicks(TimestampFrequency) ?? 0;
        Array.Clear(_registers);
        Array.Clear(_segments);
        ResetSegments();
        _segments[(int)Segment.Cs] = 0xf000;
        Eip = 0xfff0;
        Eflags = 2;
        Cr0 = 0x60000010;
        ResetSystemRegisters();
        Cr4 = 0; _tscEpochTicks = timestampEpoch;
        ResetPaging();
        _registers[(int)Register.Dx] = Model == X86CpuModel.Intel486Dx ? 0x00000400u : 0x00000524u;
        Halted = false;
        _interruptShadow = 0;
        _instructionBytes = 0; _instructionStart = 0;
        _operand32 = _address32 = false; _override = null; _repeat = 0;
        _lockPrefix = _lockedMemory = false;
        Instructions = 0;
        _waitingForNumericError = false;
        NumericErrorWaits = NumericErrorFaults = 0;
        _checkpointProtected = _controlCheckpointSaved = false; ControlCheckpointsSaved = 0;
        _repValid = false; RepBatchedElements = 0;
    }

    /// <summary>Returns the full stored general-purpose register; instruction-width access uses ReadRegister instead.</summary>
    public uint GetRegister(Register register) => _registers[(int)register];
    /// <summary>Replaces the full stored general-purpose register for controlled CPU setup and state restoration.</summary>
    public void SetRegister(Register register, uint value) => _registers[(int)register] = value;
    /// <summary>Returns the visible selector, independently of its hidden descriptor cache.</summary>
    public ushort GetSegment(Segment segment) => _segments[(int)segment];
    /// <summary>Returns the hidden segment base used for linear address formation.</summary>
    public uint GetSegmentBase(Segment segment) => _segmentCaches[(int)segment].Base;
    /// <summary>Loads a selector through the current real/protected/V86 segment-loading rules.</summary>
    public void SetSegment(Segment segment, ushort value) => LoadSegment(segment, value);

    /// <summary>Extracts the requested byte, word or doubleword, including the AH/CH/DH/BH aliases.</summary>
    private uint ReadRegister(int register, int width) => width switch
    {
        1 when register >= 4 => (_registers[register - 4] >> 8) & 0xff,
        1 => _registers[register] & 0xff,
        2 => _registers[register] & 0xffff,
        _ => _registers[register]
    };
    /// <summary>Updates the selected register width while retaining unaffected high bits and byte aliases.</summary>
    private void WriteRegister(int register, int width, uint value)
    {
        if (width == 1 && register >= 4)
            _registers[register - 4] = (_registers[register - 4] & 0xffff00ff) | ((value & 0xff) << 8);
        else
        {
            uint mask = Mask(width);
            _registers[register] = (_registers[register] & ~mask) | (value & mask);
        }
    }
    /// <summary>Returns the bit mask for the supported byte, word or doubleword access width.</summary>
    private static uint Mask(int width) => width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
    /// <summary>Current instruction operand width in bytes, independent from visible pixel dimensions.</summary>
    private int Width => _operand32 ? 4 : 2;
    /// <summary>Checks instruction length, CS and paging before fetching a byte and advancing the instruction pointer.</summary>
    private byte FetchByte()
    {
        if (++_instructionBytes > 15) throw Gp();
        CheckSegment(Segment.Cs, Eip, 1, fetch: true);
        uint physical = TranslateLinear(LinearInstructionAddress);
        if (_instructionBytes == 1) CheckInstructionBreakpoint(LinearInstructionAddress);
        if (_instructionBytes == 1) _firstInstructionPhysical = physical;
        // A device may call back into the CPU while fetching. Preserve the full
        // pre-instruction control state before allowing such observable reads.
        if (!_memory.TryReadRamByte(physical, out byte value))
        {
            if (_checkpointProtected && !_controlCheckpointSaved) EnsureControlCheckpoint();
            value = _memory.ReadByte(physical);
        }
        Eip = unchecked(Eip + 1);
        return value;
    }
    /// <summary>Fetches an immediate; a complete same-page RAM range shares one checked translation, while boundaries/devices retain ordered byte fetches.</summary>
    private uint Immediate(int width)
    {
        if (width == 1) return FetchByte();
        // No speculative code reads: only the bytes actually requested by this
        // decoder call, within CS, the 15-byte limit and a single linear page.
        // Control/device observers retain the original sequential contract.
        if (_instructionBytes > 0 && _instructionBytes <= 15 - width && !_controlCheckpointSaved && InstructionStarting is null)
        {
            var code = _segmentCaches[(int)Segment.Cs];
            uint linear = unchecked(code.Base + Eip);
            if (code.Usable && WithinSegment(code, Eip, (uint)width) && (linear & 4095) <= 4096 - width)
            {
                _instructionBytes++;
                uint physical = TranslateLinear(linear);
                // A page-table device may have changed CPU state in a callback.
                // Its checkpoint also forces the sequential remainder below.
                if (!_controlCheckpointSaved && _memory.TryGetRamSpan(physical, width, out var bytes))
                {
                    uint value = width == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    _instructionBytes += width - 1; Eip = unchecked(Eip + (uint)width);
                    return value;
                }
                if (_checkpointProtected && !_controlCheckpointSaved && !_memory.IsPlainRamByte(physical)) EnsureControlCheckpoint();
                uint first = _memory.ReadByte(physical);
                Eip = unchecked(Eip + 1);
                for (int i = 1; i < width; i++) first |= (uint)FetchByte() << (i * 8);
                return first;
            }
        }
        uint result = 0;
        for (int i = 0; i < width; i++) result |= (uint)FetchByte() << (i * 8);
        return result;
    }
    private uint _firstInstructionPhysical;

    private readonly record struct Operand(bool IsRegister, int Register, uint Address, uint Offset, Segment Segment = Segment.Ds);
    /// <summary>Decodes once with address size/segment overrides and optional post-POP ESP-base addressing.</summary>
    private Operand DecodeOperand(byte modrm, bool stackPointerAfterPop = false)
    {
        int mod = modrm >> 6, rm = modrm & 7;
        if (mod == 3) return new(true, rm, 0, 0);
        uint offset;
        bool stackSegment = false;
        if (!_address32)
        {
            uint bx = ReadRegister(3, 2), bp = ReadRegister(5, 2), si = ReadRegister(6, 2), di = ReadRegister(7, 2);
            offset = rm switch { 0 => bx + si, 1 => bx + di, 2 => bp + si, 3 => bp + di, 4 => si, 5 => di, 6 => bp, _ => bx };
            stackSegment = rm is 2 or 3 or 6;
            if (mod == 0 && rm == 6) { offset = Immediate(2); stackSegment = false; }
            if (mod == 1) offset = unchecked(offset + (uint)(sbyte)FetchByte());
            if (mod == 2) offset += Immediate(2);
            offset &= 0xffff;
        }
        else
        {
            int baseRegister = rm;
            uint index = 0;
            if (rm == 4)
            {
                byte sib = FetchByte();
                baseRegister = sib & 7;
                int indexRegister = (sib >> 3) & 7;
                if (indexRegister != 4) index = _registers[indexRegister] << (sib >> 6);
            }
            if (mod == 0 && baseRegister == 5) offset = Immediate(4);
            else
            {
                offset = _registers[baseRegister]; stackSegment = baseRegister is 4 or 5;
                // POP's ESP-based destination uses the full post-pop register, even
                // when SS.B wraps only SP. Decode code bytes once before reading data.
                if (stackPointerAfterPop && baseRegister == 4)
                    offset = StackAddressWidth == 2
                        ? (offset & 0xffff0000) | (unchecked(offset + (uint)Width) & 0xffff)
                        : unchecked(offset + (uint)Width);
            }
            offset = unchecked(offset + index);
            if (mod == 1) offset = unchecked(offset + (uint)(sbyte)FetchByte());
            if (mod == 2) offset = unchecked(offset + Immediate(4));
        }
        Segment segment = _override ?? (stackSegment ? Segment.Ss : Segment.Ds);
        return new(false, 0, unchecked(GetSegmentBase(segment) + offset), offset, segment);
    }
    /// <summary>Reads a decoded register/memory operand with the required segment, paging and write-intent checks.</summary>
    private uint Read(Operand operand, int width, bool writeIntent = false)
    {
        if (operand.IsRegister) return ReadRegister(operand.Register, width);
        CheckOperand(operand, width, writeIntent);
        return ReadMemory(operand.Address, width, writeIntent: writeIntent);
    }
    /// <summary>Writes a decoded register or a validated linear memory operand.</summary>
    private void Write(Operand operand, int width, uint value)
    {
        if (operand.IsRegister) WriteRegister(operand.Register, width, value);
        else { CheckOperand(operand, width, true); WriteMemory(operand.Address, width, value); }
    }

    /// <summary>Checks the prospective stack access before storing a value and committing the adjusted stack pointer.</summary>
    public void Push(uint value, int width)
    {
        uint sp = unchecked(StackPointer - (uint)width) & Mask(StackAddressWidth);
        CheckSegment(Segment.Ss, sp, width, true);
        WriteMemory(unchecked(GetSegmentBase(Segment.Ss) + sp), width, value);
        SetStackPointer(sp);
    }
    /// <summary>Reads a checked stack value and then advances the current stack pointer.</summary>
    public uint Pop(int width)
    {
        uint value = PeekStack(width);
        AdjustStack((uint)width);
        return value;
    }

    /// <summary>Changes only the requested EFLAGS bit and preserves unrelated flags.</summary>
    private void SetFlag(uint flag, bool value) => Eflags = value ? Eflags | flag : Eflags & ~flag;
    /// <summary>Computes sign, zero and low-byte parity for a width-masked arithmetic result.</summary>
    private void SetSzp(uint value, int width)
    {
        value &= Mask(width);
        uint flags = value == 0 ? Zero : 0;
        if ((value & (1u << (width * 8 - 1))) != 0) flags |= Sign;
        if ((System.Numerics.BitOperations.PopCount(value & 0xff) & 1) == 0) flags |= Parity;
        Eflags = (Eflags & ~(Zero | Sign | Parity)) | flags;
    }
    /// <summary>Evaluates the selected integer ALU operation and updates its architectural result flags.</summary>
    private uint Alu(int operation, uint a, uint b, int width)
    {
        uint mask = Mask(width), sign = 1u << (width * 8 - 1);
        a &= mask; b &= mask;
        uint carryIn = (Eflags & Carry) != 0 ? 1u : 0;
        uint result, flags = 0;
        if (operation is 0 or 2)
        {
            ulong sum = (ulong)a + b + (operation == 2 ? carryIn : 0u);
            result = (uint)sum & mask;
            if (sum > mask) flags |= Carry;
            if (((~(a ^ b) & (a ^ result)) & sign) != 0) flags |= Overflow;
            if (((a ^ b ^ result) & 0x10) != 0) flags |= Auxiliary;
        }
        else if (operation is 3 or 5 or 7)
        {
            ulong subtrahend = (ulong)b + (operation == 3 ? carryIn : 0u);
            result = unchecked((uint)((ulong)a - subtrahend)) & mask;
            if (a < subtrahend) flags |= Carry;
            if ((((a ^ b) & (a ^ result)) & sign) != 0) flags |= Overflow;
            if (((a ^ b ^ result) & 0x10) != 0) flags |= Auxiliary;
        }
        else
        {
            result = operation switch { 1 => a | b, 4 => a & b, 6 => a ^ b, _ => throw new InvalidOperationException() };
        }
        Eflags = (Eflags & ~(Carry | Overflow | Auxiliary)) | flags | 2;
        SetSzp(result, width);
        return result;
    }

    /// <summary>Evaluates an x86 condition code from the current arithmetic flags.</summary>
    private bool Condition(int code)
    {
        bool cf = (Eflags & Carry) != 0, zf = (Eflags & Zero) != 0, sf = (Eflags & Sign) != 0;
        bool of = (Eflags & Overflow) != 0, pf = (Eflags & Parity) != 0;
        return code switch
        {
            0 => of, 1 => !of, 2 => cf, 3 => !cf, 4 => zf, 5 => !zf, 6 => cf || zf, 7 => !cf && !zf,
            8 => sf, 9 => !sf, 10 => pf, 11 => !pf, 12 => sf != of, 13 => sf == of, 14 => zf || sf != of, _ => !zf && sf == of
        };
    }
    /// <summary>Adds a signed displacement and validates the resulting near code target.</summary>
    private void JumpRelative(int displacement) => Eip = NearTarget(unchecked(Eip + (uint)displacement));
    /// <summary>Applies operand-size truncation and validates the target against the current code segment.</summary>
    private uint NearTarget(uint value)
    {
        uint target = _operand32 ? value : value & 0xffff;
        CheckSegment(Segment.Cs, target, 1, fetch: true);
        return target;
    }
    /// <summary>Builds a real-mode interrupt frame and loads the destination through the current IDT.</summary>
    private void EnterRealInterrupt(byte vector)
    {
        uint offset = (uint)vector * 4;
        if (offset + 3 > Idtr.Limit)
            throw Unsupported("Real-mode IDT limit exceeded; nested exception/shutdown delivery is not implemented yet.");
        uint table = unchecked(Idtr.Base + offset);
        ushort targetIp = _memory.ReadWord(table), targetCs = _memory.ReadWord(unchecked(table + 2));
        InterruptEntered?.Invoke(vector);
        Push(Eflags, 2); Push(GetSegment(Segment.Cs), 2); Push(Eip, 2);
        Eflags &= ~(InterruptEnable | Trap);
        Eip = targetIp;
        SetSegment(Segment.Cs, targetCs);
        Halted = false;
        _interruptShadow = 0;
    }

    /// <returns>Approximate development timing in CPU clocks; not cycle-accurate 486 timing.</returns>
    public uint Step()
    {
        _repValid = false;
        if (Halted) return 0;
        // An unacknowledged external numeric error stops this instruction, not
        // the device clock or interrupt recognition. No instruction retires.
        if (WaitingForNumericError) return 4;
        _waitingForNumericError = false;
        if (_interruptShadow > 0) _interruptShadow--;
        uint start = Eip, previousFlags = Eflags;
        bool protectedStart = ProtectedMode;
        bool vmStart = Virtual8086;
        long previousTask = _taskSwitchEpoch;
        _checkpointProtected = protectedStart; _controlCheckpointSaved = false;
        _checkpointIp = start; _checkpointFlags = previousFlags; _checkpointEsp = GetRegister(Register.Sp);
        Span<uint> previousRegisters = stackalloc uint[8];
        if (protectedStart) _registers.AsSpan().CopyTo(previousRegisters);
        _instructionStart = start;
        _instructionBytes = 0; _operand32 = _address32 = CodeDefault32; _override = null; _repeat = 0;
        _lockPrefix = _lockedMemory = false;
        BeginDebugInstruction();
        byte opcode = 0;
        bool completed = false;
        try
        {
            while (true)
            {
                opcode = FetchByte();
                if (opcode == 0x66) { _operand32 = !CodeDefault32; continue; }
                if (opcode == 0x67) { _address32 = !CodeDefault32; continue; }
                if (opcode == 0xf0) { _lockPrefix = true; continue; }
                if (opcode is 0xf2 or 0xf3) { _repeat = opcode; continue; }
                if (opcode is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65)
                {
                    _override = opcode switch { 0x26 => Segment.Es, 0x2e => Segment.Cs, 0x36 => Segment.Ss, 0x3e => Segment.Ds, 0x64 => Segment.Fs, _ => Segment.Gs };
                    continue;
                }
                break;
            }
            if (protectedStart && (NeedsControlCheckpoint(opcode) || InstructionStarting is not null)) EnsureControlCheckpoint();
            InstructionStarting?.Invoke(_firstInstructionPhysical, opcode);
            Execute(opcode);
            if (_lockedMemory) LockedMemoryInstructions++;
            completed = true;
        }
        catch (NumericErrorWaitException)
        {
            _checkpointProtected = false;
            _debugDataActive = false;
            Eip = start;
            _waitingForNumericError = true;
            NumericErrorWaits++;
            return 4;
        }
        catch (CpuFaultException fault)
        {
            _debugDataActive = false;
            ClearDebugDeferral();
            if (!protectedStart) { Eip = start; Eflags = previousFlags; }
            else if (previousTask == _taskSwitchEpoch)
            {
                previousRegisters.CopyTo(_registers);
                if (_controlCheckpointSaved) RestoreControl(_controlCheckpoint);
                else { Eip = start; Eflags = previousFlags; }
            }
            _checkpointProtected = false;
            DeliverFault(fault);
        }
        catch (UnsupportedInstructionException ex)
        {
            _checkpointProtected = false;
            _debugDataActive = false;
            Eip = start;
            throw new UnsupportedInstructionException($"{Position} [{_firstInstructionPhysical:X8}] opcode {opcode:X2}: {ex.Message}");
        }
        Instructions++;
        if (protectedStart) ProtectedInstructions++;
        if (vmStart) Virtual8086Instructions++;
        bool repeatContinues = _repeat != 0 && Eip == start && opcode is 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0xaa or 0xab or 0xac or 0xad or 0xae or 0xaf or 0x6c or 0x6d or 0x6e or 0x6f;
        _checkpointProtected = false;
        if (completed && (!protectedStart || previousTask == _taskSwitchEpoch) && opcode is not (0xcf or 0x9d) && !repeatContinues) Eflags &= ~0x10000u;
        Eflags = (Eflags | 2) & ImplementedFlags;
        if (completed) CompleteDebugInstruction(previousFlags, !protectedStart || previousTask == _taskSwitchEpoch, repeatContinues);
        if (completed && repeatContinues && Eip == start && opcode is 0xa4 or 0xa5 or 0xaa or 0xab) RememberRep(opcode);
        return 4;
    }

    /// <summary>Dispatches a decoded primary opcode, including prefix validity and operand-width rules.</summary>
    private void Execute(byte opcode)
    {
        ValidateLockOpcode(opcode);
        int w = Width;
        if (opcode <= 0x3d && (opcode & 7) <= 5)
        {
            int operation = opcode >> 3, form = opcode & 7;
            int size = (form & 1) == 0 ? 1 : w;
            if (form <= 3)
            {
                byte m = FetchByte(); ValidateLockOperand(m); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                uint a = form < 2 ? Read(rm, size, operation != 7) : ReadRegister(reg, size);
                uint b = form < 2 ? ReadRegister(reg, size) : Read(rm, size);
                uint result = Alu(operation, a, b, size);
                if (operation != 7) { if (form < 2) Write(rm, size, result); else WriteRegister(reg, size, result); }
            }
            else
            {
                uint result = Alu(operation, ReadRegister(0, size), Immediate(size), size);
                if (operation != 7) WriteRegister(0, size, result);
            }
            return;
        }
        if (opcode is >= 0x40 and <= 0x4f)
        {
            uint cf = Eflags & Carry; int reg = opcode & 7;
            WriteRegister(reg, w, Alu(opcode < 0x48 ? 0 : 5, ReadRegister(reg, w), 1, w));
            Eflags = (Eflags & ~Carry) | cf;
            return;
        }
        if (opcode is >= 0x50 and <= 0x57) { Push(ReadRegister(opcode & 7, w), w); return; }
        if (opcode is >= 0x58 and <= 0x5f) { WriteRegister(opcode & 7, w, Pop(w)); return; }
        if (opcode is >= 0x70 and <= 0x7f) { int rel = (sbyte)FetchByte(); if (Condition(opcode & 15)) JumpRelative(rel); return; }
        if (opcode is >= 0xb0 and <= 0xb7) { WriteRegister(opcode & 7, 1, Immediate(1)); return; }
        if (opcode is >= 0xb8 and <= 0xbf) { WriteRegister(opcode & 7, w, Immediate(w)); return; }
        if (opcode is >= 0x91 and <= 0x97)
        {
            int reg = opcode & 7; uint tmp = ReadRegister(0, w);
            WriteRegister(0, w, ReadRegister(reg, w)); WriteRegister(reg, w, tmp); return;
        }
        switch (opcode)
        {
            case 0x06: Push(GetSegment(Segment.Es), w); break;
            case 0x07: PopSegment(Segment.Es, w); break;
            case 0x0e: Push(GetSegment(Segment.Cs), w); break;
            case 0x16: Push(GetSegment(Segment.Ss), w); break;
            case 0x17: PopSegment(Segment.Ss, w); break;
            case 0x1e: Push(GetSegment(Segment.Ds), w); break;
            case 0x1f: PopSegment(Segment.Ds, w); break;
            case 0x60:
            {
                CheckPushes(w, 8); uint sp = ReadRegister(4, w);
                for (int i = 0; i < 8; i++) Push(i == 4 ? sp : ReadRegister(i, w), w);
                break;
            }
            case 0x61:
                CheckSegment(Segment.Ss, StackPointer, w * 8); CheckAlignment(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), w);
                ProbeMemory(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), w * 8);
                for (int i = 7; i >= 0; i--) { uint v = Pop(w); if (i != 4) WriteRegister(i, w, v); } break;
            case 0x68: Push(Immediate(w), w); break;
            case 0x6a: Push(unchecked((uint)(sbyte)FetchByte()), w); break;
            case 0x80: case 0x81: case 0x83:
            {
                int size = opcode == 0x80 ? 1 : w;
                byte m = FetchByte(); int op = (m >> 3) & 7; ValidateLockOperand(m, op != 7); Operand rm = DecodeOperand(m);
                uint b = opcode == 0x83 ? unchecked((uint)(sbyte)FetchByte()) : Immediate(size);
                uint result = Alu(op, Read(rm, size, op != 7), b, size);
                if (op != 7) Write(rm, size, result);
                break;
            }
            case 0x84: case 0x85:
            {
                int size = opcode == 0x84 ? 1 : w; byte m = FetchByte(); Operand rm = DecodeOperand(m);
                Alu(4, Read(rm, size), ReadRegister((m >> 3) & 7, size), size); break;
            }
            case 0x86: case 0x87:
            {
                int size = opcode == 0x86 ? 1 : w; byte m = FetchByte(); ValidateLockOperand(m); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                _lockedMemory = !rm.IsRegister;
                uint value = Read(rm, size, true); Write(rm, size, ReadRegister(reg, size)); WriteRegister(reg, size, value); break;
            }
            case 0x88: case 0x89: case 0x8a: case 0x8b:
            {
                int size = (opcode & 1) == 0 ? 1 : w; byte m = FetchByte(); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                if (opcode < 0x8a) Write(rm, size, ReadRegister(reg, size)); else WriteRegister(reg, size, Read(rm, size)); break;
            }
            case 0x8c: case 0x8e:
            {
                byte m = FetchByte(); int seg = (m >> 3) & 7; Operand rm = DecodeOperand(m);
                if (seg > 5 || (opcode == 0x8e && seg == 1)) throw Unsupported("Invalid segment-register encoding.");
                if (opcode == 0x8c) Write(rm, rm.IsRegister ? w : 2, GetSegment((Segment)seg));
                else { SetSegment((Segment)seg, (ushort)Read(rm, 2)); if (seg == (int)Segment.Ss) BeginSsShadow(); }
                break;
            }
            case 0x8d:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m);
                if (rm.IsRegister) throw Unsupported("LEA requires memory addressing.");
                WriteRegister((m >> 3) & 7, w, rm.Offset); break;
            }
            case 0x8f:
            {
                byte m = FetchByte(); if (((m >> 3) & 7) != 0) throw Unsupported("Invalid POP encoding.");
                Operand rm = DecodeOperand(m, stackPointerAfterPop: true);
                if (rm.IsRegister) Write(rm, w, Pop(w)); // POP ESP replaces the post-pop pointer.
                else
                {
                    // Keep SP unchanged on a destination fault, including real mode.
                    uint value = PeekStack(w); Write(rm, w, value); AdjustStack((uint)w);
                }
                break;
            }
            case 0x90: break;
            case 0x98: WriteRegister(0, w, w == 2 ? unchecked((uint)(short)(sbyte)ReadRegister(0, 1)) : unchecked((uint)(int)(short)ReadRegister(0, 2))); break;
            case 0x99: WriteRegister(2, w, (ReadRegister(0, w) & (1u << (w * 8 - 1))) != 0 ? Mask(w) : 0); break;
            case 0x9c: CheckVmSensitive(); Push(Eflags & ~0x30000u, w); break;
            case 0x9d: CheckVmSensitive(); RestoreFlags(Pop(w), w); break;
            case 0x9e: Eflags = (Eflags & ~0xd5u) | (ReadRegister(4, 1) & 0xd5); break;
            case 0x9f: WriteRegister(4, 1, Eflags & 0xff); break;
            case 0xa0: case 0xa1: case 0xa2: case 0xa3:
            {
                int size = (opcode & 1) == 0 ? 1 : w;
                uint offset = Immediate(_address32 ? 4 : 2); Segment segment = _override ?? Segment.Ds;
                CheckSegment(segment, offset, size, opcode >= 0xa2);
                uint address = unchecked(GetSegmentBase(segment) + offset);
                if (opcode < 0xa2) WriteRegister(0, size, ReadMemory(address, size));
                else WriteMemory(address, size, ReadRegister(0, size));
                break;
            }
            case 0xa4: case 0xa5: case 0xa6: case 0xa7: case 0xaa: case 0xab: case 0xac: case 0xad: case 0xae: case 0xaf:
            case 0x6c: case 0x6d: case 0x6e: case 0x6f: ExecuteString(opcode); break;
            case 0xa8: case 0xa9: { int size = opcode == 0xa8 ? 1 : w; Alu(4, ReadRegister(0, size), Immediate(size), size); break; }
            case 0xc2: ReturnNear(w, Immediate(2)); break;
            case 0xc3: ReturnNear(w); break;
            case 0xc6: case 0xc7:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m);
                if (((m >> 3) & 7) != 0) throw Unsupported("Invalid MOV encoding.");
                int size = opcode == 0xc6 ? 1 : w; Write(rm, size, Immediate(size)); break;
            }
            case 0xca: case 0xcb: ReturnFar(w, opcode == 0xca ? Immediate(2) : 0); break;
            case 0xcc: DispatchInterrupt(3, true); break;
            case 0xcd: { byte vector = FetchByte(); CheckVmSensitive(); DispatchInterrupt(vector, true); break; }
            case 0xcf: ReturnFar(w, interrupt: true); break;
            case 0xe0: case 0xe1: case 0xe2: case 0xe3:
            {
                int rel = (sbyte)FetchByte(); int countWidth = _address32 ? 4 : 2;
                uint count = ReadRegister(1, countWidth);
                if (opcode != 0xe3) { count = (count - 1) & Mask(countWidth); WriteRegister(1, countWidth, count); }
                bool take = opcode == 0xe3 ? count == 0 : count != 0 && (opcode == 0xe2 || ((Eflags & Zero) != 0) == (opcode == 0xe1));
                if (take) JumpRelative(rel); break;
            }
            case 0xe4: case 0xe5: { ushort port = FetchByte(); int size = opcode == 0xe4 ? 1 : w; CheckPortAccess(port,size); WriteRegister(0, size, ReadPort(port, size)); break; }
            case 0xe6: case 0xe7: { ushort port = FetchByte(); int size = opcode == 0xe6 ? 1 : w; CheckPortAccess(port,size); WritePort(port, ReadRegister(0, size), size); break; }
            case 0xe8: { int rel = w == 2 ? (short)Immediate(2) : unchecked((int)Immediate(4)); uint target = NearTarget(unchecked(Eip + (uint)rel)); Push(Eip, w); Eip = target; break; }
            case 0xe9: { int rel = w == 2 ? (short)Immediate(2) : unchecked((int)Immediate(4)); Eip = NearTarget(unchecked(Eip + (uint)rel)); break; }
            case 0xea: { uint ip = Immediate(w); ushort cs = (ushort)Immediate(2); FarTransfer(cs, ip, false, w); break; }
            case 0xeb: JumpRelative((sbyte)FetchByte()); break;
            case 0xec: case 0xed: { int size = opcode == 0xec ? 1 : w; ushort port=(ushort)ReadRegister(2,2);CheckPortAccess(port,size);WriteRegister(0, size, ReadPort(port, size)); break; }
            case 0xee: case 0xef: { int size = opcode == 0xee ? 1 : w; ushort port=(ushort)ReadRegister(2,2);CheckPortAccess(port,size);WritePort(port, ReadRegister(0, size), size); break; }
            case 0xf4: RequirePrivilegeZero(); Halted = true; break;
            case 0xf5: Eflags ^= Carry; break;
            case 0xf8: Eflags &= ~Carry; break;
            case 0xf9: Eflags |= Carry; break;
            case 0xfa: CheckIoPrivilege(); Eflags &= ~InterruptEnable; break;
            case 0xfb:
                CheckIoPrivilege();
                // STI delays recognition only when it changes IF from zero to one.
                // An existing SS/STI boundary is consumed by Step, not extended here.
                if ((Eflags & InterruptEnable) == 0) _interruptShadow = 1;
                Eflags |= InterruptEnable; break;
            case 0xfc: Eflags &= ~Direction; break;
            case 0xfd: Eflags |= Direction; break;
            case 0xfe: case 0xff:
            {
                byte m = FetchByte(); int op = (m >> 3) & 7;
                // INC/DEC only change the operand and flags. Calls/jumps retain
                // the full control restart record before hidden state can change.
                if (opcode == 0xff && op > 1 && _checkpointProtected) EnsureControlCheckpoint();
                ValidateLockOperand(m, op <= 1); Operand rm = DecodeOperand(m); int size = opcode == 0xfe ? 1 : w;
                if (op <= 1)
                {
                    uint cf = Eflags & Carry; Write(rm, size, Alu(op == 0 ? 0 : 5, Read(rm, size, true), 1, size)); Eflags = (Eflags & ~Carry) | cf;
                }
                else if (opcode == 0xff && op == 2) { uint target = NearTarget(Read(rm, w)); Push(Eip, w); Eip = target; }
                else if (opcode == 0xff && op == 4) Eip = NearTarget(Read(rm, w));
                else if (opcode == 0xff && op is 3 or 5)
                {
                    if (rm.IsRegister) throw Unsupported("Far branch requires a memory pointer.");
                    CheckOperand(rm, w + 2);
                    CheckAlignment(rm.Address, w);
                    ProbeMemory(rm.Address, w + 2);
                    uint target = Read(rm, w);
                    ushort selector = (ushort)ReadMemory(unchecked(rm.Address + (uint)w), 2);
                    FarTransfer(selector, target, op == 3, w);
                }
                else if (opcode == 0xff && op == 6) Push(Read(rm, w), w);
                else throw Unsupported("This group-5 operation is not implemented.");
                break;
            }
            default: if (!ExecuteExtended(opcode)) throw Unsupported("Instruction not implemented in the development interpreter."); break;
        }
    }

    /// <summary>Executes one architecturally observable string element and preserves REP restart boundaries.</summary>
    private void ExecuteString(byte opcode)
    {
        int size = (opcode & 1) == 0 ? 1 : Width;
        int countWidth = _address32 ? 4 : 2;
        uint count = _repeat == 0 ? 1 : ReadRegister(1, countWidth);
        if (count == 0) return;
        uint si = ReadRegister(6, countWidth), di = ReadRegister(7, countWidth);
        uint source = unchecked(GetSegmentBase(_override ?? Segment.Ds) + si);
        uint destination = unchecked(GetSegmentBase(Segment.Es) + di);
        bool sourceUsed = opcode is 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0xac or 0xad or 0x6e or 0x6f;
        bool destinationUsed = opcode is 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0xaa or 0xab or 0xae or 0xaf or 0x6c or 0x6d;
        if (sourceUsed) CheckSegment(_override ?? Segment.Ds, si, size);
        if (destinationUsed) CheckSegment(Segment.Es, di, size, opcode is 0xa4 or 0xa5 or 0xaa or 0xab or 0x6c or 0x6d);
        if (opcode is >= 0x6c and <= 0x6f) CheckPortAccess((ushort)ReadRegister(2,2),size);
        if (sourceUsed) CheckAlignment(source, size);
        if (destinationUsed) CheckAlignment(destination, size);
        if (sourceUsed) ProbeMemory(source, size);
        if (destinationUsed) ProbeMemory(destination, size, opcode is 0xa4 or 0xa5 or 0xaa or 0xab or 0x6c or 0x6d);
        switch (opcode)
        {
            case 0xa4: case 0xa5: WriteMemory(destination, size, ReadMemory(source, size)); break;
            case 0xa6: case 0xa7: Alu(7, ReadMemory(source, size), ReadMemory(destination, size), size); break;
            case 0xaa: case 0xab: WriteMemory(destination, size, ReadRegister(0, size)); break;
            case 0xac: case 0xad: WriteRegister(0, size, ReadMemory(source, size)); break;
            case 0xae: case 0xaf: Alu(7, ReadRegister(0, size), ReadMemory(destination, size), size); break;
            case 0x6c: case 0x6d: ProbeMemory(destination, size, true); WriteMemory(destination, size, ReadPort((ushort)ReadRegister(2, 2), size)); break;
            case 0x6e: case 0x6f: WritePort((ushort)ReadRegister(2, 2), ReadMemory(source, size), size); break;
        }
        int delta = (Eflags & Direction) == 0 ? size : -size;
        if (sourceUsed) WriteRegister(6, countWidth, unchecked(si + (uint)delta));
        if (destinationUsed) WriteRegister(7, countWidth, unchecked(di + (uint)delta));
        if (_repeat != 0)
        {
            count--; WriteRegister(1, countWidth, count);
            bool compare = opcode is 0xa6 or 0xa7 or 0xae or 0xaf;
            bool again = count != 0 && (!compare || ((Eflags & Zero) != 0) == (_repeat == 0xf3));
            // One element per Step keeps long REP operations interruptible and device time moving.
            if (again) Eip = _instructionStart;
        }
    }

    /// <summary>Creates a diagnostic unsupported-operation exception rather than claiming an unimplemented instruction succeeded.</summary>
    private static UnsupportedInstructionException Unsupported(string message) => new(message);
}
