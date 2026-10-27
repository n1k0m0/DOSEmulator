// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    /// <summary>Whether the active protected execution context has EFLAGS.VM set.</summary>
    public bool Virtual8086 => ProtectedMode && (Eflags & 0x20000) != 0;
    /// <summary>Diagnostic count of virtual8086 entries.</summary>
    public long Virtual8086Entries { get; private set; }
    /// <summary>Diagnostic count of virtual8086 exits.</summary>
    public long Virtual8086Exits { get; private set; }
    /// <summary>Diagnostic count of virtual8086 instructions.</summary>
    public long Virtual8086Instructions { get; private set; }

    /// <summary>Installs the V86 selector/base/limit cache contract for a visible segment.</summary>
    private void LoadVirtualSegment(Segment segment, ushort selector)
    {
        _segments[(int)segment] = selector;
        _segmentCaches[(int)segment] = new((uint)selector << 4, 0xffff, segment == Segment.Cs ? (byte)0xfb : (byte)0xf3, 0, true);
    }
    /// <summary>Rejects V86 flag/interrupt-sensitive operations when IOPL does not permit them.</summary>
    private void CheckVmSensitive()
    {
        if (Virtual8086 && ((Eflags >> 12) & 3) != 3) throw Gp();
    }
    /// <summary>Validates the complete stacked V86 return frame before adopting its segments, stack and flags.</summary>
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

    /// <summary>Checks CPL/IOPL and the real TSS I/O bitmap for every byte of a port access.</summary>
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
