namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    public bool Virtual8086 => ProtectedMode && (Eflags & 0x20000) != 0;
    public long Virtual8086Entries { get; private set; }
    public long Virtual8086Exits { get; private set; }
    public long Virtual8086Instructions { get; private set; }

    private void LoadVirtualSegment(Segment segment, ushort selector)
    {
        _segments[(int)segment] = selector;
        _segmentCaches[(int)segment] = new((uint)selector << 4, 0xffff, segment == Segment.Cs ? (byte)0xfb : (byte)0xf3, 0, true);
    }
    private void CheckVmSensitive()
    {
        if (Virtual8086 && ((Eflags >> 12) & 3) != 3) throw Gp();
    }
    private void EnterVirtual8086(uint target, ushort cs, uint flags)
    {
        CheckSegment(Segment.Ss, StackPointer, 36);
        ProbeMemory(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), 36);
        uint pointer = PeekStack(4, 12);
        ushort ss = (ushort)PeekStack(4, 16), es = (ushort)PeekStack(4, 20), ds = (ushort)PeekStack(4, 24);
        ushort fs = (ushort)PeekStack(4, 28), gs = (ushort)PeekStack(4, 32);
        if (target > 0xffff) throw Gp();
        LoadVirtualSegment(Segment.Cs, cs); LoadVirtualSegment(Segment.Ss, ss);
        LoadVirtualSegment(Segment.Es, es); LoadVirtualSegment(Segment.Ds, ds);
        LoadVirtualSegment(Segment.Fs, fs); LoadVirtualSegment(Segment.Gs, gs);
        SetRegister(Register.Sp, pointer); Eip = target;
        Eflags = (flags & ImplementedFlags) | 2; _cpl = 3;
        Virtual8086Entries++;
    }

    private void CheckPortAccess(ushort port, int width)
    {
        if (!ProtectedMode || (!Virtual8086 && CurrentPrivilegeLevel <= ((Eflags >> 12) & 3))) return;
        if (!_trCache.Usable || (_trCache.Access & 15) != 11 || _trCache.Limit < 0x67) throw Gp();
        uint basis = ReadMemory(unchecked(_trCache.Base + 0x66), 2, true);
        uint offset = basis + ((uint)port >> 3);
        if (basis > 0xdfff || offset + 1 > _trCache.Limit || (uint)port + (uint)width > 0x10000) throw Gp();
        // The 486 always fetches TWO bitmap bytes, including the mandatory
        // trailing FF guard, even for one-byte I/O at an aligned bit position.
        uint permissions = ReadMemory(unchecked(_trCache.Base + offset), 2, true);
        uint mask = ((1u << width) - 1) << (port & 7);
        if ((permissions & mask) != 0) throw Gp();
    }
}
