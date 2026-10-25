namespace DOSEmulator.Cpu.X86;

/// <summary>The hidden part of a segment register, independent of later table edits.</summary>
public readonly record struct SegmentDescriptorCache(uint Base, uint Limit, byte Access, byte Flags, bool Usable)
{
    public bool Default32 => (Flags & 4) != 0;
    public bool Code => (Access & 8) != 0;
    public bool Conforming => Code && (Access & 4) != 0;
    public bool ExpandDown => !Code && (Access & 4) != 0;
    public int Privilege => (Access >> 5) & 3;
}

/// <summary>System descriptors have type/busy bits rather than code/data rights.</summary>
public readonly record struct SystemDescriptorCache(uint Base, uint Limit, byte Access, byte Flags, bool Usable);

public sealed partial class X86Cpu
{
    private readonly SegmentDescriptorCache[] _segmentCaches = new SegmentDescriptorCache[6];
    private int _cpl;
    private SegmentDescriptorCache _ldtCache;
    private SegmentDescriptorCache _trCache;
    public ushort Ldtr { get; private set; }
    public ushort Tr { get; private set; }
    public SystemDescriptorCache LdtrCache => new(_ldtCache.Base, _ldtCache.Limit, _ldtCache.Access, _ldtCache.Flags, _ldtCache.Usable);
    public SystemDescriptorCache TrCache => new(_trCache.Base, _trCache.Limit, _trCache.Access, _trCache.Flags, _trCache.Usable);
    public bool ProtectedMode => (Cr0 & 1) != 0;
    public int CurrentPrivilegeLevel => Virtual8086 ? 3 : ProtectedMode ? _cpl : 0;
    public SegmentDescriptorCache GetSegmentCache(Segment segment) => _segmentCaches[(int)segment];
    private bool CodeDefault32 => !Virtual8086 && _segmentCaches[(int)Segment.Cs].Default32;
    private int StackAddressWidth => _segmentCaches[(int)Segment.Ss].Default32 ? 4 : 2;
    private uint StackPointer => ReadRegister((int)Register.Sp, StackAddressWidth);
    private void SetStackPointer(uint value) => WriteRegister((int)Register.Sp, StackAddressWidth, value);
    private void AdjustStack(uint bytes) => SetStackPointer(unchecked(StackPointer + bytes));
    private static uint SelectorError(ushort selector) => (uint)selector & 0xfffc;
    private static CpuFaultException Gp(uint code = 0) => new(13, code);

    private void ResetSegments()
    {
        for (int i = 0; i < 6; i++) _segmentCaches[i] = new(0, 0xffff, 0x93, 0, true);
        _segmentCaches[(int)Segment.Cs] = new(0xffff0000, 0xffff, 0x9b, 0, true);
        _cpl = 0; Ldtr = Tr = 0; _ldtCache = _trCache = default;
    }

    private void RequirePrivilegeZero()
    {
        if (ProtectedMode && CurrentPrivilegeLevel != 0) throw Gp();
    }

    private void CheckIoPrivilege()
    {
        if (ProtectedMode && CurrentPrivilegeLevel > ((Eflags >> 12) & 3)) throw Gp();
    }

    private readonly record struct Descriptor(SegmentDescriptorCache Cache, uint Address, uint Low = 0, uint High = 0);
    private Descriptor ReadDescriptor(ushort selector, byte faultVector = 13)
    {
        uint offset = (uint)selector & 0xfff8;
        uint basis, limit;
        if ((selector & 4) != 0)
        {
            if (!_ldtCache.Usable) throw new CpuFaultException(faultVector, SelectorError(selector));
            basis = _ldtCache.Base; limit = _ldtCache.Limit;
        }
        else { basis = Gdtr.Base; limit = Gdtr.Limit; }
        if (offset + 7 > limit) throw new CpuFaultException(faultVector, SelectorError(selector));
        uint address = unchecked(basis + offset);
        ProbeMemory(address, 8, supervisor: true);
        uint low = ReadMemory(address, 4, true), high = ReadMemory(unchecked(address + 4), 4, true);
        uint segmentBase = (low >> 16) | ((high & 0xff) << 16) | (high & 0xff000000);
        uint segmentLimit = (low & 0xffff) | (high & 0x000f0000);
        byte flags = (byte)((high >> 20) & 15);
        if ((flags & 8) != 0) segmentLimit = (segmentLimit << 12) | 0xfff;
        return new(new(segmentBase, segmentLimit, (byte)(high >> 8), flags, true), address, low, high);
    }

    private Descriptor ValidateDataSegment(Segment segment, ushort selector)
    {
        if ((selector & 0xfffc) == 0)
        {
            if (segment == Segment.Ss) throw Gp();
            return new(default, 0);
        }
        Descriptor descriptor = ReadDescriptor(selector);
        var cache = descriptor.Cache;
        bool application = (cache.Access & 0x10) != 0;
        if (segment == Segment.Ss)
        {
            if (!application || cache.Code || (cache.Access & 2) == 0 || cache.Privilege != _cpl || (selector & 3) != _cpl)
                throw Gp(SelectorError(selector));
        }
        else
        {
            if (!application || (cache.Code && (cache.Access & 2) == 0)) throw Gp(SelectorError(selector));
            if (!cache.Conforming && Math.Max(_cpl, selector & 3) > cache.Privilege) throw Gp(SelectorError(selector));
        }
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(segment == Segment.Ss ? (byte)12 : (byte)11, SelectorError(selector));
        return descriptor;
    }

    private Descriptor ValidateCodeSegment(ushort selector, uint target, bool returning = false, bool interrupt = false, Descriptor? loaded = null)
    {
        if ((selector & 0xfffc) == 0) throw Gp();
        Descriptor descriptor = loaded ?? ReadDescriptor(selector);
        var cache = descriptor.Cache;
        if ((cache.Access & 0x10) == 0)
        {
            throw Gp(SelectorError(selector));
        }
        if (!cache.Code) throw Gp(SelectorError(selector));
        if (interrupt)
        {
            if (cache.Privilege > _cpl) throw Gp(SelectorError(selector));
        }
        else if (returning)
        {
            int requested = selector & 3;
            if (requested < _cpl || (!cache.Conforming && cache.Privilege != requested) || (cache.Conforming && cache.Privilege > requested))
                throw Gp(SelectorError(selector));
        }
        else if ((selector & 3) > _cpl || (cache.Conforming ? cache.Privilege > _cpl : cache.Privilege != _cpl))
            throw Gp(SelectorError(selector));
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, SelectorError(selector));
        // Interrupts and outer returns validate their complete stack before the offset.
        if (!interrupt && !returning && target > cache.Limit) throw Gp();
        return descriptor;
    }

    private void CommitSegment(Segment segment, ushort selector, Descriptor descriptor)
    {
        var cache = descriptor.Cache;
        if (cache.Usable && (cache.Access & 1) == 0)
        {
            WriteMemory(unchecked(descriptor.Address + 5), 1, (uint)(cache.Access | 1), true);
            cache = cache with { Access = (byte)(cache.Access | 1) };
        }
        _segments[(int)segment] = selector;
        _segmentCaches[(int)segment] = cache;
    }

    private void LoadSegment(Segment segment, ushort selector)
    {
        if (Virtual8086) { LoadVirtualSegment(segment, selector); return; }
        if (!ProtectedMode)
        {
            _segments[(int)segment] = selector;
            // Mode switches do not synthesize new hidden limits. The controlled
            // return path must first load real-mode-compatible descriptors.
            _segmentCaches[(int)segment] = _segmentCaches[(int)segment] with { Base = (uint)selector << 4, Usable = true };
            return;
        }
        if (segment == Segment.Cs) throw new InvalidOperationException("CS must be loaded through a control transfer in protected mode.");
        CommitSegment(segment, selector, ValidateDataSegment(segment, selector));
    }

    private void CheckSegment(Segment segment, uint offset, int width, bool write = false, bool fetch = false)
    {
        var cache = _segmentCaches[(int)segment];
        byte vector = segment == Segment.Ss ? (byte)12 : (byte)13;
        if (!cache.Usable) throw new CpuFaultException(vector, 0);
        if (ProtectedMode && !Virtual8086 && !fetch && ((write && (cache.Code || (cache.Access & 2) == 0)) || (!write && cache.Code && (cache.Access & 2) == 0)))
            throw new CpuFaultException(vector, 0);
        if (!WithinSegment(cache, offset, (uint)width)) throw new CpuFaultException(vector, 0);
    }

    private static bool WithinSegment(SegmentDescriptorCache cache, uint offset, uint width)
    {
        ulong last = (ulong)offset + width - 1;
        return cache.ExpandDown
            ? offset > cache.Limit && last <= (cache.Default32 ? uint.MaxValue : 0xffffu)
            : last <= cache.Limit;
    }

    private void CheckOperand(Operand operand, int width, bool write = false)
    {
        if (!operand.IsRegister) CheckSegment(operand.Segment, operand.Offset, width, write);
    }
    private uint PeekStack(int width, uint displacement = 0)
    {
        uint offset = unchecked(StackPointer + displacement) & Mask(StackAddressWidth);
        CheckSegment(Segment.Ss, offset, width);
        return ReadMemory(unchecked(GetSegmentBase(Segment.Ss) + offset), width);
    }
    private void CheckPushes(int width, int count)
    {
        uint pointer = StackPointer;
        for (int i = 0; i < count; i++)
        {
            pointer = unchecked(pointer - (uint)width) & Mask(StackAddressWidth);
            CheckSegment(Segment.Ss, pointer, width, true);
        }
        ProbeStackWrites(GetSegmentCache(Segment.Ss), StackPointer, width, count);
    }
    private void PrepareSegment(Descriptor descriptor)
    {
        if (descriptor.Cache.Usable && (descriptor.Cache.Access & 1) == 0)
            ProbeMemory(unchecked(descriptor.Address + 5), 1, true, true);
    }
    private void ProbeStackWrites(SegmentDescriptorCache cache, uint pointer, int width, int count, bool errorSlot = false, bool supervisor = false)
    {
        uint mask = cache.Default32 ? uint.MaxValue : 0xffff;
        for (int i = 0; i < count; i++)
        {
            pointer = unchecked(pointer - (uint)width) & mask;
            CheckAlignment(unchecked(cache.Base + pointer), width, supervisor);
            ProbeMemory(unchecked(cache.Base + pointer), errorSlot && i == count - 1 ? 2 : width, true, supervisor);
        }
    }
    private void PopSegment(Segment segment, int width)
    {
        ushort selector = (ushort)PeekStack(width);
        // In particular, POP SS advances the OLD stack before installing its B bit.
        Descriptor descriptor = ProtectedMode && !Virtual8086 ? ValidateDataSegment(segment, selector) : default;
        AdjustStack((uint)width);
        if (ProtectedMode && !Virtual8086) CommitSegment(segment, selector, descriptor); else LoadSegment(segment, selector);
        if (segment == Segment.Ss) BeginSsShadow();
    }
}
