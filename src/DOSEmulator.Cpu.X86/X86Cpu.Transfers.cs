namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    public bool TripleFaulted { get; private set; }

    private void FarTransfer(ushort selector, uint target, bool call, int width)
    {
        Descriptor descriptor = default;
        if (ProtectedMode && !Virtual8086)
        {
            if ((selector & 0xfffc) == 0) throw Gp();
            descriptor = ReadDescriptor(selector);
            if ((descriptor.Cache.Access & 0x10) == 0 && (descriptor.Cache.Access & 15) is 4 or 12)
            { TransferCallGate(selector, descriptor, call); return; }
            if ((descriptor.Cache.Access & 0x10) == 0 && (descriptor.Cache.Access & 15) is 1 or 3 or 5 or 9 or 11)
            { TransferTask(selector, descriptor, call); return; }
            descriptor = ValidateCodeSegment(selector, target, loaded: descriptor);
        }
        else CheckSegment(Segment.Cs, target, 1, fetch: true);
        if (call) CheckPushes(width, 2);
        if (ProtectedMode && !Virtual8086) PrepareSegment(descriptor);
        if (call) { Push(GetSegment(Segment.Cs), width); Push(Eip, width); }
        if (ProtectedMode && !Virtual8086) CommitSegment(Segment.Cs, (ushort)((selector & ~3) | _cpl), descriptor);
        else LoadSegment(Segment.Cs, selector);
        Eip = target;
    }

    private void ReturnNear(int width, uint adjustment = 0)
    {
        uint target = NearTarget(PeekStack(width));
        AdjustStack((uint)width + adjustment);
        Eip = target;
    }

    private void ReturnFar(int width, uint adjustment = 0, bool interrupt = false)
    {
        bool vm = Virtual8086;
        if (vm && interrupt) CheckVmSensitive();
        if (ProtectedMode && !vm && interrupt && (Eflags & 0x4000) != 0)
        { ReturnTask(); return; }
        uint slots = interrupt ? 3u : 2u;
        CheckSegment(Segment.Ss, StackPointer, (int)(slots * (uint)width));
        CheckAlignment(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), width);
        ProbeMemory(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), (int)(slots * (uint)width));
        uint target = PeekStack(width);
        ushort selector = (ushort)PeekStack(width, (uint)width);
        uint flags = interrupt ? PeekStack(width, (uint)width * 2) : Eflags;
        if (ProtectedMode && !vm && interrupt && width == 4 && _cpl == 0 && (flags & 0x20000) != 0)
        { EnterVirtual8086(target, selector, flags); return; }
        int privilege = selector & 3;
        bool outer = ProtectedMode && !vm && privilege > _cpl;
        uint newPointer = 0; ushort newSs = 0; Descriptor stack = default;
        if (outer)
        {
            CheckSegment(Segment.Ss, StackPointer, checked((int)((slots + 2) * (uint)width + adjustment)));
            ProbeMemory(unchecked(GetSegmentBase(Segment.Ss) + StackPointer), checked((int)((slots + 2) * (uint)width + adjustment)));
            newPointer = PeekStack(width, slots * (uint)width + adjustment);
            newSs = (ushort)PeekStack(width, (slots + 1) * (uint)width + adjustment);
        }
        Descriptor descriptor = default;
        if (ProtectedMode && !vm)
        {
            descriptor = ValidateCodeSegment(selector, target, returning: true);
            if (outer) stack = ValidateTransitionStack(newSs, privilege, false);
            if (target > descriptor.Cache.Limit) throw Gp();
            PrepareSegment(descriptor);
            if (outer) PrepareSegment(stack);
        }
        else CheckSegment(Segment.Cs, target, 1, fetch: true);
        // IOPL/IF permissions use the executing CPL, before changing CS.RPL.
        if (interrupt) RestoreFlags(flags, width, true);
        if (outer)
        {
            CommitSegment(Segment.Ss, newSs, stack);
            RestoreOuterStackPointer(newPointer, newSs, width);
            AdjustStack(adjustment);
            _cpl = privilege;
            InvalidateOuterDataSegments();
            OuterPrivilegeReturns++;
        }
        else AdjustStack(slots * (uint)width + adjustment);
        if (ProtectedMode && !vm) CommitSegment(Segment.Cs, selector, descriptor); else LoadSegment(Segment.Cs, selector);
        Eip = target;
    }

    private void RestoreFlags(uint value, int width, bool interrupt = false)
    {
        uint mask = width == 2 ? 0x7fd7u : ImplementedFlags;
        if (!interrupt) mask &= ~0x30000u; // POPF cannot change VM/RF.
        if (!ProtectedMode) mask &= ~0x20000u;
        else
        {
            if (_cpl != 0) mask &= ~0x3000u;
            if (_cpl > ((Eflags >> 12) & 3)) mask &= ~InterruptEnable;
            if (_cpl != 0) mask &= ~0x20000u;
            if (Virtual8086) mask &= ~0x33000u; // VM/IOPL/RF remain unchanged in a V86 IRET/POPF.
        }
        Eflags = ((Eflags & ~mask) | (value & mask)) | 2;
    }

    // Kept for existing real-mode callers. New runtime code uses the mode-neutral name.
    public void EnterRealModeInterrupt(byte vector) => EnterHardwareInterrupt(vector);
    public void EnterHardwareInterrupt(byte vector)
    {
        _waitingForNumericError = false;
        ControlCheckpoint checkpoint = SaveControl();
        try { DispatchInterrupt(vector, false); }
        catch (CpuFaultException fault) { RestoreControl(checkpoint); DeliverFault(fault); }
    }

    private void DispatchInterrupt(byte vector, bool software, uint? errorCode = null, bool fault = false, bool trap = false)
    {
        _debugDataActive = false; // Architectural exception frames are not data operands.
        if (software) _debugInterruptTransfer = true;
        if (vector == 1) Dr7 &= ~GeneralDetect;
        if (!ProtectedMode) { EnterRealInterrupt(vector); ClearDebugDeferral(); return; }
        bool vm = Virtual8086;
        uint index = (uint)vector * 8, tableError = index | 2u | (software || fault ? 0u : 1u);
        if (index + 7 > Idtr.Limit) throw Gp(tableError);
        uint address = unchecked(Idtr.Base + index);
        ProbeMemory(address, 8, supervisor: true);
        uint low = ReadMemory(address, 4, true), high = ReadMemory(unchecked(address + 4), 4, true);
        byte access = (byte)(high >> 8); int kind = access & 15;
        if ((access & 0x10) != 0 || kind is not (5 or 6 or 7 or 14 or 15)) throw Gp(tableError);
        if (vm && kind is 6 or 7) throw Gp(tableError);
        if (software && _cpl > ((access >> 5) & 3)) throw Gp(tableError);
        if ((access & 0x80) == 0) throw new CpuFaultException(11, tableError);
        if (kind == 5)
        {
            long epoch = _taskSwitchEpoch;
            try { SwitchTask((ushort)(low >> 16), TaskTransfer.Interrupt, software || fault ? 0u : 1u,
                gate: true, errorCode: errorCode, savedFlags: fault && !trap && vector != 1 ? Eflags | 0x10000u : Eflags); }
            finally { if (_taskSwitchEpoch != epoch) InterruptEntered?.Invoke(vector); }
            return;
        }
        int width = kind >= 14 ? 4 : 2;
        ushort selector = (ushort)(low >> 16);
        uint target = (low & 0xffff) | (width == 4 ? high & 0xffff0000 : 0);
        Descriptor code;
        try { code = ValidateCodeSegment(selector, target, interrupt: true); }
        catch (CpuFaultException invalid) when (!software && !fault && invalid.Vector is >= 10 and <= 13 && invalid.ErrorCode.HasValue)
        {
            throw new CpuFaultException(invalid.Vector, invalid.ErrorCode.Value | 1u);
        }
        int privilege = code.Cache.Conforming ? _cpl : code.Cache.Privilege;
        if (vm && (code.Cache.Conforming || privilege != 0)) throw Gp(SelectorError(selector) | (software || fault ? 0u : 1u));
        bool inner = privilege < _cpl;
        (ushort Selector, uint Pointer, Descriptor Descriptor) stack = default;
        int slots = (errorCode is null ? 3 : 4) + (inner ? 2 : 0) + (vm ? 4 : 0);
        if (inner)
        {
            stack = ReadPrivilegeStack(privilege, software || fault ? 0u : 1u);
            if (vm && (_trCache.Access & 15) != 11) throw new CpuFaultException(10, SelectorError(Tr));
            CheckTransitionPushes(stack.Descriptor.Cache, stack.Pointer, width, slots);
        }
        else
        {
            // Segment checks precede page translation; the error slot's high
            // word is reserved but not written on the reference 486.
            uint pointer = StackPointer;
            for (int i = 0; i < slots; i++)
            {
                pointer = unchecked(pointer - (uint)width) & Mask(StackAddressWidth);
                CheckSegment(Segment.Ss, pointer, width, true);
            }
        }
        if (target > code.Cache.Limit) throw Gp();
        PrepareSegment(code);
        if (inner) PrepareSegment(stack.Descriptor);
        ProbeStackWrites(inner ? stack.Descriptor.Cache : GetSegmentCache(Segment.Ss), inner ? stack.Pointer : StackPointer,
            width, slots, errorCode.HasValue, inner);
        uint savedFlags = fault && !trap && vector != 1 ? Eflags | 0x10000u : Eflags;
        InterruptEntered?.Invoke(vector);
        if (inner)
        {
            ushort oldSs = GetSegment(Segment.Ss);
            uint oldPointer = GetRegister(Register.Sp);
            CommitSegment(Segment.Ss, stack.Selector, stack.Descriptor);
            SetRegister(Register.Sp, stack.Pointer);
            _cpl = privilege;
            if (vm)
            {
                Eflags &= ~0x20000u;
                Push(GetSegment(Segment.Gs), width); Push(GetSegment(Segment.Fs), width);
                Push(GetSegment(Segment.Ds), width); Push(GetSegment(Segment.Es), width);
                foreach (Segment data in new[] { Segment.Es, Segment.Ds, Segment.Fs, Segment.Gs })
                { _segments[(int)data] = 0; _segmentCaches[(int)data] = default; }
                Virtual8086Exits++;
            }
            Push(oldSs, width); Push(oldPointer, width);
            PrivilegeStackSwitches++;
        }
        Push(savedFlags, width); Push(GetSegment(Segment.Cs), width); Push(Eip, width);
        if (errorCode is { } error) PushFaultError(error, width);
        CommitSegment(Segment.Cs, (ushort)((selector & ~3) | _cpl), code);
        Eip = target;
        Eflags &= ~(Trap | 0x34000u); // TF, NT, RF, VM; trap gates preserve IF.
        if (kind is 6 or 14) Eflags &= ~InterruptEnable;
        Halted = false; _interruptShadow = 0;
        ClearDebugDeferral();
    }

    private static bool DoubleFault(byte first, byte second)
    {
        bool contributoryFirst = first is 0 or 10 or 11 or 12 or 13;
        bool contributorySecond = second is 0 or 10 or 11 or 12 or 13;
        return (contributoryFirst && contributorySecond) || (first == 14 && (contributorySecond || second == 14));
    }

    private void DeliverFault(CpuFaultException initial)
    {
        CpuFaultException current = initial;
        // A failing benign delivery may become contributory, then #DF, then shutdown.
        for (int attempts = 0; attempts < 4; attempts++)
        {
            ControlCheckpoint checkpoint = SaveControl();
            try
            {
                uint? error = current.ErrorCode ?? (current.Vector is 8 or 10 or 11 or 12 or 13 or 14 or 17 ? 0u : null);
                DispatchInterrupt(current.Vector, false, error, true, current.Trap);
                return;
            }
            catch (CpuFaultException next)
            {
                RestoreControl(checkpoint);
                if (!ProtectedMode) throw Unsupported("Nested real-mode exception delivery is not implemented yet.");
                if (current.Vector == 8) { TripleFaulted = true; Halted = true; return; }
                current = DoubleFault(current.Vector, next.Vector) ? new(8, 0) : next;
            }
        }
        throw Unsupported("Exception-delivery chain exceeded the implemented bound.");
    }
}
