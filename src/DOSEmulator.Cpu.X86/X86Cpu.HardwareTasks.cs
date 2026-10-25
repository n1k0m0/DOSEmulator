namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private enum TaskTransfer { Jump, Call, Interrupt, Return }
    private long _taskSwitchEpoch;
    public long HardwareTaskSwitches { get; private set; }
    public long HardwareTaskReturns { get; private set; }
    public long TaskGateTransfers { get; private set; }
    public long TaskDebugTraps { get; private set; }

    private Descriptor ReadTaskDescriptor(ushort selector, byte vector, uint external)
    {
        try { return ReadDescriptor(selector, vector); }
        catch (CpuFaultException error) when (error.Vector is >= 10 and <= 13 && error.ErrorCode.HasValue)
        { throw new CpuFaultException(error.Vector, error.ErrorCode.Value | external); }
    }

    private void TransferTask(ushort selector, Descriptor descriptor, bool call)
    {
        var cache = descriptor.Cache;
        uint error = SelectorError(selector);
        if (Math.Max(_cpl, selector & 3) > cache.Privilege) throw Gp(error);
        if ((cache.Access & 15) == 5)
        {
            if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, error);
            SwitchTask((ushort)(descriptor.Low >> 16), call ? TaskTransfer.Call : TaskTransfer.Jump, gate: true);
        }
        else SwitchTask(selector, call ? TaskTransfer.Call : TaskTransfer.Jump, loaded: descriptor);
    }

    private void ReturnTask()
    {
        CheckCurrentTask();
        ushort backlink = (ushort)ReadMemory(_trCache.Base, 2, true);
        SwitchTask(backlink, TaskTransfer.Return);
    }

    private void CheckCurrentTask(uint external = 0)
    {
        if (!_trCache.Usable || (Tr & 4) != 0 || (_trCache.Access & 15) is not (3 or 11) ||
            _trCache.Limit < ((_trCache.Access & 8) != 0 ? 0x67u : 0x2bu))
            throw new CpuFaultException(10, SelectorError(Tr) | external);
    }

    private void SwitchTask(ushort selector, TaskTransfer transfer, uint external = 0,
        Descriptor? loaded = null, bool gate = false, uint? errorCode = null, uint? savedFlags = null)
    {
        bool returning = transfer == TaskTransfer.Return;
        bool nested = transfer is TaskTransfer.Call or TaskTransfer.Interrupt;
        byte invalid = returning ? (byte)10 : (byte)13;
        uint error = SelectorError(selector) | external;
        if ((selector & 0xfffc) == 0 || (selector & 4) != 0) throw new CpuFaultException(invalid, error);
        Descriptor target = loaded ?? ReadTaskDescriptor(selector, invalid, external);
        var cache = target.Cache;
        int kind = cache.Access & 15;
        if ((cache.Access & 0x10) != 0 || (returning ? kind is not (3 or 11) : kind is not (1 or 9)))
            throw new CpuFaultException(invalid, error);
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, error);
        bool tss32 = (kind & 8) != 0;
        if (cache.Limit < (tss32 ? 0x67u : 0x2bu)) throw new CpuFaultException(10, error);
        CheckCurrentTask(external);

        // These accesses use the OUTGOING address space and supervisor rights.
        // Buffer the incoming integer state, and preflight all outgoing writes.
        // Descriptor qualification below deliberately happens AFTER the commit.
        uint basis = cache.Base;
        int width = tss32 ? 4 : 2;
        uint ipOffset = tss32 ? 0x20u : 0x0eu;
        uint registerOffset = tss32 ? 0x28u : 0x12u;
        uint segmentOffset = tss32 ? 0x48u : 0x22u;
        uint newCr3 = tss32 ? ReadMemory(unchecked(basis + 0x1c), 4, true) : Cr3;
        uint ip = ReadMemory(unchecked(basis + ipOffset), width, true);
        uint flags = ReadMemory(unchecked(basis + ipOffset + (uint)width), width, true);
        Span<uint> registers = stackalloc uint[8];
        for (int i = 0; i < 8; i++) registers[i] = ReadMemory(unchecked(basis + registerOffset + (uint)(i * width)), width, true);
        Span<ushort> segments = stackalloc ushort[6];
        segments.Clear();
        for (int i = 0; i < (tss32 ? 6 : 4); i++)
            segments[i] = (ushort)ReadMemory(unchecked(basis + segmentOffset + (uint)(i * width)), 2, true);
        ushort ldt = (ushort)ReadMemory(unchecked(basis + (tss32 ? 0x60u : 0x2au)), 2, true);
        bool debug = tss32 && (ReadMemory(unchecked(basis + 0x64), 2, true) & 1) != 0;

        bool old32 = (_trCache.Access & 8) != 0;
        int oldWidth = old32 ? 4 : 2;
        uint oldBase = _trCache.Base;
        uint oldIp = old32 ? 0x20u : 0x0eu;
        uint oldRegisters = old32 ? 0x28u : 0x12u;
        uint oldSegments = old32 ? 0x48u : 0x22u;
        ProbeMemory(unchecked(oldBase + oldIp), oldWidth * 10, true, true);
        for (int i = 0; i < (old32 ? 6 : 4); i++)
            ProbeMemory(unchecked(oldBase + oldSegments + (uint)(i * oldWidth)), 2, true, true);
        uint oldBusyAddress = unchecked(Gdtr.Base + ((uint)Tr & 0xfff8) + 5);
        if (!nested && ((uint)Tr & 0xfff8) + 7 > Gdtr.Limit) throw new CpuFaultException(10, SelectorError(Tr) | external);
        if (!nested) ProbeMemory(oldBusyAddress, 1, true, true);
        if (!returning) ProbeMemory(unchecked(target.Address + 5), 1, true, true);
        if (nested) ProbeMemory(basis, 2, true, true);

        uint outgoingFlags = savedFlags ?? Eflags;
        if (returning) outgoingFlags &= ~0x4000u;
        WriteMemory(unchecked(oldBase + oldIp), oldWidth, Eip, true);
        WriteMemory(unchecked(oldBase + oldIp + (uint)oldWidth), oldWidth, outgoingFlags, true);
        for (int i = 0; i < 8; i++) WriteMemory(unchecked(oldBase + oldRegisters + (uint)(i * oldWidth)), oldWidth, _registers[i], true);
        for (int i = 0; i < (old32 ? 6 : 4); i++)
            WriteMemory(unchecked(oldBase + oldSegments + (uint)(i * oldWidth)), 2, _segments[i], true);
        // CR3, LDTR, ring stacks, I/O base and reserved padding are static TSS fields.
        if (!nested) WriteMemory(oldBusyAddress, 1, ReadMemory(oldBusyAddress, 1, true) & ~2u, true);
        if (nested) WriteMemory(basis, 2, Tr, true);
        if (!returning) WriteMemory(unchecked(target.Address + 5), 1, (uint)(cache.Access | 2), true);

        // Commit boundary: a later #TS/#NP/#SS/#PF belongs to the INCOMING task.
        // No instruction/interrupt checkpoint may restore the outgoing context.
        _taskSwitchEpoch++;
        Dr7 &= ~0x155u; // Local enables (including LE) belong to the old task.
        ClearDebugDeferral();
        _debugDataActive = false;
        HardwareTaskSwitches++;
        if (returning) HardwareTaskReturns++;
        if (gate) TaskGateTransfers++;
        bool oldVm = Virtual8086;
        Tr = selector; _trCache = cache with { Access = (byte)(cache.Access | 2) };
        Cr0 |= 8;
        if (tss32 && PagingEnabled) { Cr3 = newCr3; FlushTranslations(); }
        Eip = ip;
        Eflags = ((flags | (nested ? 0x4000u : 0)) & ImplementedFlags) | 2;
        for (int i = 0; i < 8; i++)
            // Upper halves on a 16-bit task load are architecturally undefined;
            // choose stable all-ones, never preserve another task's registers.
            _registers[i] = registers[i] | (tss32 ? 0u : 0xffff0000u);
        for (int i = 0; i < 6; i++) { _segments[i] = segments[i]; _segmentCaches[i] = default; }
        Ldtr = ldt; _ldtCache = default;
        _cpl = Virtual8086 ? 3 : segments[(int)Segment.Cs] & 3;
        Halted = false; _interruptShadow = 0;
        if (oldVm && !Virtual8086) Virtual8086Exits++;
        if (!oldVm && Virtual8086) Virtual8086Entries++;

        LoadTaskLdt(ldt, external);
        if (Virtual8086)
        {
            for (int i = 0; i < 6; i++) LoadVirtualSegment((Segment)i, segments[i]);
        }
        else
        {
            LoadTaskCode(segments[(int)Segment.Cs], external);
            CommitSegment(Segment.Ss, segments[(int)Segment.Ss], ValidateTransitionStack(segments[(int)Segment.Ss], _cpl, true, external));
            foreach (Segment segment in new[] { Segment.Es, Segment.Ds, Segment.Fs, Segment.Gs })
                LoadTaskData(segment, segments[(int)segment], external);
        }
        if (Eip > GetSegmentCache(Segment.Cs).Limit) throw Gp();
        if (errorCode is { } code) { CheckPushes(width, 1); PushFaultError(code, width); }
        if (debug)
        {
            TaskDebugTraps++;
            throw DebugException(0x8000, true);
        }
    }

    private void LoadTaskLdt(ushort selector, uint external)
    {
        if ((selector & 0xfffc) == 0) return;
        uint error = SelectorError(selector) | external;
        if ((selector & 4) != 0) throw new CpuFaultException(10, error);
        Descriptor descriptor = ReadTaskDescriptor(selector, 10, external);
        if ((descriptor.Cache.Access & 0x9f) != 0x82) throw new CpuFaultException(10, error);
        _ldtCache = descriptor.Cache;
    }

    private void LoadTaskCode(ushort selector, uint external)
    {
        uint error = SelectorError(selector) | external;
        if ((selector & 0xfffc) == 0) throw new CpuFaultException(10, external);
        Descriptor descriptor = ReadTaskDescriptor(selector, 10, external);
        var cache = descriptor.Cache;
        if ((cache.Access & 0x18) != 0x18 || (cache.Conforming ? cache.Privilege > _cpl : cache.Privilege != _cpl))
            throw new CpuFaultException(10, error);
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, error);
        CommitSegment(Segment.Cs, selector, descriptor);
    }

    private void LoadTaskData(Segment segment, ushort selector, uint external)
    {
        if ((selector & 0xfffc) == 0) return;
        Descriptor descriptor = ReadTaskDescriptor(selector, 10, external);
        var cache = descriptor.Cache;
        uint error = SelectorError(selector) | external;
        if ((cache.Access & 0x10) == 0 || (cache.Code && (cache.Access & 2) == 0) ||
            (!cache.Conforming && Math.Max(_cpl, selector & 3) > cache.Privilege))
            throw new CpuFaultException(10, error);
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, error);
        CommitSegment(segment, selector, descriptor);
    }
}
