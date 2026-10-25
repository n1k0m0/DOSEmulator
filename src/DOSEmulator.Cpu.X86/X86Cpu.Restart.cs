namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly record struct SegmentCheckpoint(ushort Selector, SegmentDescriptorCache Cache);
    private readonly record struct ControlCheckpoint(long TaskEpoch, uint Ip, uint Flags, uint Esp, int Cpl, int Shadow, bool Halt,
        long Switches, long Returns, long Calls, long VmEntries, long VmExits, SegmentCheckpoint Es, SegmentCheckpoint Cs, SegmentCheckpoint Ss,
        SegmentCheckpoint Ds, SegmentCheckpoint Fs, SegmentCheckpoint Gs);
    private SegmentCheckpoint SaveSegment(Segment segment) => new(GetSegment(segment), GetSegmentCache(segment));
    private ControlCheckpoint SaveControl() => new(_taskSwitchEpoch, Eip, Eflags, GetRegister(Register.Sp), _cpl, _interruptShadow, Halted,
        PrivilegeStackSwitches, OuterPrivilegeReturns, CallGateTransfers, Virtual8086Entries, Virtual8086Exits, SaveSegment(Segment.Es), SaveSegment(Segment.Cs), SaveSegment(Segment.Ss),
        SaveSegment(Segment.Ds), SaveSegment(Segment.Fs), SaveSegment(Segment.Gs));
    private void RestoreSegment(Segment segment, SegmentCheckpoint saved)
    {
        _segments[(int)segment] = saved.Selector; _segmentCaches[(int)segment] = saved.Cache;
    }
    private void RestoreControl(ControlCheckpoint saved)
    {
        if (saved.TaskEpoch != _taskSwitchEpoch) return;
        Eip = saved.Ip; Eflags = saved.Flags; SetRegister(Register.Sp, saved.Esp);
        _cpl = saved.Cpl; _interruptShadow = saved.Shadow; Halted = saved.Halt;
        PrivilegeStackSwitches = saved.Switches; OuterPrivilegeReturns = saved.Returns; CallGateTransfers = saved.Calls;
        Virtual8086Entries = saved.VmEntries; Virtual8086Exits = saved.VmExits;
        RestoreSegment(Segment.Es, saved.Es); RestoreSegment(Segment.Cs, saved.Cs); RestoreSegment(Segment.Ss, saved.Ss);
        RestoreSegment(Segment.Ds, saved.Ds); RestoreSegment(Segment.Fs, saved.Fs); RestoreSegment(Segment.Gs, saved.Gs);
        // CR2, page-table A/D writes and TLB fills deliberately survive a fault.
    }
}
