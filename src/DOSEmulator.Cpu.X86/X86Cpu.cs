using DOSEmulator.Core;

namespace DOSEmulator.Cpu.X86;

public enum Register { Ax, Cx, Dx, Bx, Sp, Bp, Si, Di }
public enum Segment { Es, Cs, Ss, Ds, Fs, Gs }

public sealed class UnsupportedInstructionException(string message) : Exception(message);

/// <summary>
/// Development interpreter: a deliberately limited real-mode subset, not a complete 486.
/// Unimplemented instructions/modes stop with an explicit diagnostic, never act as NOPs.
/// </summary>
public sealed partial class X86Cpu
{
    public const uint Carry = 1, Parity = 4, Auxiliary = 0x10, Zero = 0x40, Sign = 0x80;
    public const uint Trap = 0x100, InterruptEnable = 0x200, Direction = 0x400, Overflow = 0x800;
    private readonly MemoryBus _memory;
    private readonly IoPortBus _io;
    private readonly uint[] _registers = new uint[8];
    private readonly ushort[] _segments = new ushort[6];
    private readonly uint[] _segmentBases = new uint[6];
    private int _instructionBytes;
    private bool _operand32, _address32;
    private Segment? _override;
    private byte _repeat;
    private int _interruptShadow;
    private uint _instructionStart;

    public uint Eip { get; set; }
    public uint Eflags { get; set; } = 2;
    public uint Cr0 { get; private set; }
    public bool Halted { get; private set; }
    public bool CanAcceptMaskableInterrupt => (Eflags & InterruptEnable) != 0 && _interruptShadow == 0;
    public long Instructions { get; private set; }
    public uint PhysicalInstructionAddress => unchecked(_segmentBases[(int)Segment.Cs] + Eip);
    public string Position => $"{GetSegment(Segment.Cs):X4}:{Eip:X8}";
    public event Action<uint, byte>? InstructionStarting;
    public event Action<byte>? InterruptEntered;

    public X86Cpu(MemoryBus memory, IoPortBus io)
    {
        _memory = memory;
        _io = io;
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_registers);
        Array.Clear(_segments);
        Array.Clear(_segmentBases);
        _segments[(int)Segment.Cs] = 0xf000;
        _segmentBases[(int)Segment.Cs] = 0xffff0000;
        Eip = 0xfff0;
        Eflags = 2;
        Cr0 = 0x60000010;
        _registers[(int)Register.Dx] = 0x00000400;
        Halted = false;
        _interruptShadow = 0;
        Instructions = 0;
    }

    public uint GetRegister(Register register) => _registers[(int)register];
    public void SetRegister(Register register, uint value) => _registers[(int)register] = value;
    public ushort GetSegment(Segment segment) => _segments[(int)segment];
    public uint GetSegmentBase(Segment segment) => _segmentBases[(int)segment];
    public void SetSegment(Segment segment, ushort value)
    {
        _segments[(int)segment] = value;
        _segmentBases[(int)segment] = (uint)value << 4;
    }

    private uint ReadRegister(int register, int width) => width switch
    {
        1 when register >= 4 => (_registers[register - 4] >> 8) & 0xff,
        1 => _registers[register] & 0xff,
        2 => _registers[register] & 0xffff,
        _ => _registers[register]
    };
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
    private static uint Mask(int width) => width == 1 ? 0xffu : width == 2 ? 0xffffu : uint.MaxValue;
    private int Width => _operand32 ? 4 : 2;
    private byte FetchByte()
    {
        if (++_instructionBytes > 15) throw new UnsupportedInstructionException("Instruction exceeds 15 bytes.");
        if (_instructionStart + _instructionBytes > 0x10000) throw Unsupported("Instruction crosses the real-mode CS limit; exception delivery is not implemented yet.");
        byte value = _memory.ReadByte(PhysicalInstructionAddress);
        Eip = (Eip + 1) & 0xffff;
        return value;
    }
    private uint Immediate(int width)
    {
        uint result = 0;
        for (int i = 0; i < width; i++) result |= (uint)FetchByte() << (i * 8);
        return result;
    }
    private uint ReadMemory(uint address, int width) => width switch
    {
        1 => _memory.ReadByte(address), 2 => _memory.ReadWord(address), _ => _memory.ReadDword(address)
    };
    private void WriteMemory(uint address, int width, uint value)
    {
        if (width == 1) _memory.WriteByte(address, (byte)value);
        else if (width == 2) _memory.WriteWord(address, (ushort)value);
        else _memory.WriteDword(address, value);
    }

    private readonly record struct Operand(bool IsRegister, int Register, uint Address, uint Offset);
    private Operand DecodeOperand(byte modrm)
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
            else { offset = _registers[baseRegister]; stackSegment = baseRegister is 4 or 5; }
            offset = unchecked(offset + index);
            if (mod == 1) offset = unchecked(offset + (uint)(sbyte)FetchByte());
            if (mod == 2) offset = unchecked(offset + Immediate(4));
        }
        Segment segment = _override ?? (stackSegment ? Segment.Ss : Segment.Ds);
        return new(false, 0, unchecked(_segmentBases[(int)segment] + offset), offset);
    }
    private static void CheckLimit(uint offset, int width)
    {
        if ((ulong)offset + (uint)width > 0x10000) throw Unsupported("Real-mode segment limit exceeded; exception delivery is not implemented yet.");
    }
    private uint Read(Operand operand, int width)
    {
        if (operand.IsRegister) return ReadRegister(operand.Register, width);
        CheckLimit(operand.Offset, width); return ReadMemory(operand.Address, width);
    }
    private void Write(Operand operand, int width, uint value)
    {
        if (operand.IsRegister) WriteRegister(operand.Register, width, value);
        else { CheckLimit(operand.Offset, width); WriteMemory(operand.Address, width, value); }
    }

    public void Push(uint value, int width)
    {
        ushort sp = unchecked((ushort)(ReadRegister(4, 2) - width));
        CheckLimit(sp, width);
        WriteRegister(4, 2, sp);
        WriteMemory(_segmentBases[(int)Segment.Ss] + sp, width, value);
    }
    public uint Pop(int width)
    {
        ushort sp = (ushort)ReadRegister(4, 2);
        CheckLimit(sp, width);
        uint value = ReadMemory(_segmentBases[(int)Segment.Ss] + sp, width);
        WriteRegister(4, 2, unchecked((ushort)(sp + width)));
        return value;
    }

    private void SetFlag(uint flag, bool value) => Eflags = value ? Eflags | flag : Eflags & ~flag;
    private void SetSzp(uint value, int width)
    {
        value &= Mask(width);
        SetFlag(Zero, value == 0);
        SetFlag(Sign, (value & (1u << (width * 8 - 1))) != 0);
        SetFlag(Parity, (System.Numerics.BitOperations.PopCount(value & 0xff) & 1) == 0);
    }
    private uint Alu(int operation, uint a, uint b, int width)
    {
        uint mask = Mask(width), sign = 1u << (width * 8 - 1);
        a &= mask; b &= mask;
        uint carryIn = (Eflags & Carry) != 0 ? 1u : 0;
        uint result;
        if (operation is 0 or 2)
        {
            ulong sum = (ulong)a + b + (operation == 2 ? carryIn : 0u);
            result = (uint)sum & mask;
            SetFlag(Carry, sum > mask);
            SetFlag(Overflow, ((~(a ^ b) & (a ^ result)) & sign) != 0);
            SetFlag(Auxiliary, ((a ^ b ^ result) & 0x10) != 0);
        }
        else if (operation is 3 or 5 or 7)
        {
            ulong subtrahend = (ulong)b + (operation == 3 ? carryIn : 0u);
            result = unchecked((uint)((ulong)a - subtrahend)) & mask;
            SetFlag(Carry, a < subtrahend);
            SetFlag(Overflow, (((a ^ b) & (a ^ result)) & sign) != 0);
            SetFlag(Auxiliary, ((a ^ b ^ result) & 0x10) != 0);
        }
        else
        {
            result = operation switch { 1 => a | b, 4 => a & b, 6 => a ^ b, _ => throw new InvalidOperationException() };
            Eflags &= ~(Carry | Overflow | Auxiliary);
        }
        SetSzp(result, width);
        Eflags |= 2;
        return result;
    }

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
    private void JumpRelative(int displacement) => Eip = unchecked((ushort)(Eip + displacement));
    private uint NearTarget(uint value)
    {
        if (_operand32) { CheckLimit(value, 1); return value; }
        return value & 0xffff;
    }
    public void EnterRealModeInterrupt(byte vector)
    {
        InterruptEntered?.Invoke(vector);
        Push(Eflags, 2); Push(GetSegment(Segment.Cs), 2); Push(Eip, 2);
        Eflags &= ~(InterruptEnable | Trap);
        uint table = (uint)vector * 4;
        Eip = _memory.ReadWord(table);
        SetSegment(Segment.Cs, _memory.ReadWord(table + 2));
        Halted = false;
        _interruptShadow = 0;
    }

    /// <returns>Approximate development timing in CPU clocks; not cycle-accurate 486 timing.</returns>
    public uint Step()
    {
        if (Halted) return 0;
        if (_interruptShadow > 0) _interruptShadow--;
        uint start = Eip;
        _instructionStart = start;
        uint physical = PhysicalInstructionAddress;
        _instructionBytes = 0; _operand32 = false; _address32 = false; _override = null; _repeat = 0;
        byte opcode;
        while (true)
        {
            opcode = FetchByte();
            if (opcode == 0x66) { _operand32 = true; continue; }
            if (opcode == 0x67) { _address32 = true; continue; }
            if (opcode is 0xf2 or 0xf3) { _repeat = opcode; continue; }
            if (opcode is 0x26 or 0x2e or 0x36 or 0x3e or 0x64 or 0x65)
            {
                _override = opcode switch { 0x26 => Segment.Es, 0x2e => Segment.Cs, 0x36 => Segment.Ss, 0x3e => Segment.Ds, 0x64 => Segment.Fs, _ => Segment.Gs };
                continue;
            }
            break;
        }
        InstructionStarting?.Invoke(physical, opcode);
        try { Execute(opcode); }
        catch (CpuFaultException fault)
        {
            Eip = start;
            EnterRealModeInterrupt(fault.Vector);
        }
        catch (UnsupportedInstructionException ex)
        {
            Eip = start;
            throw new UnsupportedInstructionException($"{Position} [{physical:X8}] opcode {opcode:X2}: {ex.Message}");
        }
        Instructions++;
        Eflags = (Eflags | 2) & 0x003f7fd7;
        return 4;
    }

    private void Execute(byte opcode)
    {
        int w = Width;
        if (opcode <= 0x3d && (opcode & 7) <= 5)
        {
            int operation = opcode >> 3, form = opcode & 7;
            int size = (form & 1) == 0 ? 1 : w;
            if (form <= 3)
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                uint a = form < 2 ? Read(rm, size) : ReadRegister(reg, size);
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
            case 0x07: SetSegment(Segment.Es, (ushort)Pop(w)); break;
            case 0x0e: Push(GetSegment(Segment.Cs), w); break;
            case 0x16: Push(GetSegment(Segment.Ss), w); break;
            case 0x17: SetSegment(Segment.Ss, (ushort)Pop(w)); _interruptShadow = 1; break;
            case 0x1e: Push(GetSegment(Segment.Ds), w); break;
            case 0x1f: SetSegment(Segment.Ds, (ushort)Pop(w)); break;
            case 0x60:
            {
                uint sp = ReadRegister(4, w);
                for (int i = 0; i < 8; i++) Push(i == 4 ? sp : ReadRegister(i, w), w);
                break;
            }
            case 0x61: for (int i = 7; i >= 0; i--) { uint v = Pop(w); if (i != 4) WriteRegister(i, w, v); } break;
            case 0x68: Push(Immediate(w), w); break;
            case 0x6a: Push(unchecked((uint)(sbyte)FetchByte()), w); break;
            case 0x80: case 0x81: case 0x83:
            {
                int size = opcode == 0x80 ? 1 : w;
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int op = (m >> 3) & 7;
                uint b = opcode == 0x83 ? unchecked((uint)(sbyte)FetchByte()) : Immediate(size);
                uint result = Alu(op, Read(rm, size), b, size);
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
                int size = opcode == 0x86 ? 1 : w; byte m = FetchByte(); Operand rm = DecodeOperand(m); int reg = (m >> 3) & 7;
                uint value = Read(rm, size); Write(rm, size, ReadRegister(reg, size)); WriteRegister(reg, size, value); break;
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
                else { SetSegment((Segment)seg, (ushort)Read(rm, 2)); if (seg == (int)Segment.Ss) _interruptShadow = 1; }
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
                Operand rm = DecodeOperand(m); Write(rm, w, Pop(w)); break;
            }
            case 0x90: break;
            case 0x98: WriteRegister(0, w, w == 2 ? unchecked((uint)(short)(sbyte)ReadRegister(0, 1)) : unchecked((uint)(int)(short)ReadRegister(0, 2))); break;
            case 0x99: WriteRegister(2, w, (ReadRegister(0, w) & (1u << (w * 8 - 1))) != 0 ? Mask(w) : 0); break;
            case 0x9c: Push(Eflags, w); break;
            case 0x9d: Eflags = (Pop(w) & 0x003f7fd7) | 2; break;
            case 0x9e: Eflags = (Eflags & ~0xd5u) | (ReadRegister(4, 1) & 0xd5); break;
            case 0x9f: WriteRegister(4, 1, Eflags & 0xff); break;
            case 0xa0: case 0xa1: case 0xa2: case 0xa3:
            {
                int size = (opcode & 1) == 0 ? 1 : w;
                uint offset = Immediate(_address32 ? 4 : 2); CheckLimit(offset, size);
                uint address = unchecked(_segmentBases[(int)(_override ?? Segment.Ds)] + offset);
                if (opcode < 0xa2) WriteRegister(0, size, ReadMemory(address, size));
                else WriteMemory(address, size, ReadRegister(0, size));
                break;
            }
            case 0xa4: case 0xa5: case 0xa6: case 0xa7: case 0xaa: case 0xab: case 0xac: case 0xad: case 0xae: case 0xaf:
            case 0x6c: case 0x6d: case 0x6e: case 0x6f: ExecuteString(opcode); break;
            case 0xa8: case 0xa9: { int size = opcode == 0xa8 ? 1 : w; Alu(4, ReadRegister(0, size), Immediate(size), size); break; }
            case 0xc2: { uint adjust = Immediate(2); Eip = NearTarget(Pop(w)); WriteRegister(4, 2, ReadRegister(4, 2) + adjust); break; }
            case 0xc3: Eip = NearTarget(Pop(w)); break;
            case 0xc6: case 0xc7:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m);
                if (((m >> 3) & 7) != 0) throw Unsupported("Invalid MOV encoding.");
                int size = opcode == 0xc6 ? 1 : w; Write(rm, size, Immediate(size)); break;
            }
            case 0xca: case 0xcb:
            {
                uint adjust = opcode == 0xca ? Immediate(2) : 0;
                Eip = NearTarget(Pop(w)); SetSegment(Segment.Cs, (ushort)Pop(w));
                WriteRegister(4, 2, ReadRegister(4, 2) + adjust); break;
            }
            case 0xcc: EnterRealModeInterrupt(3); break;
            case 0xcd: EnterRealModeInterrupt(FetchByte()); break;
            case 0xcf: Eip = NearTarget(Pop(w)); SetSegment(Segment.Cs, (ushort)Pop(w)); Eflags = Pop(w) | 2; break;
            case 0xe0: case 0xe1: case 0xe2: case 0xe3:
            {
                int rel = (sbyte)FetchByte(); int countWidth = _address32 ? 4 : 2;
                uint count = ReadRegister(1, countWidth);
                if (opcode != 0xe3) { count = (count - 1) & Mask(countWidth); WriteRegister(1, countWidth, count); }
                bool take = opcode == 0xe3 ? count == 0 : count != 0 && (opcode == 0xe2 || ((Eflags & Zero) != 0) == (opcode == 0xe1));
                if (take) JumpRelative(rel); break;
            }
            case 0xe4: case 0xe5: { ushort port = FetchByte(); int size = opcode == 0xe4 ? 1 : w; WriteRegister(0, size, _io.Read(port, size)); break; }
            case 0xe6: case 0xe7: { ushort port = FetchByte(); int size = opcode == 0xe6 ? 1 : w; _io.Write(port, ReadRegister(0, size), size); break; }
            case 0xe8: { int rel = w == 2 ? (short)Immediate(2) : unchecked((int)Immediate(4)); uint target = NearTarget(unchecked(Eip + (uint)rel)); Push(Eip, w); Eip = target; break; }
            case 0xe9: { int rel = w == 2 ? (short)Immediate(2) : unchecked((int)Immediate(4)); Eip = NearTarget(unchecked(Eip + (uint)rel)); break; }
            case 0xea: { uint ip = Immediate(w); ushort cs = (ushort)Immediate(2); if (ip > 0xffff) throw Unsupported("Real-mode CS limit exceeded."); SetSegment(Segment.Cs, cs); Eip = ip; break; }
            case 0xeb: JumpRelative((sbyte)FetchByte()); break;
            case 0xec: case 0xed: { int size = opcode == 0xec ? 1 : w; WriteRegister(0, size, _io.Read((ushort)ReadRegister(2, 2), size)); break; }
            case 0xee: case 0xef: { int size = opcode == 0xee ? 1 : w; _io.Write((ushort)ReadRegister(2, 2), ReadRegister(0, size), size); break; }
            case 0xf4: Halted = true; break;
            case 0xf5: Eflags ^= Carry; break;
            case 0xf8: Eflags &= ~Carry; break;
            case 0xf9: Eflags |= Carry; break;
            case 0xfa: Eflags &= ~InterruptEnable; break;
            case 0xfb: Eflags |= InterruptEnable; _interruptShadow = 1; break;
            case 0xfc: Eflags &= ~Direction; break;
            case 0xfd: Eflags |= Direction; break;
            case 0xfe: case 0xff:
            {
                byte m = FetchByte(); Operand rm = DecodeOperand(m); int op = (m >> 3) & 7; int size = opcode == 0xfe ? 1 : w;
                if (op <= 1)
                {
                    uint cf = Eflags & Carry; Write(rm, size, Alu(op == 0 ? 0 : 5, Read(rm, size), 1, size)); Eflags = (Eflags & ~Carry) | cf;
                }
                else if (opcode == 0xff && op == 2) { uint target = NearTarget(Read(rm, w)); Push(Eip, w); Eip = target; }
                else if (opcode == 0xff && op == 4) Eip = NearTarget(Read(rm, w));
                else if (opcode == 0xff && op is 3 or 5)
                {
                    if (rm.IsRegister) throw Unsupported("Far branch requires a memory pointer.");
                    CheckLimit(rm.Offset, w + 2);
                    uint target = Read(rm, w); CheckLimit(target, 1);
                    ushort selector = _memory.ReadWord(rm.Address + (uint)w);
                    if (op == 3) { Push(GetSegment(Segment.Cs), w); Push(Eip, w); }
                    SetSegment(Segment.Cs, selector); Eip = target;
                }
                else if (opcode == 0xff && op == 6) Push(Read(rm, w), w);
                else throw Unsupported("This group-5 operation is not implemented.");
                break;
            }
            default: if (!ExecuteExtended(opcode)) throw Unsupported("Instruction not implemented in the development interpreter."); break;
        }
    }

    private void ExecuteString(byte opcode)
    {
        int size = (opcode & 1) == 0 ? 1 : Width;
        int countWidth = _address32 ? 4 : 2;
        uint count = _repeat == 0 ? 1 : ReadRegister(1, countWidth);
        if (count == 0) return;
        uint si = ReadRegister(6, countWidth), di = ReadRegister(7, countWidth);
        uint source = unchecked(_segmentBases[(int)(_override ?? Segment.Ds)] + si);
        uint destination = unchecked(_segmentBases[(int)Segment.Es] + di);
        bool sourceUsed = opcode is 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0xac or 0xad or 0x6e or 0x6f;
        bool destinationUsed = opcode is 0xa4 or 0xa5 or 0xa6 or 0xa7 or 0xaa or 0xab or 0xae or 0xaf or 0x6c or 0x6d;
        if (sourceUsed) CheckLimit(si, size);
        if (destinationUsed) CheckLimit(di, size);
        switch (opcode)
        {
            case 0xa4: case 0xa5: WriteMemory(destination, size, ReadMemory(source, size)); break;
            case 0xa6: case 0xa7: Alu(7, ReadMemory(source, size), ReadMemory(destination, size), size); break;
            case 0xaa: case 0xab: WriteMemory(destination, size, ReadRegister(0, size)); break;
            case 0xac: case 0xad: WriteRegister(0, size, ReadMemory(source, size)); break;
            case 0xae: case 0xaf: Alu(7, ReadRegister(0, size), ReadMemory(destination, size), size); break;
            case 0x6c: case 0x6d: WriteMemory(destination, size, _io.Read((ushort)ReadRegister(2, 2), size)); break;
            case 0x6e: case 0x6f: _io.Write((ushort)ReadRegister(2, 2), ReadMemory(source, size), size); break;
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
            if (again) Eip = unchecked((ushort)(Eip - _instructionBytes));
        }
    }

    private static UnsupportedInstructionException Unsupported(string message) => new(message);
}
