namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    // Loading TR establishes the current TSS; it does not switch tasks or set TS.
    private void ExecuteSystemSegmentGroup()
    {
        byte modrm = FetchByte();
        int operation = (modrm >> 3) & 7;
        if (!ProtectedMode || Virtual8086 || operation >= 6) throw new CpuFaultException(6);
        if (operation is 2 or 3) RequirePrivilegeZero();
        Operand operand = DecodeOperand(modrm);
        if (operation is 4 or 5) { VerifySelector(operand, operation == 5); return; }
        if (operation < 2)
        {
            // Intel486's r/m16 form preserves the upper half of a register.
            Write(operand, 2, operation == 0 ? Ldtr : Tr);
            return;
        }
        ushort selector = (ushort)Read(operand, 2);
        if (operation == 2 && (selector & 0xfffc) == 0)
        {
            Ldtr = selector; _ldtCache = default;
            return;
        }
        if ((selector & 0xfffc) == 0 || (selector & 4) != 0) throw Gp(SelectorError(selector));
        Descriptor descriptor = ReadDescriptor(selector);
        var cache = descriptor.Cache;
        int kind = cache.Access & 15;
        if ((cache.Access & 0x10) != 0 || (operation == 2 ? kind != 2 : kind is not (1 or 9)))
            throw Gp(SelectorError(selector));
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, SelectorError(selector));
        if (operation == 2) { Ldtr = selector; _ldtCache = cache; }
        else
        {
            // TSS descriptors use bit 1 for busy, not the segment accessed bit.
            WriteMemory(unchecked(descriptor.Address + 5), 1, (uint)(cache.Access | 2), true);
            Tr = selector; _trCache = cache with { Access = (byte)(cache.Access | 2) };
        }
    }

    private Descriptor ValidateTransitionStack(ushort selector, int privilege, bool fromTss, uint external = 0)
    {
        byte invalid = fromTss ? (byte)10 : (byte)13;
        if ((selector & 0xfffc) == 0) throw new CpuFaultException(invalid, external);
        Descriptor descriptor;
        try { descriptor = ReadDescriptor(selector, invalid); }
        catch (CpuFaultException error) when (external != 0 && error.Vector is >= 10 and <= 13 && error.ErrorCode.HasValue)
        { throw new CpuFaultException(error.Vector, error.ErrorCode.Value | external); }
        var cache = descriptor.Cache;
        if ((selector & 3) != privilege || cache.Privilege != privilege ||
            (cache.Access & 0x10) == 0 || cache.Code || (cache.Access & 2) == 0)
            throw new CpuFaultException(invalid, SelectorError(selector) | external);
        // All otherwise valid but absent SS loads use #SS. The old 1992
        // IRET pseudocode's #NP contradicts Intel's later 486 protection text.
        if ((cache.Access & 0x80) == 0)
            throw new CpuFaultException(12, SelectorError(selector) | external);
        return descriptor;
    }

    private (ushort Selector, uint Pointer, Descriptor Descriptor) ReadPrivilegeStack(int privilege, uint external)
    {
        if (!_trCache.Usable || (_trCache.Access & 15) is not (3 or 11))
            throw new CpuFaultException(10, SelectorError(Tr) | external);
        bool tss32 = (_trCache.Access & 8) != 0;
        uint offset = tss32 ? 4u + (uint)privilege * 8 : 2u + (uint)privilege * 4;
        int width = tss32 ? 4 : 2;
        if (offset + (uint)width + 1 > _trCache.Limit) throw new CpuFaultException(10, SelectorError(Tr) | external);
        ProbeMemory(unchecked(_trCache.Base + offset), width + 2, supervisor: true);
        uint pointer = ReadMemory(unchecked(_trCache.Base + offset), width, true);
        ushort selector = (ushort)ReadMemory(unchecked(_trCache.Base + offset + (uint)width), 2, true);
        return (selector, pointer, ValidateTransitionStack(selector, privilege, true, external));
    }

    private static void CheckTransitionPushes(SegmentDescriptorCache cache, uint pointer, int width, int count, uint error = 0)
    {
        uint mask = cache.Default32 ? uint.MaxValue : 0xffff;
        for (int i = 0; i < count; i++)
        {
            pointer = unchecked(pointer - (uint)width) & mask;
            if (!WithinSegment(cache, pointer, (uint)width)) throw new CpuFaultException(12, error);
        }
    }

    private void InvalidateOuterDataSegments()
    {
        foreach (Segment segment in new[] { Segment.Es, Segment.Ds, Segment.Fs, Segment.Gs })
        {
            var cache = _segmentCaches[(int)segment];
            if (cache.Usable && !cache.Conforming && cache.Privilege < _cpl)
            {
                _segments[(int)segment] = 0;
                _segmentCaches[(int)segment] = default;
            }
        }
    }

    private void RestoreOuterStackPointer(uint pointer, ushort selector, int width)
    {
        if (StackAddressWidth == 2) WriteRegister((int)Register.Sp, 2, pointer);
        else if (width == 2)
        {
            // Intel's architecture-compatibility chapter records this 486
            // quirk: a 16-bit interlevel RET/IRET puts SS in ESP[31:16].
            SetRegister(Register.Sp, ((uint)selector << 16) | (pointer & 0xffff));
        }
        else SetRegister(Register.Sp, pointer);
    }

    private void PushFaultError(uint value, int width)
    {
        if (width == 2) { Push(value, width); return; }
        uint pointer = unchecked(StackPointer - 4) & Mask(StackAddressWidth);
        CheckSegment(Segment.Ss, pointer, 4, true);
        // A 486 reserves a dword but writes just the low word. Pentium's
        // zero-filled dword is a different model, not a padding invariant.
        WriteMemory(unchecked(GetSegmentBase(Segment.Ss) + pointer), 2, value);
        SetStackPointer(pointer);
    }
}
