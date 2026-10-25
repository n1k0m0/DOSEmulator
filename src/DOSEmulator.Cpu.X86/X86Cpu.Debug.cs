namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private readonly uint[] _debugAddresses = new uint[4];
    public uint Dr0 => _debugAddresses[0];
    public uint Dr1 => _debugAddresses[1];
    public uint Dr2 => _debugAddresses[2];
    public uint Dr3 => _debugAddresses[3];
    public uint Dr6 { get; private set; }
    public uint Dr7 { get; private set; }
    public long InstructionBreakpointFaults { get; private set; }
    public long DataBreakpointTraps { get; private set; }
    public long SingleStepTraps { get; private set; }
    public long GeneralDetectFaults { get; private set; }
    private const uint Resume = 0x10000, GeneralDetect = 0x2000;
    private bool _debugDataActive, _debugStepShadowed, _debugInterruptTransfer;
    private uint _debugDataMatches, _debugDeferredStatus;
    private bool _debugDataTriggered, _debugDeferredTrigger, _debugDeferredData;
    private int _debugShadow;

    private void ResetDebug()
    {
        Array.Clear(_debugAddresses);
        Dr6 = 0xffff0ff0; Dr7 = 0x400;
        InstructionBreakpointFaults = DataBreakpointTraps = SingleStepTraps = GeneralDetectFaults = 0;
        _debugDataActive = _debugStepShadowed = _debugInterruptTransfer = false;
        _debugDataMatches = 0; _debugDataTriggered = false;
        ClearDebugDeferral();
    }
    private void ClearDebugDeferral()
    {
        _debugShadow = 0; _debugDeferredStatus = 0;
        _debugDeferredTrigger = _debugDeferredData = false;
    }
    private void BeginSsShadow() { _interruptShadow = 1; _debugShadow = 1; }
    private void BeginDebugInstruction()
    {
        _debugStepShadowed = _debugShadow > 0;
        if (_debugShadow > 0) _debugShadow--;
        _debugDataMatches = 0; _debugDataTriggered = false;
        _debugInterruptTransfer = false; _debugDataActive = true;
    }
    private CpuFaultException DebugException(uint status, bool trap)
    {
        Dr6 |= status; // Status is sticky until the guest writes DR6.
        Dr7 &= ~GeneralDetect;
        _debugDataActive = false;
        return new(1, trap: trap);
    }
    private void MoveDebugRegister(byte opcode)
    {
        byte modrm = FetchByte();
        RequirePrivilegeZero();
        if ((Dr7 & GeneralDetect) != 0)
        {
            GeneralDetectFaults++;
            throw DebugException(0x2000, false);
        }
        // No CR4.DE on this 486: DR4/5 alias DR6/7. The Mod bits are ignored;
        // no displacement/SIB or memory operand is consumed. Always 32 bits.
        int debug = (modrm >> 3) & 7, register = modrm & 7;
        if (debug is 4 or 5) debug += 2;
        if (opcode == 0x21)
            WriteRegister(register, 4, debug < 4 ? _debugAddresses[debug] : debug == 6 ? Dr6 : Dr7);
        else
        {
            uint value = ReadRegister(register, 4);
            if (debug < 4) _debugAddresses[debug] = value;
            else if (debug == 6) Dr6 = (value & 0xe00f) | 0xffff0ff0;
            // 486 bit 12 is writable. LE/GE are retained but exactness is fixed.
            else Dr7 = (value & 0xffff33ff) | 0x400;
        }
    }
    private uint EnabledDebugMask => (Dr7 | (Dr7 >> 1)) & 0x55;
    private bool DebugMatchEnabled(uint matches)
    {
        for (int i = 0; i < 4; i++)
            if ((matches & (1u << i)) != 0 && ((Dr7 >> (i * 2)) & 3) != 0) return true;
        return false;
    }
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
