// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Nils Kopal
namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly uint[] _debugAddresses = new uint[4];
    /// <summary>First linear hardware-breakpoint address register.</summary>
    public uint Dr0 => _debugAddresses[0];
    /// <summary>Second linear hardware-breakpoint address register.</summary>
    public uint Dr1 => _debugAddresses[1];
    /// <summary>Third linear hardware-breakpoint address register.</summary>
    public uint Dr2 => _debugAddresses[2];
    /// <summary>Fourth linear hardware-breakpoint address register.</summary>
    public uint Dr3 => _debugAddresses[3];
    /// <summary>Sticky model-specific debug status register with its documented reserved-bit mask.</summary>
    public uint Dr6 { get; private set; }
    /// <summary>Model-specific comparator/control register including local/global enables and general detect.</summary>
    public uint Dr7 { get; private set; }
    /// <summary>Diagnostic count of instruction breakpoint faults.</summary>
    public long InstructionBreakpointFaults { get; private set; }
    /// <summary>Diagnostic count of committed memory or I/O breakpoint traps.</summary>
    public long DataBreakpointTraps { get; private set; }
    /// <summary>Diagnostic count of single step traps.</summary>
    public long SingleStepTraps { get; private set; }
    /// <summary>Diagnostic count of general detect faults.</summary>
    public long GeneralDetectFaults { get; private set; }
    private const uint Resume = 0x10000, GeneralDetect = 0x2000;
    private bool _debugDataActive, _debugStepShadowed, _debugInterruptTransfer;
    private uint _debugDataMatches, _debugDeferredStatus;
    private bool _debugDataTriggered, _debugDeferredTrigger, _debugDeferredData;
    private int _debugShadow;

    /// <summary>Initializes the model's 486 debug registers, counters and deferred debug state.</summary>
    private void ResetDebug()
    {
        Array.Clear(_debugAddresses);
        Dr6 = 0xffff0ff0; Dr7 = 0x400;
        InstructionBreakpointFaults = DataBreakpointTraps = SingleStepTraps = GeneralDetectFaults = 0;
        _debugDataActive = _debugStepShadowed = _debugInterruptTransfer = false;
        _debugDataMatches = 0; _debugDataTriggered = false;
        ClearDebugDeferral();
    }
    /// <summary>Discards debug causes held behind the bounded SS-instruction shadow.</summary>
    private void ClearDebugDeferral()
    {
        _debugShadow = 0; _debugDeferredStatus = 0;
        _debugDeferredTrigger = _debugDeferredData = false;
    }
    /// <summary>Defers maskable interrupts and debug recognition across the following instruction boundary.</summary>
    private void BeginSsShadow() { _interruptShadow = 1; _debugShadow = 1; }
    /// <summary>Captures the current debug-shadow state and clears this instruction's data-match accumulator.</summary>
    private void BeginDebugInstruction()
    {
        _debugStepShadowed = _debugShadow > 0;
        if (_debugShadow > 0) _debugShadow--;
        _debugDataMatches = 0; _debugDataTriggered = false;
        _debugInterruptTransfer = false; _debugDataActive = true;
    }
    /// <summary>Latches DR6 causes, clears general detect and creates a DB fault or trap for the common dispatcher.</summary>
    private CpuFaultException DebugException(uint status, bool trap)
    {
        Dr6 |= status; // Status is sticky until the guest writes DR6.
        Dr7 &= ~GeneralDetect;
        _debugDataActive = false;
        return new(1, trap: trap);
    }
    /// <summary>Executes MOV DR with model-specific DE/alias, privilege, GD and reserved-bit rules.</summary>
    private void MoveDebugRegister(byte opcode)
    {
        byte modrm = FetchByte();
        int debug = (modrm >> 3) & 7, register = modrm & 7;
        // P54C illegal-opcode class precedes protection/general-detect faults.
        if ((Cr4 & 8) != 0 && debug is 4 or 5) throw new CpuFaultException(6);
        RequirePrivilegeZero();
        if ((Dr7 & GeneralDetect) != 0)
        {
            GeneralDetectFaults++;
            throw DebugException(0x2000, false);
        }
        // With DE clear (always on the 486), DR4/5 alias DR6/7. Mod bits
        // consume no displacement/SIB or memory operand. Transfers stay 32 bits.
        if (debug is 4 or 5) debug += 2;
        if (opcode == 0x21)
            WriteRegister(register, 4, debug < 4 ? _debugAddresses[debug] : debug == 6 ? Dr6 : Dr7);
        else
        {
            uint value = ReadRegister(register, 4);
            if (debug < 4) _debugAddresses[debug] = value;
            else if (debug == 6) Dr6 = (value & 0xe00f) | 0xffff0ff0;
            // P54C hardwires bit 12 to zero; 486 keeps its historical writable bit.
            // LE/GE remain readable but exactness is fixed in both reference models.
            else Dr7 = (value & (Model == X86CpuModel.Intel486Dx ? 0xffff33ffu : 0xffff23ffu)) | 0x400;
        }
    }
    /// <summary>Bit mask of the currently enabled local/global debug-address comparators.</summary>
    private uint EnabledDebugMask => (Dr7 | (Dr7 >> 1)) & 0x55;
    /// <summary>Checks whether any matching address comparator is locally or globally enabled.</summary>
    private bool DebugMatchEnabled(uint matches)
    {
        for (int i = 0; i < 4; i++)
            if ((matches & (1u << i)) != 0 && ((Dr7 >> (i * 2)) & 3) != 0) return true;
        return false;
    }
    /// <summary>Recognizes enabled linear execution breakpoints before instruction effects, respecting RF and SS deferral.</summary>
    private void CheckInstructionBreakpoint(uint address)
    {
        if (_debugStepShadowed || (Eflags & Resume) != 0 || EnabledDebugMask == 0) return;
        uint matches = 0;
        for (int i = 0; i < 4; i++)
            if (((Dr7 >> (16 + i * 4)) & 15) == 0 && _debugAddresses[i] == address) matches |= 1u << i;
        if (!DebugMatchEnabled(matches)) return;
        InstructionBreakpointFaults++;
        throw DebugException(matches, false);
    }
    /// <summary>Accumulates eligible data-breakpoint matches without prematurely delivering a post-instruction trap.</summary>
    private void ObserveDebugData(uint address, int width, bool write, bool supervisor)
    {
        if (!_debugDataActive || supervisor || (EnabledDebugMask == 0 && (Eflags & Trap) == 0)) return;
        uint matches = 0;
        for (int i = 0; i < 4; i++)
        {
            uint field = (Dr7 >> (16 + i * 4)) & 15, kind = field & 3;
            if (kind != 3 && !(write && kind == 1)) continue;
            int length = (field >> 2) switch { 0 => 1, 1 => 2, 3 => 4, _ => 0 };
            if (length == 0) continue; // Reserved 486 RW=10/LEN=10: no extension enabled.
            uint first = _debugAddresses[i] & ~((uint)length - 1);
            for (int part = 0; part < width; part++)
                if (unchecked(address + (uint)part - first) < length) { matches |= 1u << i; break; }
        }
        _debugDataMatches |= matches;
        _debugDataTriggered |= DebugMatchEnabled(matches);
    }
    /// <summary>Accumulates completed CPU I/O matches in the same commit/REP/SS pipeline as memory breakpoints.</summary>
    private void ObserveDebugIo(ushort port, int width)
    {
        if ((Cr4 & 8) == 0 || !_debugDataActive || (EnabledDebugMask == 0 && (Eflags & Trap) == 0)) return;
        uint matches = 0;
        for (int i = 0; i < 4; i++)
        {
            uint field = (Dr7 >> (16 + i * 4)) & 15;
            if ((field & 3) != 2) continue;
            int length = (field >> 2) switch { 0 => 1, 1 => 2, 3 => 4, _ => 0 };
            if (length == 0) continue; // LEN=10 remains an unimplemented/reserved encoding.
            uint first = _debugAddresses[i] & ~((uint)length - 1);
            // Zero-extend the CPU port; do not truncate the high DR-address bits.
            for (int part = 0; part < width; part++)
                if (unchecked((uint)port + (uint)part - first) < length) { matches |= 1u << i; break; }
        }
        _debugDataMatches |= matches;
        _debugDataTriggered |= DebugMatchEnabled(matches);
    }
    /// <summary>Reads an already permitted CPU port and records a possible post-commit I/O breakpoint.</summary>
    private uint ReadPort(ushort port, int width)
    {
        uint value = _io.Read(port, width);
        ObserveDebugIo(port, width);
        return value;
    }
    /// <summary>Writes an already permitted CPU port and records a possible post-commit I/O breakpoint.</summary>
    private void WritePort(ushort port, uint value, int width)
    {
        _io.Write(port, value, width);
        ObserveDebugIo(port, width);
    }
    /// <summary>Delivers eligible TF/data/I/O debug causes after commit, including REP boundaries and SS deferral.</summary>
    private void CompleteDebugInstruction(uint previousFlags, bool sameTask, bool repeatContinues)
    {
        _debugDataActive = false;
        if (!sameTask || _debugInterruptTransfer) { ClearDebugDeferral(); return; }
        bool single = (previousFlags & Trap) != 0;
        uint status = _debugDataMatches | (single ? 0x4000u : 0);
        bool trigger = _debugDataTriggered || single;
        if (_debugShadow != 0)
        {
            _debugDeferredStatus |= status; _debugDeferredTrigger |= trigger;
            _debugDeferredData |= _debugDataTriggered;
            return;
        }
        status |= _debugDeferredStatus; trigger |= _debugDeferredTrigger;
        bool data = _debugDataTriggered || _debugDeferredData;
        ClearDebugDeferral();
        if (!trigger) return;
        if (data) DataBreakpointTraps++;
        if ((status & 0x4000) != 0) SingleStepTraps++;
        // A trap between REP elements resumes at the first prefix; don't refault
        // on an execution breakpoint before completing the remaining elements.
        if (repeatContinues) Eflags |= Resume;
        DeliverFault(DebugException(status, true));
    }
}
