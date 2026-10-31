// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private bool _checkpointProtected, _controlCheckpointSaved;
    private uint _checkpointIp, _checkpointFlags, _checkpointEsp;
    private ControlCheckpoint _controlCheckpoint;
    /// <summary>Diagnostic count of control checkpoints saved.</summary>
    public long ControlCheckpointsSaved { get; private set; }
    // Other instructions only change GPR/EFLAGS/EIP, which retain their small
    // unconditional restart record. These families can change hidden control state.
    private static bool NeedsControlCheckpoint(byte opcode) => opcode is
        0x07 or 0x17 or 0x1f or 0x8e or 0x9a or 0xc4 or 0xc5 or
        0xca or 0xcb or 0xcc or 0xcd or 0xce or 0xcf or 0xea or
        0x6c or 0x6d or 0x6e or 0x6f or 0xe4 or 0xe5 or 0xe6 or 0xe7 or 0xec or 0xed or 0xee or 0xef;
    /// <summary>Skips the large checkpoint only for known GPR/flags-only 0F families; devices still capture lazily.</summary>
    private static bool NeedsTwoByteControlCheckpoint(byte opcode) => opcode is not (
        >= 0x80 and <= 0x9f or >= 0xc8 and <= 0xcf or
        0xaf or 0xb6 or 0xb7 or 0xbe or 0xbf or
        0xa3 or 0xab or 0xb3 or 0xbb or 0xba or 0xb0 or 0xb1 or 0xc0 or 0xc1 or
        0xa4 or 0xa5 or 0xac or 0xad or 0xbc or 0xbd);
    /// <summary>Ensures restart control state is available before a mapped-device path can expose a fault.</summary>
    private void EnsureDeviceCheckpoint(uint physical, int width = 1)
    {
        // Ordinary operands are at most four bytes. Without paging a bus
        // access can cross from a plain page into a mapped page, so inspect
        // that end before the first byte is allowed to reach any device.
        if (_checkpointProtected && !_controlCheckpointSaved &&
            (!_memory.IsPlainRamByte(physical) ||
             (physical & 4095) + width > 4096 && !_memory.IsPlainRamByte(unchecked(physical + (uint)width - 1))))
            EnsureControlCheckpoint();
    }
    /// <summary>Lazily captures large control/segment state before its first mutation; GPR restart copies remain eager.</summary>
    private void EnsureControlCheckpoint()
    {
        if (!_checkpointProtected || _controlCheckpointSaved) return;
        _controlCheckpoint = SaveControl(_checkpointIp,_checkpointFlags,_checkpointEsp);
        _controlCheckpointSaved = true; ControlCheckpointsSaved++;
    }
    private readonly record struct SegmentCheckpoint(ushort Selector, SegmentDescriptorCache Cache);
    private readonly record struct ControlCheckpoint(long TaskEpoch, uint Ip, uint Flags, uint Esp, int Cpl, int Shadow, bool Halt,
        long Switches, long Returns, long Calls, long VmEntries, long VmExits, SegmentCheckpoint Es, SegmentCheckpoint Cs, SegmentCheckpoint Ss,
        SegmentCheckpoint Ds, SegmentCheckpoint Fs, SegmentCheckpoint Gs);
    /// <summary>Captures a visible selector together with the hidden cache required for exact instruction rollback.</summary>
    private SegmentCheckpoint SaveSegment(Segment segment) => new(GetSegment(segment), GetSegmentCache(segment));
    /// <summary>Captures the control/system/segment portion of a restart checkpoint.</summary>
    private ControlCheckpoint SaveControl() => SaveControl(Eip,Eflags,GetRegister(Register.Sp));
    /// <summary>Constructs the exact restart record directly, avoiding an intermediate large struct copy.</summary>
    private ControlCheckpoint SaveControl(uint ip,uint flags,uint esp) => new(_taskSwitchEpoch, ip, flags, esp, _cpl, _interruptShadow, Halted,
        PrivilegeStackSwitches, OuterPrivilegeReturns, CallGateTransfers, Virtual8086Entries, Virtual8086Exits, SaveSegment(Segment.Es), SaveSegment(Segment.Cs), SaveSegment(Segment.Ss),
        SaveSegment(Segment.Ds), SaveSegment(Segment.Fs), SaveSegment(Segment.Gs));
    /// <summary>Restores a saved visible selector and its corresponding hidden descriptor cache.</summary>
    private void RestoreSegment(Segment segment, SegmentCheckpoint saved)
    {
        _segments[(int)segment] = saved.Selector; _segmentCaches[(int)segment] = saved.Cache;
    }
    /// <summary>Restores checkpointed control state only within the caller's current-task rollback contract.</summary>
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
