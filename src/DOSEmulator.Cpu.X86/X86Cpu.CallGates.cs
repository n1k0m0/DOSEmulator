namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    public long CallGateTransfers { get; private set; }

    private void TransferCallGate(ushort gateSelector, Descriptor gate, bool call)
    {
        if (Math.Max(_cpl, gateSelector & 3) > gate.Cache.Privilege) throw Gp(SelectorError(gateSelector));
        if ((gate.Cache.Access & 0x80) == 0) throw new CpuFaultException(11, SelectorError(gateSelector));
        // The gate type determines frame/parameter width, independently of the
        // instruction's operand size. Its operand offset is entirely ignored.
        int width = (gate.Cache.Access & 15) == 12 ? 4 : 2;
        ushort selector = (ushort)(gate.Low >> 16);
        if ((selector & 0xfffc) == 0) throw Gp();
        Descriptor code = ReadDescriptor(selector);
        var cache = code.Cache;
        if ((cache.Access & 0x10) == 0 || !cache.Code || cache.Privilege > _cpl ||
            (!call && !cache.Conforming && cache.Privilege != _cpl)) throw Gp(SelectorError(selector));
        if ((cache.Access & 0x80) == 0) throw new CpuFaultException(11, SelectorError(selector));
        uint target = (gate.Low & 0xffff) | (width == 4 ? gate.High & 0xffff0000 : 0);
        int privilege = cache.Conforming ? _cpl : cache.Privilege;
        bool inner = privilege < _cpl;
        int count = inner ? (int)(gate.High & 31) : 0;
        (ushort Selector, uint Pointer, Descriptor Descriptor) stack = default;
        if (inner)
        {
            stack = ReadPrivilegeStack(privilege, 0);
            // 486 CALL reports the new SS selector on a transition stack limit
            // fault. Same-level calls report zero, as do ordinary stack uses.
            CheckTransitionPushes(stack.Descriptor.Cache, stack.Pointer, width, count + 4, SelectorError(stack.Selector));
        }
        else if (call) CheckPushes(width, 2);
        if (target > cache.Limit) throw Gp();
        if (count != 0) CheckSegment(Segment.Ss, StackPointer, count * width);
        PrepareSegment(code);
        if (inner) PrepareSegment(stack.Descriptor);

        Span<uint> parameters = stackalloc uint[31];
        if (inner)
        {
            // Preflight in architectural frame order, before committing any
            // stack bytes or hidden registers. Source parameters use the OLD
            // CPL; new ring 0/1/2 stack accesses are supervisor accesses.
            uint pointer = stack.Pointer;
            uint mask = stack.Descriptor.Cache.Default32 ? uint.MaxValue : 0xffff;
            void ProbePush()
            {
                pointer = unchecked(pointer - (uint)width) & mask;
                ProbeMemory(unchecked(stack.Descriptor.Cache.Base + pointer), width, true, true);
            }
            ProbePush(); ProbePush(); // saved SS and ESP/SP
            for (int i = count - 1; i >= 0; i--)
            {
                uint offset = unchecked(StackPointer + (uint)(i * width));
                parameters[i] = ReadMemory(unchecked(GetSegmentBase(Segment.Ss) + offset), width);
                ProbePush();
            }
            ProbePush(); ProbePush(); // saved CS and EIP/IP
        }
        if (inner)
        {
            ushort oldSs = GetSegment(Segment.Ss);
            uint oldPointer = GetRegister(Register.Sp);
            CommitSegment(Segment.Ss, stack.Selector, stack.Descriptor);
            SetRegister(Register.Sp, stack.Pointer);
            _cpl = privilege;
            Push(oldSs, width); Push(oldPointer, width);
            for (int i = count - 1; i >= 0; i--) Push(parameters[i], width);
            PrivilegeStackSwitches++;
        }
        if (call) { Push(GetSegment(Segment.Cs), width); Push(Eip, width); }
        CommitSegment(Segment.Cs, (ushort)((selector & ~3) | _cpl), code);
        Eip = target;
        CallGateTransfers++;
    }
}
