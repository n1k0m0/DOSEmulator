namespace DOSEmulator.Cpu.X86;

public sealed partial class X86Cpu
{
    private bool _lockPrefix, _lockedMemory;
    public long LockedMemoryInstructions { get; private set; }
    public long InvalidLockPrefixes { get; private set; }
    public long AlignmentFaults { get; private set; }
    public bool AlignmentCheckEnabled => ProtectedMode && CurrentPrivilegeLevel == 3 &&
        (Cr0 & 0x40000) != 0 && (Eflags & 0x40000) != 0;

    private void InvalidLock()
    {
        InvalidLockPrefixes++;
        throw new CpuFaultException(6);
    }

    private void ValidateLockOpcode(byte opcode, bool extended = false)
    {
        if (!_lockPrefix) return;
        bool allowed = extended ? opcode is 0xab or 0xb3 or 0xbb or 0xba or 0xb0 or 0xb1 or 0xc0 or 0xc1
            : opcode == 0x0f || (opcode <= 0x31 && (opcode & 7) <= 1) ||
                opcode is 0x80 or 0x81 or 0x83 or 0x86 or 0x87 or 0xf6 or 0xf7 or 0xfe or 0xff;
        if (!allowed) InvalidLock();
    }

    private void ValidateLockOperand(byte modrm, bool allowed = true)
    {
        if (!_lockPrefix) return;
        if (!allowed || modrm >= 0xc0) InvalidLock();
        _lockedMemory = true;
    }

    private void CheckAlignment(uint address, int alignment, bool supervisor = false)
    {
        if (!supervisor && alignment > 1 && AlignmentCheckEnabled && (address & (uint)(alignment - 1)) != 0)
        {
            AlignmentFaults++;
            throw new CpuFaultException(17, 0);
        }
    }

    private void ExchangeAddCompare(byte opcode)
    {
        byte modrm = FetchByte(); ValidateLockOperand(modrm);
        Operand destination = DecodeOperand(modrm);
        int width = (opcode & 1) == 0 ? 1 : Width, source = (modrm >> 3) & 7;
        uint value = Read(destination, width, true), input = ReadRegister(source, width);
        if (opcode is 0xc0 or 0xc1)
        {
            uint sum = Alu(0, value, input, width);
            // Snapshot the address and both inputs before changing the source;
            // if the registers alias, DEST receives the sum last.
            WriteRegister(source, width, value);
            Write(destination, width, sum);
        }
        else
        {
            uint accumulator = ReadRegister(0, width);
            Alu(7, accumulator, value, width);
            // The 486 issues a destination write on BOTH comparison outcomes.
            Write(destination, width, accumulator == value ? input : value);
            if (accumulator != value) WriteRegister(0, width, value);
        }
    }
}
